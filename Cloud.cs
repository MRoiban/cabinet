// Integration (Cabinet.cs is owned by the orchestrator):
// Main, before install/mutex: if (CloudSync.HandleArgs(args)) return;
// Install after copy: CloudSync.RegisterProtocol(target);
// App constructor: CloudSync.Configure(Log, playtime.QueueCloudMinutes,
//   playtime.CloudMinutesInSteam, delegate { return playtime.RunningApps(); });
// Tray menu: menu.Items.Add(CloudSync.MenuItem());
// Each tick after detection: CloudSync.Tick(running.Count > 0 || shadows.HasChildren
//   || schemas.HasChild, running); show its non-null return using the existing balloon.
// Required Playtime methods: bool QueueCloudMinutes(string receiptId,string appId,int minutes)
// must atomically persist receipt + pending seconds in the SAME ledger transaction;
// duplicate receipt returns true. long CloudMinutesInSteam(string appId) returns imported
// minutes credited in VDF (including inflight, excluding pending/requeued), persisting
// provenance through Apply/Verify. Never independently edit playtime.txt from cloud code.
// Cloud journal watermarks + deterministic receipt IDs recover across crashes.
// --cloud-selftest uses only temporary roots and an in-memory localhost server.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Cabinet
{
    static class CloudSync
    {
        static readonly object Gate = new object();
        static CloudClient client;
        static Action<string> logger;
        static Func<string, string, int, bool> queue;
        static Func<string, long> imported;
        static Func<ICollection<string>> detect;
        static HashSet<string> running = new HashSet<string>();
        static Dictionary<string, long> localMinutes = new Dictionary<string, long>();
        static volatile bool busy, requested;
        static bool wasPlaying;
        static DateTime next = DateTime.MinValue;
        static volatile string message;
        static volatile bool rescan;
        static DateTime nextScan = DateTime.MinValue;
        static readonly Queue<Action<CloudClient>> saveActions = new Queue<Action<CloudClient>>();
        internal static SaveSync Saves { get { return Client.Saves; } }
        internal static void RequestRescan() { rescan = true; }
        // Runs on the cloud worker thread, between rounds; the action reports its own result.
        internal static void QueueSaveAction(Action<CloudClient> action) { lock (saveActions) saveActions.Enqueue(action); }
        static bool TakeSaveAction(out Action<CloudClient> action)
        { lock (saveActions) { action = saveActions.Count > 0 ? saveActions.Dequeue() : null; return action != null; } }
        static void RunWorker(Action work, string label)
        {
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { work(); }
                catch (Exception ex) { Log(label + " failed: " + ex.Message); }
                finally { busy = false; }
            });
        }
        static readonly Queue<string> connects = new Queue<string>();
        static CloudClient Client { get { if (client == null) client = new CloudClient(App.DataDir, Syncer.OnlineFixDir, LocalMinutes, Log); return client; } }
        static void Log(string s) { if (logger != null) logger("cloud: " + s); }
        public static void Configure(Action<string> log, Func<string, string, int, bool> queueMinutes,
            Func<string, long> minutesInSteam, Func<ICollection<string>> runningApps)
        { logger = log; queue = queueMinutes; imported = minutesInSteam; detect = runningApps; }
        static long LocalMinutes(string app)
        { long value; return localMinutes.TryGetValue(app, out value) ? value : 0; }
        // Snapshot ledger provenance on the tray thread; its entries are not thread-safe.
        static long ReadLocalMinutes(string app)
        {
            string account = Syncer.AccountId(); if (account == null) return 0;
            string p = Path.Combine(Syncer.SteamPath(), "userdata", account, "config", "localconfig.vdf"); long n = 0;
            if (File.Exists(p)) long.TryParse(new VdfText(File.ReadAllText(p)).GetAppValue(app, "Playtime"), out n);
            return Math.Max(0, n - (imported == null ? 0 : imported(app)));
        }
        public static bool HandleArgs(string[] args)
        {
            if (SaveCli.Handle(args, App.DataDir, delegate { return SaveEnvironment.Current(Syncer.SteamPath(), Syncer.AccountId(), Syncer.OnlineFixDir); })) return true;
            if (args.Length == 1 && args[0] == "--saves-selftest")
            { try { SaveSelfTest.Run(); } catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; } return true; }
            if (args.Length == 1 && args[0] == "--cloud-selftest")
            { try { CloudSelfTest.Run(); } catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; } return true; }
            string url = args.FirstOrDefault(a => a.StartsWith("cabinet:", StringComparison.OrdinalIgnoreCase));
            if (url == null) return false;
            try
            {
                string server, token; CloudClient.ParseLink(url, out server, out token);
                Directory.CreateDirectory(App.DataDir);
                // Unique DPAPI inbox files avoid losing links from concurrent protocol launches.
                string path = Path.Combine(App.DataDir, "cloud-connect-" + Guid.NewGuid().ToString("N") + ".dat");
                File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(url), null, DataProtectionScope.CurrentUser));
                bool active;
                try { using (Mutex.OpenExisting(@"Local\Cabinet")) active = true; }
                catch (WaitHandleCannotBeOpenedException) { active = false; }
                if (!active) System.Diagnostics.Process.Start(Application.ExecutablePath);
            }
            catch { MessageBox.Show(L.T("Invalid Cabinet Cloud connect link.", "Lien de connexion Cabinet Cloud invalide."), L.T("Cabinet Cloud", "Cabinet Cloud")); }
            return true;
        }
        public static void RegisterProtocol(string exePath)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\cabinet"))
            {
                k.SetValue("", "URL:Cabinet"); k.SetValue("URL Protocol", "");
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", "\"" + exePath + "\" \"%1\"");
            }
        }
        static bool Blocked(string app)
        {
            lock (Gate) if (running.Contains(app)) return true;
            return Shadows.HelperActive(app) || Playtime.SteamCounting(app) || (detect != null && detect().Contains(app));
        }
        public static string Tick(bool anyGameRunning, ICollection<string> runningAppIds)
        {
            lock (Gate) running = new HashSet<string>(runningAppIds ?? new string[0]);
            // Saves of games Cabinet cannot see running (non-OnlineFix) sync once they exit.
            if ((wasPlaying && !anyGameRunning) || Client.Saves.DeferredFinished()) requested = true; wasPlaying = anyGameRunning;
            string result = message; message = null; FriendLabels.Tick(Client, runningAppIds, Log); if (busy) return result;
            try
            {
                Directory.CreateDirectory(App.DataDir);
                foreach (string path in Directory.GetFiles(App.DataDir, "cloud-connect-*.dat"))
                {
                    connects.Enqueue(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)));
                    File.Delete(path);
                }
                if (queue != null && Client.Connected) Client.Deliver(queue, Blocked);
            }
            catch (Exception ex) { Log(ex.Message); }
            Action<CloudClient> action;
            if (connects.Count > 0)
            {
                string url = connects.Dequeue(); busy = true;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Client.Connect(url); message = L.T("Connected to Cabinet Cloud as ", "Connecté à Cabinet Cloud en tant que ") + Client.UserName; requested = true; }
                    catch (Exception ex) { Log("connection failed: " + ex.Message); }
                    finally { busy = false; }
                });
            }
            else if (TakeSaveAction(out action)) RunWorker(delegate { action(Client); }, "save action");
            else if (!anyGameRunning && Client.Connected && queue != null && imported != null && (requested || DateTime.UtcNow >= next))
            {
                var snapshot = new Dictionary<string, long>();
                try
                {
                    if (Directory.Exists(Syncer.OnlineFixDir)) foreach (string folder in Directory.GetDirectories(Syncer.OnlineFixDir))
                    { string id = Path.GetFileName(folder); if (Regex.IsMatch(id, @"^[0-9]{1,10}$") && !Blocked(id)) snapshot[id] = ReadLocalMinutes(id); }
                }
                catch (Exception ex) { Log("playtime snapshot failed: " + ex.Message); return result; }
                localMinutes = snapshot;
                requested = false; next = DateTime.UtcNow.AddMinutes(30); busy = true;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Client.Round(Blocked); }
                    catch (Exception ex) { Log("sync failed: " + ex.Message); next = DateTime.UtcNow.AddMinutes(1); }
                    finally { busy = false; }
                });
            }
            else if (!anyGameRunning && (rescan || (!Client.Connected && DateTime.UtcNow >= nextScan)))
            {
                // Discovery only: shows save locations even when Cabinet Cloud is not connected.
                rescan = false; nextScan = DateTime.UtcNow.AddMinutes(30); bool connected = Client.Connected;
                RunWorker(delegate { Client.ScanSaves(connected); }, "save scan");
            }
            return result;
        }
        public static ToolStripMenuItem MenuItem()
        {
            var root = new ToolStripMenuItem(L.T("Cabinet Cloud", "Cabinet Cloud"));
            var status = new ToolStripMenuItem() { Enabled = false };
            var connect = new ToolStripMenuItem(L.T("Connect…", "Se connecter…"));
            var sync = new ToolStripMenuItem(L.T("Sync now", "Synchroniser"));
            var disconnect = new ToolStripMenuItem(L.T("Disconnect", "Se déconnecter"));
            var labels = FriendLabels.MenuItem();
            root.DropDownItems.AddRange(new ToolStripItem[] { status, connect, sync, disconnect, labels });
            root.DropDownOpening += delegate
            {
                status.Text = Client.Connected ? L.T("Connected as ", "Connecté en tant que ") + Client.UserName.Replace("&", "&&") : L.T("Not connected", "Non connecté");
                labels.Visible = Client.Connected && !FriendLabels.DebugEnabled();
                connect.Enabled = !busy; sync.Enabled = Client.Connected; disconnect.Enabled = Client.Connected && !busy;
            };
            connect.Click += delegate { ShowConnect(); }; sync.Click += delegate { requested = true; };
            disconnect.Click += delegate { if (!busy) Client.Disconnect(); }; return root;
        }
        static void ShowConnect()
        {
            using (var f = new Form())
            {
                f.Text = L.T("Connect Cabinet Cloud", "Connecter Cabinet Cloud"); f.Width = 540; f.Height = 180;
                f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterScreen; f.MaximizeBox = false; f.MinimizeBox = false;
                var label = new Label { Left = 12, Top = 12, Width = 500, Text = L.T("Paste your Cabinet Cloud connect link:", "Collez votre lien de connexion Cabinet Cloud :") };
                var input = new TextBox { Left = 12, Top = 40, Width = 500 };
                var ok = new Button { Left = 310, Top = 80, Width = 100, Text = L.T("Connect", "Connexion") };
                var cancel = new Button { Left = 420, Top = 80, Width = 90, Text = L.T("Cancel", "Annuler"), DialogResult = DialogResult.Cancel };
                ok.Click += delegate
                {
                    try { string s, t; CloudClient.ParseLink(input.Text.Trim(), out s, out t); connects.Enqueue(input.Text.Trim()); f.DialogResult = DialogResult.OK; f.Close(); }
                    catch { MessageBox.Show(f, L.T("Invalid connect link.", "Lien de connexion invalide."), L.T("Cabinet Cloud", "Cabinet Cloud")); }
                };
                f.Controls.AddRange(new Control[] { label, input, ok, cancel }); f.AcceptButton = ok; f.CancelButton = cancel; f.ShowDialog();
            }
        }
    }
    // The fake endpoint is deliberately scoped to the self-test and never uses production roots.
    static class CloudSelfTest
    {
        static void Check(bool ok, string label)
        { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
        static void Seed(string root, string ach, long timestamp, string save, DateTime date)
        {
            string stats = Path.Combine(root, "123", "Stats"), saves = Path.Combine(root, "123", "Saves");
            Directory.CreateDirectory(stats); Directory.CreateDirectory(saves);
            File.WriteAllText(Path.Combine(stats, "Achievements.ini"), "; retained comment\r\n[" + ach + "]\r\nachieved=true\r\ntimestamp=" + timestamp + "\r\nextra=keep\r\n");
            File.WriteAllText(Path.Combine(stats, "Stats.ini"), "; stats comment\r\n[Stats]\r\nscore=" + timestamp + "\r\n[Other]\r\nkeep=yes\r\n");
            File.SetLastWriteTimeUtc(Path.Combine(stats, "Stats.ini"), date);
            // A null save leaves an empty Saves folder, as the OnlineFix emulator does.
            if (save != null) { File.WriteAllText(Path.Combine(saves, "slot.dat"), save); File.SetLastWriteTimeUtc(Path.Combine(saves, "slot.dat"), date); }
            Directory.SetLastWriteTimeUtc(saves, date);
        }
        // Saves never read the real profile, Steam or OnlineFix folders in the self-test.
        static void Isolate(CloudClient c, string root, string onlineFix)
        {
            string home = Path.Combine(root, "home");
            c.Saves.EnvironmentProvider = delegate
            {
                return new SaveEnvironment { Home = home, OsUserName = "tester", AppData = Path.Combine(home, "AppData", "Roaming"), LocalAppData = Path.Combine(home, "AppData", "Local"), LocalLow = Path.Combine(home, "AppData", "LocalLow"), Documents = Path.Combine(home, "Documents"), OnlineFixRoot = onlineFix };
            };
            c.Saves.GameRunning = delegate { return false; };
        }
        static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-30)); }
        public static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "Cabinet-cloud-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var fetcher = SaveManifestCache.Fetcher;
            SaveManifestCache.Fetcher = delegate { throw new System.Net.WebException("offline in self-test"); };
            try
            {
                using (var server = new FakeServer())
                {
                    string ar = Path.Combine(root, "A", "OnlineFix"), br = Path.Combine(root, "B", "OnlineFix");
                    DateTime date = DateTime.UtcNow.AddHours(-2); Seed(ar, "A", 100, "save-A", date.AddHours(1)); Seed(br, "B", 200, null, date);
                    var errors = new List<string>(); Action<string> log = delegate(string s) { if (!s.StartsWith("save manifest") && !s.StartsWith("saves ") && !s.StartsWith("save backup")) errors.Add(s); Console.WriteLine("LOG: " + s); };
                    var a = new CloudClient(Path.Combine(root, "A", "data"), ar, delegate { return 12; }, log); Isolate(a, Path.Combine(root, "A"), ar);
                    long steamB = 7, importedB = 0;
                    var b = new CloudClient(Path.Combine(root, "B", "data"), br, delegate { return steamB - importedB; }, log); Isolate(b, Path.Combine(root, "B"), br);
                    string url = "cabinet://connect?server=" + Uri.EscapeDataString(server.Origin) + "&token=" + FakeServer.Token;
                    a.Connect(url); b.Connect(server.Origin + "/connect#" + FakeServer.Token);
                    Check(a.Device != b.Device && a.UserName == "Tester", "protocol and browser link connect; independent device IDs");
                    Check(!File.ReadAllText(Path.Combine(a.Data, "cloud.json")).Contains(FakeServer.Token), "DPAPI settings contain no plaintext token");
                    Func<string, bool> idle = delegate { return false; };
                    a.Round(idle); b.Round(idle); a.Round(idle);
                    Check(server.Puts == 1 && server.LegacyPuts == 0, "only the PC with save files uploads; nothing uses the legacy route");
                    Check(File.ReadAllText(Path.Combine(br, "123", "Saves", "slot.dat")) == "save-A", "cloud save restored into the empty OnlineFix folder");
                    Check(a.Saves.StatusOf("123").Status == SaveSyncStatus.Synced && b.Saves.StatusOf("123").Status == SaveSyncStatus.Synced, "both PCs report synced only after matching the cloud");
                    var ini = Syncer.ReadIni(Path.Combine(br, "123", "Stats", "Achievements.ini"));
                    Check(ini.ContainsKey("A") && ini.ContainsKey("B") && ini["B"]["extra"] == "keep", "achievement union preserves INI sections and custom values");
                    Check(File.ReadAllText(Path.Combine(br, "123", "Stats", "Achievements.ini")).Contains("; retained comment"), "INI comments preserved");
                    Check(Syncer.ReadIni(Path.Combine(br, "123", "Stats", "Stats.ini"))["Stats"]["score"] == "100", "newer stats snapshot applied");
                    var receipts = new HashSet<string>(); int added = 0;
                    Func<string, string, int, bool> ledger = delegate(string receipt, string app, int n)
                    { if (receipts.Add(receipt)) { added += n; steamB += n; importedB += n; } return true; };
                    string before = File.ReadAllText(Path.Combine(b.Data, "cloud-state.json"));
                    b.Deliver(ledger, delegate { return true; }); Check(added == 0, "running app playtime remains pending");
                    b.Deliver(ledger, idle); Check(added == 12, "remote playtime queued once");
                    // Simulate a crash after ledger commit but before the cloud watermark commit.
                    File.WriteAllText(Path.Combine(b.Data, "cloud-state.json"), before);
                    b = new CloudClient(b.Data, br, delegate { return steamB - importedB; }, log); Isolate(b, Path.Combine(root, "B"), br); b.Deliver(ledger, idle); b.Round(idle); b.Deliver(ledger, idle);
                    Check(added == 12, "idempotent receipt replay across restart does not double count");
                    Check(CloudClient.Num(CloudClient.Map(server.App["playtime"])[a.Device]) == 12 && CloudClient.Num(CloudClient.Map(server.App["playtime"])[b.Device]) == 7, "per-device playtime max and own totals retained");
                    Check(steamB == 19 && importedB == 12, "imported playtime is subtracted before the next push");
                    server.EarlierAchievement(); b.Round(idle);
                    Check(Syncer.ReadIni(Path.Combine(br, "123", "Stats", "Achievements.ini"))["A"]["timestamp"] == "50", "earliest achievement timestamp wins");
                    string unchanged = File.ReadAllText(Path.Combine(br, "123", "Stats", "Achievements.ini")); int requests = server.Progress, puts = server.Puts;
                    Write(Path.Combine(br, "123", "Saves", "slot.dat"), "while-running");
                    b.Round(delegate { return true; }); Check(server.Progress == requests && server.Puts == puts && unchanged == File.ReadAllText(Path.Combine(br, "123", "Stats", "Achievements.ini")), "running app is never pushed, uploaded or applied");
                    Check(b.Saves.StatusOf("123").Status == SaveSyncStatus.Deferred, "running app save sync is deferred");

                    // Divergent changes: nothing is overwritten until the user chooses.
                    Write(Path.Combine(ar, "123", "Saves", "slot.dat"), "A2"); Write(Path.Combine(br, "123", "Saves", "slot.dat"), "B2");
                    a.Round(idle); b.Round(idle);
                    Check(server.Current("123").ContentHash == a.Saves.App("123").contentHash && b.Saves.StatusOf("123").Status == SaveSyncStatus.Conflict, "divergent saves become a conflict");
                    Check(File.ReadAllText(Path.Combine(br, "123", "Saves", "slot.dat")) == "B2", "conflict leaves local saves untouched");
                    b.Saves.KeepLocal("123", server.Current("123"), b, idle, b.Saves.EnvironmentProvider());
                    a.Round(idle);
                    Check(File.ReadAllText(Path.Combine(ar, "123", "Saves", "slot.dat")) == "B2", "Keep this PC uploads; the other PC restores it");
                    string backup = Directory.GetFiles(Path.Combine(a.Data, "backup", "saves", "123"), "*.zip").Single();
                    var backed = SaveArchive.Read(File.ReadAllBytes(backup), "steam:123");
                    using (var ms = new MemoryStream()) { backed.Extract(backed.Files.Single(), ms); Check(Encoding.UTF8.GetString(ms.ToArray()) == "A2", "local saves backed up before replacement"); }
                    Check(server.Versions("123") == 3, "server keeps prior versions");

                    // Empty scans and failed uploads never look synced and never replace the cloud copy.
                    File.Delete(Path.Combine(ar, "123", "Saves", "slot.dat")); puts = server.Puts; a.Round(idle);
                    Check(server.Puts == puts && a.Saves.StatusOf("123").Status == SaveSyncStatus.LocalMissing, "emptied save folder keeps the cloud copy and is not reported synced");
                    Write(Path.Combine(br, "123", "Saves", "slot.dat"), "B3"); server.FailPuts = 1; string journal = b.Saves.App("123").contentHash; b.Round(idle);
                    Check(b.Saves.StatusOf("123").Status == SaveSyncStatus.Error && b.Saves.App("123").contentHash == journal, "failed upload keeps the journal and reports an error");
                    b.Round(idle);
                    Check(b.Saves.StatusOf("123").Status == SaveSyncStatus.Synced && server.Current("123").ContentHash == b.Saves.App("123").contentHash, "failed upload is retried");

                    // Legacy backups: content restores into an empty OnlineFix folder; empty ZIPs never do.
                    server.SetLegacy("456", LegacyZip("legacy.dat", "legacy"), "old-pc"); server.SetLegacy("789", new byte[] { 0x50, 0x4b, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "old-pc");
                    server.SetLegacy("321", LegacyZip("slot.dat", "old-legacy"), "old-pc");
                    foreach (string id in new[] { "456", "789", "321" }) Directory.CreateDirectory(Path.Combine(br, id, "Saves"));
                    Write(Path.Combine(br, "321", "Saves", "slot.dat"), "real-local");
                    b.Round(idle);
                    Check(File.ReadAllText(Path.Combine(br, "456", "Saves", "legacy.dat")) == "legacy", "legacy backup restored into an empty OnlineFix folder");
                    Check(Directory.GetFiles(Path.Combine(br, "789", "Saves")).Length == 0 && b.Saves.StatusOf("789").Status == SaveSyncStatus.Empty, "22-byte legacy ZIP counts as no backup and shows 'No save files found'");
                    Check(File.ReadAllText(Path.Combine(br, "321", "Saves", "slot.dat")) == "real-local" && server.Current("321") != null, "legacy backup never replaces real local saves; they upload as format 2");
                    Check(errors.Count == 0, "end-to-end rounds completed without errors");
                    bool denied = false;
                    try { b.Connect(server.Origin + "/connect#cab_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"); } catch (WebException) { denied = true; }
                    Check(denied && b.Connected, "401 connection leaves the prior connection intact");
                    b.Disconnect(); Check(!new CloudClient(b.Data, br, delegate { return 0; }, log).Connected, "disconnect persists");
                    Console.WriteLine("Cabinet Cloud self-test passed (temporary roots only).");
                }
            }
            finally { SaveManifestCache.Fetcher = fetcher; if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        static byte[] LegacyZip(string name, string text)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) using (var w = new StreamWriter(z.CreateEntry(name).Open())) w.Write(text);
                return ms.ToArray();
            }
        }
        sealed class FakeServer : IDisposable
        {
            internal const string Token = "cab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            readonly HttpListener listener = new HttpListener(); readonly Thread thread;
            readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
            internal readonly string Origin;
            internal Dictionary<string, object> App = new Dictionary<string, object> { { "achievements", new Dictionary<string, object>() }, { "stats", new Dictionary<string, object>() }, { "statsUpdatedAt", 0L }, { "playtime", new Dictionary<string, object>() }, { "playtimeTotal", 0L }, { "save", null } };
            readonly Dictionary<string, List<KeyValuePair<Dictionary<string, object>, byte[]>>> archives = new Dictionary<string, List<KeyValuePair<Dictionary<string, object>, byte[]>>>();
            readonly Dictionary<string, KeyValuePair<Dictionary<string, object>, byte[]>> legacy = new Dictionary<string, KeyValuePair<Dictionary<string, object>, byte[]>>();
            internal int Progress, Puts, LegacyPuts, FailPuts;
            long clock = 1000;
            internal FakeServer()
            {
                var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
                Origin = "http://localhost:" + port; listener.Prefixes.Add(Origin + "/"); listener.Start();
                thread = new Thread(Loop); thread.IsBackground = true; thread.Start();
            }
            internal void EarlierAchievement() { lock (this) CloudClient.Map(App["achievements"])["A"] = 50L; }
            internal SaveCloudMeta Current(string app) { lock (this) { List<KeyValuePair<Dictionary<string, object>, byte[]>> l; return archives.TryGetValue(app, out l) && l.Count > 0 ? SaveCloudMeta.Parse(l[l.Count - 1].Key) : null; } }
            internal int Versions(string app) { lock (this) { List<KeyValuePair<Dictionary<string, object>, byte[]>> l; return archives.TryGetValue(app, out l) ? l.Count : 0; } }
            internal void SetLegacy(string app, byte[] zip, string device)
            { lock (this) legacy[app] = new KeyValuePair<Dictionary<string, object>, byte[]>(new Dictionary<string, object> { { "sha256", CloudClient.Hash(zip) }, { "size", zip.Length }, { "updatedAt", 500L }, { "deviceId", device }, { "deviceName", "Old PC" }, { "empty", zip.Length <= 22 } }, zip); }
            void Loop()
            {
                while (listener.IsListening)
                {
                    HttpListenerContext c; try { c = listener.GetContext(); } catch (HttpListenerException) { break; } catch (ObjectDisposedException) { break; }
                    try { lock (this) Handle(c); } catch (Exception ex) { c.Response.StatusCode = 500; Reply(c, new Dictionary<string, object> { { "error", ex.Message } }); }
                    finally { c.Response.Close(); }
                }
            }
            void Reply(HttpListenerContext c, object obj)
            { byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(obj)); c.Response.ContentType = "application/json"; c.Response.OutputStream.Write(bytes, 0, bytes.Length); }
            void Bytes(HttpListenerContext c, byte[] data) { c.Response.ContentType = "application/zip"; c.Response.Headers["ETag"] = CloudClient.Hash(data); c.Response.OutputStream.Write(data, 0, data.Length); }
            Dictionary<string, object> AppState(string id)
            {
                var state = id == "123" ? new Dictionary<string, object>(App) : new Dictionary<string, object> { { "achievements", new Dictionary<string, object>() }, { "stats", new Dictionary<string, object>() }, { "statsUpdatedAt", 0L }, { "playtime", new Dictionary<string, object>() }, { "playtimeTotal", 0L }, { "save", null } };
                List<KeyValuePair<Dictionary<string, object>, byte[]>> l; KeyValuePair<Dictionary<string, object>, byte[]> old;
                state["archive"] = archives.TryGetValue(id, out l) && l.Count > 0 ? l[l.Count - 1].Key : null;
                state["save"] = legacy.TryGetValue(id, out old) ? old.Key : null;
                return state;
            }
            void Handle(HttpListenerContext c)
            {
                if (c.Request.Headers["Authorization"] != "Bearer " + Token) { c.Response.StatusCode = 401; Reply(c, new { error = "unauthorized" }); return; }
                if (!c.Request.UserAgent.StartsWith("Cabinet/")) throw new Exception("Missing user agent");
                if (c.Request.HttpMethod != "GET" && c.Request.Headers["Origin"] != Origin) { c.Response.StatusCode = 403; Reply(c, new { error = "forbidden" }); return; }
                string path = c.Request.Url.AbsolutePath;
                if (path == "/api/me") { Reply(c, new { user = new { id = "test-user", name = "Tester" } }); return; }
                if (path == "/api/state")
                {
                    var apps = new Dictionary<string, object>();
                    foreach (string id in new[] { "123" }.Concat(archives.Keys).Concat(legacy.Keys).Distinct()) apps[id] = AppState(id);
                    Reply(c, new { apps = apps }); return;
                }
                var pm = System.Text.RegularExpressions.Regex.Match(path, "^/api/apps/([0-9]+)/progress$");
                if (pm.Success && pm.Groups[1].Value != "123" && c.Request.HttpMethod == "POST") { Reply(c, AppState(pm.Groups[1].Value)); return; }
                if (path == "/api/apps/123/progress" && c.Request.HttpMethod == "POST")
                {
                    Progress++; Dictionary<string, object> body;
                    using (var r = new StreamReader(c.Request.InputStream)) body = CloudClient.Map(json.DeserializeObject(r.ReadToEnd()));
                    var ach = CloudClient.Map(App["achievements"]);
                    foreach (var item in CloudClient.Map(body["achievements"]))
                    { object prev; ach[item.Key] = ach.TryGetValue(item.Key, out prev) ? Math.Min(CloudClient.Num(prev), CloudClient.Num(item.Value)) : CloudClient.Num(item.Value); }
                    if (CloudClient.Num(body["statsUpdatedAt"]) > CloudClient.Num(App["statsUpdatedAt"])) { App["stats"] = body["stats"]; App["statsUpdatedAt"] = body["statsUpdatedAt"]; }
                    var times = CloudClient.Map(App["playtime"]); string device = Convert.ToString(body["deviceId"]); object n;
                    times[device] = Math.Max(times.TryGetValue(device, out n) ? CloudClient.Num(n) : 0, CloudClient.Num(body["playtimeMinutes"])); App["playtimeTotal"] = times.Values.Sum(v => CloudClient.Num(v));
                    Reply(c, App); return;
                }
                var m = System.Text.RegularExpressions.Regex.Match(path, "^/api/apps/([0-9]+)/(archive|save)$");
                if (!m.Success) { c.Response.StatusCode = 404; Reply(c, new { error = "not_found" }); return; }
                string app = m.Groups[1].Value; var q = c.Request.QueryString;
                if (m.Groups[2].Value == "save")
                {
                    if (c.Request.HttpMethod == "PUT") { LegacyPuts++; c.Response.StatusCode = 400; Reply(c, new { error = "legacy upload in self-test" }); return; }
                    KeyValuePair<Dictionary<string, object>, byte[]> old;
                    if (legacy.TryGetValue(app, out old)) { Bytes(c, old.Value); return; }
                    c.Response.StatusCode = 404; Reply(c, new { error = "not_found" }); return;
                }
                List<KeyValuePair<Dictionary<string, object>, byte[]>> list;
                if (!archives.TryGetValue(app, out list)) { list = new List<KeyValuePair<Dictionary<string, object>, byte[]>>(); archives[app] = list; }
                if (c.Request.HttpMethod == "GET")
                {
                    var hit = list.Where(v => q["id"] == null || (string)v.Key["id"] == q["id"]).Select(v => v.Value).LastOrDefault();
                    if (hit == null) { c.Response.StatusCode = 404; Reply(c, new { error = "not_found" }); return; }
                    Bytes(c, hit); return;
                }
                Puts++;
                if (FailPuts > 0) { FailPuts--; c.Response.StatusCode = 502; Reply(c, new { error = "bad_gateway" }); return; }
                byte[] zip; using (var ms = new MemoryStream()) { c.Request.InputStream.CopyTo(ms); zip = ms.ToArray(); }
                if (c.Request.ContentType != "application/zip" || CloudClient.Hash(zip) != q["sha256"]) { c.Response.StatusCode = 400; Reply(c, new { error = "bad upload" }); return; }
                var archive = SaveArchive.Read(zip, "steam:" + app);
                if (archive.ContentHash != q["contentHash"] || archive.Files.Count != int.Parse(q["files"]) || archive.ExpandedBytes != long.Parse(q["bytes"])) { c.Response.StatusCode = 400; Reply(c, new { error = "manifest mismatch" }); return; }
                if (archive.IsEmpty && q["intent"] != "delete") { c.Response.StatusCode = 422; Reply(c, new { error = "empty_archive" }); return; }
                var current = list.Count > 0 ? list[list.Count - 1].Key : null;
                if (current != null && (string)current["contentHash"] == archive.ContentHash) { Reply(c, current); return; }
                if (current != null && (string)current["contentHash"] != (q["base"] ?? "")) { c.Response.StatusCode = 409; Reply(c, new { current = current }); return; }
                var meta = new Dictionary<string, object> { { "id", Guid.NewGuid().ToString("N") }, { "sha256", CloudClient.Hash(zip) }, { "contentHash", archive.ContentHash }, { "size", zip.Length }, { "files", archive.Files.Count }, { "bytes", archive.ExpandedBytes }, { "createdAt", ++clock }, { "deviceId", q["deviceId"] }, { "deviceName", "Fake PC" }, { "deleted", archive.IsEmpty } };
                list.Add(new KeyValuePair<Dictionary<string, object>, byte[]>(meta, zip));
                Reply(c, meta);
            }
            public void Dispose() { listener.Close(); thread.Join(5000); }
        }
    }
    sealed class CloudSettings { public string server, token, deviceId, deviceName, userName, userId; }
    sealed class CloudAppJournal
    {
        // saveHash/saveUpdatedAt belong to the retired legacy save sync; kept so older Cabinet
        // versions sharing this journal keep their watermark. Save state is in save-state.json.
#pragma warning disable 0649
        public string progressHash, statsHash, saveHash;
        public long statsUpdatedAt, saveUpdatedAt, ownMinutes;
#pragma warning restore 0649
        public Dictionary<string, long> remote = new Dictionary<string, long>();
        public Dictionary<string, long> pending = new Dictionary<string, long>();
    }
    sealed class CloudJournal { public Dictionary<string, CloudAppJournal> apps = new Dictionary<string, CloudAppJournal>(); }
    sealed class CloudClient : ISaveCloud
    {
        internal readonly SaveSync Saves;
        internal readonly string Data, Root;
        readonly Func<string, long> minutes;
        readonly Action<string> log;
        CloudSettings settings;
        CloudJournal journal;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        public string UserName { get { return settings.userName ?? settings.deviceName; } }
        public bool Connected { get { return !string.IsNullOrEmpty(settings.token); } }
        internal string Device { get { return settings.deviceId; } }
        internal CloudClient(string data, string root, Func<string, long> getMinutes, Action<string> logger)
        {
            Data = data; Root = root; minutes = getMinutes; log = logger; Directory.CreateDirectory(Data);
            settings = new CloudSettings { server = "https://cabinet.mc2.onl", deviceId = Guid.NewGuid().ToString("N"), deviceName = Environment.MachineName };
            string p = Path.Combine(Data, "cloud.json");
            if (File.Exists(p))
            {
                settings = Json.Deserialize<CloudSettings>(File.ReadAllText(p));
                if (!string.IsNullOrEmpty(settings.token))
                {
                    try { settings.token = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(settings.token), null, DataProtectionScope.CurrentUser)); }
                    catch (CryptographicException) { settings.token = null; log("Stored token cannot be decrypted for this Windows user"); }
                }
            }
            else SaveSettings();
            string state = Path.Combine(Data, "cloud-state.json");
            journal = File.Exists(state) ? Json.Deserialize<CloudJournal>(File.ReadAllText(state)) : new CloudJournal();
            Saves = new SaveSync(Data, log, delegate { return SaveEnvironment.Current(Syncer.SteamPath(), Syncer.AccountId(), Root); });
        }
        static void Atomic(string path, string text)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        void SaveJournal() { Atomic(Path.Combine(Data, "cloud-state.json"), Json.Serialize(journal)); }
        void SaveSettings()
        {
            var stored = new CloudSettings { server = settings.server, token = string.IsNullOrEmpty(settings.token) ? null : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.token), null, DataProtectionScope.CurrentUser)), deviceId = settings.deviceId, deviceName = settings.deviceName, userName = settings.userName, userId = settings.userId };
            Atomic(Path.Combine(Data, "cloud.json"), Json.Serialize(stored));
        }
        public static void ParseLink(string link, out string server, out string token)
        {
            Uri u; if (!Uri.TryCreate(link, UriKind.Absolute, out u)) throw new FormatException("Invalid link");
            if (u.Scheme == "cabinet" && u.Host == "connect")
            {
                var values = new Dictionary<string, string>();
                foreach (string pair in u.Query.TrimStart('?').Split('&'))
                { int i = pair.IndexOf('='); if (i > 0) values[Uri.UnescapeDataString(pair.Substring(0, i))] = Uri.UnescapeDataString(pair.Substring(i + 1)); }
                if (!values.TryGetValue("server", out server) || !values.TryGetValue("token", out token)) throw new FormatException("Missing credentials");
            }
            else if ((u.Scheme == "https" || (u.Scheme == "http" && u.IsLoopback)) && u.AbsolutePath == "/connect")
            { server = u.GetLeftPart(UriPartial.Authority); token = Uri.UnescapeDataString(u.Fragment.TrimStart('#')); }
            else throw new FormatException("Invalid link");
            Uri origin;
            if (!Uri.TryCreate(server, UriKind.Absolute, out origin) || !(origin.Scheme == "https" || (origin.Scheme == "http" && origin.IsLoopback)) || origin.AbsolutePath != "/" || origin.Query != "" || origin.Fragment != "" || origin.UserInfo != "") throw new FormatException("Invalid server origin");
            server = origin.GetLeftPart(UriPartial.Authority);
            if (!Regex.IsMatch(token, @"^cab_[A-Za-z0-9_-]{43}$")) throw new FormatException("Invalid token");
        }
        public void Connect(string link)
        {
            string server, token; ParseLink(link, out server, out token); var previous = settings; var previousJournal = journal;
            settings = new CloudSettings { server = server, token = token, deviceId = previous.deviceId, deviceName = previous.deviceName };
            try
            {
                var me = Map(Map(RequestJson("GET", "/api/me", null))["user"]);
                settings.userName = Convert.ToString(me["name"]); settings.userId = Convert.ToString(me["id"]);
                if (previous.server != server || previous.userId != settings.userId)
                {
                    // Account-specific remote watermarks must not suppress another account's
                    // progress, or relabel old pending receipts as belonging to the new user.
                    if (!string.IsNullOrEmpty(previous.userId))
                        Atomic(Path.Combine(Data, "cloud-account-" + Hash(Encoding.UTF8.GetBytes(previous.server + ":" + previous.userId)) + ".json"), Json.Serialize(journal));
                    string archive = Path.Combine(Data, "cloud-account-" + Hash(Encoding.UTF8.GetBytes(server + ":" + settings.userId)) + ".json");
                    journal = File.Exists(archive) ? Json.Deserialize<CloudJournal>(File.ReadAllText(archive)) : new CloudJournal();
                }
                SaveSettings(); SaveJournal();
            }
            catch { settings = previous; journal = previousJournal; throw; }
        }
        public void Disconnect() { settings.token = null; SaveSettings(); }
        internal static Dictionary<string, object> Map(object obj) { return obj as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        internal static long Num(object obj) { return Convert.ToInt64(obj, CultureInfo.InvariantCulture); }
        internal static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }
        internal static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal object RequestJson(string method, string path, object body)
        { return Json.DeserializeObject(Encoding.UTF8.GetString(Request(method, path, body == null ? null : Encoding.UTF8.GetBytes(Json.Serialize(body)), "application/json", false))); }
        internal string PresenceKey { get { return settings.server + ":" + settings.userId + ":" + settings.token; } }
        internal Func<string, string, object, object> PresenceRequest()
        {
            string server = settings.server, token = settings.token;
            return delegate(string method, string path, object body)
            {
                var json = new JavaScriptSerializer();
                byte[] bytes = RequestTo(server, token, method, path, body == null ? null : Encoding.UTF8.GetBytes(json.Serialize(body)), "application/json", false, 5000);
                return json.DeserializeObject(Encoding.UTF8.GetString(bytes));
            };
        }
        byte[] Request(string method, string path, byte[] body, string type, bool acceptConflict)
        { return RequestTo(settings.server, settings.token, method, path, body, type, acceptConflict, 30000); }
        // Any HTTP status comes back with its body; only transport failures throw.
        byte[] RequestAny(string method, string path, byte[] body, string type, out int status)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var req = (HttpWebRequest)WebRequest.Create(settings.server + path);
            req.Method = method; req.Timeout = 120000; req.ReadWriteTimeout = 120000; req.AllowAutoRedirect = false;
            req.UserAgent = "Cabinet/" + BuildInfo.Version; req.Headers[HttpRequestHeader.Authorization] = "Bearer " + settings.token;
            req.Headers["Origin"] = new Uri(settings.server).GetLeftPart(UriPartial.Authority);
            if (body != null) { req.ContentType = type; req.ContentLength = body.Length; using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length); }
            HttpWebResponse response;
            try { response = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex) { response = ex.Response as HttpWebResponse; if (response == null) throw; }
            using (response) using (var s = response.GetResponseStream()) using (var ms = new MemoryStream())
            {
                status = (int)response.StatusCode; byte[] buffer = new byte[65536]; int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0) { if (ms.Length + n > 4L * 1024 * 1024) throw new InvalidDataException("Cloud response too large"); ms.Write(buffer, 0, n); }
                return ms.ToArray();
            }
        }
        static byte[] RequestTo(string server, string token, string method, string path, byte[] body, string type, bool acceptConflict, int timeout)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var req = (HttpWebRequest)WebRequest.Create(server + path);
            req.Method = method; req.Timeout = timeout; req.ReadWriteTimeout = timeout; req.AllowAutoRedirect = false;
            req.UserAgent = "Cabinet/" + BuildInfo.Version; req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            // Bedrock rejects writes whose Origin isn't the pebble's own.
            req.Headers["Origin"] = new Uri(server).GetLeftPart(UriPartial.Authority);
            if (body != null) { req.ContentType = type; req.ContentLength = body.Length; using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length); }
            HttpWebResponse response;
            try { response = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex)
            {
                response = ex.Response as HttpWebResponse; if (response == null) throw;
                if (!acceptConflict || response.StatusCode != HttpStatusCode.Conflict) { response.Dispose(); throw; }
            }
            using (response) using (var s = response.GetResponseStream()) using (var ms = new MemoryStream())
            {
                byte[] buffer = new byte[65536]; int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0) { if (ms.Length + n > 90L * 1024 * 1024) throw new InvalidDataException("Cloud response too large"); ms.Write(buffer, 0, n); }
                return ms.ToArray();
            }
        }
        CloudAppJournal AppJournal(string app)
        { CloudAppJournal a; if (!journal.apps.TryGetValue(app, out a)) { a = new CloudAppJournal(); journal.apps[app] = a; } return a; }
        static bool AppId(string id) { return Regex.IsMatch(id, @"^[0-9]{1,10}$"); }
        string StatsPath(string app, string file) { return Path.Combine(Root, app, "Stats", file); }
        Dictionary<string, object> Achievements(string app)
        {
            var result = new Dictionary<string, object>();
            foreach (var s in Syncer.ReadIni(StatsPath(app, "Achievements.ini")))
            {
                string yes, time; long ts;
                if (!s.Value.TryGetValue("achieved", out yes) || !yes.Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
                if (!s.Value.TryGetValue("timestamp", out time) || !long.TryParse(time, out ts) || ts <= 0) ts = Playtime.Unix(File.GetLastWriteTimeUtc(StatsPath(app, "Achievements.ini")));
                result[s.Key] = ts;
            }
            return result;
        }
        Dictionary<string, object> Stats(string app)
        {
            var result = new Dictionary<string, object>(); Dictionary<string, string> section;
            if (Syncer.ReadIni(StatsPath(app, "Stats.ini")).TryGetValue("Stats", out section))
                foreach (var s in section) { double n; if (double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && !double.IsNaN(n) && !double.IsInfinity(n)) result[s.Key] = n; }
            return result;
        }
        public void Round(Func<string, bool> blocked)
        {
            foreach (string dir in Directory.Exists(Root) ? Directory.GetDirectories(Root) : new string[0])
            {
                string id = Path.GetFileName(dir); if (!AppId(id) || blocked(id)) continue;
                try { Push(id, blocked); } catch (Exception ex) { log("push " + id + ": " + ex.Message); }
            }
            var state = Map(Map(RequestJson("GET", "/api/state", null))["apps"]);
            foreach (var app in state)
            {
                if (!AppId(app.Key) || blocked(app.Key)) continue;
                try { Apply(app.Key, Map(app.Value), blocked); } catch (Exception ex) { log("pull " + app.Key + ": " + ex.Message); }
            }
            SaveJournal();
            SyncSaves(state, blocked);
        }
        // Saves of every eligible game (OnlineFix folders, LuaTools luas, overrides) plus cloud
        // backups of games installed here. See docs/save-sync.md.
        internal void SyncSaves(Dictionary<string, object> state, Func<string, bool> blocked)
        {
            Saves.UseAccount(settings.server, settings.userId);
            Saves.Manifest.Refresh(false);
            var env = Saves.CurrentEnvironment(true);
            var eligible = new HashSet<string>(Saves.Eligible(env));
            var ids = new SortedSet<string>(eligible, StringComparer.Ordinal);
            foreach (var app in state) { object archive; if (AppId(app.Key) && Map(app.Value).TryGetValue("archive", out archive) && archive != null) ids.Add(app.Key); }
            foreach (string id in ids)
            {
                object value, archive = null, save = null;
                if (state.TryGetValue(id, out value)) { Map(value).TryGetValue("archive", out archive); Map(value).TryGetValue("save", out save); }
                var cloud = SaveCloudMeta.Parse(archive);
                Saves.Sync(id, cloud, SaveLegacyMeta.Parse(save), this, blocked, env, eligible.Contains(id) || (cloud != null && env.Installed.ContainsKey(id)));
            }
        }
        internal void ScanSaves(bool connected)
        {
            Saves.Manifest.Refresh(false);
            var env = Saves.CurrentEnvironment(true);
            foreach (string id in Saves.Eligible(env)) Saves.Scan(id, env, true, connected);
        }
        internal Dictionary<string, object> AppStateOf(string appid)
        {
            var state = Map(Map(RequestJson("GET", "/api/state", null))["apps"]); object value;
            return state.TryGetValue(appid, out value) ? Map(value) : new Dictionary<string, object>();
        }
        public SavePutResult Put(string appid, SaveArchive archive, string baseHash, bool deleteIntent)
        {
            string path = "/api/apps/" + appid + "/archive?deviceId=" + Uri.EscapeDataString(Device) + "&sha256=" + Hash(archive.Bytes) + "&contentHash=" + archive.ContentHash
                + "&files=" + archive.Files.Count.ToString(CultureInfo.InvariantCulture) + "&bytes=" + archive.ExpandedBytes.ToString(CultureInfo.InvariantCulture) + "&base=" + (baseHash ?? "") + (deleteIntent ? "&intent=delete" : "");
            int status; byte[] body = RequestAny("PUT", path, archive.Bytes, "application/zip", out status);
            var result = new SavePutResult { Status = status }; Dictionary<string, object> reply;
            try { reply = Map(Json.DeserializeObject(Encoding.UTF8.GetString(body))); } catch (Exception) { reply = new Dictionary<string, object>(); }
            if (status == 200) result.Meta = SaveCloudMeta.Parse(reply);
            else if (status == 409) result.Meta = SaveCloudMeta.Parse(reply.ContainsKey("current") ? reply["current"] : null);
            else result.Error = reply.ContainsKey("error") ? Convert.ToString(reply["error"]) : null;
            return result;
        }
        public byte[] Get(string appid, string versionId)
        { return Request("GET", "/api/apps/" + appid + "/archive" + (versionId == null ? "" : "?id=" + Uri.EscapeDataString(versionId)), null, null, false); }
        public byte[] GetLegacy(string appid) { return Request("GET", "/api/apps/" + appid + "/save", null, null, false); }
        void Push(string id, Func<string, bool> blocked)
        {
            var a = AppJournal(id); var ach = Achievements(id); var stats = Stats(id);
            string statsHash = Hash(Encoding.UTF8.GetBytes(Json.Serialize(stats)));
            if (statsHash != a.statsHash)
            {
                a.statsHash = statsHash; string path = StatsPath(id, "Stats.ini");
                a.statsUpdatedAt = File.Exists(path) ? (long)(File.GetLastWriteTimeUtc(path) - new DateTime(1970, 1, 1)).TotalMilliseconds : 0;
            }
            a.ownMinutes = Math.Max(a.ownMinutes, minutes(id));
            var body = new Dictionary<string, object> { { "deviceId", Device }, { "deviceName", settings.deviceName }, { "achievements", ach }, { "stats", stats }, { "statsUpdatedAt", a.statsUpdatedAt }, { "playtimeMinutes", a.ownMinutes } };
            string progress = Hash(Encoding.UTF8.GetBytes(Json.Serialize(body))); if (blocked(id)) return;
            if (progress != a.progressHash) { RequestJson("POST", "/api/apps/" + id + "/progress", body); a.progressHash = progress; SaveJournal(); }
            // Saves sync separately (SaveSync) from every discovered save location; the legacy
            // OnlineFix-only ZIP upload is retired.
        }
        void Apply(string id, Dictionary<string, object> state, Func<string, bool> blocked)
        {
            var a = AppJournal(id); string achPath = StatsPath(id, "Achievements.ini"); var local = Syncer.ReadIni(achPath);
            foreach (var ach in Map(state["achievements"]))
            {
                if (blocked(id)) return; long ts = Num(ach.Value); Dictionary<string, string> sec; string t, yes; long old;
                if (local.TryGetValue(ach.Key, out sec) && sec.TryGetValue("achieved", out yes) && yes.Equals("true", StringComparison.OrdinalIgnoreCase) && sec.TryGetValue("timestamp", out t) && long.TryParse(t, out old) && old > 0) ts = Math.Min(ts, old);
                EditIni(achPath, ach.Key, new Dictionary<string, string> { { "achieved", "true" }, { "timestamp", ts.ToString(CultureInfo.InvariantCulture) } }, false);
            }
            long statsTime = Num(state["statsUpdatedAt"]); string sp = StatsPath(id, "Stats.ini");
            long localTime = File.Exists(sp) ? (long)(File.GetLastWriteTimeUtc(sp) - new DateTime(1970, 1, 1)).TotalMilliseconds : 0;
            if (statsTime > Math.Max(a.statsUpdatedAt, localTime) && !blocked(id))
            {
                var values = new Dictionary<string, string>(); foreach (var st in Map(state["stats"])) values[st.Key] = Convert.ToString(st.Value, CultureInfo.InvariantCulture);
                EditIni(sp, "Stats", values, true); File.SetLastWriteTimeUtc(sp, new DateTime(1970, 1, 1).AddMilliseconds(statsTime));
                a.statsUpdatedAt = statsTime; a.statsHash = Hash(Encoding.UTF8.GetBytes(Json.Serialize(Stats(id))));
            }
            foreach (var time in Map(state["playtime"]))
            {
                if (time.Key == Device) continue; long previous; a.remote.TryGetValue(time.Key, out previous); long total = Num(time.Value);
                if (total > previous) a.pending[time.Key] = total;
            }
            SaveJournal();
        }
        public void Deliver(Func<string, string, int, bool> add, Func<string, bool> blocked)
        {
            foreach (var app in journal.apps)
            {
                if (blocked(app.Key)) continue;
                foreach (var remote in app.Value.pending.ToArray())
                {
                    if (blocked(app.Key)) break;
                    long previous; app.Value.remote.TryGetValue(remote.Key, out previous); long delta = remote.Value - previous;
                    if (delta <= 0) { app.Value.pending.Remove(remote.Key); continue; }
                    int chunk = (int)Math.Min(int.MaxValue, delta); long target = previous + chunk;
                    string receipt = "cloud:" + Hash(Encoding.UTF8.GetBytes(settings.server + ":" + settings.userId + ":" + app.Key + ":" + remote.Key + ":" + target));
                    if (add(receipt, app.Key, chunk)) { app.Value.remote[remote.Key] = target; if (target == remote.Value) app.Value.pending.Remove(remote.Key); SaveJournal(); }
                }
            }
        }
        // Preserve comments, unrelated sections, detected encoding and newline style.
        static void EditIni(string path, string section, Dictionary<string, string> values, bool replace)
        {
            if (section.IndexOfAny(new[] { '\r', '\n', '[', ']' }) >= 0 || values.Keys.Any(k => k.IndexOfAny(new[] { '\r', '\n', '=' }) >= 0)) throw new InvalidDataException("Invalid INI key");
            Encoding encoding = new UTF8Encoding(false); string text = "";
            if (File.Exists(path)) using (var reader = new StreamReader(path, encoding, true)) { text = reader.ReadToEnd(); encoding = reader.CurrentEncoding; }
            string nl = text.Contains("\r\n") ? "\r\n" : "\n"; var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            int start = -1, end = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string s = lines[i].Trim();
                if (s.StartsWith("[") && s.EndsWith("]"))
                { if (start >= 0) { end = i; break; } if (s.Substring(1, s.Length - 2).Equals(section, StringComparison.OrdinalIgnoreCase)) start = i; }
            }
            if (start < 0) { lines.Add("[" + section + "]"); start = lines.Count - 1; end = lines.Count; }
            var lookup = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            var remaining = new Dictionary<string, string>(lookup, StringComparer.OrdinalIgnoreCase);
            for (int i = start + 1; i < end; i++)
            {
                string s = lines[i].Trim(); int eq = s.IndexOf('='); if (eq <= 0 || s.StartsWith(";") || s.StartsWith("#")) continue;
                string key = s.Substring(0, eq).Trim(), value;
                if (lookup.TryGetValue(key, out value)) { lines[i] = lines[i].Substring(0, lines[i].IndexOf('=') + 1) + value; remaining.Remove(key); }
                else if (replace) { lines.RemoveAt(i--); end--; }
            }
            foreach (var value in remaining) lines.Insert(end++, value.Key + "=" + value.Value);
            string next = string.Join(nl, lines); if (next == text) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string tmp = path + ".cloud.tmp"; File.WriteAllText(tmp, next, encoding);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
    }
}
