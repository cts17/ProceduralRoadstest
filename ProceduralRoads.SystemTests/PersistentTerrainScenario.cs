using System.Text.Json;
using Valheim.Testing.Game;

namespace ProceduralRoads.SystemTests;

public sealed record PersistentSample(int ZoneOffset, float X, float Z, float Before, float Expected, float Height, float ColliderHeight)
{
    public bool Passed => float.IsFinite(Height) && float.IsFinite(ColliderHeight) && Math.Abs(Height-Expected)<=.02f && Math.Abs(ColliderHeight-Expected)<=.02f;
}
public static class PersistentTerrainScenario
{
    // Independent analytic oracle for this deliberately constant, width-4 road:
    // flat to 2 m, half at 3 m, zero from 4 m; delta limited to +/-8 m.
    public static float Expected(float before, float target, int distance)
    {
        if(!float.IsFinite(before)||!float.IsFinite(target)||distance<0) throw new ArgumentException("Invalid fixture input.");
        float fraction=distance<=2 ? 1 : distance==3 ? .5f : 0;
        return before+Math.Clamp((target-before)*fraction,-8,8);
    }
    public static IReadOnlyList<PersistentSample> Compare(JsonElement data)
    {
        if(data.GetProperty("source").GetString()!="natural-heightmap-road-fixture" || !data.GetProperty("complete").GetBoolean() ||
            data.GetProperty("width").GetInt32()!=4 || data.GetProperty("margin").GetInt32()!=2)
            throw new InvalidOperationException("Wrong persistent fixture.");
        float x=data.GetProperty("x").GetSingle(),z=data.GetProperty("z").GetSingle(),target=data.GetProperty("target").GetSingle();
        if(!float.IsFinite(x)||!float.IsFinite(z)||Math.Abs(x)>20000||Math.Abs(z)>20000) throw new InvalidOperationException("Invalid fixture origin.");
        var wanted=new HashSet<(int,float,float)>();
        for(int zone=0;zone<2;zone++) foreach(int dx in zone==0?new[]{-8,0}:new[]{0,8}) foreach(int dz in new[]{0,2,3,4,6}) wanted.Add((zone,x+dx,z+dz));
        var rows=new List<PersistentSample>();
        foreach(var row in data.GetProperty("samples").EnumerateArray())
        {
            int zone=row.GetProperty("zoneOffset").GetInt32(),distance=row.GetProperty("distance").GetInt32();
            float sx=row.GetProperty("x").GetSingle(),sz=row.GetProperty("z").GetSingle(),before=row.GetProperty("before").GetSingle();
            if(!wanted.Remove((zone,sx,sz)) || sz-z!=distance) throw new InvalidOperationException("Wrong, duplicate or shifted sample identity.");
            rows.Add(new(zone,sx,sz,before,Expected(before,target,distance),row.GetProperty("height").GetSingle(),row.GetProperty("colliderHeight").GetSingle()));
        }
        if(wanted.Count!=0) throw new InvalidOperationException("Missing terrain samples.");
        foreach(var pair in rows.GroupBy(r=>(r.X,r.Z)))
            if(pair.Max(r=>r.Before)-pair.Min(r=>r.Before)>.02f) throw new InvalidOperationException("Boundary input copies disagree.");
        if(!rows.Any(r=>Math.Abs(r.Expected-r.Before)>.5f)) throw new InvalidOperationException("Fixture has no discriminating terrain change.");
        return rows;
    }
    public static void Prepare(GameActor server, ScenarioReport report, string output)
    {
        var data=server.Invoke(server.RequireCapability("roads.testing/terrain-persist"));
        File.WriteAllText(Path.Combine(output,"persistent-inputs.json"),data.GetRawText());
        var rows=Compare(data);
        File.WriteAllText(Path.Combine(output,"persistent-residuals.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        report.Step("native inputs plus analytic road profile agree",()=>{if(rows.Any(r=>!r.Passed)) throw new InvalidOperationException("Persistent fixture differs from its analytic expectation.");});
        var samples=rows.GroupBy(r=>(r.X,r.Z)).Select(g=>new HeightExpectation(g.Key.X,g.Key.Z,g.First().Expected)).ToArray();
        File.WriteAllText(Path.Combine(output,"client-height-plan.json"),JsonSerializer.Serialize(new {layer="loaded-ground",expectedFrom="Native pre-write heightmap vertices plus declared width-4 constant target, half-strength release at 3m and +/-8m clamp; persistent-inputs.json",tolerance=.05f,samples},new JsonSerializerOptions{WriteIndented=true}));
        report.Step("confirmed fixture save",()=>{if(!server.Execute("cli_save").Output.Any(l=>l.StartsWith("OK: SAVE "))) throw new InvalidOperationException("No confirmed fixture save.");});
    }
}
