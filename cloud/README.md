# Cabinet Cloud

Bedrock pebble `cabinet`, served at https://cabinet.mc2.onl. Implements
[the client contract](../docs/cloud-api.md). Data is SQLite plus a private Bedrock
bucket; no external services or runtime packages beyond Bedrock are needed.

## Deploy

Bun is required (installed for the current user during implementation). The local
Bedrock checkout is `<bedrock checkout>`. Its CLI was
registered with `bun link` in `packages\bedrock`. Ensure
`%USERPROFILE%\.bun\bin` is on PATH; open a new terminal after installation.

Dependencies use a checkout-relative `file:` path, as Bedrock source scaffolds do.
The deployment daemon substitutes its own Bedrock package. If moving this repo
to another machine, update the local dependency path to that machine's checkout.

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

## Create users

1. Open https://cabinet.mc2.onl/admin and sign in with a Bedrock creator's Google
   account. An ordinary signed-in Google user cannot access this page.
2. Enter the user's name and choose **Create user**.
3. Copy the connect link immediately. It is displayed only in that admin response;
   subsequent views cannot recover it. Share the link with its intended user.
4. The user opens the link, then **Open in Cabinet**, or copies the link into
   Cabinet. `/connect` supports English and French based on browser language.

The admin lists users, last-seen times and per-app achievements/stats counts,
total playtime and save sizes/device names. **Revoke token** disables access.
**Regenerate token** revokes every prior active token and displays a new link once;
user data is retained. Tokens are `cab_` plus 32 cryptographically random bytes
in base64url; only SHA-256 hashes are persisted. Raw tokens are never logged.

`access: "public"` still receives the daemon's HMAC-verified identity in `ctx.user`.
Admin authorization compares that identity's email against the daemon-supplied
`BEDROCK_CREATORS` list, case-insensitively. Missing identity redirects to Bedrock
login; signed-in non-creators get 403. There is no development auth bypass.
Admin POSTs require the exact public Origin, also enforced by Bedrock's daemon.
The UI is minimal server-rendered HTML, avoiding a frontend framework dependency.

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
absent, signs in using the local login flow, creates a user via `/admin`, pushes
three progress updates, uploads/downloads a ZIP, verifies a 409 conflict and
revokes the token. It stops the dev server afterward and redacts credentials.
It expects the configured local creator to be `owner@example.test`, uses port
3137 and deliberately retains development data (the test user is revoked).
On Windows, it connects to 127.0.0.1 with the actual Cabinet Host header, exercising
daemon routing without relying on wildcard localhost DNS resolution.

`bun test` covers merge rules, validation, creator auth, token hashing/revocation/
regeneration, user isolation, saves, and blocked generic bucket endpoints.

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
