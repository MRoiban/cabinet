// --saves-selftest: discovery, manifest cache, archives, restore and sync rules against
// temporary folders and an in-memory server only. Shared with LuaTools (xUnit runs it).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Cabinet
{
    static class SaveSelfTest
    {
        static int passed;
        static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        static void Skip(string label) { Console.WriteLine("SKIP: " + label); }
        static bool Windows { get { return Path.DirectorySeparatorChar == '\\'; } }
        static void Throws<T>(Action action, string label) where T : Exception
        {
            try { action(); } catch (T) { Check(true, label); return; }
            catch (Exception ex) { throw new Exception("FAIL: " + label + " (threw " + ex.GetType().Name + ": " + ex.Message + ")"); }
            throw new Exception("FAIL: " + label + " (no exception)");
        }

        internal const string Fixture = @"---
""Travellers Rest"":
  cloud:
    steam: true
  files:
    ""<home>/AppData/LocalLow/Louqou/TravellersRest/GameSaves"":
      tags:
        - save
      when:
        - os: windows
  gog:
    id: 1353960921
  installDir:
    Travellers Rest: {}
  steam:
    id: 1139980
Multi Root Game:
  files:
    ""<winDocuments>/My Games/Foo/Saves"":
      tags:
        - save
    ""<winDocuments>/My Games/Foo/Saves/slot*.sav"":
      tags:
        - save
    ""<winAppData>/Foo/*.cfg"":
      tags:
        - config
    ""<winLocalAppData>/Foo/Saved/SaveGames/**/*.sav"":
      tags:
        - save
      when:
        - os: windows
          store: steam
    ""<home>/.local/share/Foo"":
      tags:
        - save
      when:
        - os: linux
    ""<xdgData>/Foo"":
      tags:
        - save
    ""<base>/userdata/<storeUserId>/save.dat"":
      tags:
        - save
  id:
    steamExtra:
      - 1001
  installDir:
    Foo Game: {}
  registry:
    HKEY_CURRENT_USER/Software/Foo:
      tags:
        - save
  steam:
    id: 1000
Broad Game:
  files:
    ""<home>"":
      tags:
        - save
    ""<base>"":
      tags:
        - save
    ""<winDocuments>/*"":
      tags:
        - save
    ""<winDocuments>/*.brd"":
      tags:
        - save
  steam:
    id: 2000
Registry Only:
  registry:
    HKEY_CURRENT_USER/Software/RegOnly:
      tags:
        - save
  steam:
    id: 3000
Odd Entry:
  files:
    ""<base>/x"": &anchor
      tags:
        - save
  steam:
    id: 4000
";
        static SaveManifestLimits SmallLimits() { return new SaveManifestLimits { MinEntries = 3, MinGames = 3, MaxMalformed = 0.5 }; }
        internal static SaveManifestIndex FixtureIndex()
        {
            using (var r = new StringReader(Fixture)) { var i = SaveManifestIndex.Parse(r, SmallLimits()); i.Validate(SmallLimits()); i.Version = "fixture"; return i; }
        }
        static SaveEnvironment Env(string root, string user)
        {
            string home = Path.Combine(root, "Users", user), pub = Path.Combine(root, "Users", "Public");
            var e = new SaveEnvironment
            {
                Home = home, OsUserName = user, AppData = Path.Combine(home, "AppData", "Roaming"), LocalAppData = Path.Combine(home, "AppData", "Local"),
                LocalLow = Path.Combine(home, "AppData", "LocalLow"), Documents = Path.Combine(home, "Documents"), Desktop = Path.Combine(home, "Desktop"),
                Downloads = Path.Combine(home, "Downloads"), Public = pub, PublicDocuments = Path.Combine(pub, "Documents"), ProgramData = Path.Combine(root, "ProgramData"),
                WinDir = Path.Combine(root, "Windows"), ProgramFiles = Path.Combine(root, "Program Files"), ProgramFilesX86 = Path.Combine(root, "Program Files (x86)"),
                SteamRoot = Path.Combine(root, "Steam"), OnlineFixRoot = Path.Combine(pub, "Documents", "OnlineFix"),
            };
            foreach (string d in new[] { e.AppData, e.LocalAppData, e.LocalLow, e.Documents, e.OnlineFixRoot, Path.Combine(e.SteamRoot, "steamapps") }) Directory.CreateDirectory(d);
            e.ScanSteam();
            return e;
        }
        static string Put(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5)); return path;
        }
        static SaveSync NewSync(string data, SaveEnvironment env, SaveManifestIndex index)
        {
            var sync = new SaveSync(data, delegate(string s) { Console.WriteLine("LOG: " + s); }, delegate { return env; });
            sync.Manifest.Use(index); sync.GameRunning = delegate { return false; }; sync.UseAccount("http://fake", "user");
            return sync;
        }

        // Server rules from docs/save-sync.md, in memory.
        sealed class FakeSaveCloud : ISaveCloud
        {
            internal readonly Dictionary<string, List<KeyValuePair<SaveCloudMeta, byte[]>>> Archives = new Dictionary<string, List<KeyValuePair<SaveCloudMeta, byte[]>>>();
            internal readonly Dictionary<string, byte[]> Legacy = new Dictionary<string, byte[]>();
            internal int Puts, Fail; string device; long clock = 1;
            internal FakeSaveCloud As(string d) { device = d; return this; }
            internal SaveCloudMeta Current(string app) { List<KeyValuePair<SaveCloudMeta, byte[]>> l; return Archives.TryGetValue(app, out l) && l.Count > 0 ? l[l.Count - 1].Key : null; }
            public SavePutResult Put(string appid, SaveArchive archive, string baseHash, bool deleteIntent)
            {
                Puts++;
                if (Fail > 0) { Fail--; return new SavePutResult { Status = 500, Error = "server_error" }; }
                var read = SaveArchive.Read(archive.Bytes, "steam:" + appid);
                if (read.IsEmpty && !deleteIntent) return new SavePutResult { Status = 422, Error = "empty_archive" };
                var current = Current(appid);
                if (current != null && current.ContentHash == read.ContentHash) return new SavePutResult { Status = 200, Meta = current };
                if (current != null && current.ContentHash != (baseHash ?? "")) return new SavePutResult { Status = 409, Meta = current };
                var meta = new SaveCloudMeta { Id = Guid.NewGuid().ToString("N"), ContentHash = read.ContentHash, Files = read.Files.Count, Bytes = read.ExpandedBytes, Size = archive.Bytes.Length, CreatedAt = clock++, DeviceId = device, DeviceName = device, Deleted = read.IsEmpty };
                List<KeyValuePair<SaveCloudMeta, byte[]>> list;
                if (!Archives.TryGetValue(appid, out list)) { list = new List<KeyValuePair<SaveCloudMeta, byte[]>>(); Archives[appid] = list; }
                list.Add(new KeyValuePair<SaveCloudMeta, byte[]>(meta, archive.Bytes));
                return new SavePutResult { Status = 200, Meta = meta };
            }
            public byte[] Get(string appid, string versionId)
            {
                List<KeyValuePair<SaveCloudMeta, byte[]>> l;
                if (!Archives.TryGetValue(appid, out l)) return null;
                return l.Where(v => versionId == null || v.Key.Id == versionId).Select(v => v.Value).LastOrDefault();
            }
            public byte[] GetLegacy(string appid) { byte[] b; return Legacy.TryGetValue(appid, out b) ? b : null; }
        }

        static byte[] Zip(params string[] nameText)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    for (int i = 0; i < nameText.Length; i += 2) using (var w = new StreamWriter(z.CreateEntry(nameText[i]).Open())) w.Write(nameText[i + 1]);
                return ms.ToArray();
            }
        }
        static byte[] V2(string gameId, string rootId, string path, string text, int version)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            var a = new SaveArchive { Version = 2, GameId = gameId };
            a.Roots.Add(new SaveArchiveRoot { Id = rootId, Source = "manifest" });
            a.Files.Add(new SaveArchiveFile { Root = 0, Path = path, Size = data.Length, Sha256 = SaveUtil.Sha256(data) });
            a.ContentHash = SaveArchive.ComputeContentHash(a.Roots, a.Files);
            string json = a.ManifestJson().Replace("\"version\":2", "\"version\":" + version);
            return SaveZipWriter.Write(new[] { new KeyValuePair<string, byte[]>("roots/0/" + path, data) }, SaveArchive.ManifestName, Encoding.UTF8.GetBytes(json));
        }
        static string Read(SaveArchive a, string path)
        {
            using (var ms = new MemoryStream()) { a.Extract(a.Files.Single(f => f.Path == path), ms); return Encoding.UTF8.GetString(ms.ToArray()); }
        }

        public static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "Cabinet-saves-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var fetcher = SaveManifestCache.Fetcher; var sleep = SaveSnapshot.Sleep;
            SaveSnapshot.Sleep = delegate { };
            passed = 0;
            try
            {
                Manifest(root);
                Discovery(root);
                Archives(root);
                Restores(root);
                Sync(root);
                Console.WriteLine("Cabinet saves self-test passed: " + passed + " checks (temporary folders only).");
            }
            finally
            {
                SaveManifestCache.Fetcher = fetcher; SaveSnapshot.Sleep = sleep; SaveSnapshot.BeforeRead = null;
                try { if (Directory.Exists(root)) { foreach (string d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)) Unlock(d); Directory.Delete(root, true); } }
                catch (Exception ex) { Console.WriteLine("cleanup: " + ex.Message); }
            }
        }

        static void Manifest(string root)
        {
            var index = FixtureIndex();
            Check(index.Entries == 5 && index.Malformed == 1, "fixture parses; YAML anchors mark only that entry malformed");
            Check(index.Lookup("1139980").Single().Rules.Single().Path == "<home>/AppData/LocalLow/Louqou/TravellersRest/GameSaves", "Travellers Rest indexed by Steam AppID");
            Check(index.Lookup("1001").Count == 1 && !index.Lookup("1001")[0].Primary && index.Lookup("1000")[0].Primary, "steamExtra alternate IDs indexed");
            Check(!index.Games.ContainsKey("4000"), "malformed entries are not indexed");
            var reloaded = SaveManifestIndex.Load(index.Serialize());
            Check(reloaded.Games.Count == index.Games.Count && reloaded.Lookup("1000")[0].Rules.Count == index.Lookup("1000")[0].Rules.Count, "index survives serialization");
            Throws<InvalidDataException>(delegate { using (var r = new StringReader("a:\n\tb: 1\n")) SaveManifestIndex.Parse(r, SmallLimits()); }, "tab indentation rejected");
            Throws<InvalidDataException>(delegate { using (var r = new StringReader("a:\n" + new string(' ', 30) + "b:\n")) SaveManifestIndex.Parse(r, SmallLimits()); }, "excessive nesting rejected");
            Throws<InvalidDataException>(delegate { using (var r = new StringReader("a:\n  steam:\n    id: 1\n")) SaveManifestIndex.Parse(r, SmallLimits()).Validate(SmallLimits()); }, "too few entries rejected");

            // Cache: install, conditional refresh, invalid update and offline fallback.
            string data = Path.Combine(root, "cache");
            string seenEtag = "unset"; int calls = 0;
            byte[] good = Encoding.UTF8.GetBytes(Fixture);
            SaveManifestCache.Fetcher = delegate(string url, string etag, long max)
            { calls++; seenEtag = etag; return new SaveManifestResponse { Status = 200, ETag = "\"v1\"", Body = good }; };
            var cache = new SaveManifestCache(data, null) { Limits = SmallLimits() };
            Check(cache.Index == null && cache.Refresh(false) && cache.Index.Games.ContainsKey("1139980") && seenEtag == null, "first refresh downloads and installs the index");
            Check(!cache.Refresh(false) && calls == 1, "refresh waits 24 hours");
            SaveManifestCache.Fetcher = delegate(string url, string etag, long max) { calls++; seenEtag = etag; return new SaveManifestResponse { Status = 304 }; };
            Check(!cache.Refresh(true) && seenEtag == "\"v1\"" && cache.Index.Games.ContainsKey("1139980"), "conditional request sends the ETag; 304 keeps the cache");
            SaveManifestCache.Fetcher = delegate { return new SaveManifestResponse { Status = 200, ETag = "\"bad\"", Body = Encoding.UTF8.GetBytes("x:\n  steam:\n    id: 5\n") }; };
            Check(!cache.Refresh(true) && cache.Index.Games.ContainsKey("1139980") && cache.Meta.lastError != null, "invalid update keeps the last-known-good index");
            SaveManifestCache.Fetcher = delegate { return new SaveManifestResponse { Status = 200, Body = new byte[] { 0xff, 0xfe, 0x00 } }; };
            Check(!cache.Refresh(true) && cache.Index.Games.ContainsKey("1139980"), "non-UTF-8 update rejected");
            SaveManifestCache.Fetcher = delegate { return new SaveManifestResponse { Status = 200, Body = new byte[cache.Limits.MaxBytes + 1] }; };
            Check(!cache.Refresh(true) && cache.Index.Games.ContainsKey("1139980"), "oversized update rejected");
            SaveManifestCache.Fetcher = delegate { throw new System.Net.WebException("offline"); };
            Check(!cache.Refresh(true) && cache.Index.Games.ContainsKey("1139980"), "network failure keeps the cache");
            var offline = new SaveManifestCache(data, null);
            Check(offline.Index != null && offline.Index.Games.ContainsKey("1139980") && offline.Meta.etag == "\"v1\"", "cache loads from disk when offline");
            File.WriteAllText(Path.Combine(data, "ludusavi", "index.tsv"), "garbage");
            Check(new SaveManifestCache(data, null).Index == null, "corrupt cache is ignored, not trusted");
        }

        static void Discovery(string root)
        {
            var index = FixtureIndex();
            // Travellers Rest without any OnlineFix folder.
            var alice = Env(Path.Combine(root, "d1"), "Alice");
            string saves = Path.Combine(alice.LocalLow, "Louqou", "TravellersRest", "GameSaves");
            Put(Path.Combine(saves, "File_1", "SaveFile-22-10-2026-18-31-54.save"), "save-1"); Put(Path.Combine(saves, "keybinds.kbsd"), "keys");
            Put(Path.Combine(alice.LocalLow, "Louqou", "TravellersRest", "Player.log"), "not a save");
            var d = SaveResolver.Discover("1139980", index, "fixture", alice, null, null);
            Check(d.Status == SaveStatus.Found && d.FileCount == 2 && d.Name == "Travellers Rest", "Travellers Rest GameSaves found by AppID without OnlineFix");
            var tr = d.Roots.Single(r => r.Source == "manifest");
            Check(tr.Id == "manifest:<winLocalAppDataLow>/Louqou/TravellersRest" && tr.Patterns.Single() == "GameSaves" && tr.Path == Path.Combine(alice.LocalLow, "Louqou", "TravellersRest"), "logical root id is machine-independent");
            Check(d.Files.All(f => f.Relative.StartsWith("GameSaves/")) && d.Roots.Single(r => r.Source == "onlinefix").State == SaveStatus.Missing, "only GameSaves collected; absent OnlineFix folder is just missing");

            // THOR: OnlineFix created empty Saves and Stats; the game saves elsewhere.
            var thor = Env(Path.Combine(root, "thor"), "Alex");
            Directory.CreateDirectory(Path.Combine(thor.OnlineFixRoot, "1139980", "Saves")); Directory.CreateDirectory(Path.Combine(thor.OnlineFixRoot, "1139980", "Stats"));
            var empty = SaveResolver.Discover("1139980", null, null, thor, null, null);
            Check(empty.Status == SaveStatus.Empty && empty.FileCount == 0, "empty OnlineFix Saves folder resolves to empty, not found");
            Put(Path.Combine(thor.LocalLow, "Louqou", "TravellersRest", "GameSaves", "File_1", "a.save"), "real");
            var both = SaveResolver.Discover("1139980", index, "fixture", thor, null, null);
            Check(both.Status == SaveStatus.Found && both.Roots.Single(r => r.Source == "onlinefix").State == SaveStatus.Empty && both.FileCount == 1, "manifest location found next to the empty OnlineFix folder");

            // Relocated known folders and another Windows user name.
            var bob = Env(Path.Combine(root, "d2"), "Bob");
            bob.LocalLow = Path.Combine(root, "d2", "D", "Relocated", "LocalLow"); bob.Documents = Path.Combine(root, "d2", "D", "Docs");
            Directory.CreateDirectory(bob.LocalLow); Directory.CreateDirectory(bob.Documents);
            var relocated = SaveResolver.Discover("1139980", index, "fixture", bob, null, null);
            Check(relocated.Status == SaveStatus.Missing && relocated.Roots.Single(r => r.Source == "manifest").Path == Path.Combine(bob.LocalLow, "Louqou", "TravellersRest"), "<home>/AppData/LocalLow follows a relocated LocalLow");

            // Multiple roots, wildcards, overlaps, unsupported and ignored rules.
            var e = Env(Path.Combine(root, "d3"), "Carol");
            Put(Path.Combine(e.Documents, "My Games", "Foo", "Saves", "slot1.sav"), "1"); Put(Path.Combine(e.Documents, "My Games", "Foo", "Saves", "profile.dat"), "p");
            Put(Path.Combine(e.LocalAppData, "Foo", "Saved", "SaveGames", "a", "b", "deep.sav"), "deep"); Put(Path.Combine(e.LocalAppData, "Foo", "Saved", "SaveGames", "top.sav"), "top");
            Put(Path.Combine(e.LocalAppData, "Foo", "Saved", "SaveGames", "notes.txt"), "no"); Put(Path.Combine(e.AppData, "Foo", "game.cfg"), "cfg");
            var multi = SaveResolver.Discover("1000", index, "fixture", e, null, null);
            Check(multi.Status == SaveStatus.Found && multi.FileCount == 4, "multiple roots and ** wildcards collect exactly the save files");
            Check(multi.Files.Count(f => f.Relative.EndsWith("slot1.sav")) == 1, "overlapping rules deduplicate files");
            Check(multi.Files.Any(f => f.Relative == "SaveGames/a/b/deep.sav") || multi.Files.Any(f => f.Relative.EndsWith("a/b/deep.sav")), "recursive wildcard matched nested folders");
            Check(!multi.Files.Any(f => f.Relative.EndsWith(".cfg") || f.Relative.EndsWith("notes.txt")), "config-only and non-matching files excluded");
            Check(multi.Ignored.Any(x => x.StartsWith("config only")) && multi.Unsupported.Any(x => x.StartsWith("registry")) && multi.Unsupported.Any(x => x.Contains("<xdgData>")), "registry, XDG and config-only rules reported, not guessed");
            Check(!multi.Unsupported.Any(x => x.Contains(".local/share")), "rules for other operating systems are not applicable rather than unsupported");
            var alt = SaveResolver.Discover("1001", index, "fixture", e, null, null);
            Check(alt.FileCount == 4, "alternate AppID resolves to the same entry");
            var reg = SaveResolver.Discover("3000", index, "fixture", e, null, null);
            Check(reg.Status == SaveStatus.Unsupported, "registry-only game is unsupported");
            Check(SaveResolver.Discover("5555", index, "fixture", e, null, null).Status == SaveStatus.Unsupported, "unknown game without OnlineFix saves is unsupported, not 'missing'");
            Check(SaveResolver.Discover("5555", null, null, new SaveEnvironment(), null, null).Status == SaveStatus.Unsupported, "no manifest and no OnlineFix is unsupported");

            // Breadth limits.
            Put(Path.Combine(e.Documents, "x.brd"), "b"); Put(Path.Combine(e.Documents, "other.txt"), "o");
            var broad = SaveResolver.Discover("2000", index, "fixture", e, null, null);
            Check(broad.Roots.Count(r => r.State == SaveStatus.TooBroad) >= 2 && broad.FileCount == 1 && broad.Files[0].Relative == "x.brd", "whole-profile and whole-Documents rules refused; narrow *.brd allowed");

            // Overrides: supplement, replace, too broad.
            string custom = Path.Combine(root, "d3", "Custom", "Foo");
            Put(Path.Combine(custom, "manual.sav"), "m");
            var sup = SaveResolver.Discover("1000", index, "fixture", e, new SaveOverride { mode = "supplement", paths = new List<string> { custom } }, null);
            Check(sup.FileCount == 5 && sup.Roots.Any(r => r.Id == "override:1" && r.State == SaveStatus.Found), "supplement override adds a folder");
            var rep = SaveResolver.Discover("1000", index, "fixture", e, new SaveOverride { mode = "replace", paths = new List<string> { custom } }, null);
            Check(rep.FileCount == 1 && rep.Roots.Count == 1 && rep.Roots[0].Source == "override", "replace override uses only its folders");
            var tooBroad = SaveResolver.Discover("1000", index, "fixture", e, new SaveOverride { mode = "replace", paths = new List<string> { e.Home } }, null);
            Check(tooBroad.FileCount == 0 && tooBroad.Roots[0].State == SaveStatus.TooBroad, "override of the whole profile refused");
            var ovs = new SaveOverrides(Path.Combine(root, "d3", "data"));
            ovs.Set("1000", "replace", new[] { custom }, true);
            Check(new SaveOverrides(Path.Combine(root, "d3", "data")).For("1000").paths.Single() == SaveUtil.Full(custom), "overrides persist");
            Throws<ArgumentException>(delegate { ovs.Set("1000", "replace", new[] { "relative\\path" }, null); }, "relative override paths refused");
            ovs.Reset("1000"); Check(new SaveOverrides(Path.Combine(root, "d3", "data")).For("1000") == null, "override reset");

            // Missing, empty, unavailable, denied, links.
            var m = Env(Path.Combine(root, "d4"), "Dan");
            Check(SaveResolver.Discover("1139980", index, "fixture", m, null, null).Status == SaveStatus.Missing, "missing save folder reported as missing");
            Directory.CreateDirectory(Path.Combine(m.LocalLow, "Louqou", "TravellersRest", "GameSaves"));
            Check(SaveResolver.Discover("1139980", index, "fixture", m, null, null).Status == SaveStatus.Empty, "empty save folder reported as empty");
            var gone = Env(Path.Combine(root, "d5"), "Eve"); Directory.Delete(gone.LocalLow); gone.OnlineFixRoot = null;
            Check(SaveResolver.Discover("1139980", index, "fixture", gone, null, null).Status == SaveStatus.Unavailable, "missing base folder reported as unavailable");
            string locked = Path.Combine(m.LocalLow, "Louqou", "TravellersRest", "GameSaves", "locked");
            Put(Path.Combine(locked, "secret.save"), "x");
            bool enforced = false;
            if (Windows) { Deny(locked); try { Directory.GetFileSystemEntries(locked); } catch (UnauthorizedAccessException) { enforced = true; } }
            if (!enforced) { Console.WriteLine("NOTE: ACL denial not enforceable here (elevated/admin session or not Windows); simulating access denied"); SaveResolver.DenyForTest = delegate(string p) { return p == locked; }; }
            var denied = SaveResolver.Discover("1139980", index, "fixture", m, null, null);
            SaveResolver.DenyForTest = null; Unlock(locked);
            Check(denied.Denied.Any(x => x.Contains("locked")) && denied.Status == SaveStatus.Empty, "access-denied folders are reported" + (enforced ? " (real ACL)" : " (simulated)"));
            Directory.Delete(locked, true);
            if (Windows)
            {
                string outside = Path.Combine(root, "d4", "outside"); Put(Path.Combine(outside, "o.save"), "o");
                Junction(Path.Combine(m.LocalLow, "Louqou", "TravellersRest", "GameSaves", "link"), outside);
                var linked = SaveResolver.Discover("1139980", index, "fixture", m, null, null);
                Check(linked.FileCount == 0 && linked.Skipped.Any(x => x.StartsWith("link:")), "junctions inside a save folder are skipped, not followed");
                Directory.Delete(Path.Combine(m.LocalLow, "Louqou", "TravellersRest", "GameSaves", "link"));
            }
            else Skip("ACL denial and junctions need Windows");
            var limits = new SaveLimits { MaxFiles = 1 };
            Check(SaveResolver.Discover("1139980", index, "fixture", alice, null, limits).FileCount == 1, "file count limit enforced");
        }

        static void Archives(string root)
        {
            var index = FixtureIndex(); var alice = Env(Path.Combine(root, "a1"), "Alice");
            string saves = Path.Combine(alice.LocalLow, "Louqou", "TravellersRest", "GameSaves");
            Put(Path.Combine(saves, "File_1", "s.save"), "one"); Put(Path.Combine(saves, "keybinds.kbsd"), "keys");
            Func<SaveDiscovery> discover = delegate { return SaveResolver.Discover("1139980", index, "fixture", alice, null, null); };
            var first = SaveSnapshot.Capture(discover, 3); var second = SaveSnapshot.Capture(discover, 3);
            Check(first.ContentHash == second.ContentHash && first.Bytes.SequenceEqual(second.Bytes), "snapshots are deterministic");
            var read = SaveArchive.Read(first.Bytes, "steam:1139980");
            Check(read.Version == 2 && read.Files.Count == 2 && Read(read, "GameSaves/File_1/s.save") == "one", "format-2 archive round-trips");
            Check(!Encoding.UTF8.GetString(first.Bytes).Contains("Alice"), "archive holds no machine-specific absolute paths");
            using (var ms = new MemoryStream(first.Bytes)) using (var z = new ZipArchive(ms, ZipArchiveMode.Read))
                Check(z.Entries[0].FullName == SaveArchive.ManifestName && first.Bytes[8] == 0 && first.Bytes[9] == 0, "manifest is the first entry and stored");

            // A file that changes during the snapshot is retried; one that keeps changing fails.
            SaveSnapshot.BeforeRead = delegate(SaveFile f, int attempt) { if (attempt == 1 && f.Relative.EndsWith("s.save")) { File.WriteAllText(f.FullPath, "two!"); File.SetLastWriteTimeUtc(f.FullPath, DateTime.UtcNow); } };
            var retried = SaveSnapshot.Capture(discover, 3);
            Check(Read(SaveArchive.Read(retried.Bytes, "steam:1139980"), "GameSaves/File_1/s.save") == "two!", "file changing during snapshot is retried and captured consistently");
            int n = 0;
            SaveSnapshot.BeforeRead = delegate(SaveFile f, int attempt) { if (f.Relative.EndsWith("s.save")) { File.WriteAllText(f.FullPath, "x" + (n++)); File.SetLastWriteTimeUtc(f.FullPath, DateTime.UtcNow.AddSeconds(n)); } };
            Throws<SaveChangingException>(delegate { SaveSnapshot.Capture(discover, 3); }, "continuously changing saves fail as retryable");
            SaveSnapshot.BeforeRead = null;

            // Malicious and invalid archives.
            foreach (string bad in new[] { "../outside.dat", "a/../../x", "C:/Windows/x", "CON", "dir/aux.txt", "trail.", "/abs" })
                Throws<InvalidDataException>(delegate { SaveArchive.Read(Zip(bad, "x"), "steam:1"); }, "legacy entry refused: " + bad);
            foreach (string bad in new[] { "../x", "a\\..\\b", "C:/x", "NUL", "ok/../../x" })
                Throws<InvalidDataException>(delegate { SaveArchive.Read(V2("steam:1", "manifest:<home>/x", bad, "x", 2), "steam:1"); }, "format-2 path refused: " + bad);
            Throws<InvalidDataException>(delegate { SaveArchive.Read(V2("steam:1", "r", "ok.txt", "x", 3), "steam:1"); }, "newer archive version refused");
            Throws<InvalidDataException>(delegate { SaveArchive.Read(V2("steam:2", "r", "ok.txt", "x", 2), "steam:1"); }, "archive for another game refused");
            string tamperedJson = new SaveArchive { Version = 2, GameId = "steam:1", ContentHash = new string('0', 64) }.ManifestJson();
            Throws<InvalidDataException>(delegate { SaveArchive.Read(SaveZipWriter.Write(new KeyValuePair<string, byte[]>[0], SaveArchive.ManifestName, Encoding.UTF8.GetBytes(tamperedJson)), "steam:1"); }, "content hash mismatch refused");
            Throws<InvalidDataException>(delegate { SaveArchive.Read(SaveZipWriter.Write(new[] { new KeyValuePair<string, byte[]>("roots/0/extra.txt", new byte[] { 1 }) }, SaveArchive.ManifestName, Encoding.UTF8.GetBytes(new SaveArchive { Version = 2, GameId = "steam:1", ContentHash = SaveArchive.EmptyContentHash }.ManifestJson())), "steam:1"); }, "entries not in the manifest refused");

            // Legacy archives, including the 22-byte empty ZIP on the server.
            var emptyLegacy = SaveArchive.Read(new byte[] { 0x50, 0x4b, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "steam:1139980");
            Check(emptyLegacy.Version == 1 && emptyLegacy.IsEmpty, "22-byte legacy ZIP reads as no backup content");
            var dirsOnly = SaveArchive.Read(Zip("folder/", ""), "steam:1"); // a directory entry only
            Check(dirsOnly.IsEmpty, "legacy ZIP with only folders is empty");
            var legacy = SaveArchive.Read(Zip("slot.dat", "old", "sub/b.dat", "b"), "steam:1");
            Check(legacy.Version == 1 && legacy.Roots.Single().Id == "onlinefix:Saves" && legacy.Files.Count == 2, "legacy ZIP maps to the OnlineFix Saves root");
        }

        static void Restores(string root)
        {
            var index = FixtureIndex();
            var a = Env(Path.Combine(root, "r1"), "Alice"); var b = Env(Path.Combine(root, "r2"), "Bob");
            b.LocalLow = Path.Combine(root, "r2", "E", "LocalLow"); Directory.CreateDirectory(b.LocalLow);
            Put(Path.Combine(a.LocalLow, "Louqou", "TravellersRest", "GameSaves", "File_1", "s.save"), "alice-save");
            var archive = SaveSnapshot.Capture(delegate { return SaveResolver.Discover("1139980", index, "fixture", a, null, null); }, 3);
            var bLocal = SaveResolver.Discover("1139980", index, "fixture", b, null, null);
            var plan = SaveRestore.Plan(SaveArchive.Read(archive.Bytes, "steam:1139980"), bLocal, Path.Combine(root, "r2", "data"));
            string dest = Path.Combine(b.LocalLow, "Louqou", "TravellersRest", "GameSaves", "File_1", "s.save");
            Check(plan.CanApply && plan.Items.Single().Destination == dest && plan.Items[0].Action == "create" && plan.BackupPath == null, "dry run lists the destination in the second profile; no backup needed when empty");
            Check(plan.ToText().Contains(dest), "plan text shows every destination");
            Check(!File.Exists(dest), "preview writes nothing");
            SaveRestore.Apply(plan, SaveArchive.Read(archive.Bytes, "steam:1139980"), bLocal, delegate { return false; }, null);
            Check(File.ReadAllText(dest) == "alice-save", "restored into another user's relocated LocalLow");

            // Restore over existing files: backup first, then replace; deletions are backed up.
            Put(dest, "bob-own"); Put(Path.Combine(b.LocalLow, "Louqou", "TravellersRest", "GameSaves", "extra.save"), "extra");
            bLocal = SaveResolver.Discover("1139980", index, "fixture", b, null, null);
            string data = Path.Combine(root, "r2", "data");
            plan = SaveRestore.Plan(SaveArchive.Read(archive.Bytes, "steam:1139980"), bLocal, data);
            Check(plan.Items[0].Action == "replace" && plan.Deletions.Count == 1 && plan.BackupPath != null && plan.BackupPath.StartsWith(Path.Combine(data, "backup", "saves", "1139980")), "plan shows replace, delete and the backup file");
            SaveRestore.Apply(plan, SaveArchive.Read(archive.Bytes, "steam:1139980"), bLocal, delegate { return false; }, null);
            var backup = SaveArchive.Read(File.ReadAllBytes(plan.BackupPath), "steam:1139980");
            Check(File.ReadAllText(dest) == "alice-save" && !File.Exists(Path.Combine(b.LocalLow, "Louqou", "TravellersRest", "GameSaves", "extra.save")), "restore replaced and removed files");
            Check(Read(backup, "GameSaves/File_1/s.save") == "bob-own" && Read(backup, "GameSaves/extra.save") == "extra", "local backup holds the replaced files");
            SaveRestore.Recover(backup, SaveResolver.Discover("1139980", index, "fixture", b, null, null), null);
            Check(File.ReadAllText(dest) == "bob-own" && File.ReadAllText(Path.Combine(b.LocalLow, "Louqou", "TravellersRest", "GameSaves", "extra.save")) == "extra", "backup recovers the previous saves");

            // Running game, empty archive and unmapped roots never apply.
            bLocal = SaveResolver.Discover("1139980", index, "fixture", b, null, null);
            plan = SaveRestore.Plan(SaveArchive.Read(archive.Bytes, "steam:1139980"), bLocal, data);
            Throws<IOException>(delegate { SaveRestore.Apply(plan, SaveArchive.Read(archive.Bytes, "steam:1139980"), bLocal, delegate { return true; }, null); }, "restore refused when the game starts");
            Check(File.ReadAllText(dest) == "bob-own" && !Directory.GetFiles(Path.GetDirectoryName(dest)).Any(f => f.Contains(".cabinet-new-")), "deferred restore leaves no staged files");
            var emptyPlan = SaveRestore.Plan(SaveArchive.Read(new byte[] { 0x50, 0x4b, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "steam:1139980"), bLocal, data);
            Check(!emptyPlan.CanApply, "empty legacy archive cannot be restored over local saves");
            var unmapped = SaveRestore.Plan(SaveArchive.Read(V2("steam:1139980", "override:1", "x.save", "x", 2), "steam:1139980"), bLocal, data);
            Check(!unmapped.CanApply && unmapped.Unmapped.Count == 1, "roots without a local mapping are reported, not guessed");
            var outsidePattern = SaveRestore.Plan(SaveArchive.Read(V2("steam:1139980", "manifest:<winLocalAppDataLow>/Louqou/TravellersRest", "Player.log", "x", 2), "steam:1139980"), bLocal, data);
            Check(!outsidePattern.CanApply && outsidePattern.Errors.Any(), "files outside the root's save patterns are refused");

            if (Windows)
            {
                // A junction on the way to a destination must not redirect the write.
                string outside = Path.Combine(root, "r2", "outside"); Directory.CreateDirectory(outside);
                string gs = Path.Combine(b.LocalLow, "Louqou", "TravellersRest", "GameSaves");
                Directory.Delete(Path.Combine(gs, "File_1"), true);
                Junction(Path.Combine(gs, "File_1"), outside);
                bLocal = SaveResolver.Discover("1139980", index, "fixture", b, null, null);
                plan = SaveRestore.Plan(SaveArchive.Read(archive.Bytes, "steam:1139980"), bLocal, data);
                Throws<IOException>(delegate { SaveRestore.Apply(plan, SaveArchive.Read(archive.Bytes, "steam:1139980"), bLocal, delegate { return false; }, null); }, "junction in the destination path refused");
                Check(Directory.GetFiles(outside).Length == 0, "nothing written through the junction");
                Directory.Delete(Path.Combine(gs, "File_1"));

                // A failure while committing rolls back to the backup.
                Put(Path.Combine(gs, "File_1", "s.save"), "keep-me"); Put(Path.Combine(gs, "z.save"), "z-old");
                var two = new SaveArchive { Version = 2, GameId = "steam:1139980" };
                two.Roots.Add(new SaveArchiveRoot { Id = "manifest:<winLocalAppDataLow>/Louqou/TravellersRest", Source = "manifest" });
                foreach (var f in new[] { new[] { "GameSaves/File_1/s.save", "new-s" }, new[] { "GameSaves/z.save", "new-z" } })
                { byte[] bytes = Encoding.UTF8.GetBytes(f[1]); two.Files.Add(new SaveArchiveFile { Root = 0, Path = f[0], Size = bytes.Length, Sha256 = SaveUtil.Sha256(bytes) }); }
                two.ContentHash = SaveArchive.ComputeContentHash(two.Roots, two.Files);
                two.Bytes = SaveZipWriter.Write(two.Files.Select(f => new KeyValuePair<string, byte[]>("roots/0/" + f.Path, Encoding.UTF8.GetBytes(f.Path.EndsWith("s.save") ? "new-s" : "new-z"))), SaveArchive.ManifestName, Encoding.UTF8.GetBytes(two.ManifestJson()));
                bLocal = SaveResolver.Discover("1139980", index, "fixture", b, null, null);
                plan = SaveRestore.Plan(SaveArchive.Read(two.Bytes, "steam:1139980"), bLocal, data);
                using (new FileStream(Path.Combine(gs, "z.save"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    Throws<IOException>(delegate { SaveRestore.Apply(plan, SaveArchive.Read(two.Bytes, "steam:1139980"), bLocal, delegate { return false; }, null); }, "locked destination fails the commit");
                Check(File.ReadAllText(Path.Combine(gs, "File_1", "s.save")) == "keep-me" && File.ReadAllText(Path.Combine(gs, "z.save")) == "z-old", "failed commit rolled back from the backup");
                Check(!Directory.GetFiles(gs, "*", SearchOption.AllDirectories).Any(f => f.Contains(".cabinet-")), "no staged files left after rollback");
            }
            else Skip("junction and locked-file rollback need Windows");

            string pruneDir = Path.Combine(root, "prune"); Directory.CreateDirectory(pruneDir);
            for (int i = 0; i < 15; i++) File.WriteAllText(Path.Combine(pruneDir, "2026010" + (i / 10) + "T00000" + (i % 10) + ".zip"), "x");
            SaveRestore.Prune(pruneDir, SaveRestore.KeepBackups);
            Check(Directory.GetFiles(pruneDir).Length == 10 && File.Exists(Path.Combine(pruneDir, "20260101T000004.zip")), "local backups keep the newest 10");
        }

        static void Sync(string root)
        {
            var index = FixtureIndex(); var cloud = new FakeSaveCloud();
            var a = Env(Path.Combine(root, "s1"), "Alice"); var b = Env(Path.Combine(root, "s2"), "Bob");
            var sa = NewSync(Path.Combine(root, "s1", "data"), a, index); var sb = NewSync(Path.Combine(root, "s2", "data"), b, index);
            string ga = Path.Combine(a.LocalLow, "Louqou", "TravellersRest", "GameSaves"), gb = Path.Combine(b.LocalLow, "Louqou", "TravellersRest", "GameSaves");
            Func<string, bool> idle = delegate { return false; };

            // Eligibility: a LuaTools lua makes a game eligible without any OnlineFix folder.
            Put(Path.Combine(a.SteamRoot, "config", "stplug-in", "1139980.lua"), "addappid(1139980)");
            Check(sa.Eligible(a).SequenceEqual(new[] { "1139980" }), "LuaTools lua makes Travellers Rest eligible without OnlineFix");

            // THOR: empty OnlineFix folder only -> nothing uploads, status is not synced.
            Directory.CreateDirectory(Path.Combine(a.OnlineFixRoot, "1139980", "Saves"));
            sa.Sync("1139980", null, null, cloud.As("A"), idle, a, true);
            Check(cloud.Puts == 0 && sa.StatusOf("1139980").Status == SaveSyncStatus.Empty, "THOR case (empty OnlineFix Saves, game folder absent): nothing uploaded, 'No save files found'");
            var bare = Env(Path.Combine(root, "s0"), "Zed"); var sz = NewSync(Path.Combine(root, "s0", "data"), bare, index);
            sz.Sync("1139980", null, null, cloud, idle, bare, true);
            Check(cloud.Puts == 0 && sz.StatusOf("1139980").Status == SaveSyncStatus.Missing, "no save folder at all: nothing uploaded, 'Save location not found'");
            var noManifest = NewSync(Path.Combine(root, "s1", "data2"), a, null);
            noManifest.Sync("1139980", null, null, cloud, idle, a, true);
            Check(cloud.Puts == 0 && noManifest.StatusOf("1139980").Status == SaveSyncStatus.Empty, "empty OnlineFix folder alone: 'No save files found', never uploaded");

            // Running game: deferred, then sync after exit.
            Put(Path.Combine(ga, "File_1", "s.save"), "v1");
            bool running = true; sa.GameRunning = delegate { return running; };
            sa.Sync("1139980", null, null, cloud, idle, a, true);
            Check(cloud.Puts == 0 && sa.StatusOf("1139980").Status == SaveSyncStatus.Deferred, "running game defers upload");
            running = false;
            Check(sa.DeferredFinished(), "game exit triggers a sync");

            // Upload failure keeps the journal; retry succeeds.
            cloud.Fail = 1; sa.Sync("1139980", null, null, cloud, idle, a, true);
            Check(sa.App("1139980").contentHash == null && sa.StatusOf("1139980").Status == SaveSyncStatus.Error, "failed upload does not advance the journal or claim synced");
            sa.Sync("1139980", cloud.Current("1139980"), null, cloud, idle, a, true);
            Check(sa.StatusOf("1139980").Status == SaveSyncStatus.Synced && cloud.Current("1139980").Files == 1, "retry uploads the save");

            // Second profile (different user, relocated LocalLow): restores automatically into empty.
            b.LocalLow = Path.Combine(root, "s2", "D", "LocalLow"); Directory.CreateDirectory(b.LocalLow); gb = Path.Combine(b.LocalLow, "Louqou", "TravellersRest", "GameSaves");
            sb.Sync("1139980", cloud.Current("1139980"), null, cloud.As("B"), idle, b, true);
            Check(File.ReadAllText(Path.Combine(gb, "File_1", "s.save")) == "v1" && sb.StatusOf("1139980").Status == SaveSyncStatus.Synced, "fixture save restores into a second simulated user profile");
            Check(Directory.GetFiles(Path.Combine(root, "s2", "data", "restore-plans")).Length == 1, "automatic restore records its plan");

            // Unchanged on both: no new upload.
            int puts = cloud.Puts; sa.Sync("1139980", cloud.Current("1139980"), null, cloud.As("A"), idle, a, true);
            Check(cloud.Puts == puts && sa.StatusOf("1139980").Status == SaveSyncStatus.Synced, "unchanged saves are not re-uploaded");

            // Divergent changes from two devices: conflict, nothing overwritten, both preserved.
            Put(Path.Combine(ga, "File_1", "s.save"), "a2"); Put(Path.Combine(gb, "File_1", "s.save"), "b2");
            sa.Sync("1139980", cloud.Current("1139980"), null, cloud.As("A"), idle, a, true);
            sb.Sync("1139980", cloud.Current("1139980"), null, cloud.As("B"), idle, b, true);
            Check(sb.StatusOf("1139980").Status == SaveSyncStatus.Conflict && File.ReadAllText(Path.Combine(gb, "File_1", "s.save")) == "b2" && cloud.Archives["1139980"].Count == 2, "divergent changes are a conflict; both versions kept");
            sb.Restore("1139980", null, cloud.Current("1139980"), cloud, idle, b);
            Check(File.ReadAllText(Path.Combine(gb, "File_1", "s.save")) == "a2" && sb.StatusOf("1139980").Status == SaveSyncStatus.Synced, "'Use cloud' restores the other PC's version");
            var bBackup = Directory.GetFiles(Path.Combine(root, "s2", "data", "backup", "saves", "1139980")).OrderBy(x => x).Last();
            Check(Read(SaveArchive.Read(File.ReadAllBytes(bBackup), "steam:1139980"), "GameSaves/File_1/s.save") == "b2", "the replaced local version is in the local backup");

            // Empty scan after a sync: the cloud copy stays; status is localMissing.
            Directory.Delete(ga, true); Directory.CreateDirectory(ga); puts = cloud.Puts;
            sa.Sync("1139980", cloud.Current("1139980"), null, cloud.As("A"), idle, a, true);
            Check(cloud.Puts == puts && sa.StatusOf("1139980").Status == SaveSyncStatus.LocalMissing && cloud.Current("1139980").Files == 1, "empty scan never replaces the cloud backup");
            Throws<InvalidOperationException>(delegate { sa.KeepLocal("1139980", cloud.Current("1139980"), cloud, idle, a); }, "'Keep this PC' refuses an empty local folder");

            // Intentional deletion: tombstone; the other PC keeps its files.
            sa.DeleteCloud("1139980", cloud.Current("1139980"), cloud);
            Check(cloud.Current("1139980").Deleted && cloud.Archives["1139980"].Count == 3, "delete records a tombstone and keeps history");
            sb.Sync("1139980", cloud.Current("1139980"), null, cloud.As("B"), idle, b, true);
            Check(sb.StatusOf("1139980").Status == SaveSyncStatus.CloudDeleted && File.Exists(Path.Combine(gb, "File_1", "s.save")), "a cloud tombstone never deletes local saves");

            // Legacy backups: restored into an empty OnlineFix folder only; 22-byte ones ignored.
            var c = Env(Path.Combine(root, "s3"), "Carl"); var sc = NewSync(Path.Combine(root, "s3", "data"), c, index);
            cloud.Legacy["777"] = Zip("slot.dat", "legacy-save"); Directory.CreateDirectory(Path.Combine(c.OnlineFixRoot, "777", "Saves"));
            cloud.Legacy["778"] = new byte[] { 0x50, 0x4b, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            Directory.CreateDirectory(Path.Combine(c.OnlineFixRoot, "778", "Saves"));
            var legacyMeta = new SaveLegacyMeta { Sha256 = SaveUtil.Sha256(cloud.Legacy["777"]), Size = cloud.Legacy["777"].Length, DeviceId = "old" };
            sc.Sync("777", null, legacyMeta, cloud.As("C"), idle, c, true);
            Check(File.ReadAllText(Path.Combine(c.OnlineFixRoot, "777", "Saves", "slot.dat")) == "legacy-save", "existing Cabinet backups stay usable");
            sc.Sync("777", null, legacyMeta, cloud, idle, c, true);
            Check(cloud.Current("777") != null && cloud.Current("777").Files == 1, "restored legacy saves migrate to format 2");
            var emptyMeta = new SaveLegacyMeta { Sha256 = SaveUtil.Sha256(cloud.Legacy["778"]), Size = 22, Empty = true };
            sc.Sync("778", null, emptyMeta, cloud, idle, c, true);
            Check(Directory.GetFiles(Path.Combine(c.OnlineFixRoot, "778", "Saves")).Length == 0 && sc.StatusOf("778").Status == SaveSyncStatus.Empty && cloud.Current("778") == null, "22-byte legacy backup is not content and nothing uploads");
            Put(Path.Combine(c.OnlineFixRoot, "779", "Saves", "slot.dat"), "real"); cloud.Legacy["779"] = Zip("slot.dat", "stale");
            sc.Sync("779", null, new SaveLegacyMeta { Sha256 = SaveUtil.Sha256(cloud.Legacy["779"]), Size = 100 }, cloud, idle, c, true);
            Check(File.ReadAllText(Path.Combine(c.OnlineFixRoot, "779", "Saves", "slot.dat")) == "real", "legacy backup never replaces real local saves");

            // Off switch and statuses.
            sc.Overrides.Set("777", "supplement", null, false);
            Check(!sc.Eligible(c).Contains("777"), "per-game sync switch turns a game off");
            Check(sc.Statuses().All(s => s.Status != SaveSyncStatus.Synced || s.Files > 0), "no game without save files is ever reported synced");
        }

        static void Deny(string dir)
        {
            var info = new DirectoryInfo(dir); var sec = info.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.ListDirectory | FileSystemRights.ReadData, AccessControlType.Deny));
            info.SetAccessControl(sec);
        }
        static void Unlock(string dir)
        {
            if (!Windows) return;
            try
            {
                var info = new DirectoryInfo(dir); var sec = info.GetAccessControl();
                sec.RemoveAccessRuleAll(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.ListDirectory, AccessControlType.Deny));
                info.SetAccessControl(sec);
            }
            catch (Exception) { }
        }
        static void Junction(string link, string target)
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            p.StandardOutput.ReadToEnd(); p.WaitForExit();
            if (!Directory.Exists(link)) throw new Exception("could not create junction for the self-test");
        }
    }
}
