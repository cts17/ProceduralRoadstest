using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;

namespace ProceduralRoads.SystemTests;

// Callers supply an OWNED disposable session. Restart must wait for completion,
// reconnect and verify the same fixture and plugin pins before returning.
public static class RoadsScenarios
{
    private static Observation Network(GameActor server, Capability capability)
    {
        var observation = server.Observe(capability); observation.RequireComplete("roads-memory"); return observation;
    }
    private static void Empty(Observation network)
    {
        foreach (string field in new[] { "cells", "points", "crossings", "pendingZones" })
            if (network.Data.GetProperty(field).GetInt32() != 0) throw new InvalidOperationException("Network is not empty: " + field);
    }
    private static void Save(GameActor server)
    {
        var saved = server.Execute("cli_save");
        if (!saved.Output.Any(line => line.StartsWith("OK: SAVE ", StringComparison.Ordinal)))
            throw new InvalidOperationException("No confirmed world save; refusing restart.");
    }
    public static async Task EmptyNetworkReplacesOld(GameActor server, Action regenerateEmptyOnce, Func<GameActor> restartOwnedSession, ScenarioReport report, TimeSpan? generationTimeout = null, CancellationToken cancellation = default)
    {
        var capability = server.RequireCapability("roads.testing/network");
        report.Step("nonempty starting fixture", () =>
        {
            if (Network(server, capability).Data.GetProperty("cells").GetInt32() <= 0) throw new InvalidOperationException("Fixture must contain a previous network.");
        });
        cancellation.ThrowIfCancellationRequested();
        report.Step("generate empty network once", regenerateEmptyOnce);
        try
        {
            await Check.Eventually(() => server.Observe(capability), n => n.Source == "roads-memory" && n.Complete,
                generationTimeout ?? TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(500), cancellation);
            report.Step("empty before save", () => Empty(Network(server, capability)));
        }
        catch (Exception error) { report.Step("generation completed", () => throw new InvalidOperationException("Empty generation did not complete correctly.", error)); throw; }
        cancellation.ThrowIfCancellationRequested();
        report.Step("confirmed save", () => Save(server));
        GameActor? reloaded = null;
        try
        {
            report.Step("restart owned fixture", () => reloaded = restartOwnedSession());
            report.Step("empty network loaded from save", () =>
            {
                var after = Network(reloaded!, reloaded!.RequireCapability("roads.testing/network")); Empty(after);
                if (!after.Data.GetProperty("loadedFromSave").GetBoolean()) throw new InvalidOperationException("Observed regeneration, not persistence.");
            });
        }
        finally { if (reloaded != null) report.Step("disconnect reloaded actor", reloaded.Dispose); }
    }

    public static async Task BridgeAppendSurvivesRespawn(GameActor server, Action appendOnce, Func<GameActor> restartOwnedSession,
        IReadOnlyList<ZoneExpectation> expected, ScenarioReport report, CancellationToken cancellation = default)
    {
        if (expected.Count == 0 || expected.All(x => x.Pieces.Count == 0)) throw new ArgumentException("Freeze expected bridge identities independently before the run.");
        if (expected.Select(x => (x.X, x.Z)).Distinct().Count() != expected.Count) throw new ArgumentException("Duplicate zone expectations.");
        var capability = server.RequireCapability("roads.testing/network");
        report.Step("no previous pending append", () =>
        {
            if (Network(server, capability).Data.GetProperty("pendingZones").GetInt32() != 0)
                throw new InvalidOperationException("Fixture already has pending work; cannot attribute this race to the append.");
        });
        cancellation.ThrowIfCancellationRequested();
        report.Step("append once", appendOnce);
        report.Step("append is still pending", () =>
        {
            if (Network(server, capability).Data.GetProperty("pendingZones").GetInt32() == 0)
                throw new InvalidOperationException("Fixture did not exercise the race: append drained before respawn.");
        });
        report.Step("respawn while pending", () => server.Execute("road_bridges respawn"));
        // Observations only. No repeated append/respawn/save while polling.
        try
        {
            await Check.Eventually(() => Network(server, capability), n => n.Data.GetProperty("pendingZones").GetInt32() == 0,
                TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1), cancellation);
            report.Step("append queue drained", () => {});
        }
        catch (Exception error) { report.Step("append queue drained", () => throw new InvalidOperationException("Queue did not drain.", error)); throw; }
        report.Step("independent piece census", () => CompareZones(server, expected));
        cancellation.ThrowIfCancellationRequested();
        report.Step("confirmed save", () => Save(server));
        GameActor? reloaded = null;
        try
        {
            report.Step("restart owned fixture", () => reloaded = restartOwnedSession());
            report.Step("pieces persist after restart", () =>
            {
                var network = Network(reloaded!, reloaded!.RequireCapability("roads.testing/network"));
                if (!network.Data.GetProperty("loadedFromSave").GetBoolean() || network.Data.GetProperty("pendingZones").GetInt32() != 0)
                    throw new InvalidOperationException("Restart regenerated roads or still has pending bridge work.");
                CompareZones(reloaded!, expected);
            });
        }
        finally { if (reloaded != null) report.Step("disconnect reloaded actor", reloaded.Dispose); }
    }
    public static void CompareZones(GameActor server, IReadOnlyList<ZoneExpectation> expected)
    {
        var capability = server.RequireCapability("roads.testing/bridge-zone");
        foreach (var zone in expected)
        {
            var observed = server.Observe(capability, zone.X.ToString(CultureInfo.InvariantCulture), zone.Z.ToString(CultureInfo.InvariantCulture));
            observed.RequireComplete("zdo-store");
            if (observed.Data.GetProperty("zoneX").GetInt32() != zone.X || observed.Data.GetProperty("zoneZ").GetInt32() != zone.Z) throw new InvalidOperationException("Census returned a different zone.");
            var pieces = observed.Data.GetProperty("pieces").EnumerateArray().Select(Piece.FromJson).ToArray();
            PieceComparison.Match(pieces, zone.Pieces, positionTolerance: .05, angleToleranceDegrees: 1);
        }
    }
}
public sealed record ZoneExpectation(int X, int Z, IReadOnlyList<Piece> Pieces);
public sealed record Piece(int PrefabHash, double[] Position, double[] Rotation)
{
    public static Piece FromJson(JsonElement value) => new(value.GetProperty("prefabHash").GetInt32(),
        value.GetProperty("position").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
        value.GetProperty("rotation").EnumerateArray().Select(x => x.GetDouble()).ToArray());
}
// Full transform matching with one-to-one assignment: stacked identical prefabs
// cannot swap solely because their horizontal positions coincide.
public static class PieceComparison
{
    public static void Match(IReadOnlyList<Piece> actual, IReadOnlyList<Piece> expected, double positionTolerance, double angleToleranceDegrees)
    {
        if (positionTolerance < 0 || !double.IsFinite(positionTolerance) || angleToleranceDegrees < 0 || !double.IsFinite(angleToleranceDegrees)) throw new ArgumentException("Invalid tolerance.");
        if (actual.Count != expected.Count) throw new InvalidOperationException($"Expected {expected.Count} pieces; observed {actual.Count}.");
        foreach (var piece in actual.Concat(expected))
            if (piece.Position.Length != 3 || piece.Rotation.Length != 4 || piece.Position.Concat(piece.Rotation).Any(x => !double.IsFinite(x)) || piece.Rotation.Sum(x => x * x) < 1e-12)
                throw new InvalidOperationException("Invalid piece transform.");
        var assigned = Enumerable.Repeat(-1, actual.Count).ToArray();
        bool Matches(Piece a, Piece b)
        {
            if (a.PrefabHash != b.PrefabHash || Math.Sqrt(a.Position.Zip(b.Position, (x, y) => (x-y)*(x-y)).Sum()) > positionTolerance) return false;
            double dot = Math.Abs(a.Rotation.Zip(b.Rotation, (x, y) => x*y).Sum()) / Math.Sqrt(a.Rotation.Sum(x => x*x) * b.Rotation.Sum(x => x*x));
            return 2 * Math.Acos(Math.Min(1, dot)) * 180 / Math.PI <= angleToleranceDegrees;
        }
        bool Assign(int requirement, bool[] visited)
        {
            for (int i = 0; i < actual.Count; i++)
            {
                if (visited[i] || !Matches(actual[i], expected[requirement])) continue;
                visited[i] = true;
                if (assigned[i] < 0 || Assign(assigned[i], visited)) { assigned[i] = requirement; return true; }
            }
            return false;
        }
        for (int requirement = 0; requirement < expected.Count; requirement++)
            if (!Assign(requirement, new bool[actual.Count])) throw new InvalidOperationException("Bridge piece identity or transform mismatch.");
    }
}
