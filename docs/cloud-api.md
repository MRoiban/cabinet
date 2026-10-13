# Cabinet Cloud API (contract between `cloud/` pebble and `Cloud.cs` client)

Cabinet Cloud is a Bedrock pebble (`cloud/`, name `cabinet`) served at `https://cabinet.<domain>`.
It carries saves, achievements, stats and playtime for OnlineFix games across a user's PCs.

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
- Never runs while a game is running for that app. Network failures are silent (log only), retried next round.
