using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Cabinet
{
    // Only Spacewar's spare slot is writable. Steam's shared files are edited by node,
    // never restored wholesale. No undocumented IPC calls or client restart are used.
    sealed class ControllerLayouts
    {
        public sealed class Original { public string File; public string[] Node; public string Value; }
        public sealed class Snapshot { public string Steam; public List<Original> Entries = new List<Original>(); public List<string> Accounts = new List<string>(); }
        public sealed class LastGame { public string Account; public string App; }
        sealed class Layout { public string Text; public string Device; public string Description; }
        readonly string steam, data, originalPath, lastPath;
        readonly Action<string> log;
        readonly Func<bool> steamRunning;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        HashSet<string> previous = new HashSet<string>();
        string previousAccount, selected;
        bool wasSteamRunning;
        DateTime retry;
        readonly HashSet<string> reports = new HashSet<string>();
        void Report(string message) { if (reports.Add(message)) log(message); }
        static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        static readonly string[] OverrideParent = { "UserLocalConfigStore", "apps", "480" };

        public ControllerLayouts(Action<string> logger) : this(Syncer.SteamPath(), App.DataDir, Playtime.SteamRunning, logger) { }
        internal ControllerLayouts(string steamRoot, string dataRoot, Func<bool> isRunning, Action<string> logger)
        {
            steam = Path.GetFullPath(steamRoot).TrimEnd(Path.DirectorySeparatorChar);
            data = dataRoot; log = logger; steamRunning = isRunning;
            originalPath = Path.Combine(data, "controller-480-original.json");
            lastPath = Path.Combine(data, "controller-last-game.json");
            wasSteamRunning = steamRunning();
        }

        public string Tick(HashSet<string> running, string account, string latest)
        {
            bool live = steamRunning();
            if (account == null) { previous = new HashSet<string>(running); wasSteamRunning = live; return null; }
            bool launch = account != previousAccount || running.Except(previous).Any() || (latest != null && latest != selected);
            if (running.Count > 0 && (launch || selected == null)) selected = latest ?? running.OrderBy(x => x, StringComparer.Ordinal).Last();
            previous = new HashSet<string>(running); previousAccount = account;
            bool closed = wasSteamRunning && !live; wasSteamRunning = live;
            if (running.Count > 0 && selected != null && (launch || DateTime.UtcNow >= retry))
            {
                retry = DateTime.UtcNow.AddMinutes(1);
                if (Apply(selected, account))
                    return L.T("Controller layout prepared for this game. If Steam still shows the old layout, close Steam once after playing, then reopen it.",
                        "Configuration manette préparée pour ce jeu. Si Steam affiche encore l'ancienne, fermez Steam une fois après avoir joué, puis rouvrez-le.");
            }
            // Steam may rewrite its in-memory selection on shutdown. Reapply the last
            // game while closed, including after a Cabinet restart. Keep it between games.
            if (!live && running.Count == 0 && (closed || DateTime.UtcNow >= retry) && File.Exists(lastPath))
            {
                retry = DateTime.UtcNow.AddMinutes(1);
                var last = json.Deserialize<LastGame>(File.ReadAllText(lastPath));
                if (last != null && last.Account == account) Apply(last.App, account);
            }
            return null;
        }

        static void Numeric(string value)
        {
            uint n;
            if (!uint.TryParse(value, out n) || n == 0) throw new InvalidDataException("Invalid controller app/account ID");
        }
        string Config(string account) { return Path.Combine(steam, "steamapps", "common", "Steam Controller Configs", account, "config"); }
        string Local(string account) { return Path.Combine(steam, "userdata", account, "config", "localconfig.vdf"); }
        static byte[] Read(string path) { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        static string Decode(byte[] bytes) { return Utf8.GetString(bytes).TrimStart('\ufeff'); }
        static string Quote(string value) { return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
        static bool Same(byte[] a, byte[] b) { return a == null ? b == null : b != null && a.SequenceEqual(b); }
        static string Device(string text) { return new VdfText(text).StringValue("controller_mappings", "controller_type"); }
        internal static bool NativeActions(string text)
        {
            return Regex.IsMatch(text, @"\bgame_action\s", RegexOptions.IgnoreCase) ||
                new VdfText(text).RawValue("controller_mappings", "actions") != null;
        }
        static bool SafeName(string name) { return !string.IsNullOrEmpty(name) && name == Path.GetFileName(name) && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && name != "." && name != ".."; }

        string Template(string name)
        {
            if (!SafeName(name)) throw new InvalidDataException("Unsafe controller template name");
            string path = Path.Combine(steam, "controller_base", "templates", name);
            return File.Exists(path) ? path : null;
        }
        string Workshop(string id)
        {
            ulong n;
            if (!ulong.TryParse(id, out n) || n == 0) throw new InvalidDataException("Invalid controller Workshop ID");
            string dir = Path.Combine(steam, "steamapps", "workshop", "content", "241100", id);
            if (!Directory.Exists(dir)) return null;
            var files = Directory.GetFiles(dir).Where(f => f.EndsWith(".vdf", StringComparison.OrdinalIgnoreCase) || f.EndsWith("_legacy.bin", StringComparison.OrdinalIgnoreCase)).ToArray();
            // Never guess between multiple Workshop revisions.
            return files.Length == 1 ? files[0] : null;
        }
        Layout LoadLayout(string path, string device, string description)
        {
            if (path == null) return null;
            VdfText.EnsureMediaParents(path);
            var bytes = Read(path);
            if (bytes == null || bytes.Length > 4 * 1024 * 1024) return null;
            string text = Decode(bytes);
            var vdf = new VdfText(text); vdf.ValidateControllerText();
            if (vdf.RawValue("controller_mappings") == null) throw new InvalidDataException("Unknown controller layout format");
            return new Layout { Text = text, Device = device ?? Device(text), Description = description };
        }

        Layout Effective(string root, string app, string device, string selection, KV defaults)
        {
            // The active autosave is the user's current custom layout. Exported, unused
            // named layouts are not selected just because they are newer.
            string custom = Path.Combine(root, app, device + ".vdf");
            if (File.Exists(custom)) return LoadLayout(custom, device, "custom " + device);
            var vdf = selection == null ? null : new VdfText("\"selection\" " + selection);
            if (vdf != null)
            {
                string template = vdf.StringValue("selection", "template");
                string workshop = vdf.StringValue("selection", "workshop");
                if (template != null) return LoadLayout(Template(template), device, "selected template " + template);
                if (workshop != null) return LoadLayout(Workshop(workshop), device, "selected Workshop " + workshop);
                // An unavailable autosave must not silently become a guessed default.
                if (vdf.RawValue("selection", "autosave") != null) return null;
                if (selection.Trim() != "{ }" && selection.Trim() != "{}") return null;
            }
            if (defaults == null) return null;
            var details = defaults.Get("steamcontrollerconfigdetails");
            if (details != null && details.Children != null)
            {
                var choices = details.Children.Where(k => k.Get("controller_type") != null && k.Get("controller_type").Str() == device &&
                    k.Get("enabled_branches") != null && k.Get("enabled_branches").Str().Split(',').Contains("default")).ToArray();
                if (choices.Length == 1) return LoadLayout(Workshop(choices[0].Key), device, "official Workshop " + choices[0].Key);
            }
            var index = defaults.Get("steamcontrollertemplateindex");
            if (index != null && index.Str() == "1") return LoadLayout(Template(device + "_gamepad_joystick.vdf"), device, "default gamepad template");
            return null;
        }

        public bool Apply(string app, string account)
        {
            Numeric(app); Numeric(account);
            if (app == "480") throw new InvalidDataException("Source app must be the real game");
            using (var mutex = new Mutex(false, @"Local\Cabinet-controller-write"))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                    if (!held) return false;
                    Directory.CreateDirectory(data);
                    // Persist intent before writes, so partial work is retried after Steam exits.
                    Save(lastPath, json.Serialize(new LastGame { Account = account, App = app }));
                    string root = Config(account);
                    if (!Directory.Exists(root)) { Report("controller " + app + ": no controller configuration store"); return false; }
                    CaptureAccount(account);
                    KV defaults = null;
                    try { defaults = AppDefaults(app); } catch (Exception ex) { Report("controller " + app + ": defaults unavailable: " + ex.Message); }
                    bool changed = false;
                    var keptFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var devices = new Dictionary<string, Layout>(StringComparer.OrdinalIgnoreCase);
                    var sets = Directory.GetFiles(root, "configset_*.vdf").OrderBy(x => x, StringComparer.Ordinal).ToList();
                    // Per-type entries cover official/default layouts even if no custom exists.
                    foreach (var file in sets.Where(f => Path.GetFileName(f).StartsWith("configset_controller_", StringComparison.OrdinalIgnoreCase)).ToArray())
                    {
                        string device = Path.GetFileNameWithoutExtension(file).Substring(10);
                        string selection = new VdfText(Decode(Read(file))).RawValue("controller_config", app);
                        Layout layout = Effective(root, app, device, selection, defaults);
                        if (layout != null) devices[device] = Compatible(layout, app);
                        else Report("controller " + app + ": no available recorded layout for " + device);
                    }
                    // A custom device layout may precede its configset file.
                    string sourceDir = Path.Combine(root, app);
                    if (Directory.Exists(sourceDir)) foreach (var file in Directory.GetFiles(sourceDir, "controller_*.vdf"))
                    {
                        var layout = LoadLayout(file, null, "custom " + Path.GetFileName(file));
                        if (layout == null || !SafeName(layout.Device)) continue;
                        devices[layout.Device] = Compatible(layout, app);
                        string set = Path.Combine(root, "configset_" + layout.Device + ".vdf");
                        if (!sets.Contains(set)) sets.Add(set);
                    }
                    foreach (var pair in devices.ToArray())
                    {
                        if (pair.Value == null) continue;
                        string dest = Path.Combine(root, "480", pair.Key + ".vdf");
                        keptFiles.Add(dest);
                        bool copied = WriteSlotFile(dest, Utf8.GetBytes(pair.Value.Text)); changed |= copied;
                        if (copied) log("controller " + app + ": prepared " + pair.Value.Description + " for " + pair.Key);
                    }
                    foreach (var file in sets)
                    {
                        byte[] before = Read(file);
                        var vdf = new VdfText(before == null ? "\"controller_config\" { }" : Decode(before));
                        string suffix = Path.GetFileNameWithoutExtension(file).Substring(10);
                        Layout layout;
                        if (suffix.StartsWith("controller_", StringComparison.OrdinalIgnoreCase)) devices.TryGetValue(suffix, out layout);
                        else
                        {
                            // Physical-device selections override per-type selections. Resolve
                            // its real-app template/Workshop to discover the controller type.
                            string raw = vdf.RawValue("controller_config", app); layout = null;
                            string serialCustom = Path.Combine(root, app, suffix + ".vdf");
                            if (File.Exists(serialCustom))
                            {
                                layout = Compatible(LoadLayout(serialCustom, null, "physical-device custom"), app);
                                if (layout != null)
                                {
                                    string serialDest = Path.Combine(root, "480", suffix + ".vdf"); keptFiles.Add(serialDest);
                                    changed |= WriteSlotFile(serialDest, Utf8.GetBytes(layout.Text));
                                }
                            }
                            else if (raw != null)
                            {
                                var selectedVdf = new VdfText("\"selection\" " + raw);
                                string t = selectedVdf.StringValue("selection", "template"), w = selectedVdf.StringValue("selection", "workshop");
                                if (t != null || w != null)
                                {
                                    layout = LoadLayout(t != null ? Template(t) : Workshop(w), null, "physical-device selection");
                                    if (layout != null && SafeName(layout.Device))
                                    {
                                        layout = Compatible(layout, app);
                                        if (layout != null)
                                        {
                                            string serialDest = Path.Combine(root, "480", suffix + ".vdf"); keptFiles.Add(serialDest);
                                            changed |= WriteSlotFile(serialDest, Utf8.GetBytes(layout.Text));
                                        }
                                    }
                                }
                                else if (selectedVdf.StringValue("selection", "autosave") == "1")
                                {
                                    // Serial autosaves use the same serial as their configset.
                                    string path = Path.Combine(root, app, suffix + ".vdf");
                                    if (File.Exists(path))
                                    {
                                        layout = Compatible(LoadLayout(path, null, "physical-device custom"), app);
                                        if (layout != null)
                                        {
                                            string serialDest = Path.Combine(root, "480", suffix + ".vdf"); keptFiles.Add(serialDest);
                                            changed |= WriteSlotFile(serialDest, Utf8.GetBytes(layout.Text));
                                        }
                                    }
                                }
                            }
                        }
                        // Remove a previous game's stale per-device selection when no source
                        // selection exists, allowing the copied per-type selection to win.
                        string wanted = layout == null ? null : "{ \"autosave\" \"1\" }";
                        changed |= WriteNode(file, new[] { "controller_config", "480" }, wanted, "\"controller_config\" { }");
                    }
                    // Remove stale 480 autosaves left by an earlier game for unsupported types.
                    string target = Path.Combine(root, "480");
                    if (Directory.Exists(target)) foreach (var file in Directory.GetFiles(target, "*.vdf"))
                    {
                        if (!keptFiles.Contains(file)) changed |= WriteSlotFile(file, null);
                    }
                    string local = Local(account);
                    if (File.Exists(local))
                    {
                        string value = new VdfText(Decode(Read(local))).RawValue("UserLocalConfigStore", "apps", app, "UseSteamControllerConfig");
                        if (value != null && value != "\"0\"" && value != "\"1\"" && value != "\"2\"") throw new InvalidDataException("Unknown Steam Input override");
                        changed |= WriteNode(local, OverrideParent.Concat(new[] { "UseSteamControllerConfig" }).ToArray(), value, null);
                    }
                    if (changed) log("controller " + app + ": Spacewar slot updated; Steam live reload is unverified; retained for next session");
                    return changed;
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }
        Layout Compatible(Layout layout, string app)
        {
            if (layout == null || !SafeName(layout.Device)) return null;
            if (!NativeActions(layout.Text)) return layout;
            Report("controller " + app + ": " + layout.Description + " contains native game actions; using gamepad template (real-app manifest cannot be remapped to 480)");
            return LoadLayout(Template(layout.Device + "_gamepad_joystick.vdf"), layout.Device, "gamepad fallback for native actions");
        }

        string Relative(string path)
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(steam + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Controller path outside Steam");
            VdfText.EnsureMediaParents(full);
            return full.Substring(steam.Length + 1);
        }
        void CaptureAccount(string account)
        {
            var snapshot = SnapshotForWrite();
            if (snapshot.Accounts.Contains(account)) return;
            string root = Config(account);
            foreach (var file in Directory.GetFiles(root, "configset_*.vdf"))
                Remember(file, new[] { "controller_config", "480" }, new VdfText(Decode(Read(file))).RawValue("controller_config", "480"));
            string local = Local(account);
            if (File.Exists(local)) Remember(local, OverrideParent.Concat(new[] { "UseSteamControllerConfig" }).ToArray(),
                new VdfText(Decode(Read(local))).RawValue(OverrideParent.Concat(new[] { "UseSteamControllerConfig" }).ToArray()));
            string slot = Path.Combine(root, "480");
            if (Directory.Exists(slot)) foreach (var file in Directory.GetFiles(slot, "*.vdf")) Remember(file, null, Convert.ToBase64String(Read(file)));
            snapshot = SnapshotForWrite(); snapshot.Accounts.Add(account); Save(originalPath, json.Serialize(snapshot));
        }
        Snapshot SnapshotForWrite()
        {
            var snapshot = File.Exists(originalPath) ? json.Deserialize<Snapshot>(File.ReadAllText(originalPath)) : new Snapshot { Steam = steam };
            if (snapshot == null || snapshot.Entries == null || !string.Equals(snapshot.Steam, steam, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Controller backup belongs to a different Steam installation");
            return snapshot;
        }
        void Remember(string path, string[] node, string value)
        {
            string relative = Relative(path);
            var snapshot = SnapshotForWrite();
            if (snapshot.Entries.Any(e => e.File == relative && string.Join("/", e.Node ?? new string[0]) == string.Join("/", node ?? new string[0]))) return;
            snapshot.Entries.Add(new Original { File = relative, Node = node, Value = value });
            Save(originalPath, json.Serialize(snapshot));
            log("controller: original 480 slot captured before first write to " + relative);
        }
        void Commit(string path, byte[] before, byte[] after)
        {
            Relative(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".cabinet-controller-" + Guid.NewGuid().ToString("N");
            try
            {
                if (after != null) File.WriteAllBytes(temporary, after);
                if (!Same(before, Read(path))) throw new IOException("Steam changed controller file during preparation; retry later");
                if (before != null)
                {
                    string backupRoot = Path.Combine(data, "controller-file-backups"); Directory.CreateDirectory(backupRoot);
                    string hash;
                    using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(Relative(path)))).Replace("-", "");
                    SaveBytes(Path.Combine(backupRoot, hash + ".previous"), before);
                }
                if (!Same(before, Read(path))) throw new IOException("Steam changed controller file before commit; retry later");
                if (after == null) File.Delete(path);
                else if (before == null) File.Move(temporary, path);
                else File.Replace(temporary, path, null);
                log("controller: " + (after == null ? "removed " : "atomically wrote ") + Relative(path) + "; previous file backed up");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        bool WriteSlotFile(string path, byte[] after)
        {
            if (Path.GetFileName(Path.GetDirectoryName(path)) != "480") throw new InvalidDataException("Only 480 layout files are writable");
            byte[] before = Read(path); if (Same(before, after)) return false;
            Remember(path, null, before == null ? null : Convert.ToBase64String(before));
            Commit(path, before, after); return true;
        }
        bool WriteNode(string path, string[] node, string value, string empty)
        {
            if (!node.Contains("480")) throw new InvalidDataException("Only 480 nodes are writable");
            byte[] before = Read(path);
            if (before == null && empty == null) throw new FileNotFoundException("Steam configuration missing", path);
            var vdf = new VdfText(before == null ? empty : Decode(before));
            string old = vdf.RawValue(node); if (old == value) return false;
            Remember(path, node, old); vdf.SetRawValue(value, node);
            byte[] after = Utf8.GetBytes(vdf.Text);
            if (before != null && before.Length >= 3 && before[0] == 239 && before[1] == 187 && before[2] == 191)
                after = new byte[] { 239, 187, 191 }.Concat(after).ToArray();
            Commit(path, before, after); return true;
        }
        public void Restore()
        {
            // Explicit restore also works on disk while Steam is running. The client
            // can retain/rewrite its cache; explain that limitation without restarting it.
            if (steamRunning()) log(L.T("controller: restoring on disk while Steam runs; close Steam and restore again before reopening for a persistent restore.",
                "controller : restauration sur disque pendant que Steam tourne ; fermez Steam et restaurez encore avant de le rouvrir pour une restauration persistante."));
            using (var mutex = new Mutex(false, @"Local\Cabinet-controller-write"))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Controller writer is busy");
                    if (!File.Exists(originalPath)) throw new FileNotFoundException(L.T("No original controller backup exists yet.", "Aucune sauvegarde manette originale n'existe encore."));
                    var snapshot = SnapshotForWrite();
                    foreach (var entry in snapshot.Entries)
                    {
                        string path = Path.Combine(steam, entry.File); Relative(path);
                        if (entry.Node == null) WriteSlotFile(path, entry.Value == null ? null : Convert.FromBase64String(entry.Value));
                        else WriteNode(path, entry.Node, entry.Value, Path.GetFileName(path).StartsWith("configset_", StringComparison.Ordinal) ? "\"controller_config\" { }" : null);
                    }
                    if (File.Exists(lastPath)) File.Delete(lastPath);
                    log("controller: restored original 480 slot; last-game preset cleared; original snapshot retained");
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }
        static void Save(string path, string text) { byte[] bytes = Utf8.GetBytes(text); if (!Same(Read(path), bytes)) SaveBytes(path, bytes); }
        static void SaveBytes(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temp, bytes); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        // Read-only appinfo v28/v29 reader, bounded to the requested record. v29 uses
        // indexed string keys. Unknown formats are refused rather than guessed.
        KV AppDefaults(string app)
        {
            string path = Path.Combine(steam, "appcache", "appinfo.vdf");
            if (!File.Exists(path)) return null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new BinaryReader(stream, Utf8))
            {
                uint magic = reader.ReadUInt32(); reader.ReadUInt32();
                if (magic != 0x07564428 && magic != 0x07564429) throw new InvalidDataException("Unsupported appinfo format");
                string[] keys = null;
                if (magic == 0x07564429)
                {
                    long offset = checked((long)reader.ReadUInt64()); long pos = stream.Position;
                    if (offset < pos || offset > stream.Length - 4) throw new InvalidDataException("Invalid appinfo string table");
                    stream.Position = offset; int count = reader.ReadInt32();
                    if (count < 0 || count > 1000000) throw new InvalidDataException("Oversized appinfo string table");
                    keys = new string[count]; for (int n = 0; n < count; n++) keys[n] = CString(reader);
                    stream.Position = pos;
                }
                uint wanted = uint.Parse(app);
                while (stream.Position < stream.Length - 4)
                {
                    uint id = reader.ReadUInt32(); if (id == 0) return null;
                    uint size = reader.ReadUInt32(); long end = checked(stream.Position + size);
                    if (size < 60 || end > stream.Length) throw new InvalidDataException("Invalid appinfo record size");
                    if (id != wanted) { stream.Position = end; continue; }
                    stream.Position += 60;
                    var root = BinaryNodes(reader, keys, end, 0);
                    var info = root.Get("appinfo"); return info == null ? null : info.Get("config");
                }
                return null;
            }
        }
        static string CString(BinaryReader reader)
        {
            var bytes = new List<byte>(); byte b;
            while ((b = reader.ReadByte()) != 0) { if (bytes.Count >= 4 * 1024 * 1024) throw new InvalidDataException("Oversized appinfo string"); bytes.Add(b); }
            return Utf8.GetString(bytes.ToArray());
        }
        static KV BinaryNodes(BinaryReader reader, string[] keys, long end, int depth)
        {
            if (depth > 64) throw new InvalidDataException("Appinfo nesting too deep");
            var root = new KV(0, "", new List<KV>());
            while (reader.BaseStream.Position < end)
            {
                byte type = reader.ReadByte(); if (type == 8 || type == 11) return root;
                string key;
                if (keys == null) key = CString(reader);
                else { uint i = reader.ReadUInt32(); if (i >= keys.Length) throw new InvalidDataException("Invalid appinfo key"); key = keys[i]; }
                object value;
                if (type == 0) value = BinaryNodes(reader, keys, end, depth + 1).Children;
                else if (type == 1) value = CString(reader);
                else if (type == 2) value = reader.ReadInt32();
                else if (type == 7) value = reader.ReadUInt64();
                else if (type == 3) value = reader.ReadSingle();
                else throw new InvalidDataException("Unsupported appinfo value type");
                if (reader.BaseStream.Position > end) throw new InvalidDataException("Appinfo record overrun");
                root.Children.Add(new KV(type, key, value));
            }
            throw new InvalidDataException("Unterminated appinfo record");
        }
    }
}
