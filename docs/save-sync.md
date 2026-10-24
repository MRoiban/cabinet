# Save discovery and sync (format 2)

Status: implemented on `feat/save-discovery`, not deployed. Owner of the shared code: this repo
(`Saves.cs`). LuaTools copies `Saves.cs` verbatim into `src/LuaToolsGui/Services/Cabinet/Engine/`
like the other engine files; LuaTools-only changes are marked `LuaTools:`.

## Why

Cabinet used to back up exactly one folder per game: `%PUBLIC%\Documents\OnlineFix\<appid>\Saves`.
The OnlineFix emulator creates that folder (and `Stats`) when the game first starts, but many games
never write to it. Travellers Rest (1139980) keeps its saves in
`%USERPROFILE%\AppData\LocalLow\Louqou\TravellersRest\GameSaves`. On THOR, the OnlineFix emulator
created empty `1139980\Saves` and `Stats` folders at 2026-10-22 17:34:18 local, when the game
launched. After the game exited, `CloudClient.Push` saw that `Saves` existed, zipped it (`Zip()`
of an empty folder is the 22-byte end-of-central-directory record), used the folder's mtime as
`updatedAt` (15:34:18.452 UTC) and uploaded it. The server accepted it and the client journal
recorded it as synced. 72 of 88 stored saves on the server were such empty archives. Because the
server kept one ZIP per game and deleted the previous file on replacement, an empty upload could
also destroy a real backup.

## Game identity and discovery

A game is `steam:<appid>`. `SaveResolver` combines up to three sources:

| Source | Root id | Path |
|---|---|---|
| Ludusavi manifest | `manifest:<template anchor>` e.g. `manifest:<winLocalAppDataLow>/Louqou/TravellersRest/GameSaves` | rules tagged `save` that apply to Windows (and to the Steam store when a store is given) |
| OnlineFix | `onlinefix:Saves` | `%PUBLIC%\Documents\OnlineFix\<appid>\Saves` |
| User override | `override:<n>` (1-based) | an absolute local folder |

Overrides live in `%LOCALAPPDATA%\Cabinet\save-overrides.json`. They have a `mode`:
- `supplement` (default): the override folders are added to the manifest and OnlineFix roots;
- `replace`: only the override folders are used.

They can also turn sync off (`"sync": false`) or on (`"sync": true`) for one game.

### Manifest rules

- Placeholders on Windows: `<home>`, `<osUserName>`, `<winAppData>`, `<winLocalAppData>`,
  `<winLocalAppDataLow>`, `<winDocuments>`, `<winPublic>`, `<winProgramData>`, `<winDir>`,
  `<root>` (each Steam library, the Steam folder first), `<game>` (the `installdir` from the
  library's `appmanifest_<appid>.acf`, else the manifest's `installDir` names), `<base>`
  (`<root>/steamapps/common/<game>`), `<storeGameId>` (the AppID) and `<storeUserId>` (the active
  Steam account ID; a wildcard when it is unknown, which makes multiple matches ambiguous).
- `<home>/AppData/Roaming`, `<home>/AppData/Local`, `<home>/AppData/LocalLow` and `<home>/Documents`
  are rewritten to the known-folder placeholders, so relocated known folders resolve correctly.
- Reported as unsupported, never guessed: registry rules (`registry:`), `<xdgData>`/`<xdgConfig>`,
  `<regHkcu>`/`<regHklm>`, rules for other OSes or stores, untagged rules and `config`-only rules.
  Proton prefixes are not searched.
- Globs: `*` and `?` inside one path segment, `**` across segments. A rule that matches a
  folder includes everything below it.
- The longest wildcard-free prefix of a rule is its *anchor*. Archives store files relative to the
  anchor, keyed by the root id (which is the template text, not the local path), so no absolute
  path from one machine is ever used as a restore destination on another.
- Safety: an anchor equal to or above a protected folder is refused (`tooBroad`): drive roots, the
  profile, AppData/Local/LocalLow/Roaming, Documents, Desktop, Downloads, Public, Public Documents,
  ProgramData, Windows, Program Files, the Steam folder, every library, `steamapps`,
  `steamapps/common`, `userdata`, `userdata/<id>`, the OnlineFix root and a bare `<base>`
  (whole-game captures). The same applies to overrides.
- Bounds: 16 levels below an anchor, 20,000 directory entries visited per root, 10,000 files and
  512 MiB per game, links/junctions are skipped (and reported) rather than followed.
- Overlaps are removed per file: a file matched by several roots belongs to the root with the
  shortest anchor (then the first source). A root whose files are all claimed elsewhere is
  `overlapped`.

### Result

`SaveDiscovery` carries the game id, manifest version, every root (id, source, rule, local path,
exists, file count, bytes, state, note), the unsupported rules, missing paths, denied paths and
one status:

| Status | Meaning |
|---|---|
| `found` | at least one save file, no ambiguity |
| `empty` | a save folder exists but holds no files ("No save files found") |
| `missing` | rules resolved locally but no save folder exists ("Save location not found") |
| `unavailable` | the base folder of a rule (e.g. a Steam library drive) is not present |
| `ambiguous` | one logical root matched several folders that hold files (e.g. two Steam users) |
| `unsupported` | no usable Windows save rule (no manifest entry, registry-only, manifest unavailable) |

Only `found` can upload. An empty OnlineFix `Saves` folder never uploads.

## Manifest cache

- Source: `https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml`
  (about 17 MiB, 880k lines in October 2026).
- Checked at most once per 24 hours with `If-None-Match`. 304 keeps the cache. Downloads over
  64 MiB, lines over 16 KiB, more than 4 million lines, nesting deeper than 12 levels, fewer than
  10,000 entries or 2,000 indexed Steam games with save rules, or more than 2% malformed entries
  are rejected, and the last-known-good index stays in use.
- Only the AppID index is kept: `%LOCALAPPDATA%\Cabinet\ludusavi\index.tsv` plus `meta.json`
  (ETag, SHA-256 of the YAML, fetch/check times, counts). Both are written atomically.
- The YAML is parsed by a small line-oriented reader for the block subset the manifest uses
  (no anchors, aliases, tags or flow collections other than `{}`/`[]`). It is data only: nothing
  in it is executed, and paths are only resolved under the placeholders above.
- Licence: the manifest repository is MIT (Matthew T. Kennerly); its data is compiled from
  PCGamingWiki, whose content is CC BY-NC-SA 3.0. The manifest is downloaded at run time and not
  redistributed; the UI and docs credit both.

## Archive format 2

A ZIP whose first entry is `cabinet-save.json`, stored (method 0) or deflated (method 8; .NET
Framework's `ZipArchive` always deflates non-empty entries), at most 1 MiB uncompressed, followed by one entry per file named `roots/<rootIndex>/<relative path>`,
sorted, with a fixed 1980-01-01 timestamp. No directory entries.

```json
{
  "format": "cabinet-save", "version": 2, "gameId": "steam:1139980",
  "contentHash": "<sha256 hex>",
  "roots": [{ "id": "manifest:<winLocalAppDataLow>/Louqou/TravellersRest/GameSaves", "source": "manifest" }],
  "files": [{ "root": 0, "path": "File_1/SaveFile-22-10-2026-18-31-54.save", "size": 12420, "sha256": "<hex>" }]
}
```

- `files` is sorted by root index, then by `path` (ordinal), with no duplicates (case-insensitive).
- `contentHash` = SHA-256 of the UTF-8 text made of one line per file:
  `<root id>\t<path>\t<size>\t<sha256>\n`, in `files` order. It identifies the save content
  independently of compression, so Cabinet (.NET Framework) and LuaTools (.NET 8) agree.
- Paths use `/`, are relative, and must not contain `..`, `.`, empty segments, `:`, control
  characters, invalid file name characters, trailing dots/spaces or reserved device names.
- Format 1 (legacy) is any ZIP without that first entry: the old OnlineFix `Saves` ZIP. It maps to
  the single root `onlinefix:Saves`. A legacy ZIP with no file entries (including the 22-byte
  empty archive) is "no backup content": it is never restored.
- A newer `version` is refused, never partially restored.

## Server API additions (`/api/apps/:appid/archive`)

Old clients only know `save` (legacy) and never see format-2 archives, so they cannot restore
them into the wrong folder. New clients never write `save`.

| Method | Path | Notes |
|---|---|---|
| PUT | `/api/apps/<appid>/archive?deviceId=&sha256=&contentHash=&files=&bytes=&base=[&intent=delete]` | body: format-2 ZIP, `application/zip`, ≤ 90 MiB |
| GET | `/api/apps/<appid>/archive[?id=<versionId>]` | ZIP bytes; `ETag: <sha256>`, `X-Cabinet-Archive-Id`; 404 if none |
| GET | `/api/apps/<appid>/archives` | `{ "versions": ArchiveMeta[] }`, newest first |

```
ArchiveMeta = { id, sha256, contentHash, size, files, bytes, createdAt: unixMs, deviceId, deviceName, deleted }
AppState.archive = ArchiveMeta | null      (newest version)
SaveMeta.empty = boolean                   (legacy row holds no file entries)
```

PUT rules, in order:
1. Usual auth, `deviceId`, `sha256` (of the body), 64-hex `contentHash`, `files`/`bytes` integers,
   `base` empty or 64-hex. Body hash must match.
2. The body must be a format-2 archive: first local header at offset 0 named `cabinet-save.json`,
   method 0 or 8, ≤ 1 MiB uncompressed, matching CRC-32; JSON with `format`, `version` 2, `gameId` = `steam:<appid>`, roots and
   sorted/unique files as above. The server recomputes `contentHash` from the file list and
   requires it to equal both the query and the JSON. `files`/`bytes` must match the list. The
   end-of-central-directory entry count must equal `files.length + 1` (ZIP64 is refused).
3. `files == 0` is refused with `422 {"error":"empty_archive"}` unless `intent=delete`, which
   records a **tombstone** version (`deleted: true`). An intentional delete never removes history.
   `intent=delete` with files is `400`; with nothing stored for the game it is `404`.
4. Optimistic concurrency on content, not timestamps. Let `current` be the newest version.
   - `current.contentHash == contentHash`: `200` with `current` (idempotent retry, no new version).
   - `current` exists and `current.contentHash != base`: `409 {"current": ArchiveMeta}`.
   - otherwise a new version is stored and returned.
5. Retention: the newest 10 versions per user and game are kept; older rows and blobs are deleted
   after the commit. Blobs of replaced versions are not deleted otherwise.

Legacy `PUT /api/apps/<appid>/save` now refuses a ZIP with no file entries (none, or only
directory entries) with `422 {"error":"empty_archive"}` unless `allowEmpty=1`, and a body that
is not a ZIP with `400`. `SaveMeta.empty` is `size <= 22` for stored rows (the bare
end-of-central-directory record); rows stored from now on always hold a file entry unless
`allowEmpty=1` was used. Clients must still check the entry count after downloading. Old clients log
the error and retry next round; their journal does not advance.

## Client sync rules

State lives in `%LOCALAPPDATA%\Cabinet\save-state.json`, separate from `cloud-state.json` so an
older Cabinet rewriting its journal cannot drop it. Entries are per account (server + user id)
and per AppID: `contentHash` (content known to match the cloud), `archiveId`, last upload,
last download, last scan, status, error, conflict details.

Per eligible game, never while it runs (the existing checks plus Steam's `Running` registry flag
and any process under the game's install folder):

1. Discover. If not `found`, never upload. `empty`, `missing` and `unavailable` are shown as such.
2. Snapshot: hash every file, re-stat it, then write the ZIP while re-hashing. Any change, or a
   file that disappears, retries (3 attempts) and otherwise reports a retryable error.
3. Compare local `L`, cloud `C` (newest archive) and journal `J`:
   - `L == C`: synced (journal updated).
   - local changed, cloud unchanged (`C == J` or no cloud): upload with `base = J`.
   - cloud changed, local unchanged (`L == J`): restore (below).
   - both changed: **conflict**. Nothing is overwritten. The cloud copy stays in the server
     history; the user picks "Keep this PC" (upload with `base = C`) or "Use cloud" (restore).
   - The journal advances only after a `200` from the server or a completed restore.
4. Local saves gone after a sync (`J` set, local `empty`/`missing`, cloud unchanged): status
   `localMissing`. Cabinet neither uploads the emptiness nor restores automatically, because the
   user may have deleted them on purpose. "Restore" and "Delete cloud backup" (tombstone) are
   explicit actions.
5. Automatic restore only when nothing local can be lost or the local copy is the synced one:
   cloud has content and either the local saves equal `J`, or the game was never synced on this
   machine and its saves are `empty`/`missing` (with an existing base folder).
6. A tombstone in the cloud never deletes local saves; it is shown as "Cloud backup deleted".

Legacy migration: if a game has no format-2 archive but a legacy `save` with content, a new client
restores it only into `onlinefix:Saves` and only under rule 5. It is never restored over real
local saves. If local saves are found, they are uploaded as format 2 and the legacy row is
ignored from then on. Empty legacy rows (`empty: true`, or zero file entries once downloaded)
are ignored.

### Restore

`SaveRestore.Plan` is a dry run: every destination path with create/replace/delete, unmapped
roots, the backup file it will write, and whether it can apply. Automatic restores write the plan
to `%LOCALAPPDATA%\Cabinet\restore-plans\` and the log before applying. LuaTools shows the plan
before a manual restore.

`Apply`:
1. Refuse empty, unsupported-version or unmapped archives.
2. Back up every current local file of the affected roots as a format-2 archive in
   `%LOCALAPPDATA%\Cabinet\backup\saves\<appid>\<UTC time>.zip`; keep the newest 10 per game.
3. Refuse when the anchor, any existing folder on the way to a destination, or an existing
   destination file is a link/junction; every destination must stay inside its anchor.
4. Stage each file next to its destination (`*.cabinet-new-<id>`), verifying size and SHA-256.
5. Check that the game is still not running, then replace files one by one and delete local files
   that the archive does not contain (they are in the backup).
6. On any failure while committing, put the backup back and remove the staged files.

## Deploy order

1. Deploy the server: the new table and routes are additive and the legacy route keeps working,
   except that it now refuses empty ZIPs. Old clients keep using `save`.
2. Ship Cabinet and LuaTools with format 2. They read legacy saves for migration but never write
   them. Mixed fleets are safe: old clients never see format-2 archives; new clients ignore
   legacy rows once a format-2 archive exists.
3. Optional cleanup later: delete legacy `save` rows that are empty.

Rollback: an older server ignores the new table; new clients then get 404 on `archive` routes and
report an error instead of syncing (no legacy writes).
