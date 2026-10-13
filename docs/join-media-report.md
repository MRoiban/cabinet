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

The prior investigation found no safe app-ID layout swap. Cabinet does not copy or change controller layouts: the game/overlay continues using its OnlineFix identity, while the independent shadow provides real-app presence and stats. A real-game layout appearing in the overlay remains unresolved.

## Integration safety fixes and validation

- Read-only game detectors now scan without calling the ledger-counting tick. Shadow helpers and installation checks cannot overwrite the tray ledger.
- Join mirroring requires the running app to match the shadow, not merely a running count of one.
- Presence readers check parent/Steam/stop state before connecting and clean snapshots on exit.
- Screenshot image backups precede destination writes; media rejects redirected paths and repeats Steam checks before committing.
- Cloud receipts and pending provenance are atomically persisted in the playtime ledger; VDF write intent recovers interrupted applies.

Validation: C# 5 builds passed; all 20 cloud fake-server checks passed; 16 temporary-ledger checks covered legacy loading, receipt replay, persistence failure rollback/retry, Steam-running apply refusal, interrupted-write recovery, verification and requeue provenance. Eight temporary screenshot checks verified Steam-running refusal, copying, original preservation, image backups, index rewriting and collision refusal. The Workshop CLI performed its default read-only scan. The installed tray was restarted with `--update` after a running-game check reported none, exercising initial and timer ticks without an installation dialog. The installed binary hash matched the build; exactly one tray Cabinet process and no CLI/shadow/presence helpers remained. The new log entries contained only Workshop waiting/already-linked information, with no integration errors. Cloud settings had no token, and disconnected ticks were silent. No production cloud token was used.
