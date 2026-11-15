# Cabinet Cloud

Bedrock pebble `cabinet`, served at https://cabinet.mc2.onl. Implements
[the client contract](../docs/cloud-api.md). Data is SQLite plus a private Bedrock
bucket. The web UI (`web/`) is React 19 with Onyx through `@bedrock/ui`; the only
external calls are Steam CDN artwork in the browser and an hourly Steam store name lookup.

## Deploy

Bun is required (installed for the current user during implementation). The local
Bedrock checkout is `<bedrock checkout>`. Its CLI was
registered with `bun link` in `packages\bedrock`. Ensure
`%USERPROFILE%\.bun\bin` is on PATH; open a new terminal after installation.

Dependencies use `link:` packages (`bedrock`, `@bedrock/ui`), so the repo works from
any checkout once `bun link` is registered in the Bedrock checkout; `bedrock doctor`
checks this. React 19 (`react`, `react-dom`) must stay on matching versions.

```powershell
cd <cabinet repo>\cloud
bun install
bun run typecheck
bun test
bedrock login mc2.onl
bedrock deploy
```

Login opens the owner's browser for Google sign-in. No saved deployment
credentials were present during implementation, so deployment was not attempted.
Migrations, including Drizzle metadata, are included and apply automatically.
After deployment, an anonymous `GET https://cabinet.mc2.onl/api/me` must return
`401 {"error":"unauthorized"}`.

## Web UI and admin

`pebble.ts` sets `web: "./web/index.html"`: the runtime serves the React app for every
path that is not an API route, so `/admin` and `/connect#cab_…` are client routes.

1. Open https://cabinet.mc2.onl/admin and sign in with a Bedrock creator's Google
   account. Other signed-in accounts see "This account can't manage Cabinet".
2. **New user** asks for a name, then shows a connect link and a 15-minute pairing
   code once. Nothing can show them again; create new ones from the user instead.
3. The user opens the link: `/connect` shows **Get a pairing code** for a Steam Deck,
   **Open in Cabinet** (`cabinet://connect?server=…&token=…`), the link to copy, their
   games and their Steam Cloud usage, in English or French (browser default).

The admin lists users with status (active, not connected yet, revoked), active
connections, games, playtime and Steam Cloud usage, plus who is playing now. A row
opens the user's sheet: games with saves and cloud files, **Pairing code**, **New
connect link** (revokes every token, including paired devices, then shows a new link
once), **Revoke access** and **Delete cloud files…**. User data is kept on revocation.

In a user's sheet, games sort by last played, last saved, recently added or name (the choice is
remembered per browser). `app_activity` records when each game first reached Cabinet and when it
was last played, from presence reports, progress pushes with new playtime or achievements, and
CloudRedirect uploads; a migration backfilled older games from achievement, save and cloud-file
times (added never earlier than the user's creation). Clicking a game shows its achievements:
`GET /api/admin/achievements?user=<id>&appid=<id>` (creator session only) merges the user's unlock
times with Steam's keyless `IPlayerService/GetGameAchievements` list, cached a week in
`achievement_schemas`; a failed refresh keeps the last list, and without one only unlocks show.
Locked hidden achievements stay collapsed until **Reveal**.

Admin data comes from Bedrock functions in `admin.ts` (`adminOverview`, `adminUser`,
`createUser`, `newConnectLink`, `pairingCode`, `revokeAccess`, `deleteCloudFiles`).
Each checks the daemon-signed `ctx.user` email against `BEDROCK_CREATORS`
(case-insensitively): anonymous calls get 401, other accounts 403 `FORBIDDEN`.
Mutations go through the daemon's exact-Origin check. Tokens are `cab_` plus 32
random bytes in base64url; only SHA-256 hashes are stored and raw tokens are never logged.

The connect page reads the token from the URL fragment and sends it only as a
bearer to this origin's `/api/*`, with cookies omitted (the daemon drops
`Authorization` when a Bedrock session cookie is present). `web/index.html` sets a
CSP allowing images from `cdn.cloudflare.steamstatic.com` only. In `bedrock dev`,
Bun's injected hot-reload script is blocked by that CSP (a console error, no
hot reload); production bundles have no inline scripts.

Game names: presence reports store real names in `app_names`; the `appNames` job
(hourly, detached) looks up a few missing ids per run on the Steam store and
tolerates failures. Unknown names show as "App <id>" with the Steam header art.

The save bucket uses an internal, per-handler capability: generic Bedrock upload,
download, list and delete endpoints cannot access it, even with a creator session
or Cabinet bearer token. Files have null Bedrock ownership so generic owner-delete
authorization cannot bypass this policy. Save operations participate in Bedrock's
SQL/storage transactions; replacement deletes the prior file after commit.

## Local development and verification

```powershell
cd <cabinet repo>\cloud
bedrock dev --port 3137
```

Open http://cabinet.localhost:3137/admin. Before starting dev, create
`.bedrock\daemon\config.json` with a local creator email:

```json
{ "domain": "localhost", "creators": ["owner@example.test"], "port": 3137 }
```

The Bedrock email-picker lets you sign in as that creator, with no Google
credentials. The empty default creator list intentionally authorizes nobody.
Local data, sessions, and config are under ignored `.bedrock/`; tests use temporary
directories rather than the real Bedrock home.

`bun run scripts/dev-e2e.ts` starts **bedrock dev**, creates the above config when
absent, signs in using the local login flow, creates a user with the `createUser` function, pushes
three progress updates, uploads/downloads a ZIP, verifies a 409 conflict and
revokes the token. It stops the dev server afterward and redacts credentials.
It expects the configured local creator to be `owner@example.test`, uses port
3137 and deliberately retains development data (the test user is revoked).
On Windows, it connects to 127.0.0.1 with the actual Cabinet Host header, exercising
daemon routing without relying on wildcard localhost DNS resolution.

`bun test` covers merge rules, validation, the creator gate on every admin function,
each admin mutation, token hashing/revocation/new links, user isolation, saves,
game names, blocked generic bucket endpoints and that `/admin` and `/connect` serve
the web app.

## Contract clarifications and client integration

- **Origin on writes:** Bedrock rejects POST/PUT requests missing
  `Origin: https://cabinet.mc2.onl` before the pebble runs. The existing API
  contract mentions only Authorization. Cabinet must send its configured server
  origin on every write, including progress and save uploads. A missing Origin
  receives Bedrock's 403 envelope rather than the Cabinet 401/400 envelope.
- **Save deviceName:** the upload query has deviceId but no deviceName. The
  server uses the latest name supplied in a progress push from that user/device;
  before any progress push it uses deviceId as the display name. Push progress
  before uploading saves, as the contract's client sequence describes.
- **Equal save timestamps:** the contract says newer wins and older returns 409,
  but does not specify equality. This implementation returns 409 with current meta
  for equal timestamps too; it never replaces a snapshot without a newer time.
- **Stats pairing:** stats and statsUpdatedAt are optional together. Supplying
  only one is rejected with 400; both omitted leaves stored stats unchanged.
- **Size units:** the 90 MB limit uses Bedrock's convention of 90 MiB
  (94,371,840 bytes). Larger bodies may be rejected with 413 by Bun before the
  handler; handler-level size/shape/hash failures return 400. Uploads are opaque
  ZIP bytes with required application/zip MIME and a verified SHA-256, not
  extracted or interpreted by the server.

ETag is the bare SHA-256 string exactly as specified by the Cabinet contract.

## Presence

Authenticated PUT/DELETE `/api/presence` update only the token-user's account rows.
GET shares real-game names and Steam account IDs across the server's users;
reports expire after three minutes and stale rows are pruned. The `presence`
table and migration `0001_melodic_network` are included. See the client contract
for validation, privacy scope, Origin headers, and Steam's optional localhost
UI debugging port. Tests cover heartbeat upserts, cross-user reads, isolated
deletion, TTL pruning, authentication and malformed reports.

## Steam Cloud file store

`/api/cloud/*` is the per-user store behind CloudRedirect's `cabinet` provider on
Linux (see the client contract). Rows live in `cloud_files`; bytes live in the
internal `cloud` bucket, which generic Bedrock file endpoints cannot reach, like
`saves`. The routes are detached: bodies are read and hashed outside the
serialized queue, and only the database and local-storage step takes a write slot.
Each user may store 10 GiB (`CLOUD_QUOTA` in `cloud.ts`). The admin shows file
count and bytes per user and per game (CloudRedirect paths are `<accountId>/<appId>/…`);
**Delete cloud files…** removes that user's store but keeps
the user and token. `bun test` covers path validation, isolation, replacement,
HEAD, pagination, the blocked bucket and admin deletion.

## Pairing codes

`/admin` → user → **Pairing code** shows a one-time `XXXX-XXXX-XXXX` code for a user (also
available to the user from their `/connect#…` page). `POST /api/pair` trades it for
a new per-device token within 15 minutes; codes are hashed in `pairing_codes` and
work once. A server-wide brake answers 429 after 30 failed attempts per minute.
**New connect link** still revokes every token, including paired devices.

## Versioned saves (format 2)

`/api/apps/:appid/archive(s)` (`archive.ts`) stores every save version as a row in
`save_archives` (migration `20261024101820_save_archives`) and a blob in the internal
`archives` bucket, which generic Bedrock file endpoints cannot reach, like `saves`.
The server validates the archive without extracting saves (`zip.ts`): the first entry
must be `cabinet-save.json` (stored, or deflated for .NET Framework clients), the
content hash is recomputed from its file list, the file entries must match the list
exactly and every path must be a safe relative Windows path. Empty archives are
refused (`422 empty_archive`) unless `intent=delete` records a tombstone; writes
must name the content they replace (`base`) or get `409`. The newest 10 versions per
user and game are kept; older rows and blobs are deleted after the commit. The routes
are detached: bodies are read and checked outside the write queue.

The legacy `/save` route refuses ZIPs with no file entries (`422 empty_archive`,
override with `allowEmpty=1`) and non-ZIP bodies (400). `SaveMeta.empty` marks
stored legacy rows of 22 bytes (an archive with no entries): before this change the
client uploaded such ZIPs for empty OnlineFix `Saves` folders. See
[`docs/save-sync.md`](../docs/save-sync.md) for the format, the client rules and
the deploy order. `bun test` covers uploads, history, idempotent retries, 409s,
tombstones, retention with blob deletion, malformed archives and paths, user
isolation and the legacy refusal.

## Lua config cache

`/api/apps/:appid/lua` (`lua.ts`) keeps LuaTools' game configs (a manifest package: one `.lua`,
Steam `.manifest` files, small docs) **shared by every user of the server**, in table `lua_cache`
(migration `20261115141108_lua_cache`) and the internal `lua` bucket, which generic Bedrock file
endpoints cannot reach. The server checks the ZIP against the limits of LuaTools'
`ManifestContentPolicy` without extracting it, reads only the lua (to check that it declares the app
and to derive the depot→manifest pins shown in meta) and keeps the newest 3 versions per app. The
same package again is a no-op. Meta shows only the uploader's display name. The routes are detached
like the archive routes. See [the client contract](../docs/cloud-api.md#lua-config-cache-luatools).
`bun test` covers sharing across users, the no-op, replacement and retention with blob deletion,
pin parsing and malformed packages.

## Hubcap relay

`hubcap.ts` lets every Cabinet user get Hubcap Lua configs through the owner's own Hubcap API keys
(LuaTools' **Cabinet** provider). See [the client contract](../docs/cloud-api.md#hubcap-relay-luatools)
for the routes and rules.

**Keys** (`/admin` → **Hubcap relay**): add several, each with an optional label; edit a label,
replace a key or remove it. The key field is write-only: after saving, the admin shows only
`smm_…` and the last 4 characters, with per-key status (usable, limit reached, rejected or expired),
today's usage and limit, expiry and the last error. **Check keys** re-reads every key's stats now
(`POST /api/admin/hubcap/check`, creator session only); otherwise stats are re-read at most every
5 minutes and after each download. Keys are stored in the `hubcap_keys` table (migration
`20261115161651_hubcap_relay`), not in Bedrock secrets: secrets are set through the daemon's
management API or CLI with a deploy token and reach the pebble only as environment variables at the
next (re)start, so a pebble cannot write its own secret at runtime, and a list of keys with labels
and per-key state needs rows anyway. Clients only learn `{ enabled }`.

**Keys are encrypted at rest** (`hubcap-crypto.ts`): AES-256-GCM with a random IV per save, plus an
HMAC-SHA256 digest (unique column) so a duplicate is refused without decrypting anything; only the
last 4 characters are stored in the clear, for the mask. Both subkeys are derived (HKDF-SHA256) from
one pebble secret, `HUBCAP_KEY_SECRET`: 32 random bytes, base64. Database backups therefore hold
only ciphertext; the secret lives in `$BEDROCK_HOME/pebbles/cabinet/secrets.json`, outside data
backups (back it up separately, or re-enter the keys after losing it). **Set it before the first
deploy that includes the relay**, from the machine where you ran `bedrock login mc2.onl`:

```sh
# Linux/macOS: generate 32 random bytes and pipe them in (the value never touches argv)
openssl rand -base64 32 | tr -d '\n' | bedrock secrets set cabinet HUBCAP_KEY_SECRET
```

```powershell
# Windows PowerShell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) | bedrock secrets set cabinet HUBCAP_KEY_SECRET
```

`bedrock secrets set` reads the value from stdin (or a hidden prompt if you run it alone and paste)
and can be staged before the first deploy; `bedrock secrets ls cabinet` lists it without the value.
Secrets reach the pebble only when it starts, so an already-deployed pebble needs a restart: add
`--restart` to the command (it requires a deployed pebble). Leading/trailing whitespace is ignored.
Without a valid secret nothing crashes: the relay reports `{ enabled: false }`, requests fail with
`relay_disabled`, and `/admin` shows **Server secret not set** (or **invalid**) and refuses to save
keys. Changing the secret makes stored keys unreadable: they show **Can’t decrypt** and are skipped
until the old secret is back or the keys are entered again. The secret is never logged.

**Queue**: `hubcap_jobs` (one job per app while queued or running, FIFO by `seq`) and
`hubcap_requesters` (who asked; names shown in the admin under the job). The worker is an
**exclusive Bedrock service** (`hubcapRelay`): exactly one runs, also across a deploy handoff, so jobs
run one at a time. Network calls (status, stats, download) happen outside the database write queue;
only the short bookkeeping and the final store take write slots. At startup a job left `running`
is queued again; finished jobs are pruned after a day. Requests wake the worker; it also polls every
10 seconds. The service keeps the pebble awake (like the hourly `appNames` job already did).

The downloaded package goes through `inspectLua` (the Lua cache upload's own validation) and is
stored with `storeLua` (the same retention) as source `Hubcap (Cabinet)` with no user
(`lua_cache.user_id` is null; meta shows `Cabinet Cloud`). Upstream failures are recorded as short
codes; no exception text (which could quote the manifest URL and so the key) is logged or stored.

Before storing, the package's build (active pins plus `<depot>_<manifest>.manifest` file names) is
compared with the current cached copy: the same build in other bytes ends `upstream_not_newer` and
is not stored, so a still-stale Hubcap build never replaces a good entry or reaches clients as
`ready`.

`bun test` (`hubcap.test.ts`, with a fake Hubcap) covers joining and one download per app, FIFO,
restart re-queueing, the 30-minute rule, the stale-SHA rule, the same-build rule, `no_manifest` and
quota paths that spend no download, a broken body still counting as a download, validation shared
with the upload route, choosing the key with the most quota, rotation on 429 (status and download)
and on status failures, 403s confirmed by stats before a key is disabled, disabled keys, the
all-exhausted fast fail and its single-flight, bounded recovery, encryption at rest and the
missing/invalid/changed secret, creator-only key management, and that no key or secret appears in
any response or log line.
