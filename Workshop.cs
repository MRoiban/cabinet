// Automatic OnlineFix UGC Workshop links, plus optional native-loader profiles. C# 5 / .NET 4.
// Wiring (Cabinet.cs is deliberately not edited by this component):
//   constructor, before first Tick: WorkshopLinker.Initialize(Log);
//   tray Tick, after var running = playtime.RunningApps():
//     var workshopMsg = WorkshopLinker.Tick(running);
//     if (workshopMsg != null) tray.ShowBalloonTip(5000, "Cabinet", workshopMsg, ToolTipIcon.Info);
//   Main, before normal startup:
//     if (args.Length > 0 && args[0] == "--workshop") { WorkshopLinker.RunCli(args); return; }
// CLI: --workshop is READ ONLY; --workshop --link applies generic links and profiles.
//   --library <root> / --data-dir <dir> support isolated fixture testing.
// Tick is synchronous, throttled to 10s for watcher changes / 60s otherwise.
// Profile download runs in the background, once/day, with last-good cache and
// built-in fallback. No remote code, arbitrary absolute paths, or INI changes.
// OnlineFix's UGC guide requires Workshop/<itemid> beside the main exe and
// [Interfaces] UGC=true. We use the directory containing OnlineFix(64).dll
// (also the inner executable directory for Unreal games); INI files stay intact.
// https://online-fix.me/guides/17021-kak-ustanovit-mody-iz-masterskoy-steam-how-to-install-steam-workshop-mods.html
// Generic links need no profile. Profiled games (STS2) use only their native mods
// loader: with both, STS2 crashed at startup inside OnlineFix's UGC emulation.
// This component does not download subscriptions or enable mods in-game.
// Ownership journal: DataDir/workshop-links.json. Existing links are never
// adopted, replaced or deleted. Only owned matching NTFS junctions whose target
// is definitely missing are removed, using the verified junction handle.
// Steam itself can update junction targets while playing; Cabinet defers its
// own mutations using real/fake running IDs and an independent process check.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Cabinet
{
    static class WorkshopLinker
    {
        internal const string ProfilesUrl = "https://raw.githubusercontent.com/MRoiban/cabinet/main/workshop-profiles.json";
        static WorkshopEngine engine;
        static Action<string> logger = delegate { };
        static readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        static DateTime nextScan, nextWatch, nextFetch;
        static int dirty = 1, fetching;
        static readonly object gate = new object();

        public static void Initialize(Action<string> log)
        {
            lock (gate) { logger = log ?? delegate { }; if (engine == null) engine = new WorkshopEngine(App.DataDir, logger); }
        }

        public static string Tick(ICollection<string> runningAppIds)
        {
            lock (gate)
            {
                try
                {
                    if (engine == null) Initialize(logger);
                    DateTime now = DateTime.UtcNow;
                    TrackSessions(runningAppIds, now);
                    FetchProfiles(now);
                    if (now < nextScan && (VolatileReadDirty() == 0 || now < nextScan.AddSeconds(-50))) return null;
                    var libs = Playtime.Libraries().Select(WorkshopEngine.Full).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (now >= nextWatch) { Watch(libs); nextWatch = now.AddMinutes(5); }
                    Interlocked.Exchange(ref dirty, 0);
                    nextScan = now.AddSeconds(60);
                    engine.LoadProfiles();
                    return engine.Scan(libs, runningAppIds, false);
                }
                catch (Exception ex) { logger(L.T("Workshop scan failed: ", "Échec de l'analyse du Workshop : ") + ex.Message); nextScan = DateTime.UtcNow.AddSeconds(60); return null; }
            }
        }

        // A Workshop link that makes the game close within a minute, twice in a row,
        // is treated as incompatible: the next scan removes it and never recreates it.
        static readonly Dictionary<string, DateTime> sessionStart = new Dictionary<string, DateTime>();
        static readonly Dictionary<string, int> quickExits = new Dictionary<string, int>();
        static void TrackSessions(ICollection<string> running, DateTime now)
        {
            if (running == null) return;
            foreach (string id in running) if (!sessionStart.ContainsKey(id)) sessionStart[id] = now;
            foreach (string id in sessionStart.Keys.Where(k => !running.Contains(k)).ToArray())
            {
                TimeSpan length = now - sessionStart[id];
                sessionStart.Remove(id);
                if (!engine.HasRootLink(id)) continue;
                int count;
                quickExits.TryGetValue(id, out count);
                quickExits[id] = count = length < TimeSpan.FromSeconds(60) ? count + 1 : 0;
                if (count < 2) continue;
                logger(L.T("Workshop link disabled for app ", "Lien Workshop désactivé pour l'app ") + id + L.T(": the game closed right after launch twice.", " : le jeu s'est fermé juste après le lancement deux fois."));
                engine.Disable(id);
                Interlocked.Exchange(ref dirty, 1); nextScan = now;
            }
        }

        static int VolatileReadDirty() { return Interlocked.CompareExchange(ref dirty, 0, 0); }
        static void Changed(object sender, FileSystemEventArgs e) { Interlocked.Exchange(ref dirty, 1); }
        static void Watch(IEnumerable<string> libraries)
        {
            foreach (var watcher in watchers) watcher.Dispose();
            watchers.Clear();
            foreach (string lib in libraries)
            {
                string root = Path.Combine(lib, @"steamapps\workshop\content");
                if (!Directory.Exists(root)) continue;
                try
                {
                    var w = new FileSystemWatcher(root);
                    w.IncludeSubdirectories = true;
                    w.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite;
                    w.Created += Changed; w.Deleted += Changed; w.Renamed += Changed; w.Changed += Changed;
                    w.Error += delegate { Interlocked.Exchange(ref dirty, 1); };
                    w.EnableRaisingEvents = true;
                    watchers.Add(w);
                }
                catch (Exception ex) { logger(L.T("Workshop watcher unavailable; periodic scans remain active: ", "Surveillance du Workshop indisponible ; les analyses périodiques restent actives : ") + ex.Message); }
            }
        }

        static void FetchProfiles(DateTime now)
        {
            if (now < nextFetch || Interlocked.CompareExchange(ref fetching, 1, 0) != 0) return;
            nextFetch = now.AddDays(1);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // Explicit TLS 1.2 for the .NET Framework 4 runtime used by build.cmd.
                    var request = (HttpWebRequest)WebRequest.Create(ProfilesUrl);
                    request.Timeout = 10000; request.ReadWriteTimeout = 10000;
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                    using (var response = request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        char[] buffer = new char[262145]; int count = 0, n;
                        while (count < buffer.Length && (n = reader.Read(buffer, count, buffer.Length - count)) > 0) count += n;
                        if (count >= buffer.Length) throw new InvalidDataException();
                        string json = new string(buffer, 0, count);
                        WorkshopEngine.ParseProfiles(json); // Validate before replacing last-good cache.
                        lock (gate)
                        {
                            WorkshopEngine.AtomicWrite(Path.Combine(App.DataDir, "workshop-profiles.json"), json);
                            Interlocked.Exchange(ref dirty, 1);
                        }
                    }
                }
                catch { /* Offline/404/invalid response: retain the last validated cache. */ }
                finally { Interlocked.Exchange(ref fetching, 0); }
            });
        }

        public static void RunCli(string[] args)
        {
            try
            {
                bool apply = false; string data = App.DataDir;
                var libs = new List<string>();
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i] == "--link") apply = true;
                    else if (args[i] == "--library" && i + 1 < args.Length) libs.Add(WorkshopEngine.Full(args[++i]));
                    else if (args[i] == "--data-dir" && i + 1 < args.Length) data = WorkshopEngine.Full(args[++i]);
                    else throw new ArgumentException(L.T("Usage: --workshop [--link] [--library <root>] [--data-dir <dir>]", "Utilisation : --workshop [--link] [--library <racine>] [--data-dir <dossier>]"));
                }
                if (libs.Count == 0) libs.AddRange(Playtime.Libraries());
                var e = new WorkshopEngine(data, Console.WriteLine);
                e.LoadProfiles();
                Console.WriteLine(L.T(apply ? "Workshop: applying OnlineFix links and optional profiles." : "Workshop: read-only scan.", apply ? "Workshop : application des liens OnlineFix et des profils facultatifs." : "Workshop : analyse en lecture seule."));
                var message = e.Scan(libs, new HashSet<string>(), !apply);
                if (message != null) Console.WriteLine(message);
            }
            catch (Exception ex) { Console.WriteLine(L.T("Workshop failed: ", "Échec du Workshop : ") + ex.Message); Environment.ExitCode = 1; }
        }
    }

    // Internal engine accepts explicit roots so tests never have to redirect Steam.
    sealed class WorkshopEngine
    {
        internal sealed class Profile { public string name { get; set; } public string modFolder { get; set; } }
        internal sealed class OwnedLink
        {
            public bool Created { get; set; }
            public string AppId { get; set; }
            public string Game { get; set; }
            public string Destination { get; set; }
            public string Target { get; set; }
        }
        readonly string dataDir, journal, disabledFile;
        readonly Action<string> log;
        // Apps whose OnlineFix Workshop link made the game close right after launch.
        readonly HashSet<string> disabled = new HashSet<string>();
        // Static: engines are recreated per scan, and status lines should appear once per session, not every scan.
        static readonly HashSet<string> reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Profile> profiles;
        List<OwnedLink> owned;
        bool stateValid = true;
        readonly Dictionary<string, HashSet<string>> executableNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        internal WorkshopEngine(string data, Action<string> logger)
        {
            dataDir = Full(data); journal = Path.Combine(dataDir, "workshop-links.json"); log = logger;
            disabledFile = Path.Combine(dataDir, "workshop-disabled.txt");
            try { if (File.Exists(disabledFile)) foreach (string line in File.ReadAllLines(disabledFile)) if (Numeric(line.Trim())) disabled.Add(line.Trim()); }
            catch (IOException) { }
            LoadOwned();
            LoadProfiles();
        }
        void LoadOwned()
        {
            stateValid = true;
            owned = new List<OwnedLink>();
            if (File.Exists(journal))
            {
                try
                {
                    owned = new JavaScriptSerializer().Deserialize<List<OwnedLink>>(File.ReadAllText(journal));
                    if (owned == null || owned.Any(x => x == null || !Numeric(x.AppId) || string.IsNullOrEmpty(x.Game) || string.IsNullOrEmpty(x.Destination) || string.IsNullOrEmpty(x.Target))) throw new InvalidDataException();
                }
                catch (Exception ex) { owned = new List<OwnedLink>(); stateValid = false; log(L.T("Workshop ownership journal unreadable; writes disabled: ", "Journal des liens Workshop illisible ; écritures désactivées : ") + ex.Message); }
            }
        }
        internal static bool Numeric(string s) { return s != null && Regex.IsMatch(s, @"^[1-9][0-9]{0,19}$"); }
        internal static string Full(string p)
        {
            string full = Path.GetFullPath(p);
            return full.Length == Path.GetPathRoot(full).Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        static bool Same(string a, string b) { return string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase); }
        static bool Inside(string path, string root) { return Full(path).StartsWith(Full(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
        internal static Dictionary<string, Profile> ParseProfiles(string json)
        {
            var result = new JavaScriptSerializer().Deserialize<Dictionary<string, Profile>>(json);
            if (result == null || result.Count > 10000) throw new InvalidDataException();
            foreach (var pair in result)
            {
                var p = pair.Value;
                if (!Numeric(pair.Key) || p == null || string.IsNullOrWhiteSpace(p.modFolder) || p.modFolder.Length > 180 || Path.IsPathRooted(p.modFolder)) throw new InvalidDataException();
                // Every component must be an ordinary Win32 folder, without traversal,
                // device names, ADS, trailing-dot aliases or alternate separators.
                foreach (string part in p.modFolder.Split(new char[] { '\\', '/' }))
                {
                    if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                        Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) throw new InvalidDataException();
                }
                if (p.name == null || p.name.Length > 200 || p.name.Any(char.IsControl)) throw new InvalidDataException();
            }
            return result;
        }
        internal void LoadProfiles()
        {
            profiles = ParseProfiles("{\"2868840\":{\"name\":\"Slay the Spire 2\",\"modFolder\":\"mods\"}}");
            foreach (string file in new string[] { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workshop-profiles.json"), Path.Combine(dataDir, "workshop-profiles.json") })
            {
                if (!File.Exists(file)) continue;
                try { foreach (var p in ParseProfiles(File.ReadAllText(file))) profiles[p.Key] = p.Value; }
                catch (Exception ex) { Once(file, L.T("Invalid Workshop profiles ignored: ", "Profils Workshop invalides ignorés : ") + file + " - " + ex.Message); }
            }
        }
        internal static void AtomicWrite(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { byte[] bytes = Encoding.UTF8.GetBytes(text); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        void Save() { AtomicWrite(journal, new JavaScriptSerializer().Serialize(owned)); }
        void Once(string key, string message) { if (reported.Add(key + "|" + message)) log(message); }

        // GetAttributes distinguishes an absent path from inaccessible storage and
        // sees dangling junctions that Directory.Exists alone can miss.
        internal static bool Exists(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        static bool Redirected(string path) { return Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        static bool SafeParents(string path)
        {
            for (string p = Full(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p)) if (Redirected(p)) return false;
            return true;
        }
        static IEnumerable<string> Inis(string dir)
        {
            string file = Path.Combine(dir, "OnlineFix.ini");
            if (File.Exists(file) && (File.Exists(Path.Combine(dir, "OnlineFix64.dll")) || File.Exists(Path.Combine(dir, "OnlineFix.dll")))) yield return file;
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { yield break; }
            catch (IOException) { yield break; }
            foreach (string child in children)
            {
                string name = Path.GetFileName(child);
                if (Redirected(child) || name.Equals("mods", StringComparison.OrdinalIgnoreCase) || name.Equals("workshop", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string found in Inis(child)) yield return found;
            }
        }
        static string IniValue(string text, string section, string key)
        {
            string active = "";
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) active = line.Substring(1, line.Length - 2);
                if (!active.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
                var m = Regex.Match(line, "^" + Regex.Escape(key) + @"\s*=\s*([0-9]+)\s*(?:[;#].*)?$", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }
        bool Running(string game, string app, string fake, ICollection<string> running)
        {
            if (running == null || running.Contains(app) || (fake != null && running.Contains(fake))) return true;
            Process[] processes = Process.GetProcesses();
            try
            {
                foreach (Process process in processes)
                {
                    try { var module = process.MainModule; if (module != null && Inside(module.FileName, game)) return true; }
                    catch (Win32Exception)
                    {
                        // If executable inspection is denied, matching any game executable
                        // by name is enough to defer (safe false positives are intentional).
                        try { if (executableNames[game].Contains(process.ProcessName)) return true; }
                        catch { return true; }
                    }
                    catch (InvalidOperationException) { } // process exited
                    catch (NotSupportedException) { return true; }
                }
            }
            finally { foreach (Process process in processes) process.Dispose(); }
            return false;
        }

        internal string Scan(IEnumerable<string> libraries, ICollection<string> running, bool readOnly)
        {
            // CLI and tray may overlap. Serialize journal ownership across processes,
            // without blocking the UI behind an active scan.
            string token;
            using (var hash = System.Security.Cryptography.SHA256.Create())
                token = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(journal.ToUpperInvariant()))).Replace("-", "");
            using (var mutex = new Mutex(false, @"Local\Cabinet.Workshop." + token))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                    if (!held) { log(L.T("Workshop scan deferred; another scan is active.", "Analyse Workshop différée ; une autre analyse est active.")); return null; }
                    LoadOwned(); executableNames.Clear();
                    return ScanCore(libraries, running, readOnly);
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }
        static void Executables(string dir, HashSet<string> names, int depth)
        {
            foreach (string exe in Directory.GetFiles(dir, "*.exe")) names.Add(Path.GetFileNameWithoutExtension(exe));
            foreach (string child in Directory.GetDirectories(dir))
            {
                string name = Path.GetFileName(child);
                if (Redirected(child) || name.Equals("mods", StringComparison.OrdinalIgnoreCase) || name.Equals("workshop", StringComparison.OrdinalIgnoreCase)) continue;
                Executables(child, names, depth + 1);
            }
        }
        string ScanCore(IEnumerable<string> libraries, ICollection<string> running, bool readOnly)
        {
            int created = 0, removed = 0; var names = new HashSet<string>();
            var roots = libraries.Select(Full).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string lib in roots)
            {
                string common = Path.Combine(lib, @"steamapps\common");
                if (!Directory.Exists(common)) continue;
                foreach (string game in Directory.GetDirectories(common))
                {
                    try
                    {
                        if (!SafeParents(game)) continue;
                        foreach (string ini in Inis(game))
                        {
                            string text = File.ReadAllText(ini), id = IniValue(text, "Main", "RealAppId"), fake = IniValue(text, "Main", "FakeAppId");
                            if (!Numeric(id)) continue;
                            Profile profile;
                            profiles.TryGetValue(id, out profile);
                            string title = profile == null ? Path.GetFileName(game) : profile.name;
                            var exeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            Executables(game, exeNames, 0); executableNames[game] = exeNames;
                            if (Running(game, id, fake, running)) { Once(game + "running", L.T("Workshop deferred while game is running: ", "Workshop différé pendant l'exécution du jeu : ") + title); continue; }
                            string fixDir = Path.GetDirectoryName(ini);
                            if (!InterfaceEnabled(text, "UGC"))
                                Once(ini + "ugc", L.T("OnlineFix Workshop requires [Interfaces] UGC=true; INI left unchanged: ", "Le Workshop OnlineFix nécessite [Interfaces] UGC=true ; INI laissé intact : ") + ini);
                            string fixWorkshop = Path.Combine(fixDir, "Workshop");
                            string mods = profile == null ? null : Full(Path.Combine(game, profile.modFolder));
                            bool nativeLoader = mods != null && Inside(mods, game) && !Same(mods, fixWorkshop);
                            // A game's own loader is proven; OnlineFix's UGC emulation crashed STS2 at
                            // startup whenever the Workshop folder was also present.
                            if (nativeLoader)
                                DropRootLink(game, id, fake, title, fixWorkshop, running, readOnly, L.T("Removed OnlineFix Workshop link; the game loads mods from its own folder: ", "Lien Workshop OnlineFix supprimé ; le jeu charge les mods depuis son propre dossier : "), ref removed, names);
                            else if (disabled.Contains(id))
                                DropRootLink(game, id, fake, title, fixWorkshop, running, readOnly, L.T("Removed OnlineFix Workshop link; the game kept closing right after launch with it: ", "Lien Workshop OnlineFix supprimé ; le jeu se fermait juste après le lancement avec ce lien : "), ref removed, names);
                            else
                                SyncFolder(game, id, fake, title, fixWorkshop, roots, running, readOnly, true, ref created, ref removed, names);
                            if (nativeLoader)
                                SyncFolder(game, id, fake, title, mods, roots, running, readOnly, false, ref created, ref removed, names);
                        }
                    }
                    catch (Exception ex) { Once(game + "error", L.T("Workshop game scan failed: ", "Échec de l'analyse Workshop du jeu : ") + game + " - " + ex.Message); }
                }
            }
            if (created == 0 && removed == 0) return null;
            return string.Format(L.T("Workshop: linked {0}, removed {1} for {2}", "Workshop : {0} liens créés, {1} supprimés pour {2}"), created, removed, string.Join(", ", names.ToArray()));
        }
        static bool InterfaceEnabled(string text, string key)
        {
            string active = "";
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) active = line.Substring(1, line.Length - 2);
                if (active.Equals("Interfaces", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(line, "^" + Regex.Escape(key) + @"\s*=\s*true\s*(?:[;#].*)?$", RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }
        void DropRootLink(string game, string id, string fake, string title, string folder, ICollection<string> running, bool readOnly, string reason, ref int removed, HashSet<string> names)
        {
            var link = owned.FirstOrDefault(x => x.Created && x.AppId == id && Same(x.Game, game) && Same(x.Destination, folder));
            if (link == null) return;
            string current = WorkshopJunction.Target(folder);
            if (current == null || !Same(current, link.Target)) return;
            if (readOnly) { log(L.T("Would remove Workshop folder link: ", "Suppression prévue du lien du dossier Workshop : ") + folder); return; }
            if (!stateValid || Running(game, id, fake, running)) return;
            if (WorkshopJunction.Remove(folder, link.Target))
            { owned.Remove(link); Save(); removed++; names.Add(title); log(reason + folder); }
        }
        internal bool HasRootLink(string id)
        {
            return owned.Any(x => x.Created && x.AppId == id && Path.GetFileName(x.Destination).Equals("Workshop", StringComparison.OrdinalIgnoreCase));
        }
        internal void Disable(string id)
        {
            if (!disabled.Add(id)) return;
            AtomicWrite(disabledFile, string.Join("\r\n", disabled.ToArray()));
        }
        void CreateOwned(string game, string id, string destination, string target)
        {
            // Intent alone never claims a junction; only confirmed creations can be removed.
            var record = new OwnedLink { AppId = id, Game = Full(game), Destination = Full(destination), Target = Full(target) };
            owned.RemoveAll(x => Same(x.Destination, destination));
            owned.Add(record); Save();
            try { WorkshopJunction.Create(destination, target); }
            catch { owned.Remove(record); Save(); throw; }
            record.Created = true; Save();
        }
        void SyncFolder(string game, string id, string fake, string title, string mods, string[] roots,
            ICollection<string> running, bool readOnly, bool wholeFolder, ref int created, ref int removed, HashSet<string> names)
        {
            // A whole-folder junction is accepted only if it already points
            // to this app's actual content root. It is never adopted or edited.
            if (!SafeParents(Path.GetDirectoryName(mods))) { Once(mods, L.T("Workshop conflict; redirected parent left untouched: ", "Conflit Workshop ; parent redirigé laissé intact : ") + mods); return; }
            var contentRoots = roots.Select(r => Full(Path.Combine(r, @"steamapps\workshop\content", id)))
                .Where(r => Directory.Exists(r) && SafeParents(r)).ToArray();
            // One source root needs only one junction. Existing real folders and
            // content split across libraries use the safe per-item fallback below.
            if (wholeFolder && !Exists(mods) && contentRoots.Length == 1)
            {
                string source = contentRoots[0];
                if (readOnly) { log(L.T("Would link Workshop folder: ", "Lien prévu pour le dossier Workshop : ") + mods + " -> " + source); return; }
                if (!stateValid || Running(game, id, fake, running) || !Directory.Exists(source)) return;
                CreateOwned(game, id, mods, source);
                created++; names.Add(title);
                log(L.T("Linked OnlineFix Workshop folder: ", "Dossier Workshop OnlineFix lié : ") + mods + " -> " + source);
                return;
            }
            var rootLink = owned.FirstOrDefault(x => x.Created && x.AppId == id && Same(x.Game, game) && Same(x.Destination, mods));
            if (wholeFolder && rootLink != null && roots.Any(r => Same(rootLink.Target, Path.Combine(r, @"steamapps\workshop\content", id))) &&
                SafeParents(rootLink.Target) && Directory.Exists(Path.GetDirectoryName(rootLink.Target)) && !Exists(rootLink.Target))
            {
                string current = WorkshopJunction.Target(mods);
                if (current != null && Same(current, rootLink.Target))
                {
                    if (readOnly) log(L.T("Would remove owned missing-target junction: ", "Suppression prévue du lien appartenant à Cabinet dont la cible a disparu : ") + mods);
                    else if (stateValid && !Running(game, id, fake, running) && WorkshopJunction.Remove(mods, rootLink.Target))
                    { owned.Remove(rootLink); Save(); removed++; names.Add(title); }
                }
            }
            if (Redirected(mods))
            {
                string target = WorkshopJunction.Target(mods);
                bool correct = target != null && roots.Any(r => Same(target, Path.Combine(r, @"steamapps\workshop\content", id)));
                if (correct && contentRoots.Any(r => !Same(r, target))) Once(mods + "split", L.T("Workshop conflict; content in another library is not exposed by existing folder junction: ", "Conflit Workshop ; le contenu d'une autre bibliothèque n'est pas exposé par le lien existant : ") + mods);
                Once(mods, (correct ? L.T("Workshop folder already linked: ", "Dossier Workshop déjà lié : ") : L.T("Workshop conflict; folder points elsewhere: ", "Conflit Workshop ; le dossier pointe ailleurs : ")) + mods);
                return;
            }
            if (Exists(mods) && !Directory.Exists(mods)) { Once(mods, L.T("Workshop conflict; existing file left untouched: ", "Conflit Workshop ; fichier existant laissé intact : ") + mods); return; }
            // A missing entire content root can be an offline disk. Steam may
            // remove the app root when its last item is unsubscribed, so only
            // the original library's content root must still be accessible.
            foreach (OwnedLink link in owned.Where(x => x.Created && x.AppId == id && Same(x.Game, game) && Same(Path.GetDirectoryName(x.Destination), mods)).ToArray())
            {
                if (!Inside(link.Destination, game) || !SafeParents(Path.GetDirectoryName(link.Destination))) continue;
                string item = Path.GetFileName(link.Target), sourceRoot = Path.GetDirectoryName(link.Target);
                if (!Numeric(item) || !roots.Any(r => Same(sourceRoot, Path.Combine(r, @"steamapps\workshop\content", id))) || !SafeParents(sourceRoot) || !Directory.Exists(Path.GetDirectoryName(sourceRoot)) || Exists(link.Target)) continue;
                string current = WorkshopJunction.Target(link.Destination);
                if (current == null || !Same(current, link.Target)) continue;
                if (readOnly) { log(L.T("Would remove owned missing-target junction: ", "Suppression prévue du lien appartenant à Cabinet dont la cible a disparu : ") + link.Destination); continue; }
                if (!stateValid || Running(game, id, fake, running)) continue;
                if (WorkshopJunction.Remove(link.Destination, link.Target))
                { owned.Remove(link); Save(); removed++; names.Add(title); log(L.T("Removed unsubscribed Workshop junction: ", "Lien Workshop désabonné supprimé : ") + link.Destination); }
            }
            var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new HashSet<string>();
            foreach (string root in roots)
            {
                string content = Path.Combine(root, @"steamapps\workshop\content", id);
                if (!Directory.Exists(content) || !SafeParents(content)) continue;
                foreach (string source in Directory.GetDirectories(content))
                {
                    string item = Path.GetFileName(source);
                    if (!Numeric(item) || Redirected(source)) continue;
                    if (items.ContainsKey(item)) duplicates.Add(item); else items.Add(item, Full(source));
                }
            }
            if (wholeFolder && contentRoots.Length == 0)
                Once(mods + "pending", L.T("Workshop waiting for downloaded content: ", "Workshop en attente du contenu téléchargé : ") + title + " (" + id + ")");
            foreach (var item in items)
            {
                string destination = Path.Combine(mods, item.Key);
                if (Exists(destination))
                {
                    string target = WorkshopJunction.Target(destination);
                    if (target != null && Same(target, item.Value))
                    { if (readOnly) log(L.T("Already linked: ", "Déjà lié : ") + destination); }
                    else Once(destination, L.T("Workshop conflict; existing path left untouched: ", "Conflit Workshop ; chemin existant laissé intact : ") + destination);
                    continue;
                }
                if (duplicates.Contains(item.Key)) { Once(destination, L.T("Workshop conflict; duplicate item across libraries: ", "Conflit Workshop ; élément présent dans plusieurs bibliothèques : ") + item.Key); continue; }
                if (readOnly) { log(L.T("Would link: ", "Lien prévu : ") + destination + " -> " + item.Value); continue; }
                if (!stateValid || Running(game, id, fake, running) || !SafeParents(mods) || !Directory.Exists(item.Value)) continue;
                Directory.CreateDirectory(mods);
                CreateOwned(game, id, destination, item.Value);
                created++; names.Add(title); log(L.T("Linked Workshop item: ", "Élément Workshop lié : ") + destination);
            }
        }

    }

    // Mount-point junctions only. Never call cmd/mklink or recursive deletion.
    static class WorkshopJunction
    {
        const uint Tag = 0xA0000003, GetReparse = 0x000900A8, SetReparse = 0x000900A4;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateDirectory(string name, IntPtr security);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int bytes, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref int info, int size);
        static SafeFileHandle Open(string path, uint access, uint share)
        {
            var h = CreateFile(path, access, share, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (h.IsInvalid) { int error = Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(error); }
            return h;
        }
        static string Read(SafeFileHandle handle)
        {
            byte[] data = new byte[16384]; int bytes;
            if (!DeviceIoControl(handle, GetReparse, null, 0, data, data.Length, out bytes, IntPtr.Zero))
            { int error = Marshal.GetLastWin32Error(); if (error == 4390) return null; throw new Win32Exception(error); }
            if (bytes < 16 || BitConverter.ToUInt32(data, 0) != Tag) return null;
            int start = 16 + BitConverter.ToUInt16(data, 8), length = BitConverter.ToUInt16(data, 10);
            if (length == 0 || start + length > bytes) return null;
            string target = Encoding.Unicode.GetString(data, start, length);
            if (target.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + target.Substring(8);
            if (target.StartsWith(@"\??\", StringComparison.Ordinal)) return target.Substring(4);
            return null;
        }
        internal static string Target(string path)
        {
            if (!WorkshopEngine.Exists(path)) return null;
            using (var handle = Open(path, 0, 7)) return Read(handle);
        }
        internal static void Create(string destination, string target)
        {
            if (!CreateDirectory(destination, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Exclusive handle stops another writer changing the newly created entry.
            using (var handle = Open(destination, 0x40000000, 0))
            {
                byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + WorkshopEngine.Full(target));
                byte[] print = Encoding.Unicode.GetBytes(WorkshopEngine.Full(target));
                byte[] data = new byte[16 + substitute.Length + 2 + print.Length + 2];
                Array.Copy(BitConverter.GetBytes(Tag), 0, data, 0, 4);
                Array.Copy(BitConverter.GetBytes((ushort)(data.Length - 8)), 0, data, 4, 2);
                Array.Copy(BitConverter.GetBytes((ushort)substitute.Length), 0, data, 10, 2);
                Array.Copy(BitConverter.GetBytes((ushort)(substitute.Length + 2)), 0, data, 12, 2);
                Array.Copy(BitConverter.GetBytes((ushort)print.Length), 0, data, 14, 2);
                Array.Copy(substitute, 0, data, 16, substitute.Length);
                Array.Copy(print, 0, data, 18 + substitute.Length, print.Length);
                int bytes;
                if (!DeviceIoControl(handle, SetReparse, data, data.Length, null, 0, out bytes, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        internal static bool Remove(string path, string expectedTarget)
        {
            using (var handle = Open(path, 0x00010000, 0))
            {
                string target = Read(handle);
                if (target == null || !string.Equals(WorkshopEngine.Full(target), WorkshopEngine.Full(expectedTarget), StringComparison.OrdinalIgnoreCase)) return false;
                int delete = 1;
                if (!SetFileInformationByHandle(handle, 4, ref delete, 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return true;
            }
        }
    }
}
