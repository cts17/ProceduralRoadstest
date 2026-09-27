using System.Diagnostics;
using System.Text.Json;
using ProceduralRoads.SystemTests;
using Valheim.Testing.Game;
using valheim_cli.Testing;
using Xunit;

public class SessionTests
{
    private static JsonElement Reply(string token, int pid, string path, bool complete = true, bool dedicated = true) =>
        JsonSerializer.SerializeToElement(new { schemaVersion = 1, ok = true, extension = "roads.testing", data = new { source = "owned-test-session", token, pid, saveRoot = path, complete, dedicated } });
    [Theory]
    [InlineData("token")][InlineData("pid")][InlineData("path")][InlineData("client")]
    public void WrongSessionNeverBecomesAnActor(string wrong)
    {
        var data = Reply(wrong == "token" ? "other" : "ours", wrong == "pid" ? 8 : 7,
            wrong == "path" ? Path.GetTempPath() : Path.Combine(Path.GetTempPath(), "copy"), dedicated: wrong != "client");
        Assert.Throws<InvalidOperationException>(() => OwnedServerSession.CheckIdentity(data, "ours", 7, Path.Combine(Path.GetTempPath(), "copy")));
    }
    [Fact] public void IncompleteOwnedSessionIsNotReady() => Assert.False(OwnedServerSession.CheckIdentity(Reply("a", 1, Path.GetTempPath(), false), "a", 1, Path.GetTempPath()));
    [Fact] public void StartupPinsThenRestartStopsOldBeforeLaunchingAndRepins()
    {
        var fake = new Host(); using var session = fake.Session();
        session.Start(); session.Restart();
        Assert.Equal(new[] { "launch1", "probe1", "pins1", "disconnect1", "stop1", "dispose1", "launch2", "probe2", "pins2" }, fake.Events);
        Assert.NotEqual(fake.Tokens[0], fake.Tokens[1]);
    }
    [Fact] public void FailedStopNeverLaunchesReplacement()
    {
        var fake = new Host { RefuseStop = true }; var session = fake.Session(); session.Start();
        Assert.Throws<TimeoutException>(() => session.Restart()); Assert.Single(fake.Tokens);
        fake.RefuseStop = false; session.Dispose();
    }
    [Fact] public void IncorrectEnvironmentPinsDoNotRetryOrReturnActor()
    {
        var fake = new Host { BadPins = true }; using var session = fake.Session();
        Assert.Throws<InvalidOperationException>(() => session.Start());
        Assert.Equal(1, fake.Events.Count(x => x.StartsWith("pins")));
        Assert.DoesNotContain(fake.Events, x => x.StartsWith("mutation"));
    }
    [Fact] public void WrongPidFailsImmediatelyWithoutPins()
    {
        var fake = new Host { BadPid = true }; using var session = fake.Session();
        Assert.Throws<InvalidOperationException>(() => session.Start()); Assert.DoesNotContain(fake.Events, x => x.StartsWith("pins"));
    }
    [Fact] public void ExitedProcessCannotBecomeReady()
    {
        var fake = new Host { ExitOnLaunch = true }; using var session = fake.Session();
        Assert.Throws<InvalidOperationException>(() => session.Start()); Assert.DoesNotContain(fake.Events, x => x.StartsWith("probe"));
    }
    [Fact] public void CancelledSessionDoesNotLaunch()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var fake = new Host();
        using var session = new OwnedServerSession(fake.Launch, fake.Connect, Path.GetTempPath(), "cli_expect worlduid=1", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), cancellation: cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => session.Start()); Assert.Empty(fake.Tokens);
    }
    [Fact] public void ConnectionLossHasBoundedStartupAndOwnedCleanup()
    {
        var fake = new Host { NoConnection = true }; var session = fake.Session(TimeSpan.FromMilliseconds(20));
        Assert.Throws<TimeoutException>(() => session.Start()); session.Dispose();
        Assert.Contains("stop1", fake.Events);
    }
    [Fact] public void NoWorldReadinessCannotPassOnAListeningSocket()
    {
        var fake = new Host { Ready = false }; using var session = fake.Session(TimeSpan.FromMilliseconds(20));
        Assert.Throws<TimeoutException>(() => session.Start()); Assert.DoesNotContain(fake.Events, x => x.StartsWith("pins"));
    }
    [Fact] public void RecordingIncludesFailedRepliesAndClosesTransport()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            var fake = new Host { BadPins = true }; fake.Launch("t");
            using (var recording = new RecordingTransport(fake.Connect(), file)) recording.Execute("cli_expect worlduid=1", TimeSpan.FromSeconds(1));
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Assert.False(doc.RootElement.GetProperty("reply").GetProperty("Ok").GetBoolean()); Assert.Contains("disconnect1", fake.Events);
        }
        finally { File.Delete(file); }
    }
    [Fact] public void DirectProcessStopDoesNotStopAnotherProcess()
    {
        if (OperatingSystem.IsWindows()) return; // Fast Unix process test; Windows is the station gate.
        using var unrelated = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        string dir = Path.Combine(Path.GetTempPath(), "roads-process-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            using var owned = new DirectServerProcess(new ProcessStartInfo("/bin/sleep", "30"), Path.Combine(dir, "owned"));
            owned.Stop(TimeSpan.FromSeconds(3)); Assert.True(owned.HasExited); Assert.False(unrelated.HasExited);
        }
        finally { unrelated.Kill(); unrelated.WaitForExit(); Directory.Delete(dir, true); }
    }
    private sealed class Host
    {
        public bool RefuseStop, BadPins, BadPid, ExitOnLaunch, NoConnection, Ready = true;
        public List<string> Events = [], Tokens = [];
        private FakeProcess? _current;
        public IServerProcess Launch(string token)
        {
            Tokens.Add(token); int id = Tokens.Count; Events.Add("launch" + id);
            return _current = new FakeProcess(this, id) { Exited = ExitOnLaunch };
        }
        public IGameTransport Connect()
        { if (NoConnection) throw new IOException("not yet"); return new Transport(this, _current!.Id, Tokens[^1]); }
        public OwnedServerSession Session(TimeSpan? timeout = null) => new(Launch, Connect, Path.GetTempPath(), "cli_expect worlduid=1", timeout ?? TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1));
        private sealed class FakeProcess(Host host, int id) : IServerProcess
        {
            public bool Exited;
            public int Id => id; public bool HasExited => Exited;
            public void Stop(TimeSpan timeout) { host.Events.Add("stop" + id); if (host.RefuseStop) throw new TimeoutException(); Exited = true; }
            public void Dispose() => host.Events.Add("dispose" + id);
        }
        private sealed class Transport(Host host, int pid, string token) : IGameTransport
        {
            public CommandResult Execute(string command, TimeSpan timeout)
            {
                if (command.StartsWith("cli_expect"))
                { host.Events.Add("pins" + pid); return new() { Ok = !host.BadPins, Output = [host.BadPins ? "ERROR: pins" : "OK: EXPECT"] }; }
                host.Events.Add("probe" + pid);
                return new() { Ok = true, Output = ["EXTENSION_RESULT " + Reply(token, host.BadPid ? 99 : pid, Path.GetTempPath(), host.Ready).GetRawText()] };
            }
            public void Dispose() => host.Events.Add("disconnect" + pid);
        }
    }
}
