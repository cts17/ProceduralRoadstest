using System.Globalization;
using Valheim.Testing.Game;
using valheimCLI;

namespace ProceduralRoads.SystemTests;

// A Roads system-test plan: the toolkit's pinned dedicated-server plan plus the Roads scenarios and their fields.
public sealed class RunPlan : ServerRunPlan
{
    public const string SessionTokenVariable = "ROADS_TEST_SESSION_TOKEN";
    public static readonly string[] RequiredPlugins = ["warpalicious.ProceduralRoads", "valheimCLI.valheimCLI", "testing.proceduralroads.adapter"];
    public string Append { get; set; } = "";
    public List<ZoneExpectation> Expected { get; set; } = [];
    /// <summary>The client a terrain-persistence run measures from; required for that scenario's run mode.</summary>
    public ClientPlan? Client { get; set; }
    public static RunPlan Read(string path) => Read<RunPlan>(path);
    public void Validate()
    {
        if (Scenario != "empty-save" && Scenario != "bridge-respawn" && Scenario != "terrain-calibration" && Scenario != "terrain-persistence") throw new ArgumentException("Unknown scenario.");
        if (Scenario == "terrain-calibration" && (!Environment.TryGetValue("ROADS_TEST_TERRAIN_CALIBRATION", out var enabled) || enabled != "1"))
            throw new ArgumentException("Explicitly enable the terrain fixture in the owned session environment.");
        if (Scenario == "terrain-persistence" && (!Environment.TryGetValue("ROADS_TEST_PERSISTENT_TERRAIN", out var persist) || persist != "1"))
            throw new ArgumentException("Explicitly enable the persistent fixture in the owned session environment.");
        PersistentTerrainScenario.WidthFor(Environment); // Refuses unknown paint profiles before launch.
        ValidateServerPlan(RequiredPlugins, SessionTokenVariable);
        if (Scenario == "bridge-respawn")
        {
            if (!Append.StartsWith("road_path ", StringComparison.Ordinal) || Append.IndexOfAny(['\r', '\n', ';']) >= 0)
                throw new ArgumentException("Supply one road_path append command.");
            var pairs = Append[10..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pairs.Length < 2 || pairs.Any(p => p.Split(',').Length != 2 || p.Split(',').Any(n => !double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))))
                throw new ArgumentException("road_path requires finite X,Z pairs.");
            if (Expected.Count == 0 || Expected.Sum(x => x.Pieces.Count) == 0 || Expected.Select(x => (x.X, x.Z)).Distinct().Count() != Expected.Count)
                throw new ArgumentException("Freeze independent expectations for distinct bridge zones.");
            foreach (var zone in Expected)
            {
                if (Math.Abs((long)zone.X) > 320 || Math.Abs((long)zone.Z) > 320 || zone.Pieces.Count > 512) throw new ArgumentException("Invalid census zone or size.");
                TransformMatch.Match(zone.Pieces, zone.Pieces, .05, 1); // Validate transform shape/finite values before launch.
            }
        }
        else if (Append.Length != 0 || Expected.Count != 0) throw new ArgumentException("Bridge settings supplied to a different scenario.");
        if (Client != null)
        {
            if (Scenario != "terrain-persistence") throw new ArgumentException("Client settings supplied to a scenario that has no client.");
            Client.Validate();
        }
    }
    /// <summary>The server's world UID pin, which the joined client must also report.</summary>
    public string WorldUid => Pins["worlduid"];
}

/// <summary>
/// A game client, launched and signed in by the operator with ValheimCLI only, that the runner joins to its server,
/// places on the fixture and measures from. The join password stays in the client's own process environment.
/// </summary>
public sealed class ClientPlan
{
    public static readonly string[] MustBeAbsent = ["warpalicious.ProceduralRoads", "warpalicious.More_World_Locations_AIO"];
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    /// <summary>Every plugin the client loads, by exact MD5, plus Roads and MWL as <c>absent</c>. No world key: the runner adds the server's.</summary>
    public Dictionary<string, string> Pins { get; set; } = [];
    /// <summary>An existing, disposable, local-only character.</summary>
    public string Character { get; set; } = "";
    /// <summary>The server address the client joins, host:port.</summary>
    public string Join { get; set; } = "";
    /// <summary>The name of the variable in the CLIENT's process environment that holds the server password.</summary>
    public string PasswordVariable { get; set; } = "";
    /// <summary>The prepare-terrain run's client plans, pinned by SHA256.</summary>
    public PinnedFile HeightPlan { get; set; } = new();
    public PinnedFile PaintPlan { get; set; } = new();
    public int JoinSeconds { get; set; } = 180;
    public int ArrivalSeconds { get; set; } = 120;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1024 or > 65535) throw new ArgumentException("Give the client's ValheimCLI host and port.");
        foreach (var token in new[] { Character, Join, PasswordVariable })
            if (string.IsNullOrEmpty(token) || token.Any(char.IsWhiteSpace)) throw new ArgumentException("Client character, join address and password variable must be single tokens.");
        if (!Join.Contains(':')) throw new ArgumentException("Join needs host:port.");
        if (JoinSeconds is < 10 or > 900 || ArrivalSeconds is < 10 or > 600) throw new ArgumentException("Client join/arrival timeouts are out of range.");
        if (Pins.ContainsKey("worlduid") || Pins.ContainsKey("world")) throw new ArgumentException("Leave the world out of the client pins; the runner pins the server's world.");
        if (!Pins.TryGetValue("valheimCLI.valheimCLI", out var cli) || cli.Length != 32 || !cli.All(Uri.IsHexDigit)) throw new ArgumentException("Pin the client's exact ValheimCLI MD5.");
        foreach (var plugin in MustBeAbsent)
            if (!Pins.TryGetValue(plugin, out var value) || value != "absent") throw new ArgumentException($"The client must pin {plugin}=absent: it measures what a stock client sees.");
        foreach (var pin in Pins)
            if (pin.Value != "absent" && (pin.Value.Length != 32 || !pin.Value.All(Uri.IsHexDigit))) throw new ArgumentException($"Client plugin {pin.Key} needs an exact MD5 or absent.");
        HeightPlan.RequireFile("height plan"); PaintPlan.RequireFile("paint plan");
        _ = MenuExpectations; // Parses the pins before anything launches.
    }
    /// <summary>Pins at the menu (plugins only) and once joined (plus the server's world).</summary>
    public string MenuExpectations => Expect(Pins.Select(p => p.Key + "=" + p.Value));
    public string WorldExpectations(string worldUid) => Expect(Pins.Select(p => p.Key + "=" + p.Value).Append("worlduid=" + worldUid));
    private static string Expect(IEnumerable<string> lines)
    {
        var errors = new List<string>();
        var parsed = Expectations.ParseLines(lines, errors);
        if (errors.Count != 0) throw new ArgumentException("Invalid client pins: " + string.Join("; ", errors));
        return Expectations.ExpectCommand(parsed, strict: true);
    }
}

/// <summary>A file the plan depends on, by path and SHA256.</summary>
public sealed class PinnedFile
{
    public string Source { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public void RequireFile(string what)
    {
        if (string.IsNullOrEmpty(Source) || Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit)) throw new ArgumentException($"Pin the client {what} by path and SHA256.");
    }
    /// <summary>The file's path after checking its hash still matches.</summary>
    public string Verified()
    {
        if (!string.Equals(WorldFixture.Hash(Source), Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{Source} changed after it was pinned.");
        return Source;
    }
}
