using ProceduralRoads.SystemTests;
using Valheim.Testing.Game;

// The toolkit's pinned dedicated-server runner (PinnedServerRun) with the Roads plan, modes and scenarios. The runner
// owns the lifecycle: plan checks, fixture copies, provenance, the owned session and its startup events, teardown,
// the report and the result banner.
return await PinnedServerRun.MainAsync(args, new PinnedServerRunOptions<RunPlan>
{
    Name = "roads-system-test",
    ReadPlan = path => { var plan = RunPlan.Read(path); plan.Validate(); return plan; },
    SessionCapability = "roads.testing/session",
    SessionTokenVariable = RunPlan.SessionTokenVariable,
    PrepareModes = ["prepare-bridge", "prepare-terrain"],
    CheckMode = (mode, plan) =>
    {
        if (mode == "prepare-bridge" && plan.Scenario != "bridge-respawn") throw new ArgumentException("prepare-bridge requires a bridge plan.");
        if (mode == "prepare-terrain" && plan.Scenario != "terrain-persistence") throw new ArgumentException("prepare-terrain requires a terrain-persistence plan.");
        if (mode == "run" && plan.Scenario == "terrain-persistence" && plan.Client == null)
            throw new ArgumentException("A terrain-persistence run measures from a client: add the client section, or prepare the fixture with prepare-terrain.");
    },
    Provenance = (plan, provenance) =>
    {
        if (plan.Scenario == "terrain-persistence") provenance["paintProfile"] = PersistentTerrainScenario.ProfileName(PersistentTerrainScenario.WidthFor(plan.Environment));
    },
    Scenario = run => run.Mode switch
    {
        "prepare-terrain" => Sync(() => PersistentTerrainScenario.Prepare(run.Server, run.Report, run.Output, PersistentTerrainScenario.WidthFor(run.Plan.Environment))),
        "prepare-bridge" => RoadsScenarios.PrepareBridgeZones(run.Server, run.Plan.Expected, run.Report, run.Cancellation),
        _ => run.Plan.Scenario switch
        {
            "terrain-calibration" => Sync(() => TerrainCalibrationScenario.Run(run.Server, run.Report, run.Output)),
            "terrain-persistence" => Sync(() =>
            {
                using var client = TerrainClientScenario.Connect(run.Plan.Client!, run.Output);
                TerrainClientScenario.Run(run.Plan, run.Server, run.Session.Restart, client, run.Report, run.Output, run.Cancellation);
            }),
            "empty-save" => RoadsScenarios.EmptyNetworkReplacesOld(run.Server, () => run.Server.Execute("road_generate"), run.Session.Restart, run.Report, cancellation: run.Cancellation),
            _ => RoadsScenarios.BridgeAppendSurvivesRespawn(run.Server, () => run.Server.Execute(run.Plan.Append), run.Session.Restart, run.Plan.Expected, run.Report, run.Cancellation),
        },
    },
});

static Task Sync(Action scenario) { scenario(); return Task.CompletedTask; }
