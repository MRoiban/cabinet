# Cabinet Cloud API (contract between `cloud/` pebble and `Cloud.cs` client)

Cabinet Cloud is a Bedrock pebble (`cloud/`, name `cabinet`) served at `https://cabinet.<domain>`.
It carries saves, achievements, stats, playtime and real-game presence for OnlineFix games across a user's PCs.

## Accounts and tokens

- The server owner (a Bedrock creator, signed in with Google) opens `https://cabinet.<domain>/admin`,
  creates a user by name and gets a **connect link** shown once:
  `https://cabinet.<domain>/connect#<token>`.
- Tokens: `cab_` + 32 random bytes base64url. Stored hashed (SHA-256). Revocable from `/admin`.
- `/connect` (public page) reads the token from the fragment (never sent to the server by the browser) and shows
  an **Open in Cabinet** button linking `cabinet://connect?server=<url-encoded origin>&token=<token>`,
  plus the raw link to paste into Cabinet manually.
- Cabinet registers the `cabinet:` URL protocol (HKCU\Software\Classes\cabinet) so the button connects in one click.
- Every API call: `Authorization: Bearer <token>`. Missing/invalid/revoked → `401 {"error":"unauthorized"}`.
- The pebble's Bedrock `access` is `"public"`; the API authenticates by bearer token itself. `/admin` requires a
  signed-in Bedrock creator.

## Merge rules (server-side, so any device can push in any order)

- **Achievements**: union. `{ "<API_NAME>": <unix seconds unlocked> }`; on conflict keep the earliest time. Never removed.
- **Stats**: `{ "<name>": number }` replaced as a whole when the pushed `statsUpdatedAt` (unix ms) is newer.
- **Playtime**: per device, `playtime[deviceId] = max(stored, pushed)` where pushed is that device's own
  total minutes for the app. Total = sum over devices.
- **Save**: one current snapshot per app (zip of the game's OnlineFix `Saves` folder). Newer `updatedAt` (unix ms)
  wins; an older upload returns `409` with the current meta. The client keeps a local backup before replacing saves.

## Endpoints (JSON unless noted)

| Method | Path | Body | Response |
|---|---|---|---|
| PUT | `/api/presence` | `{ accountId, appId, name }` | `{ accountId, appId, name, updatedAt }` |
| DELETE | `/api/presence?accountId=` | | `{ "ok": true }` |
| GET | `/api/presence` | | `{ "players": [{ accountId, appId, name, updatedAt }] }` |
| GET | `/api/me` | | `{ "user": { "id", "name" } }` |
| GET | `/api/state` | | `{ "apps": { "<appid>": AppState } }` |
| POST | `/api/apps/<appid>/progress` | `{ deviceId, deviceName, achievements, stats?, statsUpdatedAt?, playtimeMinutes }` | merged `AppState` |
| PUT | `/api/apps/<appid>/save?deviceId=&updatedAt=&sha256=` | raw zip bytes (`application/zip`, max 90 MB) | `SaveMeta`, or `409 { current: SaveMeta }` |
| GET | `/api/apps/<appid>/save` | | zip bytes, `ETag: <sha256>`; `404` if none |

```
AppState = {
  achievements: { [apiName]: unixSeconds },
  stats: { [name]: number }, statsUpdatedAt: unixMs | 0,
  playtime: { [deviceId]: minutes }, playtimeTotal: minutes,
  save: SaveMeta | null
}
SaveMeta = { sha256, size, updatedAt: unixMs, deviceId, deviceName }
```

`<appid>` is digits only (`^\d{1,10}$`), else `400`. `deviceId` is a stable random id per Cabinet install.

## Client behaviour (Cabinet)

- Settings in `%LOCALAPPDATA%\Cabinet\cloud.json`: `{ server, token, deviceId, deviceName }` (token protected with DPAPI).
- After each game session ends (and every 30 min while idle): push progress for apps that changed; upload the save
  zip if its hash changed; then GET `/api/state` and apply anything newer from other devices:
  - achievements/stats → into OnlineFix `Stats\Achievements.ini` / `Stats.ini` (so the game sees them) and Steam's cache
    via the existing sync;
  - playtime from other devices → added to Steam's playtime via the existing ledger (never double-counted:
    remember per remote device how many minutes were already applied);
  - newer save from another device → only when that game is not running; zip the current local saves to
    `%LOCALAPPDATA%\Cabinet\backup\saves\<appid>\<timestamp>.zip` first, then replace.
- Save/progress synchronization never runs while a game is running for that app. Network failures are silent (log only), retried next round.

## Real-game presence

`accountId` is a numeric positive Steam **32-bit account ID** (1 through
4294967295), not a SteamID64. `appId` is a string of 1?10 ASCII digits; `name` is
1?100 characters, nonblank, with no control characters (U+0000?001F or
U+007F?009F). Invalid bodies or delete account IDs return 400.

PUT upserts the bearer-token user's row for that account ID and sets `updatedAt`
to server Unix milliseconds. DELETE removes only that user's row and succeeds
when absent. GET shares fresh presence across **all token-users on this server**,
without exposing their cloud user IDs. Rows older than three minutes are ignored
and pruned on GET. If multiple users report one Steam account, GET returns its
freshest report. All calls require bearer authentication; writes follow the same
Bedrock Origin policy as existing API writes. Cabinet sends its configured server
origin on every request, including DELETE.

The client publishes immediately when its detected running game/account changes,
then about every 60 seconds, and deletes on stop/disconnect/account changes as a
best effort. Its name resolver is shared with the Games menu (cached schema,
then Steam manifest, then app ID); controls are removed and names truncated to
100 characters before sharing. Several concurrent games resolve to the first
app ID in ordinal order. Crashes are covered by server TTL.

On a background worker, Cabinet fetches presence about every 60 seconds while
Steam is running and cloud is connected, excludes its own account and stale
reports, and sends serialized JSON to Steam's `SharedJSContext` through CDP.
The idempotent script changes only friends' `m_strGameExtraInfo` when their
`m_unGamePlayedAppID` is 480. A single prototype wrapper reapplies names after
`UpdateFromMessage`; a five-second local timer covers other local changes.
Previously written names are restored only if the current value still equals
Cabinet's value. Missing labels, leaving 480, disconnect, and expiration clear
Cabinet's changes. App IDs, game IDs, and join metadata are untouched. The icon
remains Spacewar's; no fragile icon lookup is installed.

The tray's opt-in creates `.cef-enable-remote-debugging` in Steam's folder; Steam
must be restarted by the user. This opens a localhost-only UI debugging port
(8080). HTTP discovery, websocket connection and receive operations have bounded
timeouts, and every round rediscovers the target so Steam restarts need no manual
reconnection. No extra resident process is started.
