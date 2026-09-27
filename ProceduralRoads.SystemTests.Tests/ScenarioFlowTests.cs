using System.Text.Json;
using ProceduralRoads.SystemTests;
using Valheim.Testing.Game;
using valheim_cli.Testing;
using Xunit;

public class ScenarioFlowTests
{
    private static readonly Piece Expected = new(10, [1, 2, 3], [0, 0, 0, 1]);
    private static readonly ZoneExpectation[] Zones = [new(0, 0, [Expected])];
    [Fact] public async Task BridgeChecksBeforeAndAfterReloadAndIssuesEachMutationOnce()
    {
        var fake = new Transport(); using var actor = Actor(fake); var report = new ScenarioReport("bridge");
        var after = new Transport { Loaded = true };
        await RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => Actor(after), Zones, report);
        Assert.True(report.Passed); Assert.Equal(1, fake.Commands.Count(x => x.StartsWith("road_path")));
        Assert.Equal(1, fake.Commands.Count(x => x == "road_bridges respawn")); Assert.Equal(1, fake.Commands.Count(x => x == "cli_save"));
        Assert.Contains("cli_extension roads.testing/bridge-zone 0 0", after.Commands);
    }
    [Fact] public async Task ExistingPendingWorkCannotStandInForOurAppend()
    {
        var fake = new Transport { Pending = 1 }; using var actor = Actor(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => throw new Exception(), Zones, new("bridge")));
        Assert.DoesNotContain(fake.Commands, x => x.StartsWith("road_path"));
    }
    [Fact] public async Task IncompleteCensusStopsBeforeSave()
    {
        var fake = new Transport { CensusComplete = false }; using var actor = Actor(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => throw new Exception(), Zones, new("bridge")));
        Assert.DoesNotContain("cli_save", fake.Commands);
    }
    [Fact] public async Task RegenerationAfterRestartDoesNotCountAsPersistence()
    {
        var fake = new Transport(); using var actor = Actor(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => Actor(new Transport()), Zones, new("bridge")));
    }
    [Fact] public async Task MissingPieceAfterRestartFailsEvenWhenQueueIsEmpty()
    {
        var fake = new Transport(); using var actor = Actor(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => Actor(new Transport { Loaded = true, Missing = true }), Zones, new("bridge")));
    }
    [Fact] public async Task CancellationBeforeAppendIssuesNoMutation()
    {
        var fake = new Transport(); using var actor = Actor(fake); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => throw new Exception(), Zones, new("bridge"), stop.Token));
        Assert.DoesNotContain(fake.Commands, x => x.StartsWith("road_path"));
    }
    [Fact] public async Task UnknownMutationOutcomeIsNotRetried()
    {
        var fake = new Transport { LoseAppendReply = true }; using var actor = Actor(fake);
        await Assert.ThrowsAsync<IOException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => throw new Exception(), Zones, new("bridge")));
        Assert.Equal(1, fake.Commands.Count(x => x.StartsWith("road_path"))); Assert.DoesNotContain("road_bridges respawn", fake.Commands);
    }
    [Fact] public async Task EmptyGenerationWaitsForCompletionRatherThanSavingTransientZero()
    {
        var fake = new Transport(); using var actor = Actor(fake);
        await RoadsScenarios.EmptyNetworkReplacesOld(actor, () => { fake.Cells = 0; fake.IncompleteReads = 1; }, () => Actor(new Transport { Loaded = true, Cells = 0 }), new("empty"));
        Assert.True(fake.ReadsAfterGenerate >= 2); Assert.Contains("cli_save", fake.Commands);
    }
    [Fact] public async Task GenerationTimeoutDoesNotSaveOrRestart()
    {
        var fake = new Transport(); using var actor = Actor(fake); bool restart = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.EmptyNetworkReplacesOld(actor, () => { fake.Cells = 0; fake.IncompleteReads = 100; },
            () => { restart = true; return Actor(new Transport()); }, new("empty"), TimeSpan.FromMilliseconds(10)));
        Assert.False(restart); Assert.DoesNotContain("cli_save", fake.Commands);
    }
    private static GameActor Actor(Transport fake)
    { var actor = new GameActor("test", fake); actor.VerifyEnvironment("cli_expect worlduid=1"); return actor; }
    private sealed class Transport : IGameTransport
    {
        public bool Loaded, Missing, LoseAppendReply, CensusComplete = true;
        public int Pending, Cells = 4, IncompleteReads, ReadsAfterGenerate;
        public List<string> Commands = [];
        public CommandResult Execute(string command, TimeSpan timeout)
        {
            Commands.Add(command);
            if (command.StartsWith("cli_expect")) return Ok("OK: EXPECT");
            if (command == "cli_extensions") return Ok("EXTENSIONS " + JsonSerializer.Serialize(new { apiVersion = 1, extensions = new[] { new { id = "roads.testing", instance = "a", closing = false,
                commands = new[] { new { name = "network", resultVersion = 1, readOnly = true }, new { name = "bridge-zone", resultVersion = 1, readOnly = true } } } } }));
            if (command.StartsWith("road_path")) { Pending = 1; if (LoseAppendReply) throw new IOException("Outcome unknown"); return Ok("OK: appended"); }
            if (command == "road_bridges respawn") { Pending = 0; return Ok("OK: respawn"); }
            if (command == "cli_save") return Ok("OK: SAVE saveNumber=2");
            object data;
            if (command.StartsWith("cli_extension roads.testing/bridge-zone"))
                data = new { source = "zdo-store", complete = CensusComplete, zoneX = 0, zoneZ = 0, pieces = Missing ? Array.Empty<object>() : new object[] { new { prefabHash = 10, position = Expected.Position, rotation = Expected.Rotation } } };
            else if (command == "cli_extension roads.testing/network")
            {
                if (Cells == 0) ReadsAfterGenerate++;
                data = new { source = "roads-memory", complete = IncompleteReads-- <= 0, cells = Cells, points = Cells, crossings = Cells, pendingZones = Pending, loadedFromSave = Loaded };
            }
            else throw new InvalidOperationException("Unexpected command: " + command);
            return Ok("EXTENSION_RESULT " + JsonSerializer.Serialize(new { schemaVersion = 1, ok = true, extension = "roads.testing", instance = "a", data }));
        }
        private static CommandResult Ok(string line) => new() { Ok = true, Output = [line] };
        public void Dispose() { }
    }
}
