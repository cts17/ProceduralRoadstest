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
    // ROADS_TEST_PAINT_PROFILE: absent selects the paved width-4 fixture,
    // "dirt-fade" the width-8 one. Any other value is refused, never guessed.
    private const string PaintProfileVariable = "ROADS_TEST_PAINT_PROFILE";
    private static bool DirtFade;
    private static int Width => DirtFade ? 8 : 4;
    private static int[] PaintOffsets => DirtFade ? new[]{0,2,3,4} : new[]{0,1,2,4};
    // Width 8 adds its half-strength release vertex at 5 m; width 4 has it at 3 m.
    private static int[] HeightOffsets => DirtFade ? new[]{0,2,3,4,5,6} : new[]{0,2,3,4,6};
    private static T Field<T>(object value, string name) => (T)(value.GetType().GetField(name, Flags)
        ?? throw new MissingFieldException(value.GetType().FullName, name)).GetValue(value);
    public static IEnumerator Run(ExtensionContext context)
    {
        if (attempted || context.Arguments.Count != 0 || Environment.GetEnvironmentVariable("ROADS_TEST_PERSISTENT_TERRAIN") != "1" ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROADS_TEST_SESSION_TOKEN")))
        { context.Fail("fixture_disabled", "Requires an owned opt-in session; once per process, no arguments."); yield break; }
        var profile = Environment.GetEnvironmentVariable(PaintProfileVariable);
        if (profile != null && profile != "dirt-fade")
        { context.Fail("invalid_paint_profile", PaintProfileVariable + " must be absent (paved, width 4) or dirt-fade (width 8); got '" + profile + "'."); yield break; }
        DirtFade = profile == "dirt-fade";
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
    private const float SeaLevel = 30f, DryMargin = 1.5f, CoreLift = 1.5f, MaxLift = 7f;
    /// <summary>The road core's height: 1.5 m above the ground, and never below DryMargin above the sea, so a player standing on it is dry.</summary>
    internal static float CoreTarget(float ground) => Math.Max(ground + CoreLift, SeaLevel + DryMargin);
    private static Vector2s[] Pick()
    {
        var generated = typeof(ZoneSystem).GetMethod("IsZoneGenerated", Flags, null, new[] { typeof(Vector2s) }, null)
            ?? throw new MissingMethodException("ZoneSystem.IsZoneGenerated");
        var locations = Field<Dictionary<Vector2s, ZoneSystem.LocationInstance>>(ZoneSystem.instance, "m_locationInstances");
        // Bounded deterministic scan. Avoid location shaping, water and steep
        // terrain; this test measures replication, not routing or site approval.
        int ringCount=0, nearCount=0, terrainCount=0, wetCount=0, generatedCount=0, savedCount=0;
        for (int z = -48; z <= 48; z += DirtFade ? 1 : 2)
        for (int x = -48; x <= 48; x += DirtFade ? 1 : 2)
        {
            var first = new Vector2s(x,z); var second = new Vector2s(x+1,z);
            var centre = ZoneSystem.GetZonePos(first) + new Vector3(32,0,0);
            // Both widths avoid the 1000 +/- 100 m dirt->stone paint handover.
            // Samples lie within 10 m of the seam and the road points painting
            // them within a half-width more, so a 20 m margin keeps each colour
            // unambiguous. Dirt-fade additionally stays inside it, on dirt.
            float radiusSq = centre.x*centre.x+centre.z*centre.z;
            if ((radiusSq >= 875*875 && radiusSq <= 1125*1125) || (DirtFade && radiusSq > 875*875)) { ringCount++; continue; }
            bool near = false;
            foreach (var entry in locations.Values)
                if (Vector2.Distance(new Vector2(centre.x,centre.z), new Vector2(entry.m_position.x,entry.m_position.z)) < 64 + entry.m_location.m_exteriorRadius) { near = true; break; }
            if (near) { nearCount++; continue; }
            if(WorldGenerator.instance.GetBiome(centre.x,centre.z) != Heightmap.Biome.Meadows) {terrainCount++;continue;}
            float min=10000,max=-10000;
            foreach(int dx in new[]{-16,0,16}) foreach(int dz in new[]{-8,0,8})
            { float h=WorldGenerator.instance.GetHeight(centre.x+dx,centre.z+dz); min=Math.Min(min,h); max=Math.Max(max,h); }
            // Dry where the player stands: Write lifts the road core to at least DryMargin above the sea (see CoreTarget),
            // which the writer's +/-8 m clamp allows only if the ground is within MaxLift of that. The verges may be lower.
            // (Until 29 Sep nothing checked this; the sites found here were shoreline and a dirt fixture left the player swimming.)
            if(WorldGenerator.instance.GetHeight(centre.x,centre.z) + MaxLift < SeaLevel + DryMargin) {wetCount++;continue;}
            if(min < (DirtFade ? 22 : 25) || max > 90 || max-min > (DirtFade ? 8 : 5)) {terrainCount++;continue;}
            if((bool)generated.Invoke(ZoneSystem.instance,new object[]{first}) || (bool)generated.Invoke(ZoneSystem.instance,new object[]{second})) {generatedCount++;continue;}
            if(Heightmap.FindHeightmap(centre)!=null || RoadTerrainModifier.HasSavedTerrainCompiler(first) || RoadTerrainModifier.HasSavedTerrainCompiler(second)) {savedCount++;continue;}
            return new[]{first,second};
        }
        throw new InvalidOperationException($"No untouched dry fixture: ring={ringCount}, site={nearCount}, terrain={terrainCount}, wet={wetCount}, generated={generatedCount}, saved={savedCount}.");
    }
    private static Dictionary<string,object?> Write(GameObject prefab, Vector2s[] zones)
    {
        var roots = new List<GameObject>(); var compObjects=new List<GameObject>();
        var maps = new List<Heightmap>(); var compilers = new List<TerrainComp>();
        var rows = new List<Dictionary<string,object?>>();
        var paintRows = new List<Dictionary<string,object?>>();
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
                // Give untouched samples a saved, explicit paint input. A
                // never-generated zone can acquire vanilla paint on first
                // arrival; that is not a stable "before" for a road-write test.
                int i=maps.Count-1;
                var paint=Field<Color[]>(compiler,"m_paintMask");
                var modified=Field<bool[]>(compiler,"m_modifiedPaint");
                if(paint.Length!=65*65 || modified.Length!=paint.Length) throw new InvalidOperationException("Expected native 65x65 compiler mask.");
                foreach(int dx in i==0 ? new[]{24,30}:new[]{34,40}) foreach(int dz in PaintOffsets)
                {
                    int index=(dz+32)*65+dx-i*64+32;
                    paint[index]=new Color(.2f,.4f,.6f,.3f); modified[index]=true;
                }
                (typeof(TerrainComp).GetMethod("Save",Flags,null,new[]{typeof(bool)},null)
                    ?? throw new MissingMethodException("TerrainComp.Save")).Invoke(compiler,new object[]{false});
                hm.Regenerate();
            }
            float target=CoreTarget(maps[0].GetHeight(64,32)+origin.y);
            for(int i=0;i<2;i++)
            {
                var hm=maps[i]; var samples=new List<(int x,int z,float before)>();
                foreach(int dx in i==0 ? new[]{24,32}:new[]{32,40}) foreach(int dz in HeightOffsets)
                    samples.Add((dx,dz,hm.GetHeight(dx-i*64+32,dz+32)+hm.transform.position.y));
                var paintSamples = new List<(int x, int z, Color before)>();
                // Paint textures have 64 cells, unlike the 65 height vertices, so a
                // paint point ON the seam is ambiguous: an observer that picks the
                // western zone reads its clamped last cell (29 Sep client run: 4
                // false failures). Sample 2 m either side of the seam instead.
                foreach (int dx in i == 0 ? new[]{24,30} : new[]{34,40}) foreach (int dz in PaintOffsets)
                    paintSamples.Add((dx,dz,hm.GetPaintMask(dx-i*64+32,dz+32)));
                var points=new List<RoadSpatialGrid.RoadPoint>();
                for(int dx=16;dx<=48;dx+=2) points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(origin.x+dx,origin.z),Width,target));
                RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zones[i],points,hm,compilers[i]);hm.Regenerate();
                foreach (var sample in paintSamples)
                {
                    var paint=hm.GetPaintMask(sample.x-i*64+32,sample.z+32);
                    paintRows.Add(new Dictionary<string,object?> { ["zoneOffset"]=i,["x"]=origin.x+sample.x,["z"]=origin.z+sample.z,["distance"]=sample.z,
                        ["before"]=new[]{sample.before.r,sample.before.g,sample.before.b,sample.before.a},["paint"]=new[]{paint.r,paint.g,paint.b,paint.a} });
                }
                var collider=hm.GetComponent<MeshCollider>();
                foreach(var sample in samples)
                {
                    float x=origin.x+sample.x,z=origin.z+sample.z;
                    if(!collider.Raycast(new Ray(new Vector3(x,200,z),Vector3.down),out var hit,300)) throw new InvalidOperationException("No native terrain collider.");
                    rows.Add(new Dictionary<string,object?> { ["zoneOffset"]=i,["x"]=x,["z"]=z,["distance"]=sample.z,["before"]=sample.before,
                        ["height"]=hm.GetHeight(sample.x-i*64+32,sample.z+32)+hm.transform.position.y,["colliderHeight"]=hit.point.y });
                }
            }
            return new Dictionary<string,object?>{["source"]="natural-heightmap-road-fixture",["complete"]=true,["target"]=target,["width"]=Width,["margin"]=2,["paintProfile"]=DirtFade ? "dirt-fade" : "paved",
                ["x"]=origin.x+32,["z"]=origin.z,["samples"]=rows.ToArray(),["paintSamples"]=paintRows.ToArray(),["paintInput"]="saved-explicit-rgba",["compilerIds"]=compObjects.ConvertAll(o=>o.GetComponent<ZNetView>().GetZDO().m_uid.ToString()).ToArray()};
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
