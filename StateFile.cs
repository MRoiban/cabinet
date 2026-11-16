// Durable engine state: flushed atomic writes, and loads that survive a file damaged by a crash
// or power loss. Shared with LuaTools (copied into Services/Cabinet/Engine/StateFile.cs): keep the
// two copies equivalent, C# 5 and BCL-only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Cabinet
{
    enum StateLoad { Missing, Loaded, Recovered, Damaged }
    // What Load does with a damaged file when no intact copy is found:
    // Replace (default): the file stays where it is until the next Write of that path, which swaps the fresh
    //   state in and keeps the damaged copy as "<name>.corrupt-<UTC>" in the same File.Replace. The path is
    //   never empty, so a crash before that write finds the damage again, not a missing (new-looking) file.
    // MoveAside: renamed "<name>.corrupt-<UTC>" at once, for files whose absence means the same as fresh.
    // Keep: left untouched for a caller that must not lose it; a missing file with only junk leftovers is
    //   then simply Missing, and the junk is removed.
    enum OnDamage { Replace, MoveAside, Keep }

    static class StateFile
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        // Full paths of damaged files (OnDamage.Replace) whose next Write keeps them as .corrupt backups.
        static readonly HashSet<string> Damaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Hosts may also forward recoveries and quarantines elsewhere (LuaTools: its event log).
#pragma warning disable 0649
        internal static Action<string> Warning;
#pragma warning restore 0649

        // Writes a unique temp file next to the target, flushes it to disk, then swaps it in with
        // File.Replace (or File.Move for a new file). Without the flush, a power loss can leave the
        // replaced file full of zero bytes. The temp file never outlives the call.
        internal static void Write(string path, byte[] bytes) { Write(path, bytes, true); }
        internal static void Write(string path, string text) { Write(path, Utf8.GetBytes(text), true); }
        // durable false: still atomic, without the flush, for throwaway files rewritten every tick.
        internal static void Write(string path, string text, bool durable) { Write(path, Utf8.GetBytes(text), durable); }
        internal static void Write(string path, byte[] bytes, bool durable)
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteFlushed(temp, bytes, durable);
                bool damaged; lock (Damaged) damaged = Damaged.Contains(full);
                if (damaged && File.Exists(path)) File.Replace(temp, path, QuarantineName(path));
                else Commit(temp, path);
                if (damaged) lock (Damaged) Damaged.Remove(full);
            }
            finally { TryDelete(temp); }
        }
        // For callers that stage a fixed temp name and check conditions before Commit.
        internal static void WriteFlushed(string path, byte[] bytes) { WriteFlushed(path, bytes, true); }
        static void WriteFlushed(string path, byte[] bytes, bool flush)
        {
            using (var s = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            { s.Write(bytes, 0, bytes.Length); if (flush) s.Flush(true); }
        }
        // Same bytes as File.WriteAllText(path, text, encoding), preamble included.
        internal static void WriteFlushed(string path, string text, Encoding encoding)
        { WriteFlushed(path, encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray()); }
        internal static void Commit(string temp, string path)
        { if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }

        // Loads a state file. parse returns null, or throws, when the text is unusable (zero bytes,
        // truncated or wrong-shaped). In order:
        // 1. the file itself;
        // 2. else the newest parseable leftover of an interrupted write beside it (File.Replace's
        //    "<name>~RF*.TMP" backup, Write's "<name>.<guid>.tmp"), which is written back durably;
        // 3. else null with Damaged, the file handled as onDamage says.
        // Missing (null) means neither the file nor any leftover exists (OnDamage.Replace: nor a
        // ".corrupt-*" copy, which means a damaged file was set aside and no fresh state replaced it).
        // Read errors, of the file or of any leftover (locked by antivirus, access denied), are not damage:
        // they are thrown and nothing is moved. Messages name files only, never their contents.
        internal static T Load<T>(string path, Func<string, T> parse, Action<string> log, out StateLoad result) where T : class
        { return Load(path, parse, log, out result, OnDamage.Replace); }
        internal static T Load<T>(string path, Func<string, T> parse, Action<string> log, out StateLoad result, OnDamage onDamage) where T : class
        {
            string name = Path.GetFileName(path), full = Path.GetFullPath(path);
            bool exists = File.Exists(path);
            if (exists)
            {
                T value = TryParse(File.ReadAllBytes(path), parse);
                if (value != null) { Sweep(path); result = StateLoad.Loaded; return value; }
            }
            var leftovers = Leftovers(path); var copies = new List<KeyValuePair<FileInfo, byte[]>>();
            foreach (var leftover in leftovers)
            {
                // An intact copy that cannot be read now must not be passed over: the damaged file would be
                // replaced by fresh state and that copy swept later.
                try { copies.Add(new KeyValuePair<FileInfo, byte[]>(leftover, File.ReadAllBytes(leftover.FullName))); }
                catch (FileNotFoundException) { }
                catch (UnauthorizedAccessException ex) { throw new IOException(leftover.Name + " cannot be read", ex); }
            }
            foreach (var copy in copies)
            {
                T value = TryParse(copy.Value, parse);
                if (value == null) continue;
                Write(path, copy.Value);
                Sweep(path);
                Warn(log, name + (exists ? " was damaged" : " was missing after an interrupted write") + "; recovered it from " + copy.Key.Name);
                result = StateLoad.Recovered; return value;
            }
            if (!exists)
            {
                if (onDamage == OnDamage.Keep) { foreach (var copy in copies) TryDelete(copy.Key.FullName); result = StateLoad.Missing; return null; }
                bool setAside = onDamage == OnDamage.Replace && Directory.GetFiles(Path.GetDirectoryName(full), name + ".corrupt-*").Length > 0;
                if (copies.Count == 0 && !setAside) { result = StateLoad.Missing; return null; }
            }
            if (onDamage == OnDamage.Keep) Warn(log, name + " was damaged and no intact copy was found; left it in place");
            else if (onDamage == OnDamage.MoveAside)
            {
                string moved = exists ? Quarantine(path) : null;
                Warn(log, name + " was damaged and no intact copy was found; " + (exists ? (moved != null ? "moved it to " + Path.GetFileName(moved) : "it could not be moved aside") + " and " : "") + "started fresh");
            }
            else
            {
                if (exists) lock (Damaged) Damaged.Add(full);
                Warn(log, name + (exists ? " was damaged and no intact copy was found; starting fresh, and it is kept as " + name + ".corrupt-<UTC time> once fresh state replaces it"
                    : " is missing after a damaged copy was set aside; starting fresh"));
            }
            result = StateLoad.Damaged; return null;
        }

        static T TryParse<T>(byte[] bytes, Func<string, T> parse) where T : class
        {
            try
            {
                string text;
                using (var reader = new StreamReader(new MemoryStream(bytes), Utf8, true)) text = reader.ReadToEnd();
                // A crash can zero all or part of a file; NUL never appears in engine state.
                return text.IndexOf('\0') >= 0 ? null : parse(text);
            }
            catch (Exception) { return null; }
        }

        // Leftovers of interrupted writes of this file, newest first.
        internal static List<FileInfo> Leftovers(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)), name = Path.GetFileName(path);
            var result = new List<FileInfo>();
            if (!Directory.Exists(dir)) return result;
            var pattern = new Regex("^" + Regex.Escape(name) + @"(~RF[0-9A-Za-z]+\.TMP|\.[0-9a-f]{32}\.tmp)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (string file in Directory.GetFiles(dir, name + "*"))
                if (pattern.IsMatch(Path.GetFileName(file))) result.Add(new FileInfo(file));
            return result.OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        }

        // Once the file itself is good, older leftovers are stale: a later recovery must never pick
        // one. Recent ones are kept in case another writer is between its temp write and replace.
        static void Sweep(string path)
        {
            try
            {
                foreach (var leftover in Leftovers(path))
                    if (DateTime.UtcNow - leftover.LastWriteTimeUtc > TimeSpan.FromMinutes(1)) TryDelete(leftover.FullName);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static string QuarantineName(string path)
        {
            string stamp = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture), target = stamp;
            for (int i = 2; File.Exists(target); i++) target = stamp + "-" + i;
            return target;
        }
        static string Quarantine(string path)
        {
            string target = QuarantineName(path);
            try { File.Move(path, target); return target; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        static void Warn(Action<string> log, string message)
        {
            if (log != null) log(message);
            var warning = Warning;
            if (warning != null) try { warning(message); } catch (Exception) { }
        }

        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
