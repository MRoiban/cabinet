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

    static class StateFile
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        // Hosts may also forward recoveries and quarantines elsewhere (LuaTools: its event log).
        internal static Action<string> Warning;

        // Writes a unique temp file next to the target, flushes it to disk, then swaps it in with
        // File.Replace (or File.Move for a new file). Without the flush, a power loss can leave the
        // replaced file full of zero bytes. The temp file never outlives the call.
        internal static void Write(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { WriteFlushed(temp, bytes); Commit(temp, path); }
            finally { TryDelete(temp); }
        }
        internal static void Write(string path, string text) { Write(path, Utf8.GetBytes(text)); }
        // For callers that stage a fixed temp name and check conditions before Commit.
        internal static void WriteFlushed(string path, byte[] bytes)
        {
            using (var s = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            { s.Write(bytes, 0, bytes.Length); s.Flush(true); }
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
        // 3. else the damaged file is renamed "<name>.corrupt-<UTC time>" (or, without quarantine, left in
        //    place for a caller that must not lose it) and null is returned (Damaged).
        // Missing (null) means neither the file nor any leftover exists. Read errors such as a locked
        // file are not damage and are rethrown. Messages name files only, never their contents.
        internal static T Load<T>(string path, Func<string, T> parse, Action<string> log, out StateLoad result, bool quarantine = true) where T : class
        {
            string name = Path.GetFileName(path);
            bool exists = File.Exists(path);
            if (exists)
            {
                T value = TryParse(File.ReadAllBytes(path), parse);
                if (value != null) { Sweep(path); result = StateLoad.Loaded; return value; }
            }
            var leftovers = Leftovers(path);
            foreach (var leftover in leftovers)
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(leftover.FullName); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                T value = TryParse(bytes, parse);
                if (value == null) continue;
                Write(path, bytes);
                Sweep(path);
                Warn(log, name + (exists ? " was damaged" : " was missing after an interrupted write") + "; recovered it from " + leftover.Name);
                result = StateLoad.Recovered; return value;
            }
            if (!exists && leftovers.Count == 0) { result = StateLoad.Missing; return null; }
            if (!quarantine) { Warn(log, name + " was damaged and no intact copy was found; left it in place"); result = StateLoad.Damaged; return null; }
            string moved = exists ? Quarantine(path) : null;
            Warn(log, name + " was damaged and no intact copy was found; " + (exists ? (moved != null ? "moved it to " + Path.GetFileName(moved) : "it could not be moved aside") + " and " : "") + "started fresh");
            result = StateLoad.Damaged; return null;
        }

        static T TryParse<T>(byte[] bytes, Func<string, T> parse) where T : class
        {
            if (bytes.Length == 0) return null;
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

        static string Quarantine(string path)
        {
            string stamp = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture), target = stamp;
            for (int i = 2; File.Exists(target); i++) target = stamp + "-" + i;
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
