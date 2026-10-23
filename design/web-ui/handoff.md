# Cabinet Cloud web UI — handoff

Design artifact: `design/web-ui/mockup.html` (sha256 prefix `e7dd1f703fd5b579`). Use the state switcher at the top, or `?s=admin|user|create|adminempty|portal|invalid`.
Design status: selected (designer-approved direction; not yet user-approved).

## Visual requirements

- Build with Onyx through `@bedrock/ui` (React 19). The mockup imitates Onyx with its dark Flexoki tokens; the real components are the source of their own styling. Map mockup parts to Onyx: `.btn` → `Button` (primary / secondary / ghost / destructive variants), `.badge` → `Badge`, `.card` → `Card` or `Panel`, `.meter` → `Meter`, table → `Table`, user sheet → `Sheet` (side, right, ~420px), new-user result → `Dialog`, delete/revoke confirmation → `AlertDialog`, `.empty` → `EmptyState`, `.input` → `Input`/`Field`, language switch → `ToggleGroup`, icons → `Icon` (codicons: `cloud`, `add`, `copy`, `key`, `refresh`, `circle-slash`, `trash`, `close`, `device-desktop`, `game`/`device-mobile`, `link`, `warning`). Toasts (`Toast`) confirm copy and actions.
- Dark theme, comfortable density; set `data-theme="dark"` and `data-density="comfortable"` on `<html>` (portals inherit from it). Paint `#100f0f` before the bundle loads.
- Type scale: page title 22px/600, section titles 15px/600, body 13px, secondary 12px in `--onyx-text-description`. Pairing codes in monospace, 26px (22px in compact cards), letter-spacing .12em.
- Spacing: page padding 28px (16px under 820px), 24px between sections, 12px grid gaps, 20px card padding. Max content width 1120px (admin), 880px (connect page).
- Steam artwork: `https://cdn.cloudflare.steamstatic.com/steam/apps/<appid>/header.jpg`, 460:215, rounded 4px; show a neutral placeholder on error. CSP must allow `img-src` for that host.

## Responsive and state requirements

- Widths to check: 1400, 1024, 820, 390.
- Admin: tiles 4 → 2 columns under 820px; table hides Last seen, Games, Playtime, Steam Cloud under 820px; the sheet becomes full-width (Onyx Sheet handles it). Row click and Enter open the sheet; Escape closes it; the selected row is highlighted.
- Admin states: overview with data, user sheet, new-user result (link + code shown once), empty (no users), loading (skeleton rows), error (alert with retry), signed-out (Bedrock sign-in), signed-in non-creator ("This account can't manage Cabinet").
- Pairing code: countdown from `expiresAt`, a meter draining, then "Expired" with "Get a new code".
- Connect page: Steam Deck card first (Get a pairing code → code + countdown + 3 steps), then Windows PC (Open in Cabinet → `cabinet://connect?server=…&token=…`, copy link, PowerShell install line). Games grid `auto-fill minmax(230px,1fr)`, 2 columns under 520px. Storage meter. States: loading, valid, no games yet (EmptyState), invalid/revoked link, network error. EN/FR toggle defaulting to the browser language.

## Integration targets (inspected)

- Pebble: `cloud/pebble.ts`, `access: "public"`. Add `web: "./web/index.html"`; the runtime serves the HTML at `/*` unless a route matches (`bedrock/src/runtime/index.ts:70`), so `/admin` and `/connect#<token>` become client routes. **Remove** the HTML handlers `GET /admin`, `POST /admin`, `GET /connect`, `GET /` and delete `admin.ts`'s HTML rendering and `pages.ts` once replaced. `/connect#<token>` and `cabinet://connect?server=&token=` must keep working: the Windows client and existing links depend on them (`docs/cloud-api.md`).
- Admin data moves to Bedrock functions gated by `creator(ctx)` from `auth.ts` (throw `BedrockError("FORBIDDEN", …)` otherwise):
  - query `adminOverview` → `{ users: [{ id, name, createdAt, lastSeen, status: "active"|"unconnected"|"revoked", connections (active tokens), games, achievements, playtimeMinutes, cloud: { files, bytes } }], totals, playing: [{ userName, appId, name }] , quota }` (reuse `allStates`, `usage`, presence rows fresher than 3 minutes).
  - query `adminUser({ id })` → user + `games: [{ appid, name|null, achievements, playtimeMinutes, save: SaveMeta|null }]` + cloud usage.
  - mutations: `createUser({ name })` → `{ user, link, code, expiresAt }`; `newConnectLink({ id })` → `{ link }` (revokes all tokens, like today's regenerate; say so in its AlertDialog); `pairingCode({ id })` → `{ code, expiresAt }` (`createCode` in `pairing.ts`); `revokeAccess({ id })`; `deleteCloudFiles({ id })` → `{ deleted }` (`deleteAll` in `cloud.ts`).
  - Use `bedrock/react` (`useQuery`, `useMutation`, `useUser`) and `SignInGate`/`UserMenu` from `@bedrock/ui`. Links are built from `location.origin`.
- Connect page uses plain `fetch` with `Authorization: Bearer <token from location.hash>` against existing routes: `GET /api/me`, `GET /api/state`, `GET /api/cloud/usage`, `POST /api/pairing-codes`. Never send the token anywhere else; clear nothing from the URL (the user may bookmark it).
- Game names: add an `app_names(appid PK, name, updated_at)` table filled from presence reports (they carry real names) and by a detached hourly job that fetches missing ids from `https://store.steampowered.com/api/appdetails?appids=<id>&filters=basic` (a few per run, no network inside slots). Expose names to the connect page with `GET /api/app-names?ids=1,2` (bearer) and include them in admin queries. Unknown names fall back to "App <id>" with the header image.
- Tests: keep `api.test.ts`, `cloud.test.ts`, `pairing.test.ts` passing; replace the `/admin` HTML tests with function-level tests for the creator gate and each mutation; add a test that `/connect` and `/admin` serve the app HTML.

## Simulations to replace

All numbers, names, avatars, art and codes in the mockup are sample data. Countdown and reveal are static. Real data: functions above and existing API routes.

## Ownership

One implementation agent owns `cloud/web/**`, `cloud/pebble.ts`, new `cloud/admin-functions.ts` (or similar), `cloud/names.ts`, schema/migration for `app_names`, removal of `pages.ts`/HTML parts of `admin.ts`, tests and `cloud/README.md`/`docs/cloud-api.md` updates. It must not change existing API route contracts. Branch `feat/web-ui` stacked on `feat/pairing-codes`.

## Acceptance

- Visual: matches the mockup at 1400/820/390 in every listed state; no layout shift when the sheet opens; focus visible on all controls; keyboard-only flow works (Tab, Enter, Escape).
- Integration: `bun run typecheck`, `bun test`, `bedrock db check` pass; `bedrock dev` shows the admin with the local creator and the connect page with a real token; existing `/connect#cab_…` links still open.
- Open assumptions: Steam store name lookup may be rate-limited (tolerate failures); FR copy needs a native-speaker pass.
