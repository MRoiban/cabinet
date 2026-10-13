# Join Game and session media report

## Join Game prototype

The previous prototype reported that Steam caches the app ID per process: two IPC pipes in one process cannot read Spacewar (480) and publish presence for a different app. Its Windows SteamID struct-return ABI issue was corrected; the implementation now derives the local SteamID from the active account for presence reads instead of calling that struct-return method. These findings were supplied with the integration task; this integration did not repeat the live lobby experiment.

Each real-app shadow starts a separate hidden `--presence-source` process connected to 480. The reader pumps callbacks and publishes an atomic timestamped JSON snapshot of `connect`, group, group size and status. If `connect` is absent but the local friend-game record exposes a Spacewar lobby, it builds `+connect_lobby <lobby-id>`. The real-app shadow calls `SetRichPresence` with those values. Snapshots older than five seconds are cleared; mirroring requires the detected running set to contain exactly the shadow's own real app. Ambiguous simultaneous games are not attributed.

The reader observes the shadow PID and start time, Steam lifetime, and a named stop event. Graceful disposal signals it, waits three seconds, then kills a reader that has not exited. It cleans snapshot files on ordinary exit. The shadow itself observes the tray PID/start time and stop event; the tray stops overdue shadow finalization after twenty seconds. A reader blocked indefinitely in a native Steam call after an abrupt shadow kill remains an uncertainty: there is no Windows job-object containment. A dead reader is not restarted during the same shadow session; its expired snapshot clears mirrored presence.

### Required friend test

1. Run one OnlineFix game, enter or host a lobby, and check that Steam shows the real game.
2. Have a friend right-click your Steam entry and choose **Join Game**.
3. Confirm that the friend's game opens or joins the correct lobby. Their game must accept `+connect_lobby <id>` on its command line or handle the in-game join callback.
4. Leave the lobby, stop the game, and check that Join Game disappears and no Cabinet helper remains. Repeat with an already-running friend's game.

Steam accepting rich presence does not prove that the friend can join. Cross-app callback forwarding is unavailable: the real-app shadow logs callbacks 333/337 but cannot deliver them to OnlineFix. Correct installed-game launch routing and each game's lobby handling still need the friend test. The prototype also reported that `TriggerScreenshot` on a windowless shadow produced no screenshot; Cabinet uses deferred re-filing instead.

## Screenshots and recordings

Cabinet records observed OnlineFix running intervals, scoped to the active Steam account, in `onlinefix-sessions.tsv`. Intervals do not extrapolate beyond observed ticks; gaps over twenty seconds start a new interval. Media must fit entirely in exactly one interval. Overlaps, unobserved times, uploaded items, missing source files and destination conflicts are skipped.

Re-filing runs only when no detected OnlineFix game, Steam process or Cabinet Steam helper is active. Steam is checked again during preparation and before publication. This process check cannot exclude a Steam startup in the final check/write gap; an exclusive Steam-wide coordination mechanism is not available.

For indexed local Spacewar screenshots, Cabinet backs up the index and original image/thumbnail files before copying to the real app's remote screenshot folder. It updates game ID, paths and index placement, atomically replaces the VDF with another index backup, and deletes source images only after committing the index. Original images remain under the screenshot store's `cabinet-backup` folder. Paths with traversal, alternate data streams or redirected parents are refused. A crash can leave duplicate copies; existing destinations are skipped rather than overwritten.

For supported local `clip_480_*` clips, the complete clip duration must fit the observed interval. Cabinet stages a complete copy, backs up metadata before rewriting it, preserves unknown protobuf fields semantically, rewrites known app IDs and identifiers, and refuses unknown additional protobuf files or mixed app IDs. It publishes the staged folder and moves the full original into the recording store's `cabinet-backup` folder. Redirected source trees are refused. Background recordings outside exported clips, unfamiliar formats and already-uploaded clips are left alone. Recording formats are inferred and still need testing with newly captured screenshots and exported clips; verify Steam opens the re-filed media before relying on it.

## Controller layouts

Spacewar (480) is now an explicitly disposable controller slot for OnlineFix games. Cabinet prepares it from the most recently launched detected real game. Physical controller activation remains a required user test.

### Storage evidence (current client)

- Current selections are text VDFs at `steamapps/common/Steam Controller Configs/<account>/config/configset_<device-or-serial>.vdf`, under `controller_config/<appid>`. Observed selection keys are `autosave`, `template` and `workshop`. Active custom autosaves are in `config/<appid>/<device-or-serial>.vdf`. Unselected named exports are not treated as effective layouts.
- Cached Workshop layouts are in `steamapps/workshop/content/241100/<published-file-id>/*_legacy.bin`; the inspected files contain text `controller_mappings` despite their extension. Templates are in `controller_base/templates`. Missing or ambiguous Workshop caches and unknown selections are logged and skipped rather than downloaded or guessed.
- This client's `appcache/appinfo.vdf` uses indexed-key format `0x07564429`. Its read-only `config/steamcontrollerconfigdetails` records official defaults by controller type. Slay the Spire 2 (2868840) records Xbox One, Switch Pro, PS5 and PS4 official layouts; all four were cached. It had no custom autosave or explicit selection. Its template index is 1 (gamepad). Cabinet also reads preceding format `0x07564428`; unknown formats are refused.
- Steam Input overrides are in `userdata/<account>/config/localconfig.vdf`, at **`UserLocalConfigStore/apps/<appid>/UseSteamControllerConfig`**, separate from the playtime `Software/Valve/Steam/apps` subtree. Cabinet copies the recorded value exactly; an absent source override removes 480's explicit override. The original 480 value was `2`.
- Original 480 selections included a PS4 gamepad template and an Xbox 360 Workshop layout. The October 11 controller UI log also loaded Spacewar's official Workshop layouts. `controller.txt` showed `game_action ship_controls ...` binding failures under 480. The existing `controller_config/game_actions_480.vdf` is left untouched.
- The historical `userdata/<account>/241100/remote` store contains three hash-named layouts and personalization, with no per-app `controller_config/<appid>` tree. The newer documented `userdata/<account>/config/controller_configs/apps` store is absent. Cabinet writes the currently observed store, not these historical files; unknown newer storage requires another investigation.

### Live reload and layout compatibility

A read-only Cabinet IPC consumer successfully connected as 480. A slot write several seconds later succeeded on disk while that session and Steam were running. Neither controller log emitted a new layout-loading record; `controller_ui.txt` had no new records since the last controller disconnect on October 11. **Without a connected controller or game overlay, this is inconclusive: it neither establishes activation nor proves reload fails.** No Steam restart or real-game launch was performed during the probe.

Existing logs show in-memory configuration cache hits, and Steam can rewrite `localconfig.vdf`. The documented `steam://controllerconfig/<appid>/<layout>` link opens a configuration for inspection/application; it is not documented as a silent cache reload. Cabinet does not invoke it automatically or restart Steam. It retains the last-played preset and reapplies it after observing Steam closed, including after a Cabinet restart. A subsequent Steam startup provides a file-read boundary; same-session launches/reconnects remain unverified. A Steam shutdown/restart between Cabinet's five-second ticks may be missed.

Legacy gamepad, keyboard and mouse layouts can be copied unchanged. Native actions depend on the game's action manifest. All four official 2868840 layouts contain `game_action Controls ...` bindings, despite metadata saying `use_action_block=false`. Cabinet inspects actual bindings/native `actions` blocks, logs incompatibility and substitutes the matching standard gamepad template when available; otherwise it skips that layout. This fallback does not recreate native actions and depends on the game accepting emulated gamepad events. Its overlay title is **Gamepad**, rather than the official layout title. Spacewar's action manifest is not replaced. See Valve's [action manifest documentation](https://partner.steamgames.com/doc/features/steam_controller/action_manifest_file) and [legacy binding documentation](https://partner.steamgames.com/doc/features/steam_controller/legacy_mode).

### Implementation and restoration

`ControllerLayouts.cs` runs before real-app shadow creation. It prioritizes active custom autosaves, then recorded per-device selections, then locally cached official/default layouts. Physical-device selections have independent 480 serial slots. Previous-game stale selections/autosaves are removed. Process start times choose the latest detected game when launches overlap; saved layouts are rechecked at most once a minute during a running session.

Only 480 layout files, 480 selection nodes and 480's Steam Input override are modified. Shared VDF edits retain unrelated text/settings; malformed input and redirected paths are refused. Existing Steam files are backed up before every write/removal in `DataDir/controller-file-backups` (one rolling previous-file copy per file). Writes use atomic replacement and refuse a file changed during preparation. A narrow final compare/replace race with Steam remains because no client-wide lock exists. Every modification is logged; balloons appear only after game-triggered changes.

Exactly one original-slot snapshot is retained in `DataDir/controller-480-original.json`, captured before modification and extended only for newly encountered accounts/files. Existing original entries never change. It stores original 480 nodes and layout bytes/absence; restore does not overwrite unrelated portions of shared files. `Cabinet-cli.exe --controller-restore` restores those entries, clears the last-game preset and retains the snapshot. Stop OnlineFix games first. For persistent restoration, close Steam, run the command, then reopen Steam. Explicit restoration while Steam runs restores disk contents and logs that the client may retain/rewrite its cached values.

### Validation and required controller test

C# 5 builds passed. Twenty-four temporary-store checks covered template/custom/community selection, independent physical-device slots, latest-launch selection, stale cleanup, recorded override states, Steam-shutdown preparation, idempotence, backups, restoration after multiple games, preservation of later unrelated edits, and malformed/path input rejection. Run them with `tests/run-controller-checks.cmd`. A temporary real 2868840 Xbox One custom autosave with a distinctive title copied byte-for-byte into 480 while Steam ran; its source stayed unchanged and was then removed. All 35 original snapshot nodes/files matched after restoration, and the test preset was cleared. Steam was neither closed nor restarted, so persistent client-cache restoration remains unverified. No temporary real-game custom layout remains. Installation ran after detection reported no OnlineFix game; the installed executable hash matched the build, exactly one installed tray process was present, and its initial ticks logged no controller errors. The separate build-location installer was displaying the requested installation MessageBox.

1. Connect a controller and launch Slay the Spire 2 through its normal OnlineFix executable. Wait at least ten seconds for Cabinet detection.
2. Open **Steam overlay → Controller settings**. With no custom layout saved, expect **Gamepad** because this game's official layouts use native actions. Test actual buttons, sticks and menus; the title alone does not prove usable input.
3. For the custom-layout test, save a recognizable **legacy gamepad or keyboard/mouse** layout for real app 2868840 in Steam. Relaunch OnlineFix and check its recognizable title/bindings under Spacewar. Native-action custom layouts intentionally fall back.
4. If the old layout remains, stop the game, close Steam yourself, wait at least ten seconds with Cabinet running, reopen Steam and relaunch. Check the mirrored Steam Input enablement setting too. Controller reconnect can also be tried, but reload on reconnect is unverified.
5. Repeat with another OnlineFix game and a different recognizable legacy layout, then return to the first game. Verify that the layout follows the most recently launched game.
6. To restore Spacewar, stop the game, close Steam, run `Cabinet-cli.exe --controller-restore`, then reopen Steam and inspect the original selections.

## Integration safety fixes and validation

- Read-only game detectors now scan without calling the ledger-counting tick. Shadow helpers and installation checks cannot overwrite the tray ledger.
- Join mirroring requires the running app to match the shadow, not merely a running count of one.
- Presence readers check parent/Steam/stop state before connecting and clean snapshots on exit.
- Screenshot image backups precede destination writes; media rejects redirected paths and repeats Steam checks before committing.
- Cloud receipts and pending provenance are atomically persisted in the playtime ledger; VDF write intent recovers interrupted applies.

Validation: C# 5 builds passed; all 20 cloud fake-server checks passed; 16 temporary-ledger checks covered legacy loading, receipt replay, persistence failure rollback/retry, Steam-running apply refusal, interrupted-write recovery, verification and requeue provenance. Eight temporary screenshot checks verified Steam-running refusal, copying, original preservation, image backups, index rewriting and collision refusal. The Workshop CLI performed its default read-only scan. The installed tray was restarted with `--update` after a running-game check reported none, exercising initial and timer ticks without an installation dialog. The installed binary hash matched the build; exactly one tray Cabinet process and no CLI/shadow/presence helpers remained. The new log entries contained only Workshop waiting/already-linked information, with no integration errors. Cloud settings had no token, and disconnected ticks were silent. No production cloud token was used.
