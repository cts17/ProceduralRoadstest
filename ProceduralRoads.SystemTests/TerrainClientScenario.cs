using System.Text.Json;
using Valheim.Testing.Game;

namespace ProceduralRoads.SystemTests;

/// <summary>
/// The maintained end-to-end terrain and paint persistence check, run against a world that prepare-terrain wrote and
/// saved. A stock client (ValheimCLI only; Roads and MWL pinned absent) joins the owned server, is protected, arrives on
/// the fixture's dry support point, and measures heights and colliders, paint, and stationary support against the
/// prepared plans, whose expectations came from native pre-write inputs, never from these readings. Then a confirmed
/// save, the client leaves, the server restarts, the client rejoins and everything is measured again. Every step must
/// pass: a prepared fixture or a paint pass never stands in for grounding, and the restart round never reuses the
/// first round's readings.
/// </summary>
public static class TerrainClientScenario
{
    /// <summary>The client's actor, recording every command to <c>client-commands.jsonl</c> in the output.</summary>
    public static GameActor Connect(ClientPlan plan, string output, IGameTransport? transport = null) =>
        new("client", new RecordingTransport(transport ?? new CliTransport(plan.Host, plan.Port), Path.Combine(output, "client-commands.jsonl")));

    public static void Run(RunPlan runPlan, GameActor server, Func<GameActor> restartOwnedServer, GameActor client, ScenarioReport report, string output, CancellationToken cancellation = default)
    {
        var plan = runPlan.Client ?? throw new ArgumentException("A terrain-persistence run needs its client.");
        SurfacePlan? height = null; PaintPlan? paint = null;
        report.Step("prepared client plans, unchanged since pinned", () =>
        {
            height = SurfacePlan.Read(plan.HeightPlan.Verified());
            paint = PaintPlan.Read(plan.PaintPlan.Verified());
            if (height.Support is not { } support) throw new InvalidOperationException("The prepared height plan declares no support point; prepare the fixture again.");
            if (support.Height < PersistentTerrainScenario.SeaLevel + PersistentTerrainScenario.DryMargin)
                throw new InvalidOperationException($"The support point at {support.Height:F2} m is not genuinely dry.");
        });
        report.Step("client at its menu loads exactly the declared plugins, Roads and MWL absent", () => client.VerifyEnvironment(plan.MenuExpectations));
        // ValheimCLI refuses mutating extension commands, the session join among them, until devcommands is on, and a
        // fresh client starts with it off. The console command toggles; its reply says the resulting state.
        report.Step("client devcommands on", () => EnableDevcommands(client));
        var round = new RoundContext(runPlan, plan, client, height!, paint!, report, output, cancellation);
        round.Run(server, "first");
        report.Step("confirmed world save", () => server.SaveConfirmed());
        round.Leave("first");
        report.Step("restart only the owned server", () => server = restartOwnedServer());
        round.Run(server, "after-restart");
        round.Leave("after-restart");
    }

    /// <summary>Turns the client's devcommands on: toggles, reads the game's reply, and toggles once more if that turned it off.</summary>
    public static void EnableDevcommands(GameActor client)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string reply = string.Join(" ", client.Execute("devcommands").Output);
            if (System.Text.RegularExpressions.Regex.IsMatch(reply, @"Dev ?commands:\s*True", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return;
            if (!System.Text.RegularExpressions.Regex.IsMatch(reply, @"Dev ?commands:\s*False", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Unrecognised devcommands reply: " + reply);
        }
        throw new InvalidOperationException("Devcommands stayed off after two toggles.");
    }

    private sealed record RoundContext(RunPlan RunPlan, ClientPlan Plan, GameActor Client, SurfacePlan Height, PaintPlan Paint, ScenarioReport Report, string Output, CancellationToken Cancellation)
    {
        public void Run(GameActor server, string round)
        {
            var session = new SessionControl(Client); var support = Height.Support!;
            Report.Step($"{round}: join the owned server with the disposable character", () =>
            {
                session.Join(Plan.Join, Plan.Character, Plan.PasswordVariable); // Exactly once; a lost reply is an unknown outcome.
                Client.VerifyEnvironment(Plan.WorldExpectations(RunPlan.WorldUid));
                session.WaitForWorld(RunPlan.WorldUid, TimeSpan.FromSeconds(Plan.JoinSeconds), Cancellation);
            });
            Report.Step($"{round}: protect the player", () => PlayerPlacement.Protect(Client));
            Report.Step($"{round}: arrive on the dry support point", () =>
                Write(Output, $"{round}-arrival.json", PlayerPlacement.Arrive(server, Client, support, TimeSpan.FromSeconds(Plan.ArrivalSeconds), Cancellation)));
            Report.Step($"{round}: client heightmap and its own collider match the prepared profile", () =>
            {
                var readings = SurfaceProbe.Compare(Client, Height.ExpectedFrom, Height.Samples, Height.Tolerance);
                Write(Output, $"{round}-surfaces.json", readings);
                if (readings.Any(r => !r.Passed)) throw new InvalidOperationException($"{readings.Count(r => !r.Passed)} of {readings.Count} surface samples differ; see {round}-surfaces.json.");
            });
            Report.Step($"{round}: client paint matches the prepared core and verge", () =>
            {
                var readings = PaintProbe.Compare(Client, Paint.ExpectedFrom, Paint.Samples, Paint.Tolerance);
                Write(Output, $"{round}-paint.json", readings);
                if (readings.Any(r => !r.Passed)) throw new InvalidOperationException($"{readings.Count(r => !r.Passed)} of {readings.Count} paint samples differ; see {round}-paint.json.");
            });
            Report.Step($"{round}: the player stands on the dry road core", () =>
            {
                try { Write(Output, $"{round}-support.json", PlayerPlacement.RequireSupported(Client, support)); }
                catch (SupportException error) { Write(Output, $"{round}-support.json", error.Readings); throw; }
            });
        }
        public void Leave(string round) => Report.Step($"{round}: the client leaves to its menu", () =>
        {
            new SessionControl(Client).Leave();
            Client.VerifyEnvironment(Plan.MenuExpectations);
        });
    }

    private static void Write(string output, string name, object value) =>
        File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
