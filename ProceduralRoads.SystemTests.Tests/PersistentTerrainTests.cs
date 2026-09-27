using System.Text.Json;
using ProceduralRoads.SystemTests;
using Xunit;
public class PersistentTerrainTests
{
    [Theory]
    [InlineData(64,65,0,65)] [InlineData(64,65,2,65)] [InlineData(64,65,3,64.5f)] [InlineData(64,65,4,64)]
    [InlineData(64,90,0,72)] [InlineData(64,0,3,56)]
    public void IndependentProfile(float before,float target,int distance,float expected)=>Assert.Equal(expected,PersistentTerrainScenario.Expected(before,target,distance));
    private static JsonElement Data(Action<List<Dictionary<string,object>>>? edit=null)
    {
        var rows=new List<Dictionary<string,object>>();
        for(int zone=0;zone<2;zone++) foreach(int x in zone==0?new[]{24,32}:new[]{32,40}) foreach(int d in new[]{0,2,3,4,6})
        {
            float expected=d<=2?65:d==3?64.5f:64;
            rows.Add(new(){["zoneOffset"]=zone,["x"]=x,["z"]=d,["distance"]=d,["before"]=64,["height"]=expected,["colliderHeight"]=expected});
        }
        edit?.Invoke(rows);
        return JsonSerializer.SerializeToElement(new{source="natural-heightmap-road-fixture",complete=true,x=32,z=0,target=65,width=4,margin=2,samples=rows});
    }
    [Fact] public void BothBoundaryCopiesRequired()=>Assert.Equal(20,PersistentTerrainScenario.Compare(Data()).Count);
    [Fact] public void MissingBoundaryRejected()=>Assert.Throws<InvalidOperationException>(()=>PersistentTerrainScenario.Compare(Data(r=>r.RemoveAt(10))));
    [Fact] public void ShiftedRowRejected()=>Assert.Throws<InvalidOperationException>(()=>PersistentTerrainScenario.Compare(Data(r=>r[0]["x"]=25)));
    [Fact] public void NoWriteIsDetected()=>Assert.Equal(12,PersistentTerrainScenario.Compare(Data(r=>r.ForEach(x=>{x["height"]=64;x["colliderHeight"]=64;}))).Count(x=>!x.Passed));
    [Fact] public void WrongColliderIsDetected()=>Assert.Single(PersistentTerrainScenario.Compare(Data(r=>r[0]["colliderHeight"]=62)).Where(x=>!x.Passed));
}
