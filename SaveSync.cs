// Save sync decisions: journal, eligibility, running checks, upload/restore/conflict rules.
// Shared with LuaTools verbatim. Rules: docs/save-sync.md ("Client sync rules").
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Cabinet
{
    sealed class SaveCloudMeta
    {
        public string Id, Sha256, ContentHash, DeviceId, DeviceName; public long Size, Bytes, CreatedAt; public int Files; public bool Deleted;
        internal static SaveCloudMeta Parse(object value)
        {
            var m = value as Dictionary<string, object>;
            if (m == null) return null;
            Func<string, string> s = delegate(string k) { object v; return m.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; };
            Func<string, long> n = delegate(string k) { object v; return m.TryGetValue(k, out v) && v != null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0; };
            object deleted; m.TryGetValue("deleted", out deleted);
            var meta = new SaveCloudMeta { Id = s("id"), Sha256 = s("sha256"), ContentHash = s("contentHash"), DeviceId = s("deviceId"), DeviceName = s("deviceName"), Size = n("size"), Bytes = n("bytes"), CreatedAt = n("createdAt"), Files = (int)n("files"), Deleted = deleted is bool && (bool)deleted };
            return meta.ContentHash == null ? null : meta;
        }
    }
    sealed class SaveLegacyMeta
    {
        public string Sha256, DeviceId; public long Size, UpdatedAt; public bool Empty;
        internal static SaveLegacyMeta Parse(object value)
        {
            var m = value as Dictionary<string, object>;
            if (m == null || !m.ContainsKey("sha256")) return null;
            object empty; m.TryGetValue("empty", out empty);
            long size = m.ContainsKey("size") ? Convert.ToInt64(m["size"], CultureInfo.InvariantCulture) : 0;
            return new SaveLegacyMeta { Sha256 = Convert.ToString(m["sha256"]), DeviceId = m.ContainsKey("deviceId") ? Convert.ToString(m["deviceId"]) : null, Size = size, UpdatedAt = m.ContainsKey("updatedAt") ? Convert.ToInt64(m["updatedAt"], CultureInfo.InvariantCulture) : 0, Empty = (empty is bool && (bool)empty) || size <= 22 };
        }
    }
    sealed class SavePutResult { public int Status; public SaveCloudMeta Meta; public string Error; }
    // Transport for /api/apps/<appid>/archive and the legacy /save download. Implemented by CloudClient.
    interface ISaveCloud
    {
        SavePutResult Put(string appid, SaveArchive archive, string baseHash, bool deleteIntent);
        byte[] Get(string appid, string versionId);
        byte[] GetLegacy(string appid);
    }

    sealed class SaveAppState
    {
        public string contentHash, archiveId, status, error, conflictCloud, conflictLocal, legacySha, localHash, fingerprint;
        public long lastUpload, lastDownload, lastScan, lastAttempt, conflictAt;
        public int files, cloudFiles; public long bytes, cloudBytes;
    }
    sealed class SaveStateFile { public int version = 1; public Dictionary<string, Dictionary<string, SaveAppState>> accounts = new Dictionary<string, Dictionary<string, SaveAppState>>(); }

    // What the UI shows per game.
    sealed class SaveGameStatus
    {
        public string AppId, Name, Status, Discovery, Sources, Error, ManifestVersion, OverrideMode;
        public int Files, CloudFiles; public long Bytes, CloudBytes, LastUpload, LastDownload;
        public bool Eligible, CloudDeleted;
        public List<SaveRoot> Roots = new List<SaveRoot>();
        public List<string> Notes = new List<string>();
    }

    static class SaveSyncStatus
    {
        public const string Synced = "synced", Pending = "pending", Conflict = "conflict", Error = "error", Missing = "missing", Empty = "empty",
            Ambiguous = "ambiguous", LocalMissing = "localMissing", CloudDeleted = "cloudDeleted", Deferred = "deferred", Offline = "offline",
            Disabled = "disabled", CloudOnly = "cloudOnly";
    }

    // Read-only command line for reviewing discovery and restores on a real PC:
    //   --saves-discover <appid> [dataDir]          locations, files and status
    //   --saves-snapshot <appid> <out.zip> [dataDir] writes the archive that would upload
    //   --saves-plan <appid> <archive.zip> [dataDir] dry run: every destination and the backup file
    // dataDir (default %LOCALAPPDATA%\Cabinet) only holds the manifest cache.
    static class SaveCli
    {
        internal static bool Handle(string[] args, string defaultData, Func<SaveEnvironment> environment)
        {
            if (args.Length < 2 || (args[0] != "--saves-discover" && args[0] != "--saves-snapshot" && args[0] != "--saves-plan")) return false;
            try
            {
                bool needsFile = args[0] != "--saves-discover";
                if (needsFile && args.Length < 3) throw new ArgumentException("Missing archive path");
                string data = args.Length > (needsFile ? 3 : 2) ? args[needsFile ? 3 : 2] : defaultData;
                var sync = new SaveSync(data, Console.WriteLine, environment);
                sync.Manifest.Refresh(false);
                var env = environment();
                var d = sync.Discover(args[1], env);
                Console.WriteLine("Manifest: " + (sync.Manifest.Version ?? "unavailable") + (sync.Manifest.Meta.lastError == null ? "" : " (last refresh error: " + sync.Manifest.Meta.lastError + ")"));
                Console.WriteLine("Eligible for automatic sync: " + sync.Eligible(env).Contains(args[1]));
                Console.Write(d.Summary());
                foreach (var f in d.Files) Console.WriteLine("  file " + d.Roots[f.Root].Id + " | " + f.Relative + " | " + f.Size + " bytes | " + f.FullPath);
                if (args[0] == "--saves-snapshot")
                {
                    var a = SaveSnapshot.Capture(delegate { return sync.Discover(args[1], env); }, 3);
                    File.WriteAllBytes(args[2], a.Bytes);
                    Console.WriteLine("Snapshot: " + a.Files.Count + " files, contentHash " + a.ContentHash + ", " + a.Bytes.Length + " bytes -> " + args[2]);
                }
                if (args[0] == "--saves-plan")
                {
                    var archive = SaveArchive.Read(File.ReadAllBytes(args[2]), SaveOverrides.GameId(args[1]));
                    Console.Write(SaveRestore.Plan(archive, d, data).ToText());
                    Console.WriteLine("Dry run only: nothing was written.");
                }
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return true;
        }
    }

    sealed class SaveSync
    {
        internal readonly string Data; readonly Action<string> log;
        internal readonly SaveManifestCache Manifest; internal readonly SaveOverrides Overrides;
        internal SaveLimits Limits = new SaveLimits();
        internal Func<SaveEnvironment> EnvironmentProvider;
        internal Func<string, SaveEnvironment, bool> GameRunning = DefaultRunning;
        internal bool AutoRestore = true;
        readonly object gate = new object();
        readonly string statePath; SaveStateFile state = new SaveStateFile(); string account = "local";
        readonly Dictionary<string, SaveGameStatus> view = new Dictionary<string, SaveGameStatus>();
        readonly HashSet<string> deferred = new HashSet<string>();
        SaveEnvironment environment;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        internal SaveSync(string dataDir, Action<string> logger, Func<SaveEnvironment> environment)
        {
            Data = dataDir; log = logger ?? delegate { }; EnvironmentProvider = environment;
            Directory.CreateDirectory(Data);
            Manifest = new SaveManifestCache(Data, log);
            Overrides = new SaveOverrides(Data);
            statePath = Path.Combine(Data, "save-state.json");
            try { if (File.Exists(statePath)) state = Json.Deserialize<SaveStateFile>(File.ReadAllText(statePath)) ?? new SaveStateFile(); }
            catch (Exception ex) { log("save state unreadable, starting fresh: " + ex.Message); state = new SaveStateFile(); }
            if (state.accounts == null) state.accounts = new Dictionary<string, Dictionary<string, SaveAppState>>();
        }
        // Journal entries are per Cabinet account so another account's state never suppresses sync.
        internal void UseAccount(string server, string userId)
        {
            lock (gate) account = string.IsNullOrEmpty(userId) ? "local" : SaveUtil.Sha256((server ?? "") + ":" + userId).Substring(0, 32);
        }
        internal SaveAppState App(string appid)
        {
            lock (gate)
            {
                Dictionary<string, SaveAppState> apps;
                if (!state.accounts.TryGetValue(account, out apps)) { apps = new Dictionary<string, SaveAppState>(); state.accounts[account] = apps; }
                SaveAppState a; if (!apps.TryGetValue(appid, out a)) { a = new SaveAppState(); apps[appid] = a; }
                return a;
            }
        }
        void SaveState() { lock (gate) SaveUtil.AtomicWrite(statePath, Json.Serialize(state)); }

        internal SaveEnvironment CurrentEnvironment(bool refresh)
        {
            SaveEnvironment e; lock (gate) e = environment;
            if (e == null || refresh) { e = EnvironmentProvider(); lock (gate) environment = e; }
            return e;
        }
        // True once a game whose sync was deferred because it was running has exited.
        internal bool DeferredFinished()
        {
            string[] ids; SaveEnvironment e;
            lock (gate) { if (deferred.Count == 0) return false; ids = deferred.ToArray(); e = environment; }
            bool any = false;
            foreach (string id in ids)
            {
                bool running; try { running = GameRunning != null && GameRunning(id, e); } catch (Exception) { running = false; }
                if (!running) { lock (gate) deferred.Remove(id); any = true; }
            }
            return any;
        }
        internal SaveDiscovery Discover(string appid, SaveEnvironment env)
        {
            return SaveResolver.Discover(appid, Manifest.Index, Manifest.Version, env, Overrides.For(appid), Limits);
        }

        // Games whose saves sync automatically: OnlineFix folders, LuaTools/SteamTools luas
        // (config/stplug-in/<appid>.lua) and overrides, minus games switched off.
        internal List<string> Eligible(SaveEnvironment env)
        {
            var ids = new HashSet<string>();
            Action<string, string> scan = delegate(string dir, string pattern)
            {
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                    foreach (string p in pattern == null ? Directory.GetDirectories(dir) : Directory.GetFiles(dir, pattern))
                    { string id = Path.GetFileNameWithoutExtension(p); if (Regex.IsMatch(id, "^[0-9]{1,10}$")) ids.Add(id); }
                }
                catch (Exception) { }
            };
            scan(env.OnlineFixRoot, null);
            if (!string.IsNullOrEmpty(env.SteamRoot)) scan(Path.Combine(env.SteamRoot, "config", "stplug-in"), "*.lua");
            foreach (string id in Overrides.AppIds()) { var o = Overrides.For(id); if (o != null && (o.sync == true || (o.paths != null && o.paths.Count > 0))) ids.Add(id); }
            return ids.Where(id => { var o = Overrides.For(id); return o == null || o.sync != false; }).OrderBy(id => id, StringComparer.Ordinal).ToList();
        }

        internal List<SaveGameStatus> Statuses() { lock (gate) return view.Values.OrderBy(v => v.Name ?? v.AppId, StringComparer.CurrentCultureIgnoreCase).ToList(); }
        internal SaveGameStatus StatusOf(string appid) { lock (gate) { SaveGameStatus s; return view.TryGetValue(appid, out s) ? s : null; } }
        void Publish(string appid, SaveDiscovery d, SaveAppState a, SaveCloudMeta cloud, bool eligible, string status, string error)
        {
            a.status = status; a.error = error;
            if (d != null) { a.files = d.FileCount; a.bytes = d.Bytes; a.lastScan = SaveUtil.Now(); }
            if (cloud != null) { a.cloudFiles = cloud.Files; a.cloudBytes = cloud.Bytes; }
            var s = new SaveGameStatus
            {
                AppId = appid, Name = d == null ? null : d.Name, Status = status, Error = error, Eligible = eligible,
                Discovery = d == null ? null : d.Status, Sources = d == null ? null : d.Sources, ManifestVersion = d == null ? Manifest.Version : d.ManifestVersion,
                OverrideMode = d == null ? null : d.OverrideMode, Files = a.files, Bytes = a.bytes, CloudFiles = a.cloudFiles, CloudBytes = a.cloudBytes,
                LastUpload = a.lastUpload, LastDownload = a.lastDownload, CloudDeleted = cloud != null && cloud.Deleted,
            };
            if (d != null)
            {
                s.Roots = d.Roots;
                s.Notes.AddRange(d.Unsupported.Select(x => "unsupported: " + x));
                s.Notes.AddRange(d.Denied.Select(x => "access denied: " + x));
                s.Notes.AddRange(d.Skipped.Take(20).Select(x => "skipped: " + x));
            }
            lock (gate) view[appid] = s;
        }
        // Discovery only (Rescan, or when not connected): never touches the cloud.
        internal SaveDiscovery Scan(string appid, SaveEnvironment env, bool eligible, bool connected)
        {
            var d = Discover(appid, env); var a = App(appid);
            string status = !eligible ? SaveSyncStatus.Disabled : MapDiscovery(d, a);
            if (eligible && status == SaveSyncStatus.Pending && !connected) status = SaveSyncStatus.Offline;
            Publish(appid, d, a, null, eligible, status, a.error);
            return d;
        }
        // Status from discovery alone; never "synced" without a cloud comparison.
        string MapDiscovery(SaveDiscovery d, SaveAppState a)
        {
            switch (d.Status)
            {
                case SaveStatus.Found: return a.status == SaveSyncStatus.Synced && a.localHash != null && a.localHash == a.contentHash && a.fingerprint == Fingerprint(d) ? SaveSyncStatus.Synced : a.status == SaveSyncStatus.Conflict ? SaveSyncStatus.Conflict : SaveSyncStatus.Pending;
                case SaveStatus.Empty: return a.contentHash != null && a.contentHash != SaveArchive.EmptyContentHash ? SaveSyncStatus.LocalMissing : SaveSyncStatus.Empty;
                case SaveStatus.Ambiguous: return SaveSyncStatus.Ambiguous;
                default: return a.contentHash != null && a.contentHash != SaveArchive.EmptyContentHash ? SaveSyncStatus.LocalMissing : SaveSyncStatus.Missing;
            }
        }
        static string Fingerprint(SaveDiscovery d)
        {
            var b = new StringBuilder();
            foreach (var f in d.Files) b.Append(d.Roots[f.Root].Id).Append('\t').Append(f.Relative).Append('\t').Append(f.Size).Append('\t').Append(f.Ticks).Append('\n');
            return SaveUtil.Sha256(b.ToString());
        }

        // One game, one round. Never throws for per-game failures; they become retryable status.
        internal void Sync(string appid, SaveCloudMeta cloud, SaveLegacyMeta legacy, ISaveCloud api, Func<string, bool> blocked, SaveEnvironment env, bool eligible)
        {
            var a = App(appid); SaveDiscovery d = null;
            try
            {
                if (!eligible)
                {
                    d = Discover(appid, env);
                    Publish(appid, d, a, cloud, false, cloud != null && cloud.Files > 0 && !cloud.Deleted ? SaveSyncStatus.CloudOnly : SaveSyncStatus.Disabled, null);
                    return;
                }
                if (IsBlocked(appid, blocked, env)) { lock (gate) deferred.Add(appid); Publish(appid, null, a, cloud, true, SaveSyncStatus.Deferred, null); return; }
                d = Discover(appid, env);
                if (d.Status == SaveStatus.Found) { SyncFound(appid, d, a, cloud, api, blocked, env); return; }
                if (d.Status == SaveStatus.Ambiguous) { Publish(appid, d, a, cloud, true, SaveSyncStatus.Ambiguous, null); return; }
                // Nothing local: never upload. Restore only under the "nothing to lose" rule.
                bool cloudContent = cloud != null && !cloud.Deleted && cloud.Files > 0;
                bool restorable = d.Status == SaveStatus.Empty || d.Status == SaveStatus.Missing;
                bool fresh = a.contentHash == null || a.contentHash == SaveArchive.EmptyContentHash;
                if (cloudContent && fresh && restorable && AutoRestore)
                {
                    RestoreCloud(appid, cloud.Id, cloud, api, blocked, env, "first sync on this PC, no local saves");
                    return;
                }
                if (cloud == null && legacy != null && !legacy.Empty && a.contentHash == null && restorable && AutoRestore && legacy.Sha256 != a.legacySha)
                {
                    RestoreLegacy(appid, legacy, api, blocked, env);
                    return;
                }
                string status;
                if (cloud != null && cloud.Deleted) status = SaveSyncStatus.CloudDeleted;
                else if (cloudContent && !fresh && a.contentHash != cloud.ContentHash) { status = SaveSyncStatus.Conflict; a.conflictCloud = cloud.ContentHash; a.conflictLocal = SaveArchive.EmptyContentHash; a.conflictAt = SaveUtil.Now(); }
                else status = MapDiscovery(d, a);
                Publish(appid, d, a, cloud, true, status, null);
                SaveState();
            }
            catch (Exception ex)
            {
                log("saves " + appid + ": " + ex.Message);
                Publish(appid, d, a, cloud, eligible, SaveSyncStatus.Error, ex.Message);
                try { SaveState(); } catch (Exception) { }
            }
        }
        bool IsBlocked(string appid, Func<string, bool> blocked, SaveEnvironment env)
        {
            return (blocked != null && blocked(appid)) || (GameRunning != null && GameRunning(appid, env));
        }

        void SyncFound(string appid, SaveDiscovery d, SaveAppState a, SaveCloudMeta cloud, ISaveCloud api, Func<string, bool> blocked, SaveEnvironment env)
        {
            string fingerprint = Fingerprint(d), local; SaveArchive snapshot = null;
            if (fingerprint == a.fingerprint && a.localHash != null) local = a.localHash;
            else
            {
                snapshot = SaveSnapshot.Capture(delegate { return Discover(appid, env); }, 3);
                local = snapshot.ContentHash; a.localHash = local; a.fingerprint = Fingerprint(Discover(appid, env)) == fingerprint ? fingerprint : null;
            }
            if (cloud == null)
            {
                // Nothing in the cloud (first upload, or the server lost it): upload.
                Upload(appid, d, a, snapshot, null, api, blocked, env, a.contentHash ?? ""); return;
            }
            if (cloud.ContentHash == local && !cloud.Deleted)
            {
                a.contentHash = local; a.archiveId = cloud.Id; a.conflictCloud = null;
                Publish(appid, d, a, cloud, true, SaveSyncStatus.Synced, null); SaveState(); return;
            }
            bool localChanged = local != a.contentHash, cloudChanged = cloud.ContentHash != a.contentHash;
            if (cloud.Deleted)
            {
                // A tombstone never deletes local saves. New local saves after it was seen upload.
                if (!localChanged) { Publish(appid, d, a, cloud, true, SaveSyncStatus.CloudDeleted, null); SaveState(); return; }
                if (!cloudChanged) { Upload(appid, d, a, snapshot, cloud, api, blocked, env, cloud.ContentHash); return; }
            }
            else if (!localChanged && cloudChanged)
            {
                if (AutoRestore) { RestoreCloud(appid, cloud.Id, cloud, api, blocked, env, "cloud changed on " + (cloud.DeviceName ?? "another PC") + ", local saves unchanged since the last sync"); return; }
            }
            else if (localChanged && !cloudChanged) { Upload(appid, d, a, snapshot, cloud, api, blocked, env, a.contentHash ?? ""); return; }
            // Both sides moved (or this PC never synced and the cloud differs): keep both.
            a.conflictCloud = cloud.ContentHash; a.conflictLocal = local; a.conflictAt = SaveUtil.Now();
            log("saves " + appid + ": conflict between this PC and " + (cloud.DeviceName ?? cloud.DeviceId ?? "the cloud") + "; nothing overwritten");
            Publish(appid, d, a, cloud, true, SaveSyncStatus.Conflict, null); SaveState();
        }

        void Upload(string appid, SaveDiscovery d, SaveAppState a, SaveArchive snapshot, SaveCloudMeta cloud, ISaveCloud api, Func<string, bool> blocked, SaveEnvironment env, string baseHash)
        {
            if (snapshot == null) snapshot = SaveSnapshot.Capture(delegate { return Discover(appid, env); }, 3);
            if (snapshot.IsEmpty) throw new InvalidOperationException("Refusing to upload an empty snapshot");
            if (IsBlocked(appid, blocked, env)) { Publish(appid, d, a, cloud, true, SaveSyncStatus.Deferred, null); return; }
            a.lastAttempt = SaveUtil.Now();
            SavePutResult result = api.Put(appid, snapshot, baseHash, false);
            if (result.Status == 200 && result.Meta != null && result.Meta.ContentHash == snapshot.ContentHash)
            {
                // The journal advances only on a confirmed upload.
                a.contentHash = snapshot.ContentHash; a.archiveId = result.Meta.Id; a.lastUpload = SaveUtil.Now(); a.localHash = snapshot.ContentHash; a.conflictCloud = null;
                log("saves " + appid + ": uploaded " + snapshot.Files.Count + " files (" + snapshot.ExpandedBytes + " bytes)");
                Publish(appid, d, a, result.Meta, true, SaveSyncStatus.Synced, null); SaveState(); return;
            }
            if (result.Status == 409)
            {
                var current = result.Meta;
                a.conflictCloud = current == null ? null : current.ContentHash; a.conflictLocal = snapshot.ContentHash; a.conflictAt = SaveUtil.Now();
                Publish(appid, d, a, current, true, SaveSyncStatus.Conflict, null); SaveState(); return;
            }
            Publish(appid, d, a, cloud, true, SaveSyncStatus.Error, "Upload failed (" + result.Status + (result.Error == null ? "" : ": " + result.Error) + "); will retry");
            SaveState();
        }

        internal SaveRestorePlan PreviewRestore(string appid, string versionId, ISaveCloud api, SaveEnvironment env, out SaveArchive archive, out SaveDiscovery local)
        {
            byte[] bytes = versionId == "legacy" ? api.GetLegacy(appid) : api.Get(appid, versionId);
            if (bytes == null) throw new IOException("No cloud backup for this game");
            archive = SaveArchive.Read(bytes, SaveOverrides.GameId(appid));
            local = Discover(appid, env);
            return SaveRestore.Plan(archive, local, Data);
        }
        void RestoreCloud(string appid, string versionId, SaveCloudMeta cloud, ISaveCloud api, Func<string, bool> blocked, SaveEnvironment env, string reason)
        {
            SaveArchive archive; SaveDiscovery local;
            var plan = PreviewRestore(appid, versionId, api, env, out archive, out local);
            if (cloud != null && archive.ContentHash != cloud.ContentHash) throw new InvalidDataException("Cloud archive changed during download; will retry");
            Apply(appid, plan, archive, local, blocked, env, reason, cloud);
        }
        void RestoreLegacy(string appid, SaveLegacyMeta legacy, ISaveCloud api, Func<string, bool> blocked, SaveEnvironment env)
        {
            SaveArchive archive; SaveDiscovery local; var a = App(appid);
            var plan = PreviewRestore(appid, "legacy", api, env, out archive, out local);
            if (archive.IsEmpty)
            {
                // Empty legacy ZIP (e.g. the 22-byte archive): no backup content, never restored.
                a.legacySha = legacy.Sha256; Publish(appid, local, a, null, true, MapDiscovery(local, a), null); SaveState(); return;
            }
            Apply(appid, plan, archive, local, blocked, env, "legacy Cabinet backup, no local saves", null);
            a.legacySha = legacy.Sha256; SaveState();
        }
        void Apply(string appid, SaveRestorePlan plan, SaveArchive archive, SaveDiscovery local, Func<string, bool> blocked, SaveEnvironment env, string reason, SaveCloudMeta cloud)
        {
            var a = App(appid);
            plan.Reason = reason;
            string text = plan.ToText();
            try { SaveUtil.AtomicWrite(Path.Combine(Data, "restore-plans", appid + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture) + ".txt"), "Reason: " + reason + "\n" + text); SaveRestore.Prune(Path.Combine(Data, "restore-plans"), 50, "*.txt"); } catch (Exception) { }
            if (!plan.CanApply)
            {
                log("saves " + appid + ": restore not possible: " + text.Replace("\n", " | "));
                Publish(appid, local, a, cloud, true, local.Status == SaveStatus.Found ? SaveSyncStatus.Conflict : SaveSyncStatus.Error, "Cloud backup cannot be restored here: " + string.Join("; ", plan.Errors.Concat(plan.Unmapped).ToArray()));
                SaveState(); return;
            }
            log("saves " + appid + ": restoring (" + reason + "): " + text.Replace("\n", " | "));
            SaveRestore.Apply(plan, archive, local, delegate { return IsBlocked(appid, blocked, env); }, log);
            var after = Discover(appid, env);
            if (archive.Version == 2)
            {
                var check = after.Status == SaveStatus.Found ? SaveSnapshot.FromDiscovery(after) : null;
                a.localHash = check == null ? null : check.ContentHash; a.fingerprint = check == null ? null : Fingerprint(after);
                // Extra local roots the archive did not cover make the content differ; that is a pending upload.
                // The cloud version is now the common base; extra local roots are a pending upload.
                a.contentHash = archive.ContentHash;
                if (cloud != null) a.archiveId = cloud.Id;
            }
            a.lastDownload = SaveUtil.Now(); a.conflictCloud = null;
            Publish(appid, after, a, cloud, true, archive.Version == 2 && a.localHash == archive.ContentHash ? SaveSyncStatus.Synced : SaveSyncStatus.Pending, null);
            SaveState();
        }

        // ---- Explicit actions (LuaTools buttons, Cabinet tray) ----
        internal void Restore(string appid, string versionId, SaveCloudMeta cloud, ISaveCloud api, Func<string, bool> blocked, SaveEnvironment env)
        {
            if (IsBlocked(appid, blocked, env)) throw new IOException("Close the game before restoring its saves");
            if (versionId == "legacy")
            {
                SaveArchive archive; SaveDiscovery local;
                var plan = PreviewRestore(appid, "legacy", api, env, out archive, out local);
                if (archive.IsEmpty) throw new InvalidOperationException("The legacy backup holds no save files");
                Apply(appid, plan, archive, local, blocked, env, "manual restore (legacy backup)", null);
                return;
            }
            RestoreCloud(appid, versionId ?? (cloud == null ? null : cloud.Id), cloud, api, blocked, env, "manual restore");
        }
        // Conflict resolution "Keep this PC": upload local saves on top of the current cloud version.
        internal void KeepLocal(string appid, SaveCloudMeta cloud, ISaveCloud api, Func<string, bool> blocked, SaveEnvironment env)
        {
            if (IsBlocked(appid, blocked, env)) throw new IOException("Close the game first");
            var d = Discover(appid, env);
            if (d.Status != SaveStatus.Found) throw new InvalidOperationException("No local save files to keep (" + d.Status + ")");
            Upload(appid, d, App(appid), null, cloud, api, blocked, env, cloud == null ? "" : cloud.ContentHash);
        }
        // Intentional deletion: a tombstone version; history stays on the server.
        internal void DeleteCloud(string appid, SaveCloudMeta cloud, ISaveCloud api)
        {
            if (cloud == null) throw new InvalidOperationException("No cloud backup to delete");
            var tomb = new SaveArchive { Version = 2, GameId = SaveOverrides.GameId(appid) };
            tomb.ContentHash = SaveArchive.ComputeContentHash(tomb.Roots, tomb.Files);
            tomb.Bytes = SaveZipWriter.Write(new KeyValuePair<string, byte[]>[0], SaveArchive.ManifestName, new UTF8Encoding(false).GetBytes(tomb.ManifestJson()));
            var result = api.Put(appid, tomb, cloud.ContentHash, true);
            if (result.Status != 200) throw new IOException("Delete failed (" + result.Status + (result.Error == null ? "" : ": " + result.Error) + ")");
            var a = App(appid); a.contentHash = tomb.ContentHash; a.archiveId = result.Meta == null ? null : result.Meta.Id; SaveState();
            Publish(appid, null, a, result.Meta, true, SaveSyncStatus.CloudDeleted, null);
        }

        // ---- Running games: Steam's Running flag or a process inside the install folder ----
        static List<string> runningImages; static DateTime runningAt;
        internal static void ResetRunningCache() { runningImages = null; }
        static bool DefaultRunning(string appid, SaveEnvironment env)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps\" + appid))
                { object v = k == null ? null : k.GetValue("Running"); if (v is int && (int)v != 0) return true; }
            }
            catch (Exception) { }
            string install = env == null ? null : env.InstallPath(appid);
            if (install == null) return false;
            if (runningImages == null || DateTime.UtcNow - runningAt > TimeSpan.FromSeconds(10)) { runningImages = ProcessImages(); runningAt = DateTime.UtcNow; }
            string prefix = SaveUtil.Full(install) + Path.DirectorySeparatorChar;
            return runningImages.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        static List<string> ProcessImages()
        {
            var list = new List<string>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    IntPtr h = IntPtr.Zero;
                    try
                    {
                        h = OpenProcess(0x1000, false, p.Id); if (h == IntPtr.Zero) continue;
                        var b = new StringBuilder(1024); int n = b.Capacity;
                        if (QueryFullProcessImageName(h, 0, b, ref n)) list.Add(b.ToString(0, n));
                    }
                    catch (Exception) { }
                    finally { if (h != IntPtr.Zero) CloseHandle(h); }
                }
            }
            return list;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    }
}
