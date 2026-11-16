# Cabinet Cloud API (contract between `cloud/` pebble and `Cloud.cs` client)

Cabinet Cloud is a Bedrock pebble (`cloud/`, name `cabinet`) served at `https://cabinet.<domain>`.
It carries saves, achievements, stats, playtime and real-game presence for OnlineFix games across a user's PCs.

## Accounts and tokens

- The server owner (a Bedrock creator, signed in with Google) opens `https://cabinet.<domain>/admin`,
  creates a user by name and gets a **connect link** (and a pairing code) shown once:
  `https://cabinet.<domain>/connect#<token>`.
- Tokens: `cab_` + 32 random bytes base64url. Stored hashed (SHA-256). Revocable from `/admin`.
- `/connect` (public page, part of the pebble's web app) reads the token from the fragment (never sent to the
  server by the browser, only used as the bearer for `/api/*` calls from that page) and shows
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
- **Save (format 2, current clients)**: versioned archives per app (`/archive`, see [save-sync.md](save-sync.md)).
  A write must name the content it replaces (`base`); otherwise `409` with the current version. The same content
  again returns the current version. An archive with no files is refused (`422 empty_archive`) unless it is an
  explicit `intent=delete` tombstone. The newest 10 versions are kept. The client keeps a local backup before
  replacing saves.
- **Save (legacy, older clients)**: one snapshot per app (zip of the game's OnlineFix `Saves` folder). Newer
  `updatedAt` (unix ms) wins; an older upload returns `409` with the current meta. A ZIP with no file entries is
  refused with `422 {"error":"empty_archive"}` unless `allowEmpty=1`; a body that is not a ZIP is `400`.

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
| PUT | `/api/apps/<appid>/archive?deviceId=&sha256=&contentHash=&files=&bytes=&base=[&intent=delete]` | format-2 zip (`application/zip`, max 90 MiB) | `ArchiveMeta`; `409 { current: ArchiveMeta }`; `422 { error: "empty_archive" }`; `404` for a delete with nothing stored |
| GET | `/api/apps/<appid>/archive[?id=<versionId>]` | | zip bytes, `ETag: <sha256>`, `X-Cabinet-Archive-Id`; `404` if none |
| GET | `/api/apps/<appid>/archives` | | `{ "versions": ArchiveMeta[] }`, newest first |
| GET | `/api/app-names?ids=<appid>,…` | | `{ "names": { "<appid>": name } }` (known names only, up to 500 ids) |

```
AppState = {
  achievements: { [apiName]: unixSeconds },
  stats: { [name]: number }, statsUpdatedAt: unixMs | 0,
  playtime: { [deviceId]: minutes }, playtimeTotal: minutes,
  save: SaveMeta | null,
  archive: ArchiveMeta | null          // newest format-2 version (may be a tombstone)
}
SaveMeta = { sha256, size, updatedAt: unixMs, deviceId, deviceName, empty }   // empty: no file entries, never restore
ArchiveMeta = { id, sha256, contentHash, size, files, bytes, createdAt: unixMs, deviceId, deviceName, deleted }
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
- State files (`cloud.json`, `cloud-state.json`, `playtime.txt`, `save-state.json`, …) are written to a temp file
  that is flushed to disk before `File.Replace`, so a power loss cannot leave them zero-filled. A file that is
  still unreadable at start is recovered from the newest parseable leftover of an interrupted write
  (`<name>~RF*.TMP`, `<name>.<guid>.tmp`), else renamed `<name>.corrupt-<UTC>` and started afresh (`StateFile.cs`):
  - a fresh `cloud-state.json` is marked `rebaseline`: the next round records the totals other devices already
    have on the server as applied instead of adding them to Steam again. Minutes that had reached the server but
    were not yet applied here are lost; that is preferred to double counting;
  - a fresh `cloud.json` starts disconnected (connect again) and keeps the `deviceId` that `cloud-state.json`
    remembers, and the same account's journal is kept on reconnect;
  - a fresh `playtime.txt` records `#ledger-reset|<id>`; the cloud journal then counts the remote minutes it had
    applied as imported (`importedBase`), so they are never pushed back as this PC's own playtime.

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

## Steam Cloud file store (CloudRedirect)

On Linux, CloudRedirect's `cabinet` provider keeps redirected Steam Cloud data
in the token-user's own namespace on this server. Every Cabinet user has an
isolated store: the bearer token selects the user, and a path never crosses users.
Inside a user's store, CloudRedirect's own layout applies
(`<accountId>/<appId>/blobs/<file>`, manifests and metadata), so one Cabinet user
can sync several Steam accounts on several devices.

A path is 1–1024 bytes of UTF-8 after decoding, split on `/` into nonempty
segments of at most 255 bytes. Segments `.` and `..`, backslashes and control
characters (U+0000–001F, U+007F) are rejected with `400`. In URLs, each segment is
percent-encoded separately and joined with `/`. Files are opaque bytes up to
90 MiB; each user may store at most 10 GiB (`507 {"error":"quota_exceeded"}`).

| Method | Path | Body | Response |
|---|---|---|---|
| PUT | `/api/cloud/files/<path>` | raw bytes (`application/octet-stream`) | `FileInfo` |
| GET | `/api/cloud/files/<path>` | | bytes, `ETag: "<sha256>"`, `Last-Modified`; `404` if absent |
| HEAD | `/api/cloud/files/<path>` | | `200` with `ETag`/`Content-Length`, or `404`; no body |
| DELETE | `/api/cloud/files/<path>` | | `{ "ok": true }`, also when absent |
| GET | `/api/cloud/list?prefix=&cursor=` | | `{ "files": FileInfo[], "next": string \| null }` |
| GET | `/api/cloud/usage` | | `{ "files", "bytes", "quota" }` |
| GET | `/api/cloud/apps` | | `{ "apps": { "<appId>": { "files", "bytes" } } }` grouped by the path's second segment |

```
FileInfo = { path, size, sha256, modifiedTime: unixSeconds }
```

- PUT replaces the file unconditionally (last write wins). An optional
  `X-Content-SHA256: <hex>` header is verified against the body; a mismatch is `400`.
- `list` returns files whose path starts with `prefix` (plain string prefix,
  not percent-encoded beyond normal query encoding; empty lists everything), sorted
  by path, at most 1000 per page. Pass `next` back as `cursor` until it is `null`.
- Writes (PUT/DELETE) need `Origin: https://cabinet.<domain>` like every other
  Cabinet write. Errors use the usual `{ "error": message }` envelope.
- The admin page shows each user's file count and bytes. Revoking a token keeps
  the files; deleting them is a manual admin action.

Provider configuration on the Deck (`~/.config/CloudRedirect/`): `config.json`
selects `"provider": "cabinet"`; the credential document (`tokens_cabinet.json` by
default) is `{ "server": "https://cabinet.<domain>", "token": "cab_…" }`. The
`cloud_redirect_cli cabinet-connect <connect link>` command writes both.

## Lua config cache (LuaTools)

LuaTools users connected to a server share the game configs (manifest packages: one `.lua` plus
Steam `.manifest` files) they download. After a successful install from a normal source, LuaTools
uploads the package it installed; when anyone on the server later adds that game, LuaTools checks
the server first and installs the cached copy if it matches the game's current Steam build. Unlike
saves, these rows are **shared across all token-users of the server**: anyone can read any app's
cached package, and any user's newer upload replaces it.

| Method | Path | Body | Response |
|---|---|---|---|
| PUT | `/api/apps/<appid>/lua?sha256=&source=&deviceId=` | the package zip (`application/zip`, max 90 MiB) | `LuaMeta` |
| GET | `/api/apps/<appid>/lua/meta` | | `LuaMeta`; `404 {"error":"not_found"}` if none |
| GET | `/api/apps/<appid>/lua[?sha256=<hex>]` | | zip bytes, `ETag: <sha256>`; `404` if none (or that version is no longer kept) |

```
LuaMeta = { appid, sha256, size, pins: { "<depotId>": "<manifestId>" }, source, uploadedAt: unixMs, uploader }
```

- `uploader` is the uploading user's display name. Their user id and device stay private (the device
  is stored for the admin's benefit only). A package the server fetched itself through the
  [Hubcap relay](#hubcap-relay-luatools) shows `"uploader": "Cabinet Cloud"` and
  `"source": "Hubcap (Cabinet)"`.
- `pins` are read by the server from the package's lua, never taken from the request, so meta always
  describes the bytes: every ACTIVE, full-line `setManifestid(<depot>, "<manifest>"[, size])` (double
  or single quotes, as LuaTools' install policy allows), sorted by depot. Commented-out lines and
  trailing `-- …` comments never count, so a pin hidden in a comment cannot vouch for a different
  active pin. LuaTools reads the package with the same rule.
- `source` is the source the uploader downloaded from (1–64 characters, no control characters).
  `deviceId` follows the save rules; the stored device name comes from that user's progress pushes.
- The same SHA-256 as the current copy is a no-op: `200` with the current `LuaMeta` (nothing changes,
  not even `uploadedAt`). Any other valid upload becomes current. The newest **3** versions per app
  are kept; `GET …/lua?sha256=` can fetch a kept older one (so a reader can download exactly the
  version whose meta it checked, even if someone uploads in between).
- Validation (`400 {"error": message}`): `<appid>` digits only; `Content-Type: application/zip`;
  body 1 byte–90 MiB whose SHA-256 equals `sha256`; a real ZIP (no ZIP64, encryption, multi-disk);
  1–4096 entries, safe relative paths (no `..`, absolute or drive paths, reserved Windows names,
  duplicates), no entry expanding beyond max(1 MiB, 200 × its compressed size), 256 MiB expanded
  in total; files are exactly one `.lua` (max 2 MiB, UTF-8, only `addappid`/`addtoken`/
  `setManifestid` lines and comments, no block strings, an ACTIVE `addappid(<appid>…)` (a commented
  one is not enough here, stricter than LuaTools' install policy), no depot pinned more than once),
  `<depot>_<manifest>.manifest` files (max 64 MiB each) and `.txt`/`.md` documentation (max 256 KiB).
  These mirror LuaTools' `ManifestContentPolicy`; the client still validates every package again
  before installing, since a cached copy comes from another user.
- **Size limit:** 90 MiB like saves (one Bedrock bucket ceiling, and Cabinet's existing response
  cap). Real packages are a lua and a few compressed manifests, well under 10 MiB; the
  client's own expanded ceiling (256 MiB) can only be approached by packages no source serves.
- Writes need `Origin: https://cabinet.<domain>` like every other write.

Client rules (LuaTools): only when connected to Cabinet. A package is **current** when every active pin
and every `<depot>_<manifest>.manifest` file it carries names the manifest Steam ships now, for each
depot Steam reports a public manifest for (a lua without pins follows Steam's latest build, so it is
current unless a manifest file it carries is stale); without Steam's depot info nothing is current.
- Lookup: `meta` first (3 s timeout), then the bytes (a deadline of 10 s plus 1 s per 256 KiB,
  at most 120 s). The copy is used only if its SHA-256 matches `meta` and the package itself is
  current; it is installed through the same validation as any other source. A server failure or
  timeout makes the client skip the cache for 5 minutes.
- Upload: automatic, in the background, after a successful install from a normal source, only when
  that package is current (so an older build never replaces a newer copy). Never for a copy that
  came from the cache, nor for sources fetched with the user's own key (Hubcap, or any source the
  catalogue marks as needing a key). Failures are only logged.

## Hubcap relay (LuaTools)

The server owner keeps a pool of Hubcap API keys in `/admin`. Every token-user can then get a
game's Hubcap Lua config through the server without a key of their own: the server queues the
request, fetches the game from Hubcap **once**, stores it as the app's current
[Lua cache](#lua-config-cache-luatools) entry and serves everyone from there. Five users asking for
the same game cost one Hubcap download. There is no per-user cap; the keys' own daily limits are the
only limit.

| Method | Path | Response |
|---|---|---|
| GET | `/api/hubcap` | `{ "enabled": bool }`: at least one key in the pool can make requests |
| POST | `/api/apps/<appid>/hubcap[?stale=<sha256>]` | `RelayStatus` (enqueue, join, or answer at once) |
| GET | `/api/apps/<appid>/hubcap` | `RelayStatus` of the app's active job, else its latest job (kept a day); `404 {"error":"not_found"}` if none |

```
RelayStatus = { state: "queued" | "running" | "ready" | "failed" | "upstream_not_newer",
                position?: n,        // queued only: 1 = runs next
                error?: RelayError,  // failed only
                meta?: LuaMeta }     // ready / upstream_not_newer: the Lua cache version to download
RelayError = no_manifest | quota_exhausted | key_expired | key_rejected | relay_disabled
           | hubcap_unavailable | invalid_package
```

Bearer auth like every `/api/*` route; the POST needs `Origin` like every write; `<appid>` digits
only and `stale` 64 hex characters, else `400`. Clients learn nothing about the keys: not how many,
not their usage. Download a `ready` package with `GET /api/apps/<appid>/lua?sha256=<meta.sha256>`.

**Request rules** (the server can't see Steam's current build, so the client decides freshness):

- The client sends `stale=<sha256>` when it found the cached copy stale, and nothing when the cache
  has no entry for the app.
- An active (queued or running) job for the app is **joined**: one job per app, every requester is
  recorded (the admin shows their names), and the answer is that job's status.
- A current cache entry whose SHA-256 differs from `stale` (someone already refreshed it, or the
  client had none) answers `ready` with its meta at once, without a Hubcap call.
- An app is downloaded from Hubcap at most once per **30 minutes**. Inside that window a request
  answers `upstream_not_newer` with the current meta (Hubcap may still be on the old build; the
  client may fall back to the cached copy), or the earlier failure when nothing is cached. A recent
  `no_manifest` is likewise repeated without asking Hubcap again.
- When no key in the pool is usable the request fails at once (`quota_exhausted`, `key_expired`,
  `key_rejected`, or `relay_disabled` with no keys or no server key secret) and no job is queued.
  When stats are due (older than 5 minutes), one shared re-check runs on the worker; requests wait
  for it at most about 2 seconds and otherwise answer from what is known, so the relay resumes on
  its own when a key may make requests again.
- Otherwise a job is queued (`{ "state": "queued", "position": n }`).

**The worker** runs one job at a time, oldest first, outside the database write queue:

1. The same rules again (a newer copy or a recent download since queuing settles the job for free).
2. Free status check (`GET /api/v1/status/<appid>`), trying every key that isn't disabled until one
   answers. No manifest: `no_manifest`, no download spent. A `429` marks that key exhausted, a `5xx`
   or timeout moves on; only when every key failed does the job fail (`hubcap_unavailable`, or the
   pool's code).
3. Pick the usable key with the most remaining daily quota (cached stats, re-read at most every
   5 minutes and after each download) and download `GET /api/v1/manifest/<appid>`. A `429` marks
   that key exhausted (until its stats allow requests again) and the same job retries with the next
   usable key; a `401` marks it disabled; a `5xx` or timeout before Hubcap answered tries the next
   key. When no key is left the job fails with the pool's code (or `hubcap_unavailable`). A `200`
   counts as a download (30-minute rule) as soon as it arrives, even if the body then fails.
   Content-Disposition is ignored (titles are not safe file names).
   A `403` on a status or manifest call is not trusted on its own (a challenge page blocks every key
   alike): the key's stats are asked, and the key is disabled only if stats also refuse it.
4. Validate with the **same check as the Lua cache upload** (`inspectLua` in `lua.ts`); a refused
   package fails `invalid_package`.
5. Compare its build (active pins plus `<depot>_<manifest>.manifest` names) with the current cache
   entry: the same SHA-256 or the same build in other bytes is `upstream_not_newer` and is **not**
   stored (Hubcap is still on the cached build). Otherwise store it as the app's current cache version
   (`source: "Hubcap (Cabinet)"`, uploader the server, retention 3 versions like any upload): `ready`.

A job left `running` by a stopped server is queued again at startup. Finished jobs are pruned after
a day. Keys are encrypted at rest with the pebble secret `HUBCAP_KEY_SECRET` and never appear in
responses, errors, admin views (only `smm_…` plus the last 4 characters) or logs; upstream failures
are recorded as the codes above.

Client rules (LuaTools, provider **Cabinet**): the provider is listed only while Cabinet Cloud is
connected and `GET /api/hubcap` says enabled; it needs no key of the user. A current cached copy is
installed directly. A cached copy whose build can't be checked (Steam's depot info unavailable) is
never reported stale: the user is asked whether to install it unchecked (agent adds don't). Otherwise
the client POSTs (with `stale=` only when its copy is verifiably stale), polls `GET` about every 2 s
showing the queue position, re-checks the returned copy against Steam, downloads exactly
`meta.sha256`, verifies it and installs it through the normal validation. On `upstream_not_newer`, or
a `ready` copy that is still behind Steam, it asks whether to install that copy anyway. A cached
copy the client can't download is a server error, not a stale copy. Cancelling stops the client only
(the job continues for others); the client gives up after about 10 minutes. A package that came
from Cabinet is never uploaded back.

## Pairing codes

A pairing code lets a device connect by typing 12 characters instead of
pasting a connect link. The server is implied: clients default to
`https://cabinet.mc2.onl` and only ask for a server under an advanced option.

- Format: 12 characters from Crockford base32 (`0-9 A-H J K M N P-T V-Z`, no
  `I L O U`), shown as `XXXX-XXXX-XXXX` (60 random bits). Input is case-insensitive;
  spaces and `-` are ignored, `O` reads as `0` and `I`/`L` as `1`.
- A code belongs to one user, is valid for **15 minutes**, works **once**, and is
  stored only as a SHA-256 hash. Creating a new code does not revoke older ones.
- Redeeming mints a **new token for that device**; the user's other tokens stay
  active. **New connect link** in `/admin` still revokes every token of the user,
  including paired devices.

| Method | Path | Auth | Body | Response |
|---|---|---|---|---|
| POST | `/api/pairing-codes` | Bearer | | `{ "code": "XXXX-XXXX-XXXX", "expiresAt": unixMs }` |
| POST | `/api/pair` | none | `{ "code", "deviceName"? }` | `{ "token": "cab_…", "user": { "id", "name" } }` |

`/api/pair` errors: malformed code → `400`; unknown, used or expired code →
`401 {"error":"invalid_code"}`; more than 30 failed attempts in the last minute
(server-wide) → `429 {"error":"too_many_attempts"}`. Like every write it needs
`Origin: https://cabinet.<domain>`.

Codes are created by the owner in `/admin` (**Pairing code** in a user's sheet, shown once)
or by the user on `/connect#<token>` (**Get a pairing code**), so a connect link
opened on a phone can pair a Deck without copying the link to it.
On the Deck, `cloud_redirect_cli cabinet-connect <code> [--server <origin>]`
redeems a code; the Decky sidebar and Lumen's Cloud Saves tab call it.
