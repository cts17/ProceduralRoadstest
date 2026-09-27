using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ProceduralRoads.TestFixtures;
using UnityEngine;
using valheimCLI.Extensions;
using Object = UnityEngine.Object;

namespace ProceduralRoads.TestAdapter;

// Deliberately opt-in fixture construction, never a production road command.
// It uses a real non-player TerrainModifier, compiler, heightmap and collider.
// No input array or compiler result is replaced by the expected answer.
internal static class TerrainCalibration
{
    private static bool busy;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static T Field<T>(object instance, string name) => (T)(instance.GetType().GetField(name, Fields)
        ?? throw new MissingFieldException(instance.GetType().FullName, name)).GetValue(instance);

    public static IEnumerator Run(ExtensionContext context)
    {
        if (context.Arguments.Count != 0 || busy || Environment.GetEnvironmentVariable("ROADS_TEST_TERRAIN_CALIBRATION") != "1" ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROADS_TEST_SESSION_TOKEN")))
        { context.Fail("fixture_disabled", "Requires an owned, explicitly enabled terrain-calibration session; no arguments."); yield break; }
        if (!ZNet.instance.IsDedicated() || ZNet.instance.GetPeers().Count != 0 || !RoadNetworkGenerator.RoadsAvailable || RoadSpatialGrid.GridCellsWithRoads != 0)
        { context.Fail("fixture_not_isolated", "Requires a dedicated server with no peers and a completed empty road network."); yield break; }
        busy = true;
        try
        {
            var prefab = Field<GameObject>(ZoneSystem.instance, "m_zonePrefab");
            var template = prefab.GetComponentInChildren<Heightmap>();
            if (template == null) throw new InvalidOperationException("No terrain prefab.");
            for (int offset = 0; offset < 2; offset++)
            {
                var zone = new Vector2s(FlatRoadFixture.ZoneX + offset, FlatRoadFixture.ZoneZ);
                var position = ZoneSystem.GetZonePos(zone);
                if (ZoneSystem.instance.IsZoneGenerated(zone) || Heightmap.FindHeightmap(position) != null || TerrainComp.FindTerrainCompiler(position) != null)
                    throw new InvalidOperationException("Calibration zone must be ungenerated and unloaded.");
                float deadline = Time.realtimeSinceStartup + 30;
                while (!HeightmapBuilder.instance.IsTerrainReady(position, Field<int>(template, "m_width"), template.m_scale, template.IsDistantLod, WorldGenerator.instance))
                {
                    if (context.Cancelled) throw new OperationCanceledException();
                    if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Fixture terrain builder did not become ready.");
                    yield return null;
                }
            }
            if (context.Cancelled) throw new OperationCanceledException();
            // No yields from creation through cleanup. Temporary tuning cannot
            // leak into unrelated frame work while this synchronous fixture runs.
            context.Succeed(Measure(prefab));
        }
        finally { busy = false; }
    }

    private static Dictionary<string, object?> Measure(GameObject prefab)
    {
        var rows = new List<Dictionary<string, object?>>();
        GameObject? platform = null;
        using (var tuning = new FixtureTuning())
        try
        {
            platform = new GameObject("Roads testing declared platform");
            platform.SetActive(false);
            platform.transform.position = new Vector3(FlatRoadFixture.Origin + 32, FlatRoadFixture.Platform, FlatRoadFixture.Origin);
            var modifier = platform.AddComponent<TerrainModifier>();
            modifier.m_playerModifiction = false; modifier.m_useTerrainCompiler = false;
            modifier.m_level = true; modifier.m_square = true; modifier.m_levelRadius = 128;
            modifier.m_levelOffset = 0; modifier.m_smooth = false; modifier.m_paintCleared = false;
            modifier.m_sortOrder = int.MaxValue;
            platform.SetActive(true);
            for (int offset = 0; offset < 2; offset++) MeasureZone(prefab, offset, rows);
        }
        finally { if (platform != null) Object.DestroyImmediate(platform); }
        return new Dictionary<string, object?>
        {
            ["source"] = "declared-platform-real-compiler", ["fixture"] = FlatRoadFixture.Id,
            ["complete"] = true, ["temporaryObjectsReleased"] = true,
            ["scope"] = "server terrain writer, compiler, heightmap and local collider; not client replication or natural world generation",
            ["samples"] = rows.ToArray()
        };
    }

    private static void MeasureZone(GameObject prefab, int offset, List<Dictionary<string, object?>> rows)
    {
        GameObject? root = null, compilerObject = null; ZDO? made = null;
        try
        {
            var zone = new Vector2s(FlatRoadFixture.ZoneX + offset, FlatRoadFixture.ZoneZ);
            root = Object.Instantiate(prefab, ZoneSystem.GetZonePos(zone), Quaternion.identity);
            var hm = root.GetComponentInChildren<Heightmap>();
            hm.Regenerate();
            if (Field<int>(hm, "m_width") != 64 || hm.m_scale != 1f) throw new InvalidOperationException("Fixture requires a 65x65 vertex grid at 1 metre spacing.");
            ZNetView.StartGhostInit();
            try { compilerObject = Object.Instantiate(Field<GameObject>(hm, "m_terrainCompilerPrefab"), hm.transform.position, Quaternion.identity); }
            finally { ZNetView.FinishGhostInit(); }
            var compiler = compilerObject.GetComponent<TerrainComp>();
            made = compilerObject.GetComponent<ZNetView>().GetZDO();
            if (made == null || Field<Heightmap>(compiler, "m_hmap") != hm) throw new InvalidOperationException("Compiler did not attach to owned terrain.");
            hm.Regenerate(); Capture(hm, offset, "baseline", rows);
            for (int stage = 0; stage < FlatRoadFixture.Stages.Length; stage++)
            {
                var points = new List<RoadSpatialGrid.RoadPoint>();
                for (int x = 16; x <= 48; x++) points.Add(new RoadSpatialGrid.RoadPoint(
                    new Vector2(FlatRoadFixture.Origin + x, FlatRoadFixture.Origin), FlatRoadFixture.Width, FlatRoadFixture.Targets[stage]));
                RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, compiler);
                hm.Regenerate();
                Capture(hm, offset, FlatRoadFixture.Stages[stage], rows);
            }
        }
        finally
        {
            try { if (compilerObject != null) Object.DestroyImmediate(compilerObject); }
            finally
            {
                try
                {
                    if (made != null)
                    {
                        var destroy = typeof(ZDOMan).GetMethod("DestroyZDO", Fields, null, new[] { typeof(ZDO) }, null)
                            ?? throw new MissingMethodException("ZDOMan.DestroyZDO(ZDO)");
                        destroy.Invoke(ZDOMan.instance, new object[] { made });
                    }
                }
                finally { if (root != null) Object.DestroyImmediate(root); }
            }
        }
    }

    private static void Capture(Heightmap hm, int offset, string stage, List<Dictionary<string, object?>> rows)
    {
        var collider = hm.GetComponent<MeshCollider>();
        if (collider == null) throw new InvalidOperationException("Heightmap has no collider.");
        foreach (var sample in FlatRoadFixture.Samples(offset))
        {
            float x = FlatRoadFixture.Origin + sample.x, z = FlatRoadFixture.Origin + sample.z;
            float height = hm.GetHeight(sample.x - offset * 64 + 32, sample.z + 32) + hm.transform.position.y;
            if (!collider.Raycast(new Ray(new Vector3(x, 128, z), Vector3.down), out var hit, 128))
                throw new InvalidOperationException("Owned terrain collider did not answer a fixture sample.");
            rows.Add(new Dictionary<string, object?>
            {
                ["stage"] = stage, ["zoneOffset"] = offset, ["x"] = sample.x, ["z"] = sample.z,
                ["height"] = height, ["colliderHeight"] = hit.point.y
            });
        }
    }

    private sealed class FixtureTuning : IDisposable
    {
        private readonly List<(FieldInfo field, object value)> restore = new List<(FieldInfo, object)>();
        public FixtureTuning()
        {
            try
            {
                Zero("ProceduralRoads.RoadEarthworkNoise", "Amplitude");
                Zero("ProceduralRoads.RoadEarthworkNoise", "FillSpread");
                Zero("ProceduralRoads.RoadTerrainModifier", "BatterPerMetre");
            }
            catch { Dispose(); throw; }
        }
        private void Zero(string type, string name)
        {
            var field = typeof(RoadTerrainModifier).Assembly.GetType(type)?.GetField(name, Fields)
                ?? throw new MissingFieldException(type, name);
            restore.Add((field, field.GetValue(null))); field.SetValue(null, 0f);
        }
        public void Dispose() { foreach (var entry in restore) entry.field.SetValue(null, entry.value); restore.Clear(); }
    }
}
