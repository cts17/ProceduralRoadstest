# Roads system-test pilot

Optional test-side library. No game files are included and it does not launch a game automatically.
References the incubating toolkit in a sibling `cli-test-framework` checkout; these become preview package references when published.

`RoadsScenarios.EmptyNetworkReplacesOld` and `BridgeAppendSurvivesRespawn` execute the two established regressions. Supply an actor verified with exact world/plugin pins, a disposable owned server session, a one-shot mutation, and a restart callback that waits for the old process to exit and verifies the replacement. The framework deliberately does not call private station scripts or stop processes by name.

Before using empty-network: copy a world containing roads, configure zero selected islands on the COPY, start it, and pass `() => server.Execute("road_generate")`. Use `cli_save`, never console `save`; a successful confirmed save is required before restart.

Before using bridge-respawn: prepare generated zones around a short manual crossing and capture a fixture expectation independent of the actual result being checked. Include all marked bridge pieces in each selected zone, including pre-existing ones. Pass a one-shot `road_path` command as the append. The scenario refuses an already-drained queue rather than passing without exercising the race. Actual marked ZDOs are compared by prefab and full transform (5 cm/1 degree), including multiplicity, before and after restart. It does not assume all worlds contain 443 pieces.

Install the stable CLI core normally. Install `ProceduralRoads.TestAdapter.dll` only in test sessions, normally or via ScriptEngine. Do not ship it with the Roads production release. Existing save/append/respawn commands remain the mutation path; the adapter adds only bounded observations.

Status: compiled and locally checked; these NEW automated scenarios have not yet run in Valheim. Earlier private-script runs are evidence for the underlying bug, not a pass of this new runner.
