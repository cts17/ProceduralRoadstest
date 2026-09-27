using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ProceduralRoads.SystemTests;
using Valheim.Testing.Game;

if (args.Length != 3 || (args[0] != "validate" && args[0] != "run" && args[0] != "prepare-bridge" && args[0] != "prepare-terrain"))
{
    Console.Error.WriteLine("Usage: ProceduralRoads.SystemTests validate|run|prepare-bridge|prepare-terrain <plan.json> <new-output-directory>");
    return 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var report = new ScenarioReport("roads-system-test");
OwnedServerSession? session = null;
string output = Path.GetFullPath(args[2]);
bool ownOutput = false;
try
{
    if (Path.Exists(output)) throw new IOException("Use a new output directory; existing evidence is never overwritten.");
    var plan = RunPlan.Read(args[1]); plan.Validate(); plan.CheckOutput(output);
    if (args[0] != "validate" && !OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("This launch pilot supports the Windows dedicated server only. Use validate on Mac; it never launches a game.");
    if (args[0] == "prepare-bridge" && plan.Scenario != "bridge-respawn") throw new ArgumentException("prepare-bridge requires a bridge plan.");
    if ((args[0] == "prepare-terrain") != (plan.Scenario == "terrain-persistence") && args[0] != "validate") throw new ArgumentException("terrain-persistence is preparation only; use prepare-terrain.");
    report.Provenance["planSha256"] = WorldFixture.Hash(args[1]);
    report.Provenance["scenario"] = plan.Scenario;
    report.Provenance["runnerSha256"] = WorldFixture.Hash(typeof(RunPlan).Assembly.Location);
    report.Provenance["toolkitSha256"] = WorldFixture.Hash(typeof(GameActor).Assembly.Location);
    report.Provenance["mode"] = args[0];
    // No deleting these copies automatically: failed stop/partial saves must remain inspectable.
    // WorldFixture verifies every input and copied byte; runtime contains config/plugins too.
    if (Path.GetFullPath(plan.Runtime.Source) == output || Path.GetFullPath(plan.World.Source) == output)
        throw new IOException("Output cannot replace a source.");
    Directory.CreateDirectory(output); ownOutput = true;
    WorldFixture? runtime = null, world = null;
    report.Step("copy and verify pinned runtime", () => { runtime = WorldFixture.Copy(plan.Runtime.Source, output, plan.Runtime.Sha256); runtime.Preserve = true; });
    report.Step("copy and verify pinned world", () => { world = WorldFixture.Copy(plan.World.Source, output, plan.World.Sha256); world.Preserve = true; });
    report.Provenance["runtime"] = runtime!.DirectoryPath; report.Provenance["world"] = world!.DirectoryPath;
    File.WriteAllText(Path.Combine(output, "input-hashes.json"), JsonSerializer.Serialize(new { runtime = runtime.SourceHashes, world = world.SourceHashes }, new JsonSerializerOptions { WriteIndented = true }));
    string executable = Path.GetFullPath(Path.Combine(runtime.DirectoryPath, plan.Executable));
    if (!File.Exists(executable)) throw new FileNotFoundException("Executable is missing from copied runtime.");
    if (args[0] == "validate") report.Step("prepared only; no game launched", () => {});
    else
    {
        // Catch an occupied port without issuing even a read to an unrelated server.
        report.Step("CLI port is free", () =>
        {
            var reservation = new TcpListener(IPAddress.Loopback, plan.Port);
            try { reservation.Start(); } finally { reservation.Stop(); }
        });
        int boot = 0, connection = 0;
        session = new OwnedServerSession(token =>
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = runtime.DirectoryPath };
            foreach (var argument in plan.Arguments) start.ArgumentList.Add(plan.Expand(argument, runtime.DirectoryPath, world.DirectoryPath));
            foreach (var entry in plan.Environment) start.Environment[entry.Key] = plan.Expand(entry.Value, runtime.DirectoryPath, world.DirectoryPath);
            start.Environment["ROADS_TEST_SESSION_TOKEN"] = token;
            var process = new DirectServerProcess(start, Path.Combine(output, "boot-" + ++boot),
                Path.Combine(runtime.DirectoryPath, "BepInEx", "LogOutput.log"), Path.Combine(runtime.DirectoryPath, "toolkit-unity.log"));
            try { File.WriteAllText(Path.Combine(output, "boot-" + boot + ".process.json"), JsonSerializer.Serialize(new { pid = process.Id, startedUtc = DateTime.UtcNow, world = world.DirectoryPath })); }
            catch { process.Stop(TimeSpan.FromSeconds(15)); process.Dispose(); throw; }
            return process;
        }, () => new RecordingTransport(new CliTransport("127.0.0.1", plan.Port), Path.Combine(output, "connection-" + ++connection + ".jsonl")), world.DirectoryPath, plan.ExpectCommand, "roads.testing/session",
            TimeSpan.FromSeconds(plan.StartupSeconds), TimeSpan.FromSeconds(plan.CommandSeconds), cancellation: cancellation.Token);
        GameActor? server = null;
        report.Step("start and verify owned dedicated fixture", () => server = session.Start());
        report.Step("enable test devcommands", () =>
        {
            var capability = server!.RequireCapability("roads.testing/session");
            if (!server.Observe(capability).Data.GetProperty("devcommands").GetBoolean()) server.Execute("devcommands");
            if (!server.Observe(capability).Data.GetProperty("devcommands").GetBoolean()) throw new InvalidOperationException("Devcommands did not enable.");
        });
        if (args[0] == "prepare-terrain")
            PersistentTerrainScenario.Prepare(server!, report, output);
        else if (args[0] == "prepare-bridge")
            await RoadsScenarios.PrepareBridgeZones(server!, plan.Expected, report, cancellation.Token);
        else if (plan.Scenario == "terrain-calibration")
            TerrainCalibrationScenario.Run(server!, report, output);
        else if (plan.Scenario == "empty-save")
            await RoadsScenarios.EmptyNetworkReplacesOld(server!, () => server!.Execute("road_generate"), session.Restart, report, cancellation: cancellation.Token);
        else
            await RoadsScenarios.BridgeAppendSurvivesRespawn(server!, () => server!.Execute(plan.Append), session.Restart, plan.Expected, report, cancellation.Token);
    }
}
catch (Exception error)
{
    try { report.Step("runner failed", () => throw new InvalidOperationException(error.Message, error)); } catch { }
    Console.Error.WriteLine(error.Message);
}
finally
{
    if (session != null)
    {
        try { report.Step("stop only owned server", session.Dispose); } catch (Exception error) { Console.Error.WriteLine("Teardown: " + error.Message); }
        report.Provenance["ownedPids"] = string.Join(",", session.StartedProcesses);
    }
    if (ownOutput) report.Write(output);
}
Console.WriteLine(report.Passed ? "PASS (see report mode: validation alone is not a game test)" : "FAIL");
return report.Passed ? 0 : 1;
