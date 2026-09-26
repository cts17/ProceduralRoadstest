using ProceduralRoads.SystemTests;
using Valheim.Testing.Game;
using valheim_cli.Testing;
using Xunit;

public class PilotTests
{
    private static Piece Piece(int prefab = 1, double y = 0, double yaw = 0) => new(prefab, [1, y, 3], [0, Math.Sin(yaw*Math.PI/360), 0, Math.Cos(yaw*Math.PI/360)]);
    [Fact] public void StackedPiecesMatchFullTransform() => PieceComparison.Match([Piece(y: 3), Piece(y: 0)], [Piece(y: 0), Piece(y: 3)], .05, 1);
    [Fact] public void DuplicateCannotReplaceMissingPiece() => Assert.Throws<InvalidOperationException>(() => PieceComparison.Match([Piece(), Piece()], [Piece(), Piece(y: 3)], .05, 1));
    [Fact] public void RotatedPieceCannotMatchByPosition() => Assert.Throws<InvalidOperationException>(() => PieceComparison.Match([Piece(yaw: 90)], [Piece()], .05, 1));
    [Fact] public void OrdinarySaveRotationNoiseIsAccepted() => PieceComparison.Match([Piece(yaw: .8)], [Piece()], .05, 1);
    [Fact] public void AssignmentCanRevisitAnEarlierAmbiguousMatch()
    {
        PieceComparison.Match([Piece(y: .04), Piece(y: -.04)], [Piece(), Piece(y: .08)], .05, 1);
    }
    [Fact] public void EmptySaveScenarioConfirmsReloadAndSave()
    {
        var fake = new Fake(); using var actor = Actor(fake); var report = new ScenarioReport("empty");
        RoadsScenarios.EmptyNetworkReplacesOld(actor, () => fake.Cells = 0, () => Actor(new Fake { Cells = 0, Loaded = true }), report);
        Assert.True(report.Passed); Assert.Equal(1, fake.Saves);
    }
    [Fact] public void OldNetworkReturningFailsPersistence()
    {
        var fake = new Fake(); using var actor = Actor(fake); var report = new ScenarioReport("empty");
        Assert.Throws<InvalidOperationException>(() => RoadsScenarios.EmptyNetworkReplacesOld(actor, () => fake.Cells = 0, () => Actor(new Fake { Loaded = true }), report));
        Assert.False(report.Passed);
    }
    [Fact] public void UnconfirmedSaveCannotTriggerRestart()
    {
        var fake = new Fake { ConfirmSave = false }; using var actor = Actor(fake); bool restarted = false;
        Assert.Throws<InvalidOperationException>(() => RoadsScenarios.EmptyNetworkReplacesOld(actor, () => fake.Cells = 0, () => { restarted = true; return Actor(new Fake()); }, new("empty")));
        Assert.False(restarted);
    }
    [Fact] public async Task AlreadyDrainedAppendCannotPassRaceTest()
    {
        var fake = new Fake(); using var actor = Actor(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => {}, () => Actor(new Fake()), [new(0, 0, [Piece()])], new("bridge")));
        Assert.DoesNotContain("road_bridges respawn", fake.Commands);
    }
    private static GameActor Actor(Fake transport)
    { var actor = new GameActor("server", transport); actor.VerifyEnvironment("cli_expect world=fixture plugin=hash"); return actor; }
    private sealed class Fake : IGameTransport
    {
        public int Cells = 10, Saves; public bool Loaded, ConfirmSave = true; public List<string> Commands = [];
        public CommandResult Execute(string command, TimeSpan timeout)
        {
            Commands.Add(command); string line;
            if (command.StartsWith("cli_expect ")) line = "OK: EXPECT";
            else if (command == "cli_extensions") line = "EXTENSIONS {\"apiVersion\":1,\"extensions\":[{\"id\":\"roads.testing\",\"instance\":\"a\",\"closing\":false,\"commands\":[{\"name\":\"network\",\"resultVersion\":1,\"readOnly\":true}]}]}";
            else if (command == "cli_save") { Saves++; line = ConfirmSave ? "OK: SAVE saveNumber=2" : "Saving.."; }
            else line = "EXTENSION_RESULT " + System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, ok = true, extension = "roads.testing", instance = "a", data = new { source = "roads-memory", complete = true, cells = Cells, points = Cells*2, crossings = Cells, pendingZones = 0, loadedFromSave = Loaded } });
            return new() { Ok = true, Output = [line] };
        }
        public void Dispose() { }
    }
}
