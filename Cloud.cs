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
            if (wasPlaying && !anyGameRunning) requested = true; wasPlaying = anyGameRunning;
            string result = message; message = null; if (busy) return result;
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
            return result;
        }
        public static ToolStripMenuItem MenuItem()
        {
            var root = new ToolStripMenuItem(L.T("Cabinet Cloud", "Cabinet Cloud"));
            var status = new ToolStripMenuItem() { Enabled = false };
            var connect = new ToolStripMenuItem(L.T("Connect…", "Se connecter…"));
            var sync = new ToolStripMenuItem(L.T("Sync now", "Synchroniser"));
            var disconnect = new ToolStripMenuItem(L.T("Disconnect", "Se déconnecter"));
            root.DropDownItems.AddRange(new ToolStripItem[] { status, connect, sync, disconnect });
            root.DropDownOpening += delegate
            {
                status.Text = Client.Connected ? L.T("Connected as ", "Connecté en tant que ") + Client.UserName.Replace("&", "&&") : L.T("Not connected", "Non connecté");
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
            File.WriteAllText(Path.Combine(saves, "slot.dat"), save); File.SetLastWriteTimeUtc(Path.Combine(saves, "slot.dat"), date); Directory.SetLastWriteTimeUtc(saves, date);
        }
        public static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "Cabinet-cloud-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                using (var server = new FakeServer())
                {
                    string ar = Path.Combine(root, "A", "OnlineFix"), br = Path.Combine(root, "B", "OnlineFix");
                    DateTime date = DateTime.UtcNow.AddHours(-2); Seed(ar, "A", 100, "save-A", date.AddHours(1)); Seed(br, "B", 200, "save-B-old", date);
                    var errors = new List<string>(); Action<string> log = delegate(string s) { errors.Add(s); Console.WriteLine("LOG: " + s); };
                    var a = new CloudClient(Path.Combine(root, "A", "data"), ar, delegate { return 12; }, log);
                    long steamB = 7, importedB = 0;
                    var b = new CloudClient(Path.Combine(root, "B", "data"), br, delegate { return steamB - importedB; }, log);
                    string url = "cabinet://connect?server=" + Uri.EscapeDataString(server.Origin) + "&token=" + FakeServer.Token;
                    a.Connect(url); b.Connect(server.Origin + "/connect#" + FakeServer.Token);
                    Check(a.Device != b.Device && a.UserName == "Tester", "protocol and browser link connect; independent device IDs");
                    Check(!File.ReadAllText(Path.Combine(a.Data, "cloud.json")).Contains(FakeServer.Token), "DPAPI settings contain no plaintext token");
                    Func<string, bool> idle = delegate { return false; };
                    byte[] one = CloudClient.Zip(Path.Combine(ar, "123", "Saves")); File.SetLastWriteTimeUtc(Path.Combine(ar, "123", "Saves", "slot.dat"), date.AddHours(1));
                    Check(CloudClient.Hash(one) == CloudClient.Hash(CloudClient.Zip(Path.Combine(ar, "123", "Saves"))), "deterministic ZIP hash");
                    a.Round(idle); b.Round(idle); a.Round(idle);
                    Check(server.Conflicts == 1, "older save upload returns 409 and client pulls winner");
                    Check(File.ReadAllText(Path.Combine(br, "123", "Saves", "slot.dat")) == "save-A", "remote save downloaded and replaced");
                    string backup = Directory.GetFiles(Path.Combine(b.Data, "backup", "saves", "123"), "*.zip").Single();
                    using (var archive = ZipFile.OpenRead(backup)) using (var reader = new StreamReader(archive.GetEntry("slot.dat").Open())) Check(reader.ReadToEnd() == "save-B-old", "local save backed up before replacement");
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
                    b = new CloudClient(b.Data, br, delegate { return steamB - importedB; }, log); b.Deliver(ledger, idle); b.Round(idle); b.Deliver(ledger, idle);
                    Check(added == 12, "idempotent receipt replay across restart does not double count");
                    Check(CloudClient.Num(CloudClient.Map(server.App["playtime"])[a.Device]) == 12 && CloudClient.Num(CloudClient.Map(server.App["playtime"])[b.Device]) == 7, "per-device playtime max and own totals retained");
                    Check(steamB == 19 && importedB == 12, "imported playtime is subtracted before the next push");
                    server.EarlierAchievement(); b.Round(idle);
                    Check(Syncer.ReadIni(Path.Combine(br, "123", "Stats", "Achievements.ini"))["A"]["timestamp"] == "50", "earliest achievement timestamp wins");
                    string unchanged = File.ReadAllText(Path.Combine(br, "123", "Stats", "Achievements.ini")); int requests = server.Progress;
                    b.Round(delegate { return true; }); Check(server.Progress == requests && unchanged == File.ReadAllText(Path.Combine(br, "123", "Stats", "Achievements.ini")), "running app is never pushed or applied");
                    Check(errors.Count == 0, "end-to-end rounds completed without errors");
                    bool denied = false;
                    try { b.Connect(server.Origin + "/connect#cab_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"); } catch (WebException) { denied = true; }
                    Check(denied && b.Connected, "401 connection leaves the prior connection intact");
                    byte[] unsafeZip;
                    using (var ms = new MemoryStream())
                    {
                        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) using (var s = z.CreateEntry("../outside.dat").Open()) s.WriteByte(1);
                        unsafeZip = ms.ToArray();
                    }
                    bool refused = false;
                    try { b.ReplaceSaves("123", unsafeZip, idle); } catch (InvalidDataException) { refused = true; }
                    Check(refused && File.ReadAllText(Path.Combine(br, "123", "Saves", "slot.dat")) == "save-A", "unsafe archive rejected before saves are touched");
                    b.Disconnect(); Check(!new CloudClient(b.Data, br, delegate { return 0; }, log).Connected, "disconnect persists");
                    Console.WriteLine("Cabinet Cloud self-test passed (temporary roots only).");
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        sealed class FakeServer : IDisposable
        {
            internal const string Token = "cab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            readonly HttpListener listener = new HttpListener(); readonly Thread thread;
            readonly JavaScriptSerializer json = new JavaScriptSerializer();
            internal readonly string Origin;
            internal Dictionary<string, object> App = new Dictionary<string, object> { { "achievements", new Dictionary<string, object>() }, { "stats", new Dictionary<string, object>() }, { "statsUpdatedAt", 0L }, { "playtime", new Dictionary<string, object>() }, { "playtimeTotal", 0L }, { "save", null } };
            byte[] save;
            internal int Conflicts, Progress;
            internal FakeServer()
            {
                var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
                Origin = "http://localhost:" + port; listener.Prefixes.Add(Origin + "/"); listener.Start();
                thread = new Thread(Loop); thread.IsBackground = true; thread.Start();
            }
            internal void EarlierAchievement() { CloudClient.Map(App["achievements"])["A"] = 50L; }
            void Loop()
            {
                while (listener.IsListening)
                {
                    HttpListenerContext c; try { c = listener.GetContext(); } catch (HttpListenerException) { break; } catch (ObjectDisposedException) { break; }
                    try { Handle(c); } catch (Exception ex) { c.Response.StatusCode = 500; Reply(c, new Dictionary<string, object> { { "error", ex.Message } }); }
                    finally { c.Response.Close(); }
                }
            }
            void Reply(HttpListenerContext c, object obj)
            { byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(obj)); c.Response.ContentType = "application/json"; c.Response.OutputStream.Write(bytes, 0, bytes.Length); }
            void Handle(HttpListenerContext c)
            {
                if (c.Request.Headers["Authorization"] != "Bearer " + Token) { c.Response.StatusCode = 401; Reply(c, new { error = "unauthorized" }); return; }
                if (!c.Request.UserAgent.StartsWith("Cabinet/")) throw new Exception("Missing user agent");
                string path = c.Request.Url.AbsolutePath;
                if (path == "/api/me") { Reply(c, new { user = new { id = "test-user", name = "Tester" } }); return; }
                if (path == "/api/state") { Reply(c, new { apps = new Dictionary<string, object> { { "123", App } } }); return; }
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
                if (path == "/api/apps/123/save" && c.Request.HttpMethod == "PUT")
                {
                    long updated = long.Parse(c.Request.QueryString["updatedAt"]); var current = CloudClient.Map(App["save"]);
                    if (current.Count > 0 && updated <= CloudClient.Num(current["updatedAt"])) { Conflicts++; c.Response.StatusCode = 409; Reply(c, new { current = current }); return; }
                    using (var ms = new MemoryStream()) { c.Request.InputStream.CopyTo(ms); save = ms.ToArray(); }
                    if (CloudClient.Hash(save) != c.Request.QueryString["sha256"]) throw new Exception("Hash mismatch");
                    App["save"] = new Dictionary<string, object> { { "sha256", CloudClient.Hash(save) }, { "size", save.Length }, { "updatedAt", updated }, { "deviceId", c.Request.QueryString["deviceId"] }, { "deviceName", "Fake PC" } }; Reply(c, App["save"]); return;
                }
                if (path == "/api/apps/123/save" && c.Request.HttpMethod == "GET" && save != null)
                { c.Response.ContentType = "application/zip"; c.Response.Headers["ETag"] = CloudClient.Hash(save); c.Response.OutputStream.Write(save, 0, save.Length); return; }
                c.Response.StatusCode = 404; Reply(c, new { error = "not_found" });
            }
            public void Dispose() { listener.Close(); thread.Join(5000); }
        }
    }
    sealed class CloudSettings { public string server, token, deviceId, deviceName, userName, userId; }
    sealed class CloudAppJournal
    {
        public string progressHash, statsHash, saveHash;
        public long statsUpdatedAt, saveUpdatedAt, ownMinutes;
        public Dictionary<string, long> remote = new Dictionary<string, long>();
        public Dictionary<string, long> pending = new Dictionary<string, long>();
    }
    sealed class CloudJournal { public Dictionary<string, CloudAppJournal> apps = new Dictionary<string, CloudAppJournal>(); }
    sealed class CloudClient
    {
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
        byte[] Request(string method, string path, byte[] body, string type, bool acceptConflict)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var req = (HttpWebRequest)WebRequest.Create(settings.server + path);
            req.Method = method; req.Timeout = 30000; req.ReadWriteTimeout = 30000; req.AllowAutoRedirect = false;
            req.UserAgent = "Cabinet/" + BuildInfo.Version; req.Headers[HttpRequestHeader.Authorization] = "Bearer " + settings.token;
            // Bedrock rejects writes whose Origin isn't the pebble's own.
            req.Headers["Origin"] = new Uri(settings.server).GetLeftPart(UriPartial.Authority);
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
        }
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
            string saves = Path.Combine(Root, id, "Saves"); if (!Directory.Exists(saves) || blocked(id)) return;
            byte[] zip = Zip(saves); string hash = Hash(zip); if (hash == a.saveHash) return;
            DateTime modified = SafeEntries(saves).Select(File.GetLastWriteTimeUtc).Concat(new[] { Directory.GetLastWriteTimeUtc(saves) }).Max();
            long updated = (long)(modified - new DateTime(1970, 1, 1)).TotalMilliseconds; if (blocked(id)) return;
            var reply = Map(Json.DeserializeObject(Encoding.UTF8.GetString(Request("PUT", "/api/apps/" + id + "/save?deviceId=" + Uri.EscapeDataString(Device) + "&updatedAt=" + updated + "&sha256=" + hash, zip, "application/zip", true))));
            // 409 remains unacknowledged until pull applies the winning snapshot.
            if (!reply.ContainsKey("current")) { a.saveHash = hash; a.saveUpdatedAt = Num(reply["updatedAt"]); SaveJournal(); }
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
            object sm;
            if (state.TryGetValue("save", out sm) && sm != null)
            {
                var meta = Map(sm); string hash = Convert.ToString(meta["sha256"]); long updated = Num(meta["updatedAt"]);
                if (hash == a.saveHash) a.saveUpdatedAt = Math.Max(a.saveUpdatedAt, updated);
                if (Convert.ToString(meta["deviceId"]) != Device && updated > a.saveUpdatedAt && hash != a.saveHash && !blocked(id))
                {
                    byte[] bytes = Request("GET", "/api/apps/" + id + "/save", null, null, false);
                    if (Hash(bytes) != hash) throw new InvalidDataException("Save changed during download; retry");
                    ReplaceSaves(id, bytes, blocked); a.saveHash = hash; a.saveUpdatedAt = updated;
                }
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
        internal static byte[] Zip(string folder)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    long size = 0;
                    foreach (string path in SafeEntries(folder).OrderBy(p => p, StringComparer.Ordinal))
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Save contains a reparse point");
                        bool dir = Directory.Exists(path); string name = path.Substring(folder.TrimEnd('\\', '/').Length + 1).Replace('\\', '/') + (dir ? "/" : "");
                        if (!dir) { size += new FileInfo(path).Length; if (size > 512L * 1024 * 1024) throw new InvalidDataException("Expanded save too large"); }
                        var entry = zip.CreateEntry(name, CompressionLevel.Optimal); entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        if (!dir) using (var src = File.OpenRead(path)) using (var dst = entry.Open()) src.CopyTo(dst);
                        if (ms.Length > 90L * 1024 * 1024) throw new InvalidDataException("Save exceeds 90 MB");
                    }
                }
                if (ms.Length > 90L * 1024 * 1024) throw new InvalidDataException("Save exceeds 90 MB"); return ms.ToArray();
            }
        }
        static IEnumerable<string> SafeEntries(string folder)
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Save contains a reparse point");
            foreach (string path in Directory.GetFileSystemEntries(folder))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Save contains a reparse point");
                yield return path;
                if (Directory.Exists(path)) foreach (string child in SafeEntries(path)) yield return child;
            }
        }
        internal void ReplaceSaves(string id, byte[] bytes, Func<string, bool> blocked)
        {
            string parent = Path.Combine(Root, id); Directory.CreateDirectory(parent);
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("App directory is a reparse point");
            string saves = Path.Combine(parent, "Saves"), stage = Path.Combine(parent, ".cloud-" + Guid.NewGuid().ToString("N")), old = stage + "-old";
            Directory.CreateDirectory(stage);
            try
            {
                using (var ms = new MemoryStream(bytes)) using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    long size = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (zip.Entries.Count > 10000) throw new InvalidDataException("Too many archive entries");
                    foreach (var entry in zip.Entries)
                    {
                        size += entry.Length; if (size > 512L * 1024 * 1024) throw new InvalidDataException("Expanded save too large");
                        string name = entry.FullName.Replace('/', '\\');
                        if (name.Contains(":") || name.Split('\\').Any(p => p == ".." || p.EndsWith(".") || p.EndsWith(" ") || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))) throw new InvalidDataException("Unsafe archive name");
                        string target = Path.GetFullPath(Path.Combine(stage, name));
                        if (!target.StartsWith(Path.GetFullPath(stage) + "\\", StringComparison.OrdinalIgnoreCase) || !names.Add(target)) throw new InvalidDataException("Unsafe archive path");
                        if (entry.FullName.EndsWith("/")) Directory.CreateDirectory(target);
                        else { Directory.CreateDirectory(Path.GetDirectoryName(target)); using (var src = entry.Open()) using (var dst = File.Create(target)) src.CopyTo(dst); }
                    }
                }
                if (blocked(id)) throw new IOException("Game started; save deferred");
                if (Directory.Exists(saves))
                {
                    string backup = Path.Combine(Data, "backup", "saves", id); Directory.CreateDirectory(backup);
                    File.WriteAllBytes(Path.Combine(backup, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + ".zip"), Zip(saves));
                    if (blocked(id)) throw new IOException("Game started; save deferred"); Directory.Move(saves, old);
                }
                try { Directory.Move(stage, saves); }
                catch { if (Directory.Exists(old)) Directory.Move(old, saves); throw; }
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); if (Directory.Exists(old)) Directory.Delete(old, true); }
        }
    }
}
