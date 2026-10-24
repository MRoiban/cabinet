// Save archives (format 2 and legacy), snapshots and restores. Shared with LuaTools verbatim.
// Format and rules: docs/save-sync.md.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Cabinet
{
    sealed class SaveArchiveRoot { public string Id, Source; }
    sealed class SaveArchiveFile { public int Root; public string Path, Sha256; public long Size; }
    sealed class SaveArchive
    {
        internal const string ManifestName = "cabinet-save.json";
        internal const long MaxArchive = 90L * 1024 * 1024, MaxExpanded = 512L * 1024 * 1024;
        internal const int MaxEntries = 10000;
        public int Version;              // 1 = legacy OnlineFix ZIP, 2 = cabinet-save
        public string GameId, ContentHash;
        public List<SaveArchiveRoot> Roots = new List<SaveArchiveRoot>();
        public List<SaveArchiveFile> Files = new List<SaveArchiveFile>();
        public byte[] Bytes;
        public bool IsEmpty { get { return Files.Count == 0; } }
        public long ExpandedBytes { get { return Files.Sum(f => f.Size); } }

        internal static string ComputeContentHash(IList<SaveArchiveRoot> roots, IEnumerable<SaveArchiveFile> files)
        {
            var b = new StringBuilder();
            foreach (var f in files) b.Append(roots[f.Root].Id).Append('\t').Append(f.Path).Append('\t').Append(f.Size.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(f.Sha256).Append('\n');
            return SaveUtil.Sha256(b.ToString());
        }
        internal static string EmptyContentHash { get { return SaveUtil.Sha256(""); } }
        string EntryName(SaveArchiveFile f) { return Version == 1 ? f.Path : "roots/" + f.Root.ToString(CultureInfo.InvariantCulture) + "/" + f.Path; }

        internal string ManifestJson()
        {
            var b = new StringBuilder();
            b.Append("{\"format\":\"cabinet-save\",\"version\":2,\"gameId\":").Append(SaveUtil.Json(GameId)).Append(",\"contentHash\":").Append(SaveUtil.Json(ContentHash)).Append(",\"roots\":[");
            for (int i = 0; i < Roots.Count; i++) b.Append(i == 0 ? "" : ",").Append("{\"id\":").Append(SaveUtil.Json(Roots[i].Id)).Append(",\"source\":").Append(SaveUtil.Json(Roots[i].Source)).Append('}');
            b.Append("],\"files\":[");
            for (int i = 0; i < Files.Count; i++)
            {
                var f = Files[i];
                b.Append(i == 0 ? "" : ",").Append("{\"root\":").Append(f.Root.ToString(CultureInfo.InvariantCulture)).Append(",\"path\":").Append(SaveUtil.Json(f.Path))
                    .Append(",\"size\":").Append(f.Size.ToString(CultureInfo.InvariantCulture)).Append(",\"sha256\":").Append(SaveUtil.Json(f.Sha256)).Append('}');
            }
            return b.Append("]}").ToString();
        }

        // Reads and validates an archive. Throws InvalidDataException on anything unsafe.
        internal static SaveArchive Read(byte[] bytes, string expectedGameId)
        {
            if (bytes == null || bytes.LongLength > MaxArchive) throw new InvalidDataException("Archive missing or larger than 90 MiB");
            var a = new SaveArchive { Bytes = bytes };
            using (var ms = new MemoryStream(bytes, false)) using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > MaxEntries + 1) throw new InvalidDataException("Too many archive entries");
                var first = zip.Entries.FirstOrDefault();
                if (first != null && first.FullName == ManifestName)
                {
                    if (first.Length > 1024 * 1024) throw new InvalidDataException("Archive manifest too large");
                    string text; using (var r = new StreamReader(first.Open(), new UTF8Encoding(false, true))) text = r.ReadToEnd();
                    ParseManifest(a, text, expectedGameId);
                    var entries = zip.Entries.Skip(1).ToList();
                    if (entries.Count != a.Files.Count) throw new InvalidDataException("Archive entries do not match its manifest");
                    for (int i = 0; i < entries.Count; i++)
                        if (entries[i].FullName != a.EntryName(a.Files[i]) || entries[i].Length != a.Files[i].Size) throw new InvalidDataException("Archive entry does not match its manifest: " + entries[i].FullName);
                }
                else
                {
                    // Legacy: the OnlineFix Saves folder; directory entries carry no content.
                    a.Version = 1; a.GameId = expectedGameId;
                    a.Roots.Add(new SaveArchiveRoot { Id = "onlinefix:Saves", Source = "onlinefix" });
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in zip.Entries)
                    {
                        string name = e.FullName.Replace('\\', '/');
                        if (name.EndsWith("/")) { if (!SaveUtil.SafeRelative(name.TrimEnd('/'))) throw new InvalidDataException("Unsafe archive name"); continue; }
                        if (!SaveUtil.SafeRelative(name) || !seen.Add(name)) throw new InvalidDataException("Unsafe archive name: " + name);
                        a.Files.Add(new SaveArchiveFile { Root = 0, Path = name, Size = e.Length });
                    }
                    a.Files = a.Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
                }
            }
            if (a.ExpandedBytes > MaxExpanded) throw new InvalidDataException("Expanded save too large");
            return a;
        }
        static void ParseManifest(SaveArchive a, string text, string expectedGameId)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
            var m = json.DeserializeObject(text) as Dictionary<string, object>;
            if (m == null || !"cabinet-save".Equals(m.ContainsKey("format") ? m["format"] : null)) throw new InvalidDataException("Not a Cabinet save archive");
            int version = Convert.ToInt32(m.ContainsKey("version") ? m["version"] : 0, CultureInfo.InvariantCulture);
            if (version != 2) throw new InvalidDataException("Unsupported save archive version " + version + "; update Cabinet");
            a.Version = 2; a.GameId = m.ContainsKey("gameId") ? m["gameId"] as string : null; a.ContentHash = m.ContainsKey("contentHash") ? m["contentHash"] as string : null;
            if (expectedGameId != null && a.GameId != expectedGameId) throw new InvalidDataException("Archive belongs to " + a.GameId);
            var roots = m.ContainsKey("roots") ? m["roots"] as object[] : null; var files = m.ContainsKey("files") ? m["files"] as object[] : null;
            if (roots == null || files == null || roots.Length > 64 || files.Length > MaxEntries) throw new InvalidDataException("Archive manifest is malformed");
            foreach (object o in roots)
            {
                var r = o as Dictionary<string, object>;
                string id = r == null ? null : r["id"] as string, source = r == null || !r.ContainsKey("source") ? null : r["source"] as string;
                if (string.IsNullOrEmpty(id) || id.Length > 1024 || a.Roots.Any(x => x.Id == id)) throw new InvalidDataException("Archive root is malformed");
                a.Roots.Add(new SaveArchiveRoot { Id = id, Source = source });
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); SaveArchiveFile previous = null;
            foreach (object o in files)
            {
                var f = o as Dictionary<string, object>;
                if (f == null) throw new InvalidDataException("Archive file is malformed");
                var file = new SaveArchiveFile { Root = Convert.ToInt32(f["root"], CultureInfo.InvariantCulture), Path = f["path"] as string, Size = Convert.ToInt64(f["size"], CultureInfo.InvariantCulture), Sha256 = f["sha256"] as string };
                if (file.Root < 0 || file.Root >= a.Roots.Count || !SaveUtil.SafeRelative(file.Path) || file.Size < 0 || file.Sha256 == null || !System.Text.RegularExpressions.Regex.IsMatch(file.Sha256, "^[0-9a-f]{64}$"))
                    throw new InvalidDataException("Unsafe or malformed archive file: " + file.Path);
                if (!seen.Add(file.Root + "/" + file.Path)) throw new InvalidDataException("Duplicate archive file: " + file.Path);
                if (previous != null && (previous.Root > file.Root || (previous.Root == file.Root && string.CompareOrdinal(previous.Path, file.Path) >= 0))) throw new InvalidDataException("Archive files are not sorted");
                a.Files.Add(file); previous = file;
            }
            if (a.ContentHash != ComputeContentHash(a.Roots, a.Files)) throw new InvalidDataException("Archive content hash does not match its files");
        }

        // Streams one file's verified bytes (hash checked for format 2) to a destination stream.
        internal void Extract(SaveArchiveFile file, Stream destination)
        {
            using (var ms = new MemoryStream(Bytes, false)) using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry(EntryName(file));
                if (entry == null) throw new InvalidDataException("Archive entry missing: " + file.Path);
                using (var sha = SHA256.Create()) using (var src = entry.Open())
                {
                    byte[] buffer = new byte[81920]; int n; long total = 0;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += n; if (total > file.Size) throw new InvalidDataException("Archive entry larger than declared");
                        sha.TransformBlock(buffer, 0, n, null, 0); destination.Write(buffer, 0, n);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    if (total != file.Size) throw new InvalidDataException("Archive entry size mismatch");
                    if (Version == 2 && SaveUtil.Hex(sha.Hash) != file.Sha256) throw new InvalidDataException("Archive entry hash mismatch: " + file.Path);
                }
            }
        }
    }

    // Deterministic ZIP writer: fixed 1980-01-01 times, sorted entries, manifest stored first.
    static class SaveZipWriter
    {
        static readonly uint[] Crc = MakeCrc();
        static uint[] MakeCrc()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
            return t;
        }
        internal static uint Crc32(byte[] data) { uint c = 0xFFFFFFFFu; foreach (byte b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFFu; }
        sealed class Entry { public string Name; public byte[] Data; public uint Crc; public long Size; public bool Stored; public long Offset; }
        internal static byte[] Write(IEnumerable<KeyValuePair<string, byte[]>> files, string storedFirst, byte[] storedData)
        {
            var entries = new List<Entry>();
            if (storedFirst != null) entries.Add(new Entry { Name = storedFirst, Data = storedData, Crc = Crc32(storedData), Size = storedData.Length, Stored = true });
            foreach (var f in files)
            {
                byte[] packed;
                using (var ms = new MemoryStream()) { using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(f.Value, 0, f.Value.Length); packed = ms.ToArray(); }
                bool store = packed.Length >= f.Value.Length;
                entries.Add(new Entry { Name = f.Key, Data = store ? f.Value : packed, Crc = Crc32(f.Value), Size = f.Value.Length, Stored = store });
            }
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                foreach (var e in entries)
                {
                    e.Offset = ms.Position; byte[] name = Encoding.UTF8.GetBytes(e.Name);
                    w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write((ushort)(e.Stored ? 0 : 8));
                    w.Write((ushort)0); w.Write((ushort)0x0021); w.Write(e.Crc); w.Write((uint)e.Data.Length); w.Write((uint)e.Size);
                    w.Write((ushort)name.Length); w.Write((ushort)0); w.Write(name); w.Write(e.Data);
                    if (ms.Length > SaveArchive.MaxArchive) throw new InvalidDataException("Save exceeds 90 MiB");
                }
                long cd = ms.Position;
                foreach (var e in entries)
                {
                    byte[] name = Encoding.UTF8.GetBytes(e.Name);
                    w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write((ushort)(e.Stored ? 0 : 8));
                    w.Write((ushort)0); w.Write((ushort)0x0021); w.Write(e.Crc); w.Write((uint)e.Data.Length); w.Write((uint)e.Size);
                    w.Write((ushort)name.Length); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write((uint)e.Offset); w.Write(name);
                }
                long end = ms.Position;
                w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)entries.Count); w.Write((ushort)entries.Count);
                w.Write((uint)(end - cd)); w.Write((uint)cd); w.Write((ushort)0);
                w.Flush();
                if (ms.Length > SaveArchive.MaxArchive) throw new InvalidDataException("Save exceeds 90 MiB");
                return ms.ToArray();
            }
        }
    }

    sealed class SaveChangingException : IOException { public SaveChangingException(string m) : base(m) { } }

    static class SaveSnapshot
    {
        internal static Action<int> Sleep = delegate(int ms) { Thread.Sleep(ms); };
        internal static Action<SaveFile, int> BeforeRead;   // test hook: called before each file read

        // Builds a format-2 archive from a fresh discovery. A file that changes size, time or
        // content between the hash pass, the write pass and the final check causes a retry.
        internal static SaveArchive Capture(Func<SaveDiscovery> discover, int attempts)
        {
            string last = null;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                var d = discover();
                if (d.Status != SaveStatus.Found) throw new InvalidOperationException("Nothing to snapshot (" + d.Status + ")");
                try { return Build(d, attempt); }
                catch (SaveChangingException ex) { last = ex.Message; }
                catch (FileNotFoundException ex) { last = "file disappeared: " + ex.FileName; }
                catch (DirectoryNotFoundException ex) { last = "folder disappeared: " + ex.Message; }
                if (attempt < attempts) Sleep(500 * attempt);
            }
            throw new SaveChangingException("Saves kept changing during the snapshot (" + last + "); will retry");
        }
        internal static SaveArchive FromDiscovery(SaveDiscovery d) { return Build(d, 1); }
        static SaveArchive Build(SaveDiscovery d, int attempt)
        {
            var a = new SaveArchive { Version = 2, GameId = d.GameId };
            var map = new Dictionary<int, int>();
            foreach (int r in d.Files.Select(f => f.Root).Distinct().OrderBy(r => r)) { map[r] = a.Roots.Count; a.Roots.Add(new SaveArchiveRoot { Id = d.Roots[r].Id, Source = d.Roots[r].Source }); }
            var contents = new List<KeyValuePair<string, byte[]>>(); long total = 0;
            var ordered = d.Files.OrderBy(f => map[f.Root]).ThenBy(f => f.Relative, StringComparer.Ordinal).ToList();
            // Pass 1: read and hash each file, checking its metadata around the read.
            var datas = new List<byte[]>();
            foreach (var f in ordered)
            {
                if (BeforeRead != null) BeforeRead(f, attempt);
                byte[] data = ReadStable(f);
                total += data.Length; if (total > SaveArchive.MaxExpanded) throw new InvalidDataException("Expanded save too large");
                datas.Add(data);
                a.Files.Add(new SaveArchiveFile { Root = map[f.Root], Path = f.Relative, Size = data.Length, Sha256 = SaveUtil.Sha256(data) });
            }
            // Case-insensitive duplicates cannot be restored on Windows.
            if (a.Files.GroupBy(f => f.Root + "/" + f.Path, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) throw new InvalidDataException("Save contains names that differ only by case");
            a.ContentHash = SaveArchive.ComputeContentHash(a.Roots, a.Files);
            for (int i = 0; i < ordered.Count; i++) contents.Add(new KeyValuePair<string, byte[]>("roots/" + a.Files[i].Root.ToString(CultureInfo.InvariantCulture) + "/" + a.Files[i].Path, datas[i]));
            byte[] manifest = new UTF8Encoding(false).GetBytes(a.ManifestJson());
            a.Bytes = SaveZipWriter.Write(contents, SaveArchive.ManifestName, manifest);
            // Pass 2: nothing changed since it was read.
            for (int i = 0; i < ordered.Count; i++)
            {
                var info = new FileInfo(ordered[i].FullPath);
                if (!info.Exists) throw new FileNotFoundException("Save disappeared", ordered[i].FullPath);
                if (info.Length != a.Files[i].Size || info.LastWriteTimeUtc.Ticks != ordered[i].Ticks) throw new SaveChangingException(ordered[i].FullPath + " changed");
            }
            return a;
        }
        static byte[] ReadStable(SaveFile f)
        {
            var before = new FileInfo(f.FullPath);
            if (!before.Exists) throw new FileNotFoundException("Save disappeared", f.FullPath);
            if (before.Length != f.Size || before.LastWriteTimeUtc.Ticks != f.Ticks) throw new SaveChangingException(f.FullPath + " changed since the scan");
            byte[] data;
            using (var s = new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (s.Length > 90L * 1024 * 1024) throw new InvalidDataException("Save file too large");
                data = new byte[s.Length]; int read = 0, n;
                while (read < data.Length && (n = s.Read(data, read, data.Length - read)) > 0) read += n;
                if (read != data.Length || s.ReadByte() != -1) throw new SaveChangingException(f.FullPath + " changed while reading");
            }
            var after = new FileInfo(f.FullPath);
            if (!after.Exists || after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc) throw new SaveChangingException(f.FullPath + " changed while reading");
            return data;
        }
    }

    // ---------- Restore ----------
    sealed class SaveRestoreItem { public string Root, RootPath, Relative, Destination, Action; public long Size; public int FileIndex; }
    sealed class SaveRestorePlan
    {
        public string GameId, AppId, BackupPath, Reason;
        public int ArchiveVersion;
        public List<SaveRestoreItem> Items = new List<SaveRestoreItem>();
        public List<SaveRestoreItem> Deletions = new List<SaveRestoreItem>();
        public List<string> Unmapped = new List<string>(), Errors = new List<string>();
        public bool CanApply { get { return Errors.Count == 0 && Unmapped.Count == 0 && Items.Count > 0; } }
        public string ToText()
        {
            var b = new StringBuilder();
            b.Append("Restore plan for ").Append(GameId).Append(" (archive format ").Append(ArchiveVersion).Append(")\n");
            b.Append("Local backup first: ").Append(BackupPath ?? "(none needed: no local files)").Append('\n');
            foreach (var i in Items) b.Append("  ").Append(i.Action).Append(' ').Append(i.Destination).Append(" (").Append(i.Size).Append(" bytes)\n");
            foreach (var i in Deletions) b.Append("  delete ").Append(i.Destination).Append(" (kept in the backup)\n");
            foreach (string u in Unmapped) b.Append("  UNMAPPED ").Append(u).Append('\n');
            foreach (string e in Errors) b.Append("  ERROR ").Append(e).Append('\n');
            b.Append(CanApply ? "Can apply.\n" : "Cannot apply.\n");
            return b.ToString();
        }
    }

    static class SaveRestore
    {
        internal const int KeepBackups = 10;
        internal static string BackupDir(string dataDir, string appid) { return Path.Combine(dataDir, "backup", "saves", appid); }

        // Dry run: maps every archive file to a local destination via the local discovery.
        internal static SaveRestorePlan Plan(SaveArchive archive, SaveDiscovery local, string dataDir)
        {
            var plan = new SaveRestorePlan { GameId = local.GameId, AppId = local.AppId, ArchiveVersion = archive.Version };
            if (archive.IsEmpty) { plan.Errors.Add("The cloud archive holds no save files"); return plan; }
            if (archive.GameId != null && archive.GameId != local.GameId) { plan.Errors.Add("Archive belongs to " + archive.GameId); return plan; }
            var mapped = new Dictionary<int, SaveRoot>();
            for (int i = 0; i < archive.Roots.Count; i++)
            {
                if (!archive.Files.Any(f => f.Root == i)) continue;
                var root = local.Roots.FirstOrDefault(r => r.Id == archive.Roots[i].Id);
                if (root == null) { plan.Unmapped.Add(archive.Roots[i].Id + ": no matching save location on this PC"); continue; }
                if (root.State == SaveStatus.Ambiguous) { plan.Unmapped.Add(root.Id + ": ambiguous (" + root.Note + ")"); continue; }
                if (!root.Restorable) { plan.Unmapped.Add(root.Id + ": " + root.State + (root.Note == null ? "" : " (" + root.Note + ")")); continue; }
                try { if (root.Base != null && !Directory.Exists(root.Base)) { plan.Unmapped.Add(root.Id + ": base folder not present: " + root.Base); continue; } }
                catch (Exception ex) { plan.Unmapped.Add(root.Id + ": " + ex.Message); continue; }
                mapped[i] = root;
            }
            var targets = new HashSet<string>(SaveUtil.PathComparer);
            for (int index = 0; index < archive.Files.Count; index++)
            {
                var f = archive.Files[index]; SaveRoot root;
                if (!mapped.TryGetValue(f.Root, out root)) continue;
                if (!SaveUtil.SafeRelative(f.Path)) { plan.Errors.Add("Unsafe path: " + f.Path); continue; }
                if (archive.Version == 2 && !root.Covers(f.Path)) { plan.Errors.Add("Path outside its save location: " + f.Path); continue; }
                string dest = Path.GetFullPath(Path.Combine(root.Path, f.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!SaveUtil.Within(dest, root.Path) || SaveUtil.PathComparer.Equals(SaveUtil.Full(dest), SaveUtil.Full(root.Path))) { plan.Errors.Add("Destination escapes its save location: " + f.Path); continue; }
                if (!targets.Add(dest)) { plan.Errors.Add("Two files map to " + dest); continue; }
                string action = File.Exists(dest) ? "replace" : "create";
                plan.Items.Add(new SaveRestoreItem { Root = root.Id, RootPath = root.Path, Relative = f.Path, Destination = dest, Action = action, Size = f.Size, FileIndex = index });
            }
            // Local files of the restored roots that the archive does not contain.
            foreach (var lf in local.Files)
            {
                var root = local.Roots[lf.Root];
                if (!mapped.Values.Contains(root) || targets.Contains(lf.FullPath)) continue;
                plan.Deletions.Add(new SaveRestoreItem { Root = root.Id, RootPath = root.Path, Relative = lf.Relative, Destination = lf.FullPath, Action = "delete", Size = lf.Size });
            }
            if (local.Files.Count > 0) plan.BackupPath = Path.Combine(BackupDir(dataDir, local.AppId), DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff", CultureInfo.InvariantCulture) + ".zip");
            return plan;
        }

        // Refuses links on the way to a destination: the anchor, every existing folder below
        // it and an existing destination file must be ordinary.
        static void CheckChain(SaveRestoreItem item)
        {
            if (Directory.Exists(item.RootPath) && SaveUtil.IsLink(item.RootPath)) throw new IOException("Save location is a link: " + item.RootPath);
            string current = item.RootPath;
            var parts = item.Relative.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                if ((Directory.Exists(current) || File.Exists(current)) && SaveUtil.IsLink(current)) throw new IOException("Destination path contains a link: " + current);
                if (i < parts.Length - 1 && File.Exists(current)) throw new IOException("A file is in the way of " + item.Destination);
            }
        }

        // Applies a plan. backupSource is the local discovery used to build the plan.
        internal static void Apply(SaveRestorePlan plan, SaveArchive archive, SaveDiscovery backupSource, Func<bool> blocked, Action<string> log)
        {
            if (!plan.CanApply) throw new InvalidOperationException("Restore plan cannot be applied: " + string.Join("; ", plan.Errors.Concat(plan.Unmapped).ToArray()));
            if (archive.IsEmpty) throw new InvalidOperationException("Refusing to restore an empty archive");
            foreach (var item in plan.Items.Concat(plan.Deletions)) CheckChain(item);
            // 1. Recoverable backup of every current file in the restored roots.
            SaveArchive backup = null;
            if (plan.BackupPath != null)
            {
                var roots = new HashSet<string>(plan.Items.Select(i => i.Root));
                var subset = new SaveDiscovery { GameId = backupSource.GameId, AppId = backupSource.AppId, Status = SaveStatus.Found, Roots = backupSource.Roots };
                subset.Files = backupSource.Files.Where(f => roots.Contains(backupSource.Roots[f.Root].Id)).ToList();
                if (subset.Files.Count > 0)
                {
                    backup = SaveSnapshot.FromDiscovery(subset);
                    SaveUtil.AtomicWrite(plan.BackupPath, backup.Bytes);
                    if (log != null) log("save backup written: " + plan.BackupPath);
                }
                Prune(Path.GetDirectoryName(plan.BackupPath), KeepBackups);
            }
            // 2. Stage next to each destination, verifying size and hash.
            var staged = new List<KeyValuePair<SaveRestoreItem, string>>();
            var created = new List<string>();
            try
            {
                foreach (var item in plan.Items)
                {
                    string dir = Path.GetDirectoryName(item.Destination);
                    if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); created.Add(dir); }
                    CheckChain(item);
                    string temp = item.Destination + ".cabinet-new-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write)) { archive.Extract(archive.Files[item.FileIndex], s); s.Flush(true); }
                    staged.Add(new KeyValuePair<SaveRestoreItem, string>(item, temp));
                }
                if (blocked != null && blocked()) throw new IOException("Game started; restore deferred");
            }
            catch
            {
                foreach (var s in staged) TryDelete(s.Value);
                throw;
            }
            // 3. Commit file by file; roll back from the backup on failure.
            var committed = new List<SaveRestoreItem>();
            try
            {
                foreach (var s in staged)
                {
                    CheckChain(s.Key);
                    if (File.Exists(s.Key.Destination)) File.Replace(s.Value, s.Key.Destination, null); else File.Move(s.Value, s.Key.Destination);
                    committed.Add(s.Key);
                }
                foreach (var del in plan.Deletions) { CheckChain(del); if (File.Exists(del.Destination)) File.Delete(del.Destination); }
            }
            catch (Exception ex)
            {
                if (log != null) log("restore failed, rolling back: " + ex.Message);
                foreach (var s in staged) TryDelete(s.Value);
                foreach (var c in committed) if (c.Action == "create") TryDelete(c.Destination);
                if (backup != null) Recover(backup, backupSource, log);
                throw;
            }
        }
        static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { } }

        // Writes a backup archive's files back to their original locations (same machine).
        internal static void Recover(SaveArchive backup, SaveDiscovery local, Action<string> log)
        {
            foreach (var f in backup.Files)
            {
                try
                {
                    var root = local.Roots.FirstOrDefault(r => r.Id == backup.Roots[f.Root].Id);
                    if (root == null || root.Path == null) continue;
                    string dest = Path.GetFullPath(Path.Combine(root.Path, f.Path.Replace('/', Path.DirectorySeparatorChar)));
                    if (!SaveUtil.Within(dest, root.Path)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    string temp = dest + ".cabinet-recover-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    try
                    {
                        using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write)) backup.Extract(f, s);
                        if (File.Exists(dest)) File.Replace(temp, dest, null); else File.Move(temp, dest);
                    }
                    finally { TryDelete(temp); }
                }
                catch (Exception ex) { if (log != null) log("recover " + f.Path + ": " + ex.Message); }
            }
        }
        internal static void Prune(string dir, int keep, string pattern = "*.zip")
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (string old in Directory.GetFiles(dir, pattern).OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal).Skip(keep)) File.Delete(old);
            }
            catch (Exception) { }
        }
    }
}
