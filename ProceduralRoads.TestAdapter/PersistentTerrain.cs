using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using valheimCLI.Extensions;
using Object = UnityEngine.Object;

namespace ProceduralRoads.TestAdapter;

// A fixture preparer, not an assertion. Expected heights belong in the runner.
// Native pre-write heightmap vertices are captured as inputs; saved stock
// TerrainComp ZDOs carry only the real Roads writer's deltas to a vanilla client.
internal static class PersistentTerrain
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static bool attempted;
    private static T Field<T>(object value, string name) => (T)(value.GetType().GetField(name, Flags)
        ?? throw new MissingFieldException(value.GetType().FullName, name)).GetValue(value);
    public static IEnumerator Run(ExtensionContext context)
    {
        if (attempted || context.Arguments.Count != 0 || Environment.GetEnvironmentVariable("ROADS_TEST_PERSISTENT_TERRAIN") != "1" ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROADS_TEST_SESSION_TOKEN")))
        { context.Fail("fixture_disabled", "Requires an owned opt-in session; once per process, no arguments."); yield break; }
        if (!ZNet.instance.IsDedicated() || ZNet.instance.GetPeers().Count != 0 || !RoadNetworkGenerator.RoadsAvailable || RoadSpatialGrid.GridCellsWithRoads != 0)
        { context.Fail("fixture_not_isolated", "Requires a dedicated server, no peers, and an empty completed road network."); yield break; }
        attempted = true;
        var zones = Pick();
        var prefab = Field<GameObject>(ZoneSystem.instance, "m_zonePrefab");
        var template = prefab.GetComponentInChildren<Heightmap>();
        foreach (var zone in zones)
        {
            float deadline = Time.realtimeSinceStartup + 30;
            while (!HeightmapBuilder.instance.IsTerrainReady(ZoneSystem.GetZonePos(zone), Field<int>(template, "m_width"), template.m_scale, template.IsDistantLod, WorldGenerator.instance))
            {
                if (context.Cancelled) throw new OperationCanceledException();
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Native fixture terrain was not ready.");
                yield return null;
            }
        }
        if (context.Cancelled) throw new OperationCanceledException();
        context.Succeed(Write(prefab, zones));
    }
    private static Vector2s[] Pick()
    {
        var generated = typeof(ZoneSystem).GetMethod("IsZoneGenerated", Flags, null, new[] { typeof(Vector2s) }, null)
            ?? throw new MissingMethodException("ZoneSystem.IsZoneGenerated");
        var locations = Field<Dictionary<Vector2s, ZoneSystem.LocationInstance>>(ZoneSystem.instance, "m_locationInstances");
        // Bounded deterministic scan. Avoid location shaping, water and steep
        // terrain; this test measures replication, not routing or site approval.
        for (int z = 4; z <= 36; z += 2)
        for (int x = 4; x <= 36; x += 2)
        {
            var first = new Vector2s(x,z); var second = new Vector2s(x+1,z);
            var centre = ZoneSystem.GetZonePos(first) + new Vector3(32,0,0);
            bool near = false;
            foreach (var entry in locations.Values)
                if (Vector2.Distance(new Vector2(centre.x,centre.z), new Vector2(entry.m_position.x,entry.m_position.z)) < 256 + entry.m_location.m_exteriorRadius) { near = true; break; }
            if (near || WorldGenerator.instance.GetBiome(centre.x,centre.z) != Heightmap.Biome.Meadows) continue;
            float min=10000,max=-10000;
            foreach(int dx in new[]{-16,0,16}) foreach(int dz in new[]{-8,0,8})
            { float h=WorldGenerator.instance.GetHeight(centre.x+dx,centre.z+dz); min=Math.Min(min,h); max=Math.Max(max,h); }
            if(min < 35 || max > 80 || max-min > 3) continue;
            if((bool)generated.Invoke(ZoneSystem.instance,new object[]{first}) || (bool)generated.Invoke(ZoneSystem.instance,new object[]{second})) continue;
            if(Heightmap.FindHeightmap(centre)!=null || RoadTerrainModifier.HasSavedTerrainCompiler(first) || RoadTerrainModifier.HasSavedTerrainCompiler(second)) continue;
            return new[]{first,second};
        }
        throw new InvalidOperationException("No dry, untouched two-zone fixture away from POIs in the bounded search.");
    }
    private static Dictionary<string,object?> Write(GameObject prefab, Vector2s[] zones)
    {
        var roots = new List<GameObject>(); var compObjects=new List<GameObject>();
        var maps = new List<Heightmap>(); var compilers = new List<TerrainComp>();
        var rows = new List<Dictionary<string,object?>>();
        var origin=ZoneSystem.GetZonePos(zones[0]);
        using(var tuning=new TerrainCalibration.FixtureTuning())
        try
        {
            foreach(var zone in zones)
            {
                var root=Object.Instantiate(prefab,ZoneSystem.GetZonePos(zone),Quaternion.identity); roots.Add(root);
                var hm=root.GetComponentInChildren<Heightmap>(); maps.Add(hm); hm.Regenerate();
                if(Field<int>(hm,"m_width")!=64 || hm.m_scale!=1) throw new InvalidOperationException("Expected 65x65 native grid.");
                GameObject obj;
                ZNetView.StartGhostInit();
                try{ obj=Object.Instantiate(Field<GameObject>(hm,"m_terrainCompilerPrefab"),hm.transform.position,Quaternion.identity); }
                finally{ZNetView.FinishGhostInit();}
                compObjects.Add(obj); var compiler=obj.GetComponent<TerrainComp>();compilers.Add(compiler);
                if(obj.GetComponent<ZNetView>().GetZDO()==null || Field<Heightmap>(compiler,"m_hmap")!=hm) throw new InvalidOperationException("No saved native compiler attached.");
                hm.Regenerate();
            }
            float target=maps[0].GetHeight(64,32)+origin.y+1.5f;
            for(int i=0;i<2;i++)
            {
                var hm=maps[i]; var samples=new List<(int x,int z,float before)>();
                foreach(int dx in i==0 ? new[]{24,32}:new[]{32,40}) foreach(int dz in new[]{0,2,3,4,6})
                    samples.Add((dx,dz,hm.GetHeight(dx-i*64+32,dz+32)+hm.transform.position.y));
                var points=new List<RoadSpatialGrid.RoadPoint>();
                for(int dx=16;dx<=48;dx++) points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(origin.x+dx,origin.z),4,target));
                RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zones[i],points,hm,compilers[i]);hm.Regenerate();
                var collider=hm.GetComponent<MeshCollider>();
                foreach(var sample in samples)
                {
                    float x=origin.x+sample.x,z=origin.z+sample.z;
                    if(!collider.Raycast(new Ray(new Vector3(x,200,z),Vector3.down),out var hit,300)) throw new InvalidOperationException("No native terrain collider.");
                    rows.Add(new Dictionary<string,object?> { ["zoneOffset"]=i,["x"]=x,["z"]=z,["distance"]=sample.z,["before"]=sample.before,
                        ["height"]=hm.GetHeight(sample.x-i*64+32,sample.z+32)+hm.transform.position.y,["colliderHeight"]=hit.point.y });
                }
            }
            return new Dictionary<string,object?>{["source"]="natural-heightmap-road-fixture",["complete"]=true,["target"]=target,["width"]=4,["margin"]=2,
                ["x"]=origin.x+32,["z"]=origin.z,["samples"]=rows.ToArray(),["compilerIds"]=compObjects.ConvertAll(o=>o.GetComponent<ZNetView>().GetZDO().m_uid.ToString()).ToArray()};
        }
        finally
        {
            // Keep the persistent ZDOs, release Unity objects. This differs from
            // terrain-calibrate, whose non-networked baseline cannot be saved.
            foreach(var obj in compObjects) Object.DestroyImmediate(obj);
            foreach(var root in roots) Object.DestroyImmediate(root);
        }
    }
}
