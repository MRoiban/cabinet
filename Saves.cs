// Save discovery: Ludusavi manifest index, local environment, user overrides and the resolver.
// Shared with LuaTools (copied verbatim into Services/Cabinet/Engine). Keep it C# 5 and free of
// UI and tray dependencies. Design and rules: docs/save-sync.md.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Cabinet
{
    static class SaveUtil
    {
        internal static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        internal static string Sha256(byte[] bytes) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
        internal static string Sha256(string text) { return Sha256(new UTF8Encoding(false).GetBytes(text)); }
        internal static string Hex(byte[] hash) { return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(); }
        internal static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }
        internal static void AtomicWrite(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write)) { s.Write(bytes, 0, bytes.Length); s.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        internal static void AtomicWrite(string path, string text) { AtomicWrite(path, new UTF8Encoding(false).GetBytes(text)); }
        internal static string Json(string s)
        {
            var b = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') b.Append("\\\""); else if (c == '\\') b.Append("\\\\");
                else if (c < 0x20 || c > 0x7e) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }
        internal static string Full(string path) { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        // True when child is path itself or below it.
        internal static bool Within(string child, string path)
        {
            string c = Full(child), p = Full(path);
            if (PathComparer.Equals(c, p)) return true;
            string prefix = p.EndsWith(Path.DirectorySeparatorChar.ToString()) ? p : p + Path.DirectorySeparatorChar;
            return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        internal static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        static readonly Regex Reserved = new Regex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase);
        static readonly char[] Invalid = new[] { '<', '>', ':', '"', '|', '?', '*', '\\' };
        // Relative archive path with '/' separators; rejects traversal, devices and names Windows mangles.
        internal static bool SafeRelative(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.StartsWith("/")) return false;
            foreach (string part in path.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.Length > 255) return false;
                if (part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Invalid) >= 0 || Reserved.IsMatch(part)) return false;
                foreach (char c in part) if (c < 0x20 || c == 0x7f) return false;
            }
            return true;
        }
    }

    // ---------- Ludusavi manifest ----------
    sealed class SaveRuleSpec
    {
        public string Path;
        public bool Save, Config, Registry;
        public List<string[]> When = new List<string[]>();   // [os, store]; empty strings mean any
        public bool Untagged { get { return !Save && !Config; } }
        public bool AppliesTo(string os, string store)
        {
            if (When.Count == 0) return true;
            foreach (var w in When)
                if ((w[0] == "" || w[0] == os) && (w[1] == "" || w[1] == store)) return true;
            return false;
        }
    }
    sealed class SaveManifestGame
    {
        public string Name; public bool Primary;
        public List<string> InstallDirs = new List<string>();
        public List<SaveRuleSpec> Rules = new List<SaveRuleSpec>();
    }
    sealed class SaveManifestLimits
    {
        public long MaxBytes = 64L * 1024 * 1024;
        public int MaxLines = 4000000, MaxLineLength = 16384, MaxDepth = 12;
        public int MinEntries = 10000, MinGames = 2000;
        public double MaxMalformed = 0.02;
    }
    sealed class SaveManifestIndex
    {
        public readonly Dictionary<string, List<SaveManifestGame>> Games = new Dictionary<string, List<SaveManifestGame>>();
        public int Entries, Malformed;
        public string Version = "";   // short SHA-256 of the source YAML
        public List<SaveManifestGame> Lookup(string appid)
        {
            List<SaveManifestGame> list;
            if (!Games.TryGetValue(appid, out list)) return new List<SaveManifestGame>();
            var primary = list.Where(g => g.Primary).ToList();
            return primary.Count > 0 ? primary : list;
        }
        void Add(string appid, SaveManifestGame game)
        {
            List<SaveManifestGame> list;
            if (!Games.TryGetValue(appid, out list)) { list = new List<SaveManifestGame>(); Games[appid] = list; }
            list.Add(game);
        }

        // Line reader for the block-YAML subset the manifest uses. Untrusted data: anything
        // outside that subset inside a section we read marks the entry malformed and skips it.
        internal static SaveManifestIndex Parse(TextReader reader, SaveManifestLimits limits)
        {
            var index = new SaveManifestIndex();
            string line; int lines = 0;
            string name = null, section = null, sub = null; bool bad = false;
            string steamId = null; var extras = new List<string>(); var installs = new List<string>();
            var rules = new List<SaveRuleSpec>(); SaveRuleSpec rule = null; string[] when = null;
            Action flush = delegate
            {
                if (name == null) return;
                index.Entries++;
                if (bad) index.Malformed++;
                else if (rules.Count > 0)
                {
                    if (steamId != null) index.Add(steamId, new SaveManifestGame { Name = name, Primary = true, InstallDirs = installs, Rules = rules });
                    foreach (string id in extras.Distinct()) if (id != steamId) index.Add(id, new SaveManifestGame { Name = name, Primary = false, InstallDirs = installs, Rules = rules });
                }
                name = null; section = null; sub = null; bad = false; steamId = null; extras = new List<string>();
                installs = new List<string>(); rules = new List<SaveRuleSpec>(); rule = null; when = null;
            };
            while ((line = reader.ReadLine()) != null)
            {
                if (++lines > limits.MaxLines) throw new InvalidDataException("Manifest has too many lines");
                if (line.Length > limits.MaxLineLength) throw new InvalidDataException("Manifest line too long");
                if (line.Trim().Length == 0 || (lines == 1 && line == "---")) continue;
                int indent = 0; while (indent < line.Length && line[indent] == ' ') indent++;
                if (indent < line.Length && line[indent] == '\t') throw new InvalidDataException("Tabs are not valid YAML indentation");
                if (indent / 2 > limits.MaxDepth) throw new InvalidDataException("Manifest nesting too deep");
                if (line[indent] == '#') continue;
                string text = line.Substring(indent), key, value;
                if (indent == 0)
                {
                    flush();
                    if (!SplitKey(text, out key, out value) || value != null) { name = "?"; bad = true; continue; }
                    name = key; continue;
                }
                if (name == null) throw new InvalidDataException("Manifest does not start with an entry");
                if (bad) continue;
                if (indent == 2)
                {
                    if (!SplitKey(text, out key, out value)) { bad = true; continue; }
                    section = key; sub = null; rule = null; when = null;
                    if ((section == "files" || section == "registry" || section == "steam" || section == "id" || section == "installDir") && value != null && value != "{}") bad = true;
                    continue;
                }
                if (section == "steam")
                {
                    if (indent == 4 && SplitKey(text, out key, out value) && value != null)
                    { if (key == "id") { if (IsId(value)) steamId = value; else bad = true; } }
                    else if (indent == 4) bad = true;
                    continue;
                }
                if (section == "id")
                {
                    if (indent == 4) { if (!SplitKey(text, out key, out value)) bad = true; else sub = key; continue; }
                    if (sub == "steamExtra" && indent == 6)
                    { if (text.StartsWith("- ") && IsId(text.Substring(2).Trim())) extras.Add(text.Substring(2).Trim()); else bad = true; }
                    continue;
                }
                if (section == "installDir")
                {
                    if (indent == 4) { if (SplitKey(text, out key, out value) && (value == null || value == "{}")) installs.Add(key); else bad = true; }
                    continue;
                }
                if (section != "files" && section != "registry") continue;
                if (indent == 4)
                {
                    if (!SplitKey(text, out key, out value) || (value != null && value != "{}")) { bad = true; continue; }
                    rule = new SaveRuleSpec { Path = key, Registry = section == "registry" }; rules.Add(rule); sub = null; when = null; continue;
                }
                if (rule == null) { bad = true; continue; }
                if (indent == 6)
                {
                    if (!SplitKey(text, out key, out value) || (value != null && value != "[]")) { bad = true; continue; }
                    sub = key; when = null; continue;
                }
                if (indent == 8 && text.StartsWith("- "))
                {
                    string item = text.Substring(2).Trim();
                    if (sub == "tags")
                    {
                        if (item == "save") rule.Save = true; else if (item == "config") rule.Config = true;
                        else if (!Regex.IsMatch(item, "^[a-z]+$")) bad = true;
                        continue;
                    }
                    if (sub == "when")
                    {
                        when = new[] { "", "" }; rule.When.Add(when);
                        if (item != "{}" && !WhenPart(item, when)) bad = true;
                        continue;
                    }
                    continue;
                }
                if (indent == 10 && sub == "when" && when != null) { if (!WhenPart(text, when)) bad = true; continue; }
                if (indent > 8 && sub != "when") continue;
                bad = true;
            }
            flush();
            return index;
        }
        static bool IsId(string s) { return Regex.IsMatch(s, @"^[0-9]{1,10}$"); }
        static bool WhenPart(string text, string[] when)
        {
            string key, value;
            if (!SplitKey(text, out key, out value) || value == null) return false;
            if (key == "os") when[0] = value; else if (key == "store") when[1] = value;
            else if (key != "bit") return false;
            return true;
        }
        // key: | key: value | "quoted key": value. Values: plain, double-quoted, {} or [].
        internal static bool SplitKey(string text, out string key, out string value)
        {
            key = null; value = null; int end;
            if (text.StartsWith("\""))
            {
                if (!Quoted(text, 0, out key, out end) || end >= text.Length || text[end] != ':') return false;
                end++;
            }
            else
            {
                if ("&*!|>'{[%@`-?".IndexOf(text[0]) >= 0) return false;
                int colon = text.IndexOf(": ", StringComparison.Ordinal);
                if (colon < 0) { if (!text.EndsWith(":")) return false; colon = text.Length - 1; }
                key = text.Substring(0, colon).Trim(); end = colon + 1;
                if (key.Length == 0 || key.IndexOf(" #", StringComparison.Ordinal) >= 0) return false;
            }
            string rest = text.Substring(end).Trim();
            if (rest.Length == 0) return true;
            if (rest.StartsWith("\""))
            {
                int close; if (!Quoted(rest, 0, out value, out close) || rest.Substring(close).Trim().Length > 0) return false;
                return true;
            }
            if (rest == "{}" || rest == "[]") { value = rest; return true; }
            if ("&*!|>'{[%@`".IndexOf(rest[0]) >= 0 || rest.Contains(" #")) return false;
            value = rest; return true;
        }
        static bool Quoted(string s, int start, out string result, out int end)
        {
            var b = new StringBuilder(); result = null; end = -1;
            for (int i = start + 1; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') { result = b.ToString(); end = i + 1; return true; }
                if (c != '\\') { b.Append(c); continue; }
                if (++i >= s.Length) return false;
                switch (s[i])
                {
                    case '"': b.Append('"'); break;
                    case '\\': b.Append('\\'); break;
                    case '/': b.Append('/'); break;
                    case 't': b.Append('\t'); break;
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case '0': b.Append('\0'); break;
                    case 'u':
                        int code;
                        if (i + 4 >= s.Length || !int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) return false;
                        b.Append((char)code); i += 4; break;
                    default: return false;
                }
            }
            return false;
        }

        internal void Validate(SaveManifestLimits limits)
        {
            if (Entries < limits.MinEntries) throw new InvalidDataException("Manifest has too few entries (" + Entries + ")");
            if (Games.Count < limits.MinGames) throw new InvalidDataException("Manifest has too few Steam games with rules (" + Games.Count + ")");
            if (Malformed > Entries * limits.MaxMalformed) throw new InvalidDataException("Manifest has too many malformed entries (" + Malformed + ")");
        }

        // Compact index: only Steam-keyed entries with files/registry rules.
        internal string Serialize()
        {
            var b = new StringBuilder();
            b.Append("#cabinet-ludusavi-index\t1\t").Append(Version).Append('\t').Append(Entries).Append('\t').Append(Malformed).Append('\n');
            foreach (var pair in Games.OrderBy(p => p.Key, StringComparer.Ordinal))
                foreach (var g in pair.Value)
                {
                    if (!Clean(g.Name)) continue;
                    b.Append("G\t").Append(pair.Key).Append('\t').Append(g.Primary ? "P" : "E").Append('\t').Append(g.Name).Append('\n');
                    foreach (string dir in g.InstallDirs) if (Clean(dir)) b.Append("I\t").Append(dir).Append('\n');
                    foreach (var r in g.Rules)
                    {
                        if (!Clean(r.Path)) continue;
                        b.Append(r.Registry ? "R\t" : "F\t").Append(r.Path).Append('\t')
                            .Append(r.Save ? "s" : "").Append(r.Config ? "c" : "").Append('\t')
                            .Append(string.Join(";", r.When.Select(w => w[0] + "/" + w[1]).ToArray())).Append('\n');
                    }
                }
            return b.ToString();
        }
        static bool Clean(string s) { return s != null && s.All(c => c >= 0x20 && c != 0x7f); }
        internal static SaveManifestIndex Load(string text)
        {
            var index = new SaveManifestIndex(); SaveManifestGame game = null; string appid = null;
            using (var reader = new StringReader(text))
            {
                string header = reader.ReadLine();
                var h = header == null ? new string[0] : header.Split('\t');
                if (h.Length < 5 || h[0] != "#cabinet-ludusavi-index" || h[1] != "1") throw new InvalidDataException("Unknown index format");
                index.Version = h[2]; index.Entries = int.Parse(h[3], CultureInfo.InvariantCulture); index.Malformed = int.Parse(h[4], CultureInfo.InvariantCulture);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var p = line.Split('\t');
                    if (p[0] == "G" && p.Length == 4 && IsId(p[1]))
                    { appid = p[1]; game = new SaveManifestGame { Name = p[3], Primary = p[2] == "P" }; index.Add(appid, game); }
                    else if (game == null) throw new InvalidDataException("Index line before a game");
                    else if (p[0] == "I" && p.Length == 2) game.InstallDirs.Add(p[1]);
                    else if ((p[0] == "F" || p[0] == "R") && p.Length == 4)
                    {
                        var r = new SaveRuleSpec { Path = p[1], Registry = p[0] == "R", Save = p[2].Contains("s"), Config = p[2].Contains("c") };
                        if (p[3].Length > 0) foreach (string w in p[3].Split(';')) { var parts = w.Split('/'); if (parts.Length == 2) r.When.Add(parts); }
                        game.Rules.Add(r);
                    }
                    else throw new InvalidDataException("Malformed index line");
                }
            }
            return index;
        }
    }

    sealed class SaveManifestMeta { public string etag, sha256, version; public long fetchedAt, checkedAt; public int entries, games, malformed; public string lastError; }
    sealed class SaveManifestResponse { public int Status; public string ETag; public byte[] Body; }

    // Last-known-good AppID index with conditional refresh. The fetcher is host-specific:
    // Cabinet uses HttpWebRequest; LuaTools injects its outbound-policy client.
    sealed class SaveManifestCache
    {
        internal const string Url = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";
        internal static Func<string, string, long, SaveManifestResponse> Fetcher = DefaultFetch;
        readonly string dir; readonly Action<string> log; readonly object gate = new object();
        internal SaveManifestLimits Limits = new SaveManifestLimits();
        internal TimeSpan RefreshEvery = TimeSpan.FromHours(24);
        SaveManifestIndex index; SaveManifestMeta meta = new SaveManifestMeta();
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        internal SaveManifestCache(string dataDir, Action<string> logger)
        {
            dir = Path.Combine(dataDir, "ludusavi"); log = logger ?? delegate { };
            try
            {
                if (File.Exists(MetaPath)) meta = Json.Deserialize<SaveManifestMeta>(File.ReadAllText(MetaPath)) ?? new SaveManifestMeta();
                if (File.Exists(IndexPath)) index = SaveManifestIndex.Load(File.ReadAllText(IndexPath));
            }
            catch (Exception ex) { log("save manifest cache unreadable: " + ex.Message); index = null; }
        }
        string IndexPath { get { return Path.Combine(dir, "index.tsv"); } }
        string MetaPath { get { return Path.Combine(dir, "meta.json"); } }
        internal SaveManifestIndex Index { get { lock (gate) return index; } }
        internal SaveManifestMeta Meta { get { lock (gate) return meta; } }
        internal string Version { get { var i = Index; var m = Meta; return i == null ? null : i.Version + (m.fetchedAt > 0 ? "@" + new DateTime(1970, 1, 1).AddMilliseconds(m.fetchedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : ""); } }
        internal bool Due { get { var m = Meta; return Index == null || SaveUtil.Now() - m.checkedAt > (long)RefreshEvery.TotalMilliseconds; } }

        // Returns true when a new index was installed. Never throws; failures keep the old index.
        internal bool Refresh(bool force)
        {
            if (!force && !Due) return false;
            SaveManifestMeta m = Meta; long now = SaveUtil.Now();
            try
            {
                var response = Fetcher(Url, Index == null ? null : m.etag, Limits.MaxBytes);
                if (response == null) throw new IOException("No response");
                if (response.Status == 304 && Index != null) { m.checkedAt = now; m.lastError = null; SaveMeta(m); return false; }
                if (response.Status != 200 || response.Body == null) throw new IOException("HTTP " + response.Status);
                if (response.Body.LongLength > Limits.MaxBytes) throw new InvalidDataException("Manifest too large");
                var next = Install(response.Body);
                var nm = new SaveManifestMeta { etag = response.ETag, sha256 = SaveUtil.Sha256(response.Body), version = next.Version, fetchedAt = now, checkedAt = now, entries = next.Entries, games = next.Games.Count, malformed = next.Malformed };
                SaveUtil.AtomicWrite(IndexPath, next.Serialize());
                SaveMeta(nm);
                lock (gate) { index = next; meta = nm; }
                log("save manifest updated: " + next.Games.Count + " Steam games, version " + next.Version);
                return true;
            }
            catch (Exception ex)
            {
                // Offline or invalid update: keep the last-known-good index; retry in an hour.
                m.lastError = ex.Message; m.checkedAt = now - (long)RefreshEvery.TotalMilliseconds + 3600000;
                try { SaveMeta(m); } catch { }
                log("save manifest refresh failed (keeping " + (Index == null ? "no index" : "cached index") + "): " + ex.Message);
                return false;
            }
        }
        internal SaveManifestIndex Install(byte[] yaml)
        {
            SaveManifestIndex next;
            using (var reader = new StreamReader(new MemoryStream(yaml), new UTF8Encoding(false, true))) next = SaveManifestIndex.Parse(reader, Limits);
            next.Validate(Limits);
            next.Version = SaveUtil.Sha256(yaml).Substring(0, 12);
            return next;
        }
        void SaveMeta(SaveManifestMeta m) { SaveUtil.AtomicWrite(MetaPath, Json.Serialize(m)); lock (gate) meta = m; }
        // Test hook: install an index without network.
        internal void Use(SaveManifestIndex next) { lock (gate) { index = next; meta = new SaveManifestMeta { version = next == null ? null : next.Version, checkedAt = SaveUtil.Now() }; } }

        static SaveManifestResponse DefaultFetch(string url, string etag, long max)
        {
            System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Timeout = 60000; req.ReadWriteTimeout = 60000; req.AllowAutoRedirect = false;
            req.UserAgent = "Cabinet (save discovery)";
            if (!string.IsNullOrEmpty(etag)) req.Headers[System.Net.HttpRequestHeader.IfNoneMatch] = etag;
            System.Net.HttpWebResponse response;
            try { response = (System.Net.HttpWebResponse)req.GetResponse(); }
            catch (System.Net.WebException ex)
            {
                response = ex.Response as System.Net.HttpWebResponse; if (response == null) throw;
                using (response) return new SaveManifestResponse { Status = (int)response.StatusCode };
            }
            using (response) using (var s = response.GetResponseStream()) using (var ms = new MemoryStream())
            {
                if (response.ContentLength > max) throw new InvalidDataException("Manifest too large");
                byte[] buffer = new byte[65536]; int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0) { if (ms.Length + n > max) throw new InvalidDataException("Manifest too large"); ms.Write(buffer, 0, n); }
                return new SaveManifestResponse { Status = (int)response.StatusCode, ETag = response.Headers["ETag"], Body = ms.ToArray() };
            }
        }
    }

    // ---------- Local environment ----------
    sealed class SaveEnvironment
    {
        public string Home, OsUserName, AppData, LocalAppData, LocalLow, Documents, Desktop, Downloads, Public, PublicDocuments, ProgramData, WinDir, ProgramFiles, ProgramFilesX86;
        public string SteamRoot, SteamAccountId, OnlineFixRoot;
        public List<string> Libraries = new List<string>();
        // appid -> (library, installdir) from appmanifest_<appid>.acf
        public Dictionary<string, KeyValuePair<string, string>> Installed = new Dictionary<string, KeyValuePair<string, string>>();

        internal static SaveEnvironment Current(string steamRoot, string accountId, string onlineFixRoot)
        {
            var e = new SaveEnvironment();
            e.Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            e.OsUserName = Environment.UserName;
            e.AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            e.LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            e.Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            e.Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            e.PublicDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            e.Public = string.IsNullOrEmpty(e.PublicDocuments) ? Environment.GetEnvironmentVariable("PUBLIC") : Path.GetDirectoryName(e.PublicDocuments);
            e.ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            e.WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            e.ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            e.ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            e.LocalLow = KnownFolder("A520A1A4-1780-4FF6-BD18-167343C5AF16");
            if (string.IsNullOrEmpty(e.LocalLow) && !string.IsNullOrEmpty(e.Home)) e.LocalLow = Path.Combine(e.Home, "AppData", "LocalLow");
            e.Downloads = KnownFolder("374DE290-123F-4565-9164-39C4925E467B");
            e.SteamRoot = steamRoot; e.SteamAccountId = accountId; e.OnlineFixRoot = onlineFixRoot;
            e.ScanSteam();
            return e;
        }
        static string KnownFolder(string guid)
        {
            IntPtr p = IntPtr.Zero;
            try { return SHGetKnownFolderPath(new Guid(guid), 0, IntPtr.Zero, out p) == 0 ? System.Runtime.InteropServices.Marshal.PtrToStringUni(p) : null; }
            catch { return null; }
            finally { if (p != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p); }
        }
        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        // Steam folder first, then libraryfolders.vdf entries; appmanifests give each app's installdir.
        internal void ScanSteam()
        {
            Libraries.Clear(); Installed.Clear();
            if (string.IsNullOrEmpty(SteamRoot)) return;
            Libraries.Add(SteamRoot);
            try
            {
                string lf = Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf");
                if (File.Exists(lf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(lf), "\"path\"\\s+\"([^\"]+)\""))
                    {
                        string p = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (Libraries.Count < 32 && !Libraries.Any(l => SaveUtil.PathComparer.Equals(SaveUtil.Full(l), SaveUtil.Full(p)))) Libraries.Add(p);
                    }
            }
            catch (Exception) { }
            foreach (string lib in Libraries)
            {
                try
                {
                    string apps = Path.Combine(lib, "steamapps");
                    if (!Directory.Exists(apps)) continue;
                    foreach (string acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                    {
                        string id = Path.GetFileNameWithoutExtension(acf).Substring("appmanifest_".Length);
                        if (!Regex.IsMatch(id, "^[0-9]{1,10}$") || Installed.ContainsKey(id)) continue;
                        var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                        if (m.Success && SaveUtil.SafeRelative(m.Groups[1].Value.Replace("\\\\", "/"))) Installed[id] = new KeyValuePair<string, string>(lib, m.Groups[1].Value.Replace("\\\\", "\\"));
                    }
                }
                catch (Exception) { }
            }
        }
        internal string InstallPath(string appid)
        {
            KeyValuePair<string, string> v;
            return Installed.TryGetValue(appid, out v) ? Path.Combine(v.Key, "steamapps", "common", v.Value) : null;
        }
        internal string Folder(string placeholder)
        {
            switch (placeholder)
            {
                case "home": return Home;
                case "osUserName": return OsUserName;
                case "winAppData": return AppData;
                case "winLocalAppData": return LocalAppData;
                case "winLocalAppDataLow": return LocalLow;
                case "winDocuments": return Documents;
                case "winPublic": return Public;
                case "winProgramData": return ProgramData;
                case "winDir": return WinDir;
                default: return null;
            }
        }
        // Folders that must never be captured whole (tier A) and, additionally, never as a
        // whole-folder capture (tier B, Steam structure, OnlineFix root).
        internal List<string> BroadFolders()
        {
            var list = new List<string> { Home, AppData, LocalAppData, LocalLow, Documents, Desktop, Downloads, Public, PublicDocuments, ProgramData, WinDir, ProgramFiles, ProgramFilesX86, SteamRoot };
            if (!string.IsNullOrEmpty(Home)) { list.Add(Path.Combine(Home, "AppData")); list.Add(Path.GetDirectoryName(SaveUtil.Full(Home))); }
            list.AddRange(Libraries);
            return list.Where(p => !string.IsNullOrEmpty(p)).ToList();
        }
        internal List<string> StructureFolders()
        {
            var list = new List<string>();
            foreach (string lib in Libraries) { list.Add(Path.Combine(lib, "steamapps")); list.Add(Path.Combine(lib, "steamapps", "common")); list.Add(Path.Combine(lib, "userdata")); }
            if (!string.IsNullOrEmpty(SteamRoot) && Directory.Exists(Path.Combine(SteamRoot, "userdata")))
                try { list.AddRange(Directory.GetDirectories(Path.Combine(SteamRoot, "userdata"))); } catch (Exception) { }
            if (!string.IsNullOrEmpty(OnlineFixRoot)) list.Add(OnlineFixRoot);
            foreach (string id in Installed.Keys) list.Add(InstallPath(id));
            return list;
        }
    }

    // ---------- User overrides (%LOCALAPPDATA%\Cabinet\save-overrides.json) ----------
    sealed class SaveOverride { public string mode = "supplement"; public List<string> paths = new List<string>(); public bool? sync; }
    sealed class SaveOverrideFile { public int version = 1; public Dictionary<string, SaveOverride> games = new Dictionary<string, SaveOverride>(); }
    sealed class SaveOverrides
    {
        readonly string path; readonly object gate = new object(); SaveOverrideFile file = new SaveOverrideFile();
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        internal SaveOverrides(string dataDir)
        {
            path = Path.Combine(dataDir, "save-overrides.json");
            if (File.Exists(path))
            {
                file = Json.Deserialize<SaveOverrideFile>(File.ReadAllText(path)) ?? new SaveOverrideFile();
                if (file.games == null) file.games = new Dictionary<string, SaveOverride>();
            }
        }
        internal static string GameId(string appid) { return "steam:" + appid; }
        internal SaveOverride For(string appid)
        {
            lock (gate) { SaveOverride o; return file.games.TryGetValue(GameId(appid), out o) ? o : null; }
        }
        internal IEnumerable<string> AppIds()
        {
            lock (gate) return file.games.Keys.Where(k => Regex.IsMatch(k, "^steam:[0-9]{1,10}$")).Select(k => k.Substring(6)).ToList();
        }
        internal void Set(string appid, string mode, IEnumerable<string> paths, bool? sync)
        {
            if (mode != "supplement" && mode != "replace") throw new ArgumentException("mode must be supplement or replace");
            var clean = new List<string>();
            foreach (string p in paths ?? new string[0])
            {
                if (string.IsNullOrEmpty(p) || !Path.IsPathRooted(p) || p.StartsWith(@"\\")) throw new ArgumentException("Override folders must be absolute local paths");
                clean.Add(SaveUtil.Full(p));
            }
            lock (gate)
            {
                file.games[GameId(appid)] = new SaveOverride { mode = mode, paths = clean.Distinct(SaveUtil.PathComparer).ToList(), sync = sync };
                SaveUtil.AtomicWrite(path, Json.Serialize(file));
            }
        }
        internal void Reset(string appid)
        {
            lock (gate) { if (file.games.Remove(GameId(appid))) SaveUtil.AtomicWrite(path, Json.Serialize(file)); }
        }
    }

    // ---------- Discovery ----------
    static class SaveStatus
    {
        public const string Found = "found", Empty = "empty", Missing = "missing", Unavailable = "unavailable",
            Ambiguous = "ambiguous", Unsupported = "unsupported", Denied = "denied", TooBroad = "tooBroad", Overlapped = "overlapped";
    }
    sealed class SaveFile { public int Root; public string Relative, FullPath; public long Size, Ticks; }
    sealed class SaveRoot
    {
        public string Id, Source, Rule, Path, Base, State = SaveStatus.Missing, Note;
        public List<string> Patterns = new List<string>();   // relative glob patterns; "**" = everything
        public List<string> Candidates = new List<string>(); // other anchors for the same id (e.g. several libraries)
        public bool Exists; public int Files; public long Bytes;
        internal bool UserWildcard;
        // True when the relative archive path is inside one of this root's patterns.
        internal bool Covers(string relative)
        {
            var parts = relative.Split('/');
            foreach (string pattern in Patterns)
            {
                if (pattern == "**") return true;
                if (SaveResolver.Match(pattern.Split('/'), 0, parts, 0)) return true;
            }
            return false;
        }
        internal bool Restorable { get { return Path != null && (State == SaveStatus.Found || State == SaveStatus.Empty || State == SaveStatus.Missing || State == SaveStatus.Overlapped); } }
    }
    sealed class SaveDiscovery
    {
        public string GameId, AppId, Name, ManifestVersion, Status = SaveStatus.Unsupported, OverrideMode;
        public List<SaveRoot> Roots = new List<SaveRoot>();
        public List<string> Unsupported = new List<string>(), Ignored = new List<string>(), Missing = new List<string>(), Denied = new List<string>(), Skipped = new List<string>();
        public List<SaveFile> Files = new List<SaveFile>();
        public int FileCount { get { return Files.Count; } }
        public long Bytes { get { return Files.Sum(f => f.Size); } }
        public string Sources { get { return string.Join(", ", Roots.Where(r => r.Files > 0 || r.Exists).Select(r => r.Source).Distinct().ToArray()); } }
        public string Summary()
        {
            var b = new StringBuilder();
            b.Append(GameId).Append(": ").Append(Status).Append(", ").Append(FileCount).Append(" files, ").Append(Bytes).Append(" bytes, manifest ").Append(ManifestVersion ?? "none").Append('\n');
            foreach (var r in Roots) b.Append("  ").Append(r.State).Append(' ').Append(r.Id).Append(" -> ").Append(r.Path ?? "(unresolved)").Append(" [").Append(string.Join(", ", r.Patterns.ToArray())).Append("] ").Append(r.Files).Append(" files").Append(r.Note == null ? "" : " (" + r.Note + ")").Append('\n');
            foreach (string s in Unsupported) b.Append("  unsupported: ").Append(s).Append('\n');
            foreach (string s in Denied) b.Append("  denied: ").Append(s).Append('\n');
            foreach (string s in Skipped) b.Append("  skipped: ").Append(s).Append('\n');
            return b.ToString();
        }
    }
    sealed class SaveLimits
    {
        public int MaxDepth = 16, MaxEntriesPerRoot = 20000, MaxFiles = 10000, MaxExpansions = 64;
        public long MaxBytes = 512L * 1024 * 1024, MaxFileBytes = 90L * 1024 * 1024;
    }

    static class SaveResolver
    {
        static readonly Regex Placeholder = new Regex("<([A-Za-z]+)>");
        internal static SaveDiscovery Discover(string appid, SaveManifestIndex index, string manifestVersion, SaveEnvironment env, SaveOverride ov, SaveLimits limits)
        {
            if (!Regex.IsMatch(appid ?? "", "^[0-9]{1,10}$")) throw new ArgumentException("Invalid appid");
            limits = limits ?? new SaveLimits();
            var d = new SaveDiscovery { GameId = SaveOverrides.GameId(appid), AppId = appid, ManifestVersion = manifestVersion, OverrideMode = ov == null ? null : ov.mode };
            bool replace = ov != null && ov.mode == "replace" && ov.paths != null && ov.paths.Count > 0;
            if (!replace)
            {
                if (index == null) d.Unsupported.Add("Ludusavi manifest not available yet");
                else
                {
                    var games = index.Lookup(appid);
                    if (games.Count == 0) d.Unsupported.Add("No Ludusavi manifest entry for this AppID");
                    foreach (var game in games)
                    {
                        if (d.Name == null) d.Name = game.Name;
                        foreach (var rule in game.Rules)
                        {
                            if (!rule.AppliesTo("windows", "steam")) continue;
                            if (rule.Registry) { if (rule.Save) d.Unsupported.Add("registry: " + rule.Path); continue; }
                            if (!rule.Save) { d.Ignored.Add((rule.Config ? "config only: " : "untagged: ") + rule.Path); continue; }
                            AddManifestRule(d, appid, game, rule.Path, env, limits);
                        }
                    }
                }
                if (!string.IsNullOrEmpty(env.OnlineFixRoot))
                {
                    string anchor = Path.Combine(env.OnlineFixRoot, appid, "Saves");
                    d.Roots.Add(new SaveRoot { Id = "onlinefix:Saves", Source = "onlinefix", Rule = "<OnlineFix>/" + appid + "/Saves", Path = anchor, Base = env.OnlineFixRoot, Patterns = { "**" } });
                }
            }
            if (ov != null && ov.paths != null)
                for (int i = 0; i < ov.paths.Count; i++)
                    d.Roots.Add(new SaveRoot { Id = "override:" + (i + 1), Source = "override", Rule = ov.paths[i], Path = ov.paths[i], Base = Path.GetPathRoot(ov.paths[i]), Patterns = { "**" } });
            foreach (var root in d.Roots) if (root.Source != "manifest") CheckBroad(root, env);
            Scan(d, env, limits);
            return d;
        }

        static string Normalize(string rule)
        {
            string p = rule.Replace('\\', '/');
            while (p.Contains("//")) p = p.Replace("//", "/");
            string[,] rewrites = { { "<home>/AppData/LocalLow", "<winLocalAppDataLow>" }, { "<home>/AppData/Roaming", "<winAppData>" }, { "<home>/AppData/Local", "<winLocalAppData>" }, { "<home>/Documents", "<winDocuments>" } };
            for (int i = 0; i < rewrites.GetLength(0); i++)
            {
                string from = rewrites[i, 0];
                if (p.StartsWith(from, StringComparison.OrdinalIgnoreCase) && (p.Length == from.Length || p[from.Length] == '/')) { p = rewrites[i, 1] + p.Substring(from.Length); break; }
            }
            return p.TrimEnd('/');
        }
        static bool HasGlob(string segment) { return segment.IndexOf('*') >= 0 || segment.IndexOf('?') >= 0; }

        static void AddManifestRule(SaveDiscovery d, string appid, SaveManifestGame game, string raw, SaveEnvironment env, SaveLimits limits)
        {
            string rule = Normalize(raw);
            var segments = rule.Split('/');
            foreach (Match m in Placeholder.Matches(rule))
            {
                string p = m.Groups[1].Value;
                if (p == "root" || p == "game" || p == "base" || p == "storeGameId" || p == "storeUserId") continue;
                if (env.Folder(p) == null) { d.Unsupported.Add("<" + p + "> is not supported on Windows: " + raw); return; }
            }
            if (segments.Length == 0 || !segments[0].StartsWith("<")) { d.Unsupported.Add("absolute or relative path without a placeholder: " + raw); return; }
            if (segments.Length == 1)
            {
                // A bare placeholder (<home>, <base>, <winDocuments>) would capture a whole folder.
                if (!d.Roots.Any(r => r.Id == "manifest:" + rule)) d.Roots.Add(new SaveRoot { Id = "manifest:" + rule, Source = "manifest", Rule = raw, State = SaveStatus.TooBroad, Note = "would capture the whole " + rule });
                return;
            }
            bool userWildcard = rule.Contains("<storeUserId>") && string.IsNullOrEmpty(env.SteamAccountId);
            // Anchor: literal prefix; for a literal-only rule, its parent (the leaf becomes the pattern).
            int k = 0;
            while (k < segments.Length && !HasGlob(segments[k]) && !(userWildcard && segments[k].Contains("<storeUserId>"))) k++;
            if (k == segments.Length) k = segments.Length - 1;
            if (k == 0) { d.Unsupported.Add("pattern starts with a wildcard: " + raw); return; }
            string id = "manifest:" + string.Join("/", segments, 0, k);
            string pattern = string.Join("/", segments, k, segments.Length - k);
            int literal = k; while (literal < segments.Length && !HasGlob(segments[literal]) && !(userWildcard && segments[literal].Contains("<storeUserId>"))) literal++;

            // Expand multi-valued placeholders (<root>, <game>, <base>) into concrete anchors.
            var combos = new List<Dictionary<string, string>> { new Dictionary<string, string>() };
            bool needsRoot = rule.Contains("<root>"), needsGame = rule.Contains("<game>") || rule.Contains("<base>");
            if (needsRoot || needsGame)
            {
                combos.Clear();
                var libs = env.Libraries.ToList();
                if (libs.Count == 0) { d.Unsupported.Add("Steam library not found: " + raw); return; }
                KeyValuePair<string, string> installed; bool isInstalled = env.Installed.TryGetValue(appid, out installed);
                foreach (string lib in libs)
                {
                    var names = new List<string>();
                    if (needsGame)
                    {
                        if (isInstalled && SaveUtil.PathComparer.Equals(SaveUtil.Full(installed.Key), SaveUtil.Full(lib))) names.Add(installed.Value);
                        else if (!isInstalled) names.AddRange(game.InstallDirs.Where(n => SaveUtil.SafeRelative(n)));
                        if (names.Count == 0) continue;
                    }
                    else names.Add("");
                    foreach (string n in names.Distinct(SaveUtil.PathComparer))
                    {
                        if (combos.Count >= limits.MaxExpansions) break;
                        combos.Add(new Dictionary<string, string> { { "root", lib }, { "game", n }, { "base", needsGame ? Path.Combine(lib, "steamapps", "common", n) : null } });
                    }
                }
                if (combos.Count == 0) { d.Missing.Add(raw + " (game not installed)"); AddRoot(d, id, "manifest", raw, pattern, null, null, userWildcard, "game not installed"); return; }
            }
            foreach (var combo in combos)
            {
                Func<string, string> sub = delegate(string seg)
                {
                    return Placeholder.Replace(seg, delegate(Match m)
                    {
                        string p = m.Groups[1].Value, v;
                        if (combo.TryGetValue(p, out v) && v != null) return v;
                        if (p == "storeGameId") return appid;
                        if (p == "storeUserId") return userWildcard ? "*" : env.SteamAccountId;
                        return env.Folder(p) ?? m.Value;
                    });
                };
                string anchor = JoinLocal(segments.Take(k).Select(sub));
                string baseFolder = JoinLocal(new[] { sub(segments[0]) });
                string subPattern = string.Join("/", segments.Skip(k).Select(sub).ToArray());
                // Breadth: the literal capture target (before any wildcard) must not contain a
                // protected folder, unless a narrow final pattern like "*.sav" limits it. A broad
                // rule is refused on its own; other rules sharing the root id still apply.
                // An unknown <storeUserId> is one account folder, not a wildcard over userdata.
                int wide = 0; while (wide < segments.Length && !HasGlob(segments[wide])) wide++;
                Func<string, string> one = delegate(string seg) { return sub(seg.Replace("<storeUserId>", "0")); };
                string target = JoinLocal(segments.Take(wide).Select(one));
                string rest = string.Join("/", segments.Skip(wide).Select(one).ToArray());
                if (IsBroad(target, env, true) && (rest.Length == 0 || !NarrowPattern(rest)))
                {
                    if (!d.Roots.Any(r => r.Id == id)) d.Roots.Add(new SaveRoot { Id = id, Source = "manifest", Rule = raw, Path = anchor, Base = baseFolder, State = SaveStatus.TooBroad, Note = "would capture " + target });
                    else d.Skipped.Add("too broad: " + raw);
                    continue;
                }
                AddRoot(d, id, "manifest", raw, subPattern, anchor, baseFolder, userWildcard, null);
            }
        }
        static string JoinLocal(IEnumerable<string> parts)
        {
            string result = null;
            foreach (string part in parts) result = result == null ? part : Path.Combine(result, part.Replace('/', Path.DirectorySeparatorChar));
            return result == null ? null : result.Replace('/', Path.DirectorySeparatorChar);
        }
        // Several rules and several libraries can share one logical root id.
        static SaveRoot AddRoot(SaveDiscovery d, string id, string source, string rule, string pattern, string anchor, string baseFolder, bool userWildcard, string note)
        {
            var root = d.Roots.FirstOrDefault(r => r.Id == id && (anchor == null || r.Path == null || SaveUtil.PathComparer.Equals(r.Path, anchor)));
            if (root == null)
            {
                root = d.Roots.FirstOrDefault(r => r.Id == id);
                if (root != null) { if (anchor != null && !root.Candidates.Contains(anchor, SaveUtil.PathComparer)) root.Candidates.Add(anchor); }
                else { root = new SaveRoot { Id = id, Source = source, Rule = rule, Path = anchor, Base = baseFolder, Note = note, UserWildcard = userWildcard }; d.Roots.Add(root); }
            }
            if (root.State == SaveStatus.TooBroad) { root.State = SaveStatus.Missing; root.Note = "a broader rule for this folder was refused"; root.Path = anchor; root.Base = baseFolder; }
            if (root.Path == null && anchor != null) { root.Path = anchor; root.Base = baseFolder; root.Note = null; }
            if (!root.Patterns.Contains(pattern)) root.Patterns.Add(pattern);
            root.UserWildcard |= userWildcard;
            return root;
        }
        static bool NarrowPattern(string pattern)
        {
            // e.g. "*.sav": one segment with at least three literal characters, no recursion.
            if (pattern.Contains("/") || pattern.Contains("**")) return false;
            return pattern.Count(c => c != '*' && c != '?') >= 3;
        }
        internal static bool IsBroad(string target, SaveEnvironment env, bool includeStructure)
        {
            if (string.IsNullOrEmpty(target)) return true;
            string full;
            try { full = SaveUtil.Full(target); } catch (Exception) { return true; }
            if (full.Length <= 3 || SaveUtil.PathComparer.Equals(full, SaveUtil.Full(Path.GetPathRoot(full)))) return true;
            var folders = env.BroadFolders();
            if (includeStructure) folders.AddRange(env.StructureFolders());
            foreach (string f in folders)
            {
                if (string.IsNullOrEmpty(f)) continue;
                try { if (SaveUtil.Within(f, full)) return true; } catch (Exception) { }
            }
            return false;
        }
        // Override and OnlineFix roots capture a whole folder.
        static void CheckBroad(SaveRoot root, SaveEnvironment env)
        {
            if (root.Path != null && root.Patterns.Contains("**") && IsBroad(root.Path, env, true)) { root.State = SaveStatus.TooBroad; root.Note = "would capture " + root.Path; }
        }

        static void Scan(SaveDiscovery d, SaveEnvironment env, SaveLimits limits)
        {
            long total = 0;
            var claimed = new HashSet<string>(SaveUtil.PathComparer);
            // Shortest anchor first, so overlapping roots keep files under the widest safe root.
            var order = d.Roots.Select((r, i) => new { r, i }).OrderBy(x => x.r.Path == null ? int.MaxValue : x.r.Path.Length).ThenBy(x => x.i).Select(x => x.r).ToList();
            foreach (var root in order)
            {
                if (root.Path == null || root.State == SaveStatus.TooBroad || root.State == SaveStatus.Unsupported) continue;
                var anchors = new List<string> { root.Path }; anchors.AddRange(root.Candidates);
                var results = new List<KeyValuePair<string, List<SaveFile>>>();
                bool anyExists = false, baseExists = false, denied = false;
                foreach (string anchor in anchors)
                {
                    var files = new List<SaveFile>(); var ctx = new ScanContext { Limits = limits, Discovery = d };
                    bool exists = false;
                    try
                    {
                        if (Directory.Exists(anchor))
                        {
                            if (SaveUtil.IsLink(anchor)) { d.Skipped.Add("link: " + anchor); continue; }
                            foreach (string pattern in root.Patterns)
                            {
                                if (pattern == "**") { exists = true; Walk(anchor, "", files, ctx, 0); continue; }
                                Expand(anchor, "", pattern.Split('/'), 0, files, ctx, ref exists);
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { d.Denied.Add(anchor); denied = true; }
                    catch (IOException ex) { d.Denied.Add(anchor + " (" + ex.Message + ")"); denied = true; }
                    if (ctx.Denied) denied = true;
                    anyExists |= exists;
                    try { baseExists |= root.Base == null || Directory.Exists(root.Base); } catch (Exception) { }
                    if (exists) results.Add(new KeyValuePair<string, List<SaveFile>>(anchor, files));
                }
                var withFiles = results.Where(r => r.Value.Count > 0).ToList();
                if (withFiles.Count > 1 || (root.UserWildcard && withFiles.Count == 1 && DistinctUsers(root, withFiles[0].Value) > 1))
                {
                    root.State = SaveStatus.Ambiguous; root.Exists = true;
                    root.Note = withFiles.Count > 1 ? "several folders hold saves: " + string.Join(" | ", withFiles.Select(w => w.Key).ToArray()) : "several Steam accounts hold saves";
                    continue;
                }
                if (withFiles.Count == 1) root.Path = withFiles[0].Key;
                else if (results.Count > 0) root.Path = results[0].Key;
                root.Exists = anyExists;
                var chosen = withFiles.Count == 1 ? withFiles[0].Value : new List<SaveFile>();
                int rootIndex = d.Roots.IndexOf(root), kept = 0; long bytes = 0;
                foreach (var f in chosen.OrderBy(f => f.Relative, StringComparer.Ordinal))
                {
                    if (!claimed.Add(f.FullPath)) continue;
                    if (d.Files.Count >= limits.MaxFiles) { d.Skipped.Add("file limit reached (" + limits.MaxFiles + ")"); break; }
                    if (total + f.Size > limits.MaxBytes) { d.Skipped.Add("size limit reached: " + f.FullPath); break; }
                    f.Root = rootIndex; d.Files.Add(f); kept++; bytes += f.Size; total += f.Size;
                }
                root.Files = kept; root.Bytes = bytes;
                if (kept > 0) root.State = SaveStatus.Found;
                else if (chosen.Count > 0) root.State = SaveStatus.Overlapped;
                else if (denied && !anyExists) root.State = SaveStatus.Denied;
                else if (anyExists) root.State = SaveStatus.Empty;
                else if (!baseExists) { root.State = SaveStatus.Unavailable; root.Note = root.Note ?? "base folder not present: " + root.Base; }
                else { root.State = SaveStatus.Missing; d.Missing.Add(root.Path); }
            }
            d.Files = d.Files.OrderBy(f => f.Root).ThenBy(f => f.Relative, StringComparer.Ordinal).ToList();
            if (d.Roots.Any(r => r.State == SaveStatus.Ambiguous)) d.Status = SaveStatus.Ambiguous;
            else if (d.Files.Count > 0) d.Status = SaveStatus.Found;
            else if (d.Roots.Any(r => r.State == SaveStatus.Empty || r.State == SaveStatus.Overlapped)) d.Status = SaveStatus.Empty;
            // Only fallback roots (OnlineFix, overrides) are missing and the manifest has no usable
            // rule: the real location is unknown or unsupported, not merely absent.
            else if (d.Roots.Any(r => r.State == SaveStatus.Missing) && (d.Roots.Any(r => r.State == SaveStatus.Missing && r.Source == "manifest") || d.Unsupported.Count == 0)) d.Status = SaveStatus.Missing;
            else if (d.Roots.Any(r => r.State == SaveStatus.Unavailable || r.State == SaveStatus.Denied)) d.Status = SaveStatus.Unavailable;
            else d.Status = SaveStatus.Unsupported;
        }
        static int DistinctUsers(SaveRoot root, List<SaveFile> files)
        {
            // The first wildcard segment of a <storeUserId> rule is the account folder.
            return files.Select(f => f.Relative.Split('/')[0]).Distinct(SaveUtil.PathComparer).Count();
        }
        sealed class ScanContext { public SaveLimits Limits; public SaveDiscovery Discovery; public int Entries; public bool Denied; }
        static bool Count(ScanContext ctx)
        {
            if (++ctx.Entries > ctx.Limits.MaxEntriesPerRoot) { ctx.Discovery.Skipped.Add("scan limit reached (" + ctx.Limits.MaxEntriesPerRoot + " entries)"); return false; }
            return true;
        }
        static void AddFile(string full, string rel, List<SaveFile> files, ScanContext ctx)
        {
            if (SaveUtil.IsLink(full)) { ctx.Discovery.Skipped.Add("link: " + full); return; }
            if (!SaveUtil.SafeRelative(rel)) { ctx.Discovery.Skipped.Add("unsafe name: " + full); return; }
            var info = new FileInfo(full);
            if (info.Length > ctx.Limits.MaxFileBytes) { ctx.Discovery.Skipped.Add("file too large: " + full); return; }
            files.Add(new SaveFile { Relative = rel, FullPath = info.FullName, Size = info.Length, Ticks = info.LastWriteTimeUtc.Ticks });
        }
        internal static Func<string, bool> DenyForTest;   // self-test hook when ACLs cannot be enforced
        static void Walk(string dir, string rel, List<SaveFile> files, ScanContext ctx, int depth)
        {
            if (depth > ctx.Limits.MaxDepth) { ctx.Discovery.Skipped.Add("too deep: " + dir); return; }
            string[] entries;
            try { if (DenyForTest != null && DenyForTest(dir)) throw new UnauthorizedAccessException(); entries = Directory.GetFileSystemEntries(dir); }
            catch (UnauthorizedAccessException) { ctx.Discovery.Denied.Add(dir); ctx.Denied = true; return; }
            foreach (string e in entries.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!Count(ctx)) return;
                string name = Path.GetFileName(e), r = rel.Length == 0 ? name : rel + "/" + name;
                if (Directory.Exists(e)) { if (SaveUtil.IsLink(e)) ctx.Discovery.Skipped.Add("link: " + e); else Walk(e, r, files, ctx, depth + 1); }
                else
                {
                    try { AddFile(e, r, files, ctx); }
                    catch (UnauthorizedAccessException) { ctx.Discovery.Denied.Add(e); ctx.Denied = true; }
                    catch (IOException) { ctx.Discovery.Denied.Add(e); ctx.Denied = true; }
                }
            }
        }
        static void Expand(string dir, string rel, string[] pattern, int i, List<SaveFile> files, ScanContext ctx, ref bool exists)
        {
            if (rel.Split('/').Length > ctx.Limits.MaxDepth) return;
            if (i == pattern.Length)
            {
                // A trailing "**" matched this folder itself.
                exists = true;
                if (SaveUtil.IsLink(dir)) ctx.Discovery.Skipped.Add("link: " + dir); else Walk(dir, rel, files, ctx, rel.Split('/').Length);
                return;
            }
            string seg = pattern[i];
            if (seg == "**")
            {
                Expand(dir, rel, pattern, i + 1, files, ctx, ref exists);
                string[] subdirs;
                try { subdirs = Directory.GetDirectories(dir); } catch (UnauthorizedAccessException) { ctx.Discovery.Denied.Add(dir); ctx.Denied = true; return; }
                foreach (string sd in subdirs.OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (!Count(ctx)) return;
                    if (SaveUtil.IsLink(sd)) continue;
                    string name = Path.GetFileName(sd);
                    Expand(sd, rel.Length == 0 ? name : rel + "/" + name, pattern, i, files, ctx, ref exists);
                }
                return;
            }
            IEnumerable<string> matches;
            if (!HasGlob(seg))
            {
                string p = Path.Combine(dir, seg);
                matches = (Directory.Exists(p) || File.Exists(p)) ? new[] { p } : new string[0];
            }
            else
            {
                try { var rx = SegmentRegex(seg); matches = Directory.GetFileSystemEntries(dir).Where(e => rx.IsMatch(Path.GetFileName(e))).OrderBy(x => x, StringComparer.Ordinal).ToList(); }
                catch (UnauthorizedAccessException) { ctx.Discovery.Denied.Add(dir); ctx.Denied = true; return; }
            }
            foreach (string m in matches)
            {
                if (!Count(ctx)) return;
                string name = Path.GetFileName(m), r = rel.Length == 0 ? name : rel + "/" + name;
                if (Directory.Exists(m))
                {
                    if (SaveUtil.IsLink(m)) { ctx.Discovery.Skipped.Add("link: " + m); continue; }
                    if (i + 1 == pattern.Length) { exists = true; Walk(m, r, files, ctx, r.Split('/').Length); }
                    else Expand(m, r, pattern, i + 1, files, ctx, ref exists);
                }
                else if (i + 1 == pattern.Length)
                {
                    exists = true;
                    try { AddFile(m, r, files, ctx); }
                    catch (UnauthorizedAccessException) { ctx.Discovery.Denied.Add(m); ctx.Denied = true; }
                }
            }
        }
        static readonly Dictionary<string, Regex> Regexes = new Dictionary<string, Regex>();
        static Regex SegmentRegex(string seg)
        {
            lock (Regexes)
            {
                Regex rx;
                if (!Regexes.TryGetValue(seg, out rx))
                {
                    var b = new StringBuilder("^");
                    foreach (char c in seg) b.Append(c == '*' ? "[^/\\\\]*" : c == '?' ? "[^/\\\\]" : Regex.Escape(c.ToString()));
                    rx = new Regex(b.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (Regexes.Count < 4096) Regexes[seg] = rx;
                }
                return rx;
            }
        }
        // Pattern match of relative path parts; a pattern that matches a prefix covers the subtree.
        internal static bool Match(string[] pattern, int i, string[] parts, int j)
        {
            if (i == pattern.Length) return true;
            if (pattern[i] == "**")
            {
                for (int k = j; k <= parts.Length; k++) if (Match(pattern, i + 1, parts, k)) return true;
                return false;
            }
            if (j == parts.Length) return false;
            bool ok = HasGlob(pattern[i]) ? SegmentRegex(pattern[i]).IsMatch(parts[j]) : string.Equals(pattern[i], parts[j], StringComparison.OrdinalIgnoreCase);
            return ok && Match(pattern, i + 1, parts, j + 1);
        }
    }
}
