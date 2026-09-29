using System.Text.Json;
using ProceduralRoads.SystemTests;
using Valheim.Testing.Game;
using Xunit;

public class PlanTests
{
    internal static RunPlan Valid() => new()
    {
        Scenario = "empty-save", Runtime = new() { Source = Path.GetTempPath(), Sha256 = new() { ["server.exe"] = new('a', 64) } },
        World = new() { Source = Path.GetTempPath(), Sha256 = new() { ["worlds_local/test.db"] = new('b', 64) } },
        Executable = "valheim_server.exe", Arguments = ["-batchmode", "-nographics", "-savedir", "{world}"],
        Pins = new() { ["worlduid"] = "123", ["warpalicious.ProceduralRoads"] = new('1', 32), ["valheimCLI.valheimCLI"] = new('2', 32), ["testing.proceduralroads.adapter"] = new('3', 32) }
    };
    [Fact] public void TerrainFixtureRequiresExplicitEnablementAndNoBridgeMutation()
    {
        var plan = Valid(); plan.Scenario = "terrain-calibration";
        Assert.Throws<ArgumentException>(plan.Validate);
        plan.Environment["ROADS_TEST_TERRAIN_CALIBRATION"] = "1"; plan.Validate();
        plan.Append = "road_path 1,2 3,4";
        Assert.Throws<ArgumentException>(plan.Validate);
    }
    [Theory][InlineData("paved")][InlineData("Dirt-Fade")][InlineData("")][InlineData("dirt-fade ")]
    public void PaintProfileMustBeAbsentOrDirtFade(string profile)
    {
        var p = Valid(); p.Scenario = "terrain-persistence"; p.Environment["ROADS_TEST_PERSISTENT_TERRAIN"] = "1"; p.Validate();
        p.Environment["ROADS_TEST_PAINT_PROFILE"] = "dirt-fade"; p.Validate();
        p.Environment["ROADS_TEST_PAINT_PROFILE"] = profile; Assert.Throws<ArgumentException>(p.Validate);
    }
    [Fact] public void PaintProfileKeyCannotDifferOnlyInCase()
    {
        var p = Valid(); p.Environment["roads_test_paint_profile"] = "dirt-fade"; Assert.Throws<ArgumentException>(p.Validate);
    }
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
    [Theory][InlineData("")][InlineData("valheim_server.exe")][InlineData("valheim_server.x86_64")]
    public void ExecutableIsOptionalOrAKnownServerName(string file)
    { var p = Valid(); p.Executable = file; p.Validate(); }
    [Fact] public void NullExecutableCountsAsOmitted()
    { var p = Valid(); p.Executable = null!; p.Validate(); p.CheckExecutable(ServerPlatform.Linux); }
    [Theory][InlineData("server.exe")][InlineData("bin/valheim_server.exe")][InlineData("./valheim_server.x86_64")][InlineData("start_server_bepinex.sh")][InlineData(" ")]
    public void OtherExecutablesAreRefusedBecauseServerLaunchStartsTheRootServer(string file)
    { var p = Valid(); p.Executable = file; Assert.Throws<ArgumentException>(p.Validate); }
    [Theory]
    [InlineData("", ServerPlatform.Windows, true)][InlineData("", ServerPlatform.Linux, true)]
    [InlineData("valheim_server.exe", ServerPlatform.Windows, true)][InlineData("valheim_server.x86_64", ServerPlatform.Linux, true)]
    [InlineData("valheim_server.exe", ServerPlatform.Linux, false)][InlineData("valheim_server.x86_64", ServerPlatform.Windows, false)]
    public void StatedExecutableMustMatchTheDetectedRuntime(string file, ServerPlatform platform, bool matches)
    {
        var p = Valid(); p.Executable = file;
        if (matches) p.CheckExecutable(platform); else Assert.Throws<ArgumentException>(() => p.CheckExecutable(platform));
    }
    [Theory]
    [InlineData(ServerPlatform.Windows, true, true)][InlineData(ServerPlatform.Linux, false, true)]
    [InlineData(ServerPlatform.Linux, true, false)][InlineData(ServerPlatform.Windows, false, false)]
    public void RuntimeMustRunOnItsOwnPlatform(ServerPlatform platform, bool windowsHost, bool allowed)
    {
        if (allowed) RunPlan.CheckLaunchHost(platform, windowsHost);
        else Assert.Throws<PlatformNotSupportedException>(() => RunPlan.CheckLaunchHost(platform, windowsHost));
    }
    [Theory][InlineData("DOORSTOP_ENABLED")][InlineData("DOORSTOP_TARGET_ASSEMBLY")][InlineData("doorstop_enabled")][InlineData("DOORSTOP_MONO_DLL_SEARCH_PATH_OVERRIDE")]
    public void DoorstopVariablesAreLauncherOwned(string name)
    { var p = Valid(); p.Environment[name] = "1"; Assert.Throws<ArgumentException>(p.Validate); }
    [Fact] public void LoaderPathsStayCallerExtensible()
    { var p = Valid(); p.Environment["LD_LIBRARY_PATH"] = "{runtime}/extra"; p.Environment["SteamAppId"] = "892970"; p.Validate(); }
    [Fact] public void LinuxAbsoluteSourcesAreAcceptedOnUnixHosts()
    {
        if (OperatingSystem.IsWindows()) return; // Sources are local to the driver's host; Windows has no rooted '/' form.
        var p = Valid(); p.Executable = "valheim_server.x86_64";
        p.Runtime.Source = "/srv/roads-test/runtime"; p.World.Source = "/srv/roads-test/world"; p.Validate();
        Assert.Throws<ArgumentException>(() => p.CheckOutput("/srv/roads-test/world/run"));
        p.CheckOutput("/srv/roads-test/runs/new");
    }
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
