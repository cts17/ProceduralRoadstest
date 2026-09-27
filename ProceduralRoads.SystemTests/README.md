# Roads system-test pilot

Optional development tooling, outside the normal Roads release. Unit tests and
simulated runner tests work on Mac without Valheim or Steam. Real game execution
is a separate, bounded Windows dedicated-server gate. No scheduled runs or remote
station control are built in.

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
process and copied save directory the runner owns. A fresh CLI actor then verifies
strict world UID and full plugin MD5 pins. Restart waits for the old owned process
to exit, generates a new token, reconnects and repeats verification. It never
kills by process name or takes ownership of an attached game.

The runner starts the executable directly with argument tokens, without a shell.
Its working directory is the copied runtime. Supply `-batchmode`, `-nographics`
and exactly one `-savedir {world}`. A launch wrapper that detaches the game is not
supported; the PID check refuses it. The game must respect its save-root argument
at startup: the identity check detects a wrong save root before test mutations,
but cannot undo writes the game made while booting. This is one reason `run` is
Windows-only in the pilot; Mac's client save-root behavior is not assumed.

Stop uses only the owned process tree. Scenarios confirm saves before their planned
restart; failure teardown may terminate an unsaved **disposable copy**. A failed
stop prevents another launch and is reported as a test failure. Ctrl-C requests cancellation and owned-process cleanup; an in-flight CLI call
can take up to its command timeout to return. A forced kill of the runner cannot
guarantee cleanup; retained per-boot PID records help the operator inspect it.
Copies are always
retained; remove them manually only after confirming the owned processes are gone.

Station claim, manifest/backup discipline, free game/CLI ports, and operator
coordination remain prerequisites. The local port check is not a machine-wide
reservation. Use private test server settings, keep players out, and do not run
against a production/runtime directory. No account or Steam session is needed for
the dedicated gate, but the station operator must still coordinate its use.

## Build and fast checks

```sh
dotnet test ProceduralRoads.SystemTests.Tests/ProceduralRoads.SystemTests.Tests.csproj
dotnet publish ProceduralRoads.SystemTests/ProceduralRoads.SystemTests.csproj -c Release -p:UseAppHost=false -o /new/runner-output
dotnet build ProceduralRoads.TestAdapter/ProceduralRoads.TestAdapter.csproj -c Release \
  -p:CliDll=/path/to/valheimCLI.dll -p:RoadsDll=/path/to/ProceduralRoads.dll
```

The adapter build imports compile references only, has embedded symbols and no
deployment target. Install the stable CLI core and this adapter in the **staged**
runtime; do not copy a second CLI/API assembly alongside it. Both normally belong
in plugins for this persistence gate. ScriptEngine adapter reload is a separate
check. The production Roads/MWL releases have no testing-framework dependency.

Development project references point at the sibling `cli-test-framework` checkout
and will become pinned package references when the shared packages are released.

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

Include all other loaded plugins in strict pins too. Use the isolated runtime's
CLI config to select the same port. `{world}`, `{runtime}` and `{port}` expand in
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
# Optional Windows fixture preparation; never counts as acceptance.
dotnet ProceduralRoads.SystemTests.dll prepare-bridge bridge-plan.json C:/runs/prepare-unique
# Windows only; use a different new output directory.
dotnet ProceduralRoads.SystemTests.dll run plan.json C:/runs/roads-test-unique
```

A validation pass has report mode `validate`; it is not game validation. Inspect
actual command/reply records and game warnings/errors before calling a `run` pass
accepted. The tool does not yet classify BepInEx warnings automatically.

## Validation and remaining boundaries

The local suite has 47 Roads runner/scenario tests, plus 49 shared-toolkit tests.
The lifecycle tests moved with their implementation into the toolkit; they were
not dropped. A
Windows dedicated-server campaign also exercised the runner against the previously
accepted Roads save/respawn fixes, with stable CLI core and the optional adapter.
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
pins. Earlier private-script negative controls reproduced the underlying mod bugs;
this runner's initial failures were runner/adapter failures, not substitutes for
those controls. Do not describe it as a completed cross-version control matrix.

The shared synthetic terrain unit model remains the bottom of the test pyramid.
These simulated sessions exercise orchestration and assertions, not Unity terrain,
RPCs or save encoding. The declared terrain fixture below now calibrates the
writer/compiler boundary in game. Natural-input replay and stock-client collision
remain separate future checks.


## Shared lifecycle dependency

`OwnedServerSession`, `DirectServerProcess` and `RecordingTransport` now live in
`Valheim.Testing.Game` preview 2. The Roads runner supplies
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
station pilot did not capture raw road bytes before/after, so its exact +160
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
about 7 seconds after startup. The local suite is now 47 runner/scenario tests,
plus 766 production-writer tests on both runtimes.

Two game boundaries were fixed while establishing this result: session permission
facts must report the CLI gate's explicit `Terminal.m_cheat` flag rather than
`IsCheatsEnabled()` (other mods can patch the latter), and the shipped private
`IsZoneGenerated` method requires an exact reflected lookup despite publicized
compile references. For fixture manifests use the target platform's native
relative paths; the current copier compares those path identities exactly.

## Persistent native terrain and CLI-only client

`prepare-terrain` with scenario `terrain-persistence` and environment
`ROADS_TEST_PERSISTENT_TERRAIN=1` prepares a **new copy** of an empty saved network.
The once-per-process adapter picks two ungenerated dry Meadows zones without an
existing compiler and away from known location exterior footprints. The search
is bounded; not finding a site is a failed preparation, not a passed test.

Unlike the temporary platform calibration, it captures native pre-write vertices
and retains the real compiler ZDOs. A width-4, constant-height road crosses the
boundary. The independent runner calculates full blend through 2 m, half at 3 m,
zero from 4 m and a +/-8 m delta limit. Tuning is temporarily the same analytic
profile as the earlier calibration. Both boundary copies must agree, with at
least one meaningful terrain change. It writes inputs, all 20 residuals and a
client height plan, confirms a save and stops only its owned process.

Preparation is not client acceptance. On a separate disposable copy of that save,
join a CLI-only client with Roads and MWL explicitly pinned `absent`. Arrange
arrival, then use the toolkit's `examples/ClientSurfaceCheck`: 15 unique native
vertices, each heightmap and its own collider, plus three stationary grounded
player observations. Save, restart and rejoin, then repeat the **same** plan.
Do not deploy the Roads adapter to that client. Character backup, protection,
account coordination and restoration are the operator's responsibility.

Measured on Valheim 1.0.16: all 15 client heights and colliders matched exactly
before and after a confirmed save/server restart/rejoin. The player was grounded
at the target, speed zero, not flying or attached. The unchanged-ground negative
expectation failed 8 of 15 points at the 5 cm tolerance. This establishes this
small native-input replication fixture, not natural generator emulation, paint,
noisy earthworks, pathfinding or a human walking/visual pass.

Client-side `cli_arrive` was refused by the game's separate achievement/cheat
confirmation in this run. The existing server-side `cli_teleport_peer` arranged
arrival; the client's own observations proved where it ended up. A teleport
request or a CLI transport success is not arrival evidence.

Package consumers can pass `-p:ToolkitPackageVersion=0.1.0-preview.3` to the unit
and system projects, with the matching local NuGet feed. This removes the sibling
checkout dependency. Omit that property for source development. Packages are
external test-driver libraries; the production mod has no new dependency.
