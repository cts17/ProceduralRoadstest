using System.Text.Json;
using ProceduralRoads.SystemTests;
using Xunit;

public class PlanTests
{
    internal static RunPlan Valid() => new()
    {
        Scenario = "empty-save", Runtime = new() { Source = Path.GetTempPath(), Sha256 = new() { ["server.exe"] = new('a', 64) } },
        World = new() { Source = Path.GetTempPath(), Sha256 = new() { ["worlds_local/test.db"] = new('b', 64) } },
        Executable = "server.exe", Arguments = ["-batchmode", "-nographics", "-savedir", "{world}"],
        Pins = new() { ["worlduid"] = "123", ["warpalicious.ProceduralRoads"] = new('1', 32), ["valheimCLI.valheimCLI"] = new('2', 32), ["testing.proceduralroads.adapter"] = new('3', 32) }
    };
    [Fact] public void ValidPlanUsesStrictPinsAndTokenizedArguments()
    {
        var plan = Valid(); plan.Validate(); Assert.StartsWith("cli_expect --strict ", plan.ExpectCommand);
        Assert.Equal("a b/5577", plan.Expand("{world}/{port}", "runtime", "a b"));
    }
    [Theory][InlineData("any")][InlineData("12345678")][InlineData("absent")]
    public void PluginPinMustBeFullHash(string pin)
    { var p = Valid(); p.Pins["warpalicious.ProceduralRoads"] = pin; Assert.Throws<ArgumentException>(p.Validate); }
    [Fact] public void PersistentWorldIdentityIsRequired()
    { var p = Valid(); p.Pins.Remove("worlduid"); Assert.Throws<ArgumentException>(p.Validate); }
    [Fact] public void FileHashPinWouldBecomeStaleAfterSaveAndIsRefused()
    { var p = Valid(); p.Pins["worldfiles"] = new('f', 32); Assert.Throws<ArgumentException>(p.Validate); }
    [Theory][InlineData("../server.exe")][InlineData("/server.exe")][InlineData("..\\server.exe")]
    public void ExecutableCannotEscapeCopiedRuntime(string file)
    { var p = Valid(); p.Executable = file; Assert.Throws<ArgumentException>(p.Validate); }
    [Fact] public void MissingIsolatedSaveRootIsRefused()
    { var p = Valid(); p.Arguments = ["-batchmode", "-nographics"]; Assert.Throws<ArgumentException>(p.Validate); }
    [Fact] public void DuplicateSavedirCouldOverrideIsolationAndIsRefused()
    { var p = Valid(); p.Arguments = [..p.Arguments, "-savedir", "live-save"]; Assert.Throws<ArgumentException>(p.Validate); }
    [Fact] public void CallerCannotChooseTheSessionNonce()
    { var p = Valid(); p.Environment["ROADS_TEST_SESSION_TOKEN"] = "reused"; Assert.Throws<ArgumentException>(p.Validate); }
    [Theory][InlineData("road_generate")][InlineData("road_path 1,2")][InlineData("road_path 1,2 NaN,3")][InlineData("road_path 1,2 3,4\nquit")]
    public void BridgePlanRefusesMalformedOrDifferentMutation(string command)
    { var p = Valid(); p.Scenario = "bridge-respawn"; p.Append = command; Assert.Throws<ArgumentException>(p.Validate); }
    [Fact] public void OutputCannotBeCreatedInsideAPinnedSource()
    {
        var p = Valid();
        Assert.Throws<ArgumentException>(() => p.CheckOutput(Path.Combine(p.World.Source, "new-run")));
    }
    [Fact] public void UnknownJsonFieldsCannotSilentlyDefault()
    {
        string file = Path.GetTempFileName();
        try { File.WriteAllText(file, "{\"Scenaro\":\"empty-save\"}"); Assert.Throws<JsonException>(() => RunPlan.Read(file)); }
        finally { File.Delete(file); }
    }
    [Fact] public void BridgeFixtureNeedsIndependentTransforms()
    {
        var p = Valid(); p.Scenario = "bridge-respawn"; p.Append = "road_path 1,2 3,4";
        Assert.Throws<ArgumentException>(p.Validate);
        p.Expected = [new(0, 0, [new(1, [0, 0, 0], [0, 0, 0, 1])])]; p.Validate();
        p.Expected.Add(p.Expected[0]); Assert.Throws<ArgumentException>(p.Validate);
    }
}
