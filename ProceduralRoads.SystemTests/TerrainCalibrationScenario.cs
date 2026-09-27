using System.Text.Json;
using ProceduralRoads.TestFixtures;
using Valheim.Testing.Game;

namespace ProceduralRoads.SystemTests;

public sealed record TerrainResidual(string Stage, int ZoneOffset, int X, int Z, float Expected, float Height, float ColliderHeight)
{
    public bool Passed => float.IsFinite(Height) && float.IsFinite(ColliderHeight) &&
        Math.Abs(Height - Expected) <= .02f && Math.Abs(ColliderHeight - Expected) <= .02f;
}

public static class TerrainCalibrationScenario
{
    public static IReadOnlyList<TerrainResidual> Compare(JsonElement data)
    {
        if (data.GetProperty("source").GetString() != "declared-platform-real-compiler" ||
            data.GetProperty("fixture").GetString() != FlatRoadFixture.Id || !data.GetProperty("complete").GetBoolean() ||
            !data.GetProperty("temporaryObjectsReleased").GetBoolean())
            throw new InvalidOperationException("Incomplete or wrong terrain fixture; cleanup must complete.");
        var expected = new Dictionary<(string stage, int zone, int x, int z), float>();
        foreach (string stage in new[] { "baseline" }.Concat(FlatRoadFixture.Stages))
            for (int zone = 0; zone < 2; zone++)
                foreach (var sample in FlatRoadFixture.Samples(zone)) expected.Add((stage, zone, sample.x, sample.z), FlatRoadFixture.Expected(stage, sample.z));
        var results = new List<TerrainResidual>();
        foreach (var row in data.GetProperty("samples").EnumerateArray())
        {
            var key = (row.GetProperty("stage").GetString()!, row.GetProperty("zoneOffset").GetInt32(), row.GetProperty("x").GetInt32(), row.GetProperty("z").GetInt32());
            if (!expected.Remove(key, out var height)) throw new InvalidOperationException("Duplicate or unexpected terrain sample identity.");
            results.Add(new(key.Item1, key.Item2, key.Item3, key.Item4, height, row.GetProperty("height").GetSingle(), row.GetProperty("colliderHeight").GetSingle()));
        }
        if (expected.Count != 0) throw new InvalidOperationException("Terrain samples missing; counts alone do not establish coverage.");
        return results;
    }

    public static void Run(GameActor server, ScenarioReport report, string output)
    {
        JsonElement response = default;
        report.Step("run owned declared terrain fixture once", () => response = server.Invoke(server.RequireCapability("roads.testing/terrain-calibrate")));
        File.WriteAllText(Path.Combine(output, "terrain-observations.json"), response.GetRawText());
        IReadOnlyList<TerrainResidual>? rows = null;
        report.Step("complete terrain identities and cleanup", () => rows = Compare(response));
        File.WriteAllText(Path.Combine(output, "terrain-residuals.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        report.Step("declared heights and server colliders agree within 2 cm", () =>
        {
            if (rows!.Any(x => !x.Passed)) throw new InvalidOperationException($"{rows!.Count(x => !x.Passed)} terrain samples differ; see residuals.");
        });
    }
}
