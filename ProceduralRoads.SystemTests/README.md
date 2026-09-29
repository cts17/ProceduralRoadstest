# Roads system-test pilot

Optional development tooling, outside the normal Roads release. Unit tests and
simulated runner tests work on Mac without Valheim or Steam. Real game execution
is a separate, bounded dedicated-server gate on a Windows host or a Linux host
or container ([Linux dedicated server](#linux-dedicated-server)). No scheduled
runs or remote machine control are built in.

## Quick start and migration

No Valheim or Steam is needed for these local layers. Every pinned package restores
from NuGet.org with no extra configuration. In Visual Studio 2026, Rider
or VS Code, open `ProceduralRoads.Testing.slnx` and use the IDE's test runner
([IDE notes](https://github.com/tvongaza/ValheimTesting/blob/main/docs/getting-started.md#using-visual-studio-rider-or-vs-code)).
From a terminal at the Roads repository root:

```sh
dotnet test ProceduralRoads.Tests/ProceduralRoads.Tests.csproj -c Release -f net10.0
dotnet test ProceduralRoads.SystemTests.Tests/ProceduralRoads.SystemTests.Tests.csproj -c Release
```

To try a ValheimTesting build that is not yet on NuGet.org, build its local `.packages`
feed as its [getting-started guide](https://github.com/tvongaza/ValheimTesting/blob/main/docs/getting-started.md)
describes and restore with it as an extra source:

```sh
# Replace this path with the local feed you built.
dotnet restore ProceduralRoads.Tests/ProceduralRoads.Tests.csproj -p:RestoreAdditionalProjectSources=/absolute/ValheimTesting/.packages
dotnet restore ProceduralRoads.SystemTests.Tests/ProceduralRoads.SystemTests.Tests.csproj -p:RestoreAdditionalProjectSources=/absolute/ValheimTesting/.packages
```

Then add `--no-restore` to the test commands above. The property keeps your configured NuGet.org source; passing NuGet.org as a second `--source` failed on Windows with .NET SDK 10.0.401.

On Windows, `dotnet test ProceduralRoads.Tests/ProceduralRoads.Tests.csproj` runs both the net10.0 and net48 legs (net48 on .NET Framework). On macOS/Linux use the repository's `ProceduralRoads.Tests/run-tests.sh` for the net48 leg under Mono. The optional adapter build below is the separate step requiring actual game compile references and the exact CLI/Roads DLLs.

| Stays in Roads | Shared dependency |
|---|---|
| Existing xUnit suite, Roads-only doubles (`ModShims`, `PluginShims`, the `Heightmap` hooks) and mod assertions | Game doubles from `Valheim.Testing.Doubles`; `SyntheticWorld` delegates to `Valheim.Testing` terrain; `StaticOverride` |
| Terrain/paint, empty-save and bridge scenario decisions | `Valheim.Testing.Game` observations, owned sessions and reports |
| Optional game-side Roads adapter | ValheimCLI extension API from the installed core |

Exact package pins:

| Project | Target | Package |
|---|---|---|
| `ProceduralRoads.Tests` | net48 and net10.0 | `Valheim.Testing` 0.1.0-preview.6 and `Valheim.Testing.Doubles` 0.1.0-preview.4 (source package) |
| `ProceduralRoads.SystemTests` | net10.0 (`RollForward` Major) | `Valheim.Testing.Game` 0.1.0-preview.11, which pins the ValheimCLI transport `Valheim.Testing.Cli` 0.1.0-preview.5 |
| `ProceduralRoads.SystemTests.Tests` | net10.0 | through the runner project |

Generic lifecycle tests moved with their implementation into the shared library; they were not removed. This is incremental migration, not a rewrite of every Roads test or a production dependency. No sibling ValheimCLI source checkout is needed.

For small standalone tools, start with [NoGameTerrain](https://github.com/tvongaza/ValheimTesting/blob/main/examples/NoGameTerrain/README.md), then [ClientSurfaceCheck](https://github.com/tvongaza/ValheimTesting/blob/main/examples/ClientSurfaceCheck/README.md) or [PaintCheck](https://github.com/tvongaza/ValheimTesting/blob/main/examples/PaintCheck/README.md). [WalkingReview](https://github.com/tvongaza/ValheimTesting/blob/main/examples/WalkingReview/README.md) records human judgement separately. The [complete example index](https://github.com/tvongaza/ValheimTesting/blob/main/examples/README.md) identifies which tools are read-only and which replace a disposable probe.

## What is implemented

- `empty-save`: require an existing nonempty network, request empty generation
  once, wait for a complete empty result, require `cli_save` confirmation, restart
  and require an empty network **loaded from the save**, not regenerated.
- `bridge-respawn`: require no prior pending append, append once, prove that append
  is pending, respawn once, wait for it to drain, compare marked bridge pieces,
  confirm save, restart, and compare again. An already-drained append fails the
  fixture check; it is not a pass or skip.
- Bridge expectations are frozen independently of the candidate's census. Include
  pre-existing marked pieces in each selected zone. Matching uses prefab, complete
  3D position, quaternion angle and multiplicity (5 cm / 1 degree). It does not
  assume every fixture has 443 pieces.
- JSON/JUnit results, per-connection command/reply evidence, boot stdout/stderr,
  source/copy SHA256 manifests and runner/toolkit hashes are retained. BepInEx and
  Unity logs are archived per boot, before a restart can overwrite them. Failed runs
  keep their copies for diagnosis. Existing output directories are refused.

## Ownership and isolation

Prepare a dedicated runtime tree, including its executable, BepInEx, plugins,
configuration and launcher dependencies, separately from any live installation.
The runner hashes and copies that tree and a pinned save-root fixture into a new
output directory. **No live installation is deployed to or edited.**

The optional Roads adapter reports a per-boot random token, process ID, actual
save root and readiness. Before scenario commands run, these must match the
process and copied save directory the runner owns. A fresh ValheimCLI actor then verifies
strict world UID and full plugin MD5 pins. Restart waits for the old owned process
to exit, generates a new token, reconnects and repeats verification. It never
kills by process name or takes ownership of an attached game.

The runner builds the launch with ValheimTesting's `ServerLaunch` and starts the
server executable directly with argument tokens, without a shell or the BepInEx
pack's start script. Its working directory is the copied runtime. `ServerLaunch`
detects the platform from the runtime (`valheim_server.exe` or
`valheim_server.x86_64` at its root), requires BepInEx's preloader and Doorstop
loader in it, sets `SteamAppId` to the dedicated server's 892970 unless the plan
sets it and, for Linux, sets the Doorstop variables the pack's script would.
Supply `-batchmode`, `-nographics` and exactly one `-savedir {world}`. A launch
wrapper that detaches the game is not supported; the PID check refuses it. The
game must respect its save-root argument at startup: the identity check detects a
wrong save root before test mutations, but cannot undo writes the game made while
booting. This is one reason launches need a Windows or Linux dedicated server;
there is no macOS server, and Mac's client save-root behavior is not assumed.
A runtime runs only on a host of its own platform, and a Mac refuses every launch
mode before copying anything.

Stop uses only the owned process tree. Scenarios confirm saves before their planned
restart; failure teardown may terminate an unsaved **disposable copy**. A failed
stop prevents another launch and is reported as a test failure. Ctrl-C requests cancellation and owned-process cleanup; an in-flight ValheimCLI call
can take up to its command timeout to return. A forced kill of the runner cannot
guarantee cleanup; retained per-boot PID records help with inspecting it.
Copies are always
retained; remove them manually only after confirming the owned processes are gone.

Run native checks only on a disposable test machine or install that nothing
else is using at the time. Back up and manifest anything the run could touch,
choose free game and ValheimCLI ports, use private test server settings and keep
players out. Never run against a production runtime directory or a save that
people play on. The local port check is not a machine-wide reservation. No
account or Steam session is needed for the dedicated gate.

## Build and fast checks

```sh
dotnet test ProceduralRoads.SystemTests.Tests/ProceduralRoads.SystemTests.Tests.csproj
dotnet publish ProceduralRoads.SystemTests/ProceduralRoads.SystemTests.csproj -c Release -p:UseAppHost=false -o /new/runner-output
dotnet build ProceduralRoads.TestAdapter/ProceduralRoads.TestAdapter.csproj -c Release \
  -p:CliDll=/path/to/valheimCLI.dll -p:RoadsDll=/path/to/ProceduralRoads.dll
```

`CliDll` has no default and the adapter build stops with an error without it:
point it at the exact ValheimCLI core `valheimCLI.dll` installed in the staged
runtime. `RoadsDll` defaults to this repository's Debug build output.
The adapter build imports compile references only, has embedded symbols and no
deployment target. Install the matching ValheimCLI core, Standard and World Tools packs, Roads, and this adapter in the **staged** runtime; do not copy a second CLI/API assembly alongside it. Both normally belong
in plugins for this persistence gate. ScriptEngine adapter reload is a separate
check. The production Roads/MWL releases have no testing-framework dependency.

## Plan and invocation

A plan is JSON. Unknown keys are errors. Paths are local to the machine running
the driver; input manifests must cover **every** file in their source directories.
The world source is a save root containing `worlds_local`, not just a .db file.
Do not add the hash manifest itself to its hashed source directory.

Example shape (replace placeholders; these hashes are not real):

```json
{
  "scenario": "empty-save",
  "runtime": {"source": "C:/test-fixtures/runtime", "sha256": {"valheim_server.exe": "FULL_SHA256_AND_ALL_OTHER_FILES"}},
  "world": {"source": "C:/test-fixtures/old-network", "sha256": {"worlds_local/Test.db": "FULL_SHA256_AND_ALL_OTHER_FILES"}},
  "executable": "valheim_server.exe",
  "arguments": ["-batchmode", "-nographics", "-savedir", "{world}", "-world", "Test", "-name", "Roads test", "-port", "2476", "-public", "0"],
  "environment": {},
  "pins": {
    "worlduid": "123",
    "warpalicious.ProceduralRoads": "FULL_PLUGIN_MD5",
    "valheimCLI.valheimCLI": "FULL_PLUGIN_MD5",
    "testing.proceduralroads.adapter": "FULL_PLUGIN_MD5"
  },
  "port": 5577,
  "startupSeconds": 300,
  "commandSeconds": 30
}
```

`executable` is optional. The runner always starts the server at the runtime's
root, detected from its contents; when the plan names it, it must be exactly
`valheim_server.exe` (Windows) or `valheim_server.x86_64` (Linux) and match the
runtime, so a plan cannot silently run on the other platform. Existing plans
naming `valheim_server.exe` stay valid; any other value, including a path, is
refused. Plan environments may not set `DOORSTOP_*` variables in any letter case:
`ServerLaunch` owns BepInEx's loader settings. `LD_LIBRARY_PATH` and `LD_PRELOAD`
may be set; the runtime's own loader entries are prepended to them.

Include all other loaded plugins in strict pins too. Use the isolated runtime's
ValheimCLI config to select the same port. `{world}`, `{runtime}` and `{port}` expand in
argument/environment values. The runner supplies `ROADS_TEST_SESSION_TOKEN`.
Store any required temporary server password in your private plan; do not publish
plans/logs with credentials. `worldfiles` cannot be pinned across a save; input
SHA256 and stable world UID cover the appropriate boundaries instead.

For empty-save, the copied runtime config must select zero islands while the
starting saved world still contains a road network. For bridge-respawn, use
`"scenario": "bridge-respawn"`, supply `"append": "road_path X,Z X,Z"`, and an
`"expected"` array, for example
`[{"x":0,"z":0,"pieces":[{"prefabHash":123,"position":[1,2,3],"rotation":[0,0,0,1]}]}]`.
Use real independent values, including all marked pieces in those zones. The
fixture must already have generated every zone occupied by the crossing; this
acceptance scenario does not drive a player or invent a crossing location.
For an independently chosen crossing, `prepare-bridge` can generate the listed
zones in a fresh disposable copy and confirm a save. It has a 60-second bound per
zone and records mode `prepare-bridge`; success is fixture preparation, not bridge
acceptance. Freeze and hash the resulting `worlds_local` folder in a separate
save-root input before running the real scenario.

```sh
# Hashes/copies inputs and checks the plan; NEVER launches a game (Mac supported).
dotnet ProceduralRoads.SystemTests.dll validate plan.json /new/preflight-output
# Optional fixture preparation on a Windows or Linux host; never counts as acceptance.
dotnet ProceduralRoads.SystemTests.dll prepare-bridge bridge-plan.json C:/runs/prepare-unique
# Windows or Linux host matching the runtime; use a different new output directory.
dotnet ProceduralRoads.SystemTests.dll run plan.json C:/runs/roads-test-unique
```

A validation pass has report mode `validate`; it is not game validation. Inspect
actual command/reply records and game warnings/errors before calling a `run` pass
accepted. The tool does not yet classify BepInEx warnings automatically.

## Linux dedicated server

`run`, `prepare-bridge` and `prepare-terrain` also launch the Linux dedicated
server, inside a Linux container or on an isolated Linux host. The runner still
refuses a Mac host; on Apple Silicon the container runs only under x86-64
emulation, which ValheimTesting documents as experimental. Not yet exercised:
no Roads scenario has run on the Linux server through this runner. ValheimTesting's
own smoke check booted BepInEx on it with the same `ServerLaunch`; treat the first
Roads run as a new native check and review its logs.

**Runtime.** Build ValheimTesting's
[Linux server image](https://github.com/tvongaza/ValheimTesting/blob/main/docker/linux-server/README.md)
(dedicated server plus BepInExPack_Valheim and the .NET 10 SDK) where it will be
used. It contains game files: never push, export or publish the image, a
container's filesystem or a runtime copied from it. In a fresh container, copy
`/opt/valheim/server` to a new runtime source directory with `cp -a` (which keeps
file modes), then add the same
managed DLLs the Windows gate uses to its `BepInEx/plugins`: `ProceduralRoads.dll`,
the ValheimCLI core `valheimCLI.dll` with its Standard and World Tools packs, and
the Roads adapter built against that exact core (see
[Build and fast checks](#build-and-fast-checks)). Add the ValheimCLI and Roads
configuration the scenario needs, including the ValheimCLI port. Start from a
runtime without `BepInEx/LogOutput.log`, then hash every file. Keep
`valheim_server.x86_64` executable: the manifest hashes contents, not file modes,
and the runner refuses a copy without the execute bit. Record the image's
`/opt/valheim/server-buildid.txt` with the result. Build or publish the runner
with the container's .NET SDK and run it inside the container.

**Plan fields.** Use absolute Linux paths for `runtime.source`, `world.source` and
the output directory, with `/` in manifest keys (the relative paths the runner
computes on Linux), for example:

```json
{
  "runtime": {"source": "/home/valheim/fixtures/runtime", "sha256": {"valheim_server.x86_64": "FULL_SHA256", "BepInEx/plugins/ProceduralRoads.dll": "FULL_SHA256_AND_ALL_OTHER_FILES"}},
  "world": {"source": "/home/valheim/fixtures/old-network", "sha256": {"worlds_local/Test.db": "FULL_SHA256_AND_ALL_OTHER_FILES"}},
  "executable": "valheim_server.x86_64",
  "arguments": ["-batchmode", "-nographics", "-savedir", "{world}", "-world", "Test", "-name", "Roads test", "-port", "2476", "-public", "0"]
}
```

The remaining fields, scenarios and pins are unchanged. `executable` may be
omitted; the runner detects `valheim_server.x86_64`. Do not set `DOORSTOP_*`
variables. Run `validate` first, then the launch mode, each with a new output
directory:

```sh
dotnet ProceduralRoads.SystemTests.dll validate plan.json /home/valheim/runs/preflight-unique
dotnet ProceduralRoads.SystemTests.dll run plan.json /home/valheim/runs/roads-test-unique
```

**Production safety applies unchanged.** Never point a plan at a production
server, its runtime directory or a world people play on; use only disposable
copies. Run in an isolated container or on a host with nothing else running.
Choose a game port other than 2456 (the default that live servers use, as in
`-port 2476` above), keep `-public 0` so the server is never listed, and do not
enable crossplay (`-crossplay`). The image publishes no ports by default; keep it
that way, so nothing outside the container can join. Copy out the report,
per-boot logs and command records, never `/opt/valheim` or the runtime copy.

## Validation and remaining boundaries

The local runner suite covers plan validation, bridge piece matching, the
empty-save, bridge and bridge-preparation flows against simulated transports, and
the independent calibration, persistent height and paint oracles, including their
negative cases. The owned-session lifecycle tests
live with their implementation in ValheimTesting. A
Windows dedicated-server run also exercised the runner against the previously
accepted Roads save/respawn fixes, with stable ValheimCLI core and the optional adapter.
Empty-save and the bridge append/respawn scenario both require real saves and
separate owned processes. The bridge fixture freezes 443 independently recorded
piece transforms across five generated zones; a different fixture may use another
count.

The first game runs exposed boundaries that local doubles did not model:
TCP readiness preceded Console initialization, the fixture copier omitted empty
ScriptEngine directories, and a census method was private in the shipped game
despite being public in compile references. These are corrected. The census uses
an exact reflected signature and fails if that signature changes. Captured logs
must still be reviewed: a passing scenario is not proof that every mod in the
fixture has a clean startup or that all terrain baking succeeded.

For another Roads/game build, repeat this small gate with exact runtime and world
pins. Earlier standalone negative-control scripts reproduced the underlying mod bugs;
this runner's initial failures were runner/adapter failures, not substitutes for
those controls. Do not describe it as a completed cross-version control matrix.

The shared synthetic terrain unit model remains the bottom of the test pyramid.
These simulated sessions exercise orchestration and assertions, not Unity terrain,
RPCs or save encoding. The declared terrain fixture below calibrates the
writer/compiler boundary in game. Natural-input replay and stock-client collision
remain separate future checks.


## Shared lifecycle dependency

`OwnedServerSession`, `DirectServerProcess` and `RecordingTransport` live in
`Valheim.Testing.Game`. The Roads runner supplies
`roads.testing/session`; the library has no Roads dependency. Readiness and
observations remain in the optional Roads adapter, and fixture/scenario decisions
remain here. The extraction is locally tested and passed a dedicated empty-save repeat;
the same runner rejected the archived broken Roads build after reload. Bridge
acceptance is still the preceding Roads-local lifecycle run, not a repeated test
of the extracted binary.

`TotalRoadPoints` is not a persistence identity: generation adds planned path
points, bridge levelling does not update that counter, and loading totals all
stored grid entries (including overlap copies). The existing production serializer
round-trip test reproduces counter drift while serialized bytes and query results
remain unchanged. Do not add equality of that counter as a save assertion. The
native bridge run did not capture raw road bytes before/after, so its exact +160
cannot by itself prove full network identity either.

## Declared terrain calibration

`terrain-calibration` is a bounded third scenario. Use an already-empty saved
network and set `ROADS_TEST_TERRAIN_CALIBRATION=1` in the plan's environment.
The adapter also requires a runner session token, dedicated mode, no peers and
ungenerated/unloaded fixture zones (200,200) and (201,200). It is not a command
for an attached player's world.

The fixture creates a vanilla non-player square level modifier at height 64,
two real zone heightmaps and ghost terrain compilers. A 4 m road crosses their
shared edge. It applies targets 65, 65 again, 80 and 48. Earthwork noise, fill
spread and cut batter are disabled only during this synchronous fixture and
restored in `finally`; no frame yields occur while tuning is changed. No
production source changes or expected-height injection are involved.

The independently declared samples cover the centre, 2 m flat edge, 3 m
half-strength release, 4 m outer edge and an untouched 6 m control. Targets
beyond the compiler range must stop at 64 +/- 8. Every sample is read from the
heightmap **and its own real mesh collider**, including both copies of the zone
boundary. The runner requires all 100 identities and compares each layer within
2 cm. Responses and residuals are retained even when heights disagree.

The same declaration is exercised against the real Roads writer compiled with
the existing minimal game doubles. This calibrates that writer/compiler test
boundary, not the synthetic world's terrain generator, natural routing, stock
client replication or character support. Those remain distinct checks. The
platform is temporary and the scenario does not save it: its objects are destroyed
and its ghost compiler ZDOs are requested for destruction, then the runner stops
its disposable process. No game assets are redistributed by the fixture.

Measured on Valheim 1.0.16 dedicated: all 100 height and local-collider samples
matched exactly. A measurement-only adapter with the writer omitted failed 48
samples (the baseline and undeformed controls still passed). The command took
about 7 seconds after startup.

Two game boundaries were fixed while establishing this result: session permission
facts must report the ValheimCLI gate's explicit `Terminal.m_cheat` flag rather than
`IsCheatsEnabled()` (other mods can patch the latter), and the shipped private
`IsZoneGenerated` method requires an exact reflected lookup despite publicized
compile references. For fixture manifests use the target platform's native
relative paths; the current copier compares those path identities exactly.

## Persistent native terrain and ValheimCLI-only client

Two steps. `prepare-terrain` with scenario `terrain-persistence` and environment
`ROADS_TEST_PERSISTENT_TERRAIN=1` prepares a **new copy** of an empty saved network.
The once-per-process adapter picks two ungenerated Meadows zones without an existing
compiler and away from known location exterior footprints, and writes a
constant-height road across their boundary: width 4 by default, width 8 with the
dirt-fade paint profile (see below). The road core is written 1.5 m above the seam
ground but never below 31.5 m, so a player standing on it is dry (sea level is 30 m).
The search is bounded; not finding a site is a failed preparation, not a passed test.

It captures native pre-write vertices and paint and retains the real compiler ZDOs.
The independent runner calculates full blend through the half-width, half one metre
beyond, zero from two metres beyond and a +/-8 m delta limit, and the paint core,
fade and unpainted verge. Both boundary copies must agree, with at least one
meaningful terrain change. Paint samples sit 2 m either side of the seam: paint has
64 cells per zone and heights 65 vertices, so a paint point on the seam is ambiguous.
Preparation writes inputs, every residual (20 at width 4, 24 at width 8), a client
height plan with a support point on the road core (refused if it is not at least
1.5 m above the sea), a client paint plan, confirms a save and stops only its owned
process. Preparation is not client acceptance.

`run` on the prepared world, with a `client` section in the plan, is the acceptance
test. The client is an operator-launched game with ValheimCLI only: the plan pins its
plugins exactly and Roads and MWL as `absent`, names a disposable local character and
the variable in the client's own environment that holds the server password, and pins
the prepared client plans by SHA256. The runner then:

1. verifies the plans are unchanged, the client's menu pins, and turns the client's
   devcommands on (ValheimCLI refuses the session join without it);
2. joins, verifies the world pins, protects the player (god/ghost, flying off), waits
   until it has stood still for 3 s (a first join rides in on the Valkyrie, and the
   game drops a teleport within 2 s of a spawn), has the server teleport it once onto
   the support point and waits for the client's own observations to show it settled;
3. measures every prepared vertex on the heightmap and its own collider (15 at width 4,
   18 at width 8), 16 paint samples, and three stationary grounded readings;
4. confirms a server save, leaves, restarts only the owned server, rejoins and measures
   everything again.

Do not deploy the Roads adapter to the client. Back up the client install and any
character or account data the run could touch, and restore them afterwards.

Measured on Valheim 1.0.16 (29 Sep 2026), both profiles passed every step: all heights
and colliders matched exactly (15 paved, 18 dirt/fade), 16/16 paint samples, and the
player stood grounded at 31.5 m, before and after the save, restart and rejoin. An
earlier run's unchanged-ground negative expectation failed 8 of 15 points at the 5 cm
tolerance. This establishes terrain/paint persistence to a stock client and stationary
support on this small native-input fixture, not natural generator emulation, noisy
earthworks, pathfinding, walking usability or visual quality.

Client-side `cli_arrive` is refused by the game's achievement/cheat confirmation on a
client that is not an admin. The server-side `cli_teleport_peer` arranges arrival; the
client's own observations prove where it ended up. A teleport request or a ValheimCLI
transport success is not arrival evidence.

## Standalone testing library

The runner consumes the exact `Valheim.Testing.Game` pin listed under
[Quick start](#quick-start-and-migration) from [ValheimTesting](https://github.com/tvongaza/ValheimTesting), which in turn uses the pinned ValheimCLI transport `Valheim.Testing.Cli`. Both restore from NuGet.org once published; until then use the local feed. These are external test dependencies; ordinary mod releases have no dependency on them.

## Terrain, paint and walking follow-up

The local `TerrainPaintMatrixTests` runs eight real-writer cases: positive and
negative cross-slopes, fill/cut, +/-8m clamping, 2/4/8m widths, dirt and paved
regions, paint-only roads, pre-existing RGBA and ordinary-load preservation.
Every vertex across the road is declared: the core is levelled to the road height
(so a sloped core keeps no cross-fall unless the +/-8m limit binds), the vertex one
metre into the 2 m release moves half way, and the ground beyond is untouched.
The 8m case independently calculates fading-edge paint channels for one disc.
Noise, fill spread and cut batter are disabled in this analytic matrix. Their
separate unit tests still apply; this is not evidence of their native appearance.

The persistent two-zone fixture also carries paint. Before the road write, the adapter seeds and saves selected compiler entries with RGBA (0.2, 0.4, 0.6, 0.3), then reads the native quantized baseline. It invokes the exact private `TerrainComp.Save(bool)` signature, failing explicitly if the game changes that contract. For the default width-4 profile the runner independently expects eight core texels to change RGB, eight verge texels to stay unchanged and all sixteen to preserve alpha. Both sides of the zone seam are sampled. Site selection keeps both profiles 20 m clear of the 1000 +/- 100 m dirt-to-stone paint handover, so each sample's colour is unambiguous.

`ROADS_TEST_PAINT_PROFILE` selects the profile. It must be absent (paved, width 4) or exactly `dirt-fade` (width 8, inside the dirt region). Plan validation refuses any other value and a variable name that differs only in letter case; the adapter independently refuses any other value it receives. The runner requires the adapter's reported width and profile to match the plan and records the profile in the report provenance. The dirt-fade oracle uses 0.352 paint strength at 3 m (one road point, smoothstep fade from 2.4 m to 3.4 m) and preserves the saved alpha; its height samples add the 5 m half-strength release vertex on both sides of the seam.

Measured on Valheim 1.0.16, width 4: the server passed all 20 height/collider samples, all 16 paint samples and a confirmed save. A ValheimCLI-only client (Roads/MWL pinned absent) passed the same 16 paint samples, 15 unique height/collider samples and three stationary grounded observations, both before and after a confirmed save/server restart/rejoin. The unchanged-paint negative expectation failed exactly the eight painted samples; the eight untouched ones still passed. ValheimCLI core plus all four optional packs were used; no game-side test adapter was installed on the client.

Measured in a native run on 27 Sep 2026, dirt-fade: the 20 server height/collider samples of the original layout and all 16 paint samples passed; the ValheimCLI-only client matched all 16 paint samples before and after a confirmed save/restart/rejoin. The unchanged-input negative control fails the painted core and fade samples. The 5 m release samples, the profile validation and the width-4 handover avoidance were added after that run and still need a native repeat. The profile is bounded, requires an owned isolated session and an empty network, and restores the normal terrain tuning.

The game runs exposed a command-pack build regression: private game-member calls require the same Mono verification metadata as ValheimCLI core. ValheimCLI #40 preserves that setting. A responsive ValheimCLI was also observed while MWL still held world load; require the adapter's complete session state before joining.

Remaining native follow-ups are ordinary noise/fill-spread/cut-batter appearance and human walking. The analytic fixtures do not establish those. A person should walk a short switchback, hillside POI approach and bridge/ford in both directions; use WalkingReview to retain checkpoints and support observations alongside a separate human verdict. Small cosmetic bumps are judgement calls. PaintCheck and WalkingReview are external library examples, not production Roads commands, and telemetry cannot replace that verdict.

## Shared synthetic fixtures

The grade and search-margin tests use shared planes and composed causeways through `TerrainTestWorld`. `SharedZoneWriterTests` uses shared multi-zone height/paint state with the real Roads writer; both zone orders must meet at the edge, preserve an earlier verge edit and paint, and remain unchanged on repeat application. The [shared fixture guide](https://github.com/tvongaza/ValheimTesting/blob/main/docs/shared-world.md) documents grid layout and limitations. The Roads compiler double and all Roads-specific assertions remain here. These tests do not simulate native saves.

## Session capabilities and strict pins

`Valheim.Testing.Game` provides reusable bounded capture/replay and typed session capabilities. See [TerrainCapture](https://github.com/tvongaza/ValheimTesting/blob/main/examples/TerrainCapture/README.md) and [SessionControl](https://github.com/tvongaza/ValheimTesting/blob/main/examples/SessionControl/README.md). The existing Roads scenarios do not silently switch from their measured text-command paths; native acceptance of the new capability surface is separate.

The shared actor strictly checks the pinned environment before every actor command, including expected-refusal probes. RunPlan emits `cli_expect --strict`; the actor prevents callers from weakening that default and catches drift after startup. The pre-world owned-process identity probe is read-only and is followed by strict verification before any scenario action. The native run on 27 Sep 2026 (Game preview.8; preview.10 has the same API and preview.11 adds only `ServerLaunch`) kept per-command strict preflights and the persistent dispatch guard enabled. A deliberately wrong core hash refused logout before execution, leaving the client in the same world.
