using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Valheim.Testing.Game;
using valheimCLI;

namespace ProceduralRoads.SystemTests;

public sealed class PinnedDirectory
{
    public string Source { get; set; } = "";
    public Dictionary<string, string> Sha256 { get; set; } = [];
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(Source) || Sha256.Count == 0) throw new ArgumentException("A full source path and fixture hashes are required.");
        foreach (var hash in Sha256)
            if (hash.Value.Length != 64 || !hash.Value.All(Uri.IsHexDigit)) throw new ArgumentException("Use full SHA256 fixture hashes.");
    }
}
public sealed class RunPlan
{
    public string Scenario { get; set; } = "";
    public PinnedDirectory Runtime { get; set; } = new();
    public PinnedDirectory World { get; set; } = new();
    public string Executable { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = [];
    public Dictionary<string, string> Pins { get; set; } = [];
    public int Port { get; set; } = 5577;
    public int StartupSeconds { get; set; } = 300;
    public int CommandSeconds { get; set; } = 30;
    public string Append { get; set; } = "";
    public List<ZoneExpectation> Expected { get; set; } = [];
    public static RunPlan Read(string path) => JsonSerializer.Deserialize<RunPlan>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
        ?? throw new ArgumentException("Empty plan.");
    public void Validate()
    {
        Runtime.Validate(); World.Validate();
        if (Scenario != "empty-save" && Scenario != "bridge-respawn") throw new ArgumentException("Unknown scenario.");
        if (Port < 1024 || Port > 65535 || StartupSeconds < 1 || StartupSeconds > 1800 || CommandSeconds < 1 || CommandSeconds > 120)
            throw new ArgumentException("Invalid port or time budget.");
        if (string.IsNullOrWhiteSpace(Executable) || Path.IsPathRooted(Executable) || Executable.Split('/', '\\').Contains(".."))
            throw new ArgumentException("Executable must be inside the copied runtime.");
        if (Environment.ContainsKey("ROADS_TEST_SESSION_TOKEN")) throw new ArgumentException("Session token is runner-owned.");
        int savedir = Array.IndexOf(Arguments, "-savedir");
        if (savedir < 0 || savedir + 1 >= Arguments.Length || Arguments[savedir + 1] != "{world}" || Arguments.Count(x => x == "-savedir") != 1 ||
            !Arguments.Contains("-batchmode") || !Arguments.Contains("-nographics"))
            throw new ArgumentException("Dedicated launch requires -batchmode -nographics and exactly one -savedir {world}.");
        var errors = new List<string>();
        var parsed = Expectations.ParseLines(Pins.Select(x => x.Key + "=" + x.Value), errors);
        if (errors.Count != 0) throw new ArgumentException("Invalid environment pins: " + string.Join("; ", errors));
        foreach (string key in new[] { "worlduid", "warpalicious.ProceduralRoads", "valheimCLI.valheimCLI", "testing.proceduralroads.adapter" })
            if (!Pins.TryGetValue(key, out var value) || (key != "worlduid" && (value.Length != 32 || !value.All(Uri.IsHexDigit))))
                throw new ArgumentException("Pin worlduid and full MD5s for Roads, CLI and adapter.");
        if (parsed.Any(x => !Expectations.IsWorldKey(x.Key) && (x.Value == "any" || x.Value == "absent")))
            throw new ArgumentException("All listed plugins require exact MD5 pins.");
        if (Pins.ContainsKey("worldfiles")) throw new ArgumentException("World bytes change after save; pin input SHA256 and persistent worlduid instead.");
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
                PieceComparison.Match(zone.Pieces, zone.Pieces, .05, 1); // Validate transform shape/finite values before launch.
            }
        }
        else if (Append.Length != 0 || Expected.Count != 0) throw new ArgumentException("Bridge settings supplied to empty-save.");
    }
    public void CheckOutput(string output)
    {
        output = Path.GetFullPath(output);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string source in new[] { Runtime.Source, World.Source })
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            if (output.Equals(full, comparison) || output.StartsWith(full + Path.DirectorySeparatorChar, comparison))
                throw new ArgumentException("Output must be outside both pinned sources.");
        }
    }
    public string ExpectCommand => Expectations.ExpectCommand(Expectations.ParseLines(Pins.Select(x => x.Key + "=" + x.Value), new()), strict: true);
    public string Expand(string value, string runtime, string world) => value.Replace("{runtime}", runtime).Replace("{world}", world).Replace("{port}", Port.ToString(CultureInfo.InvariantCulture));
}
