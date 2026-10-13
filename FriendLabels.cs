using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Cabinet
{
    static class FriendLabels
    {
        static volatile bool busy;
        static DateTime next;
        static string desired;
        static uint published;
        static string publishedKey;
        static Func<string, string, object, object> publishedRequest;
        static bool installed;
        static readonly HashSet<string> logged = new HashSet<string>();
        static void Once(Action<string> log, string kind, Exception ex)
        { if (logged.Add(kind)) log(kind + ": " + ex.Message); }
        internal static string CleanName(string name)
        {
            if (name == null) return "";
            var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (clean.Length > 100) clean = clean.Substring(0, char.IsHighSurrogate(clean[99]) ? 99 : 100);
            return clean;
        }
        static string DebugFlag() { return Path.Combine(Syncer.SteamPath(), ".cef-enable-remote-debugging"); }
        // Steam only checks that the name exists; other tools create it as a folder or link.
        internal static bool DebugEnabled()
        {
            try { File.GetAttributes(DebugFlag()); return true; }
            catch { return false; }
        }
        internal static ToolStripMenuItem MenuItem()
        {
            var item = new ToolStripMenuItem(L.T("Show friends' real games (applies after Steam restarts)", "Afficher les vrais jeux des amis (après le redémarrage de Steam)"));
            item.Click += delegate
            {
                string flag = DebugFlag();
                try { using (File.Open(flag, FileMode.OpenOrCreate, FileAccess.Write)) { } }
                catch (UnauthorizedAccessException)
                {
                    // Steam under Program Files is often admin-only: ask Windows for elevation once.
                    try
                    {
                        var psi = new ProcessStartInfo("cmd.exe", "/c type nul > \"" + flag + "\"");
                        psi.Verb = "runas"; psi.UseShellExecute = true; psi.WindowStyle = ProcessWindowStyle.Hidden;
                        using (var p = Process.Start(psi)) p.WaitForExit(15000);
                    }
                    catch (Win32Exception) { } // UAC declined
                }
                catch (IOException) { }
                if (DebugEnabled())
                    MessageBox.Show(L.T("Friends' real games will appear after you restart Steam.", "Les vrais jeux des amis apparaîtront après le redémarrage de Steam."), "Cabinet");
                else
                    MessageBox.Show(L.T("Cannot write to the Steam folder. Create .cef-enable-remote-debugging there, then restart Steam.", "Impossible d'écrire dans le dossier Steam. Créez-y .cef-enable-remote-debugging, puis redémarrez Steam."), "Cabinet");
            };
            return item;
        }
        internal static void Tick(CloudClient client, ICollection<string> running, Action<string> log)
        {
            bool connected = client.Connected;
            string app = connected && running != null ? running.OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault() : null;
            // A changed session runs immediately; periodic heartbeats survive Steam restarts.
            string key = connected ? client.PresenceKey : null;
            string state = connected ? key + ":" + Syncer.AccountId() + ":" + app : null;
            if (busy || (state == desired && DateTime.UtcNow < next)) return;
            desired = state; next = DateTime.UtcNow.AddSeconds(60); busy = true;
            uint own; uint.TryParse(connected ? Syncer.AccountId() : null, out own);
            var request = connected ? client.PresenceRequest() : null;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (published != 0 && (!connected || published != own || publishedKey != key || app == null))
                    {
                        try { publishedRequest("DELETE", "/api/presence?accountId=" + published.ToString(CultureInfo.InvariantCulture), null); }
                        catch (Exception ex) { Once(log, "presence delete failed", ex); }
                        published = 0; publishedRequest = null;
                    }
                    if (connected && own != 0 && app != null)
                    {
                        try
                        {
                            string name = CleanName(App.GameName(app));
                            if (name.Length > 0 && Regex.IsMatch(app, @"^[0-9]{1,10}\z"))
                            {
                                request("PUT", "/api/presence", new { accountId = own, appId = app, name = name });
                                published = own; publishedRequest = request; publishedKey = key;
                            }
                        }
                        catch (Exception ex) { Once(log, "presence publish failed", ex); }
                    }
                    if (!Playtime.SteamRunning()) { installed = false; return; }
                    var labels = new Dictionary<string, string>();
                    if (connected)
                    {
                        try { labels = ReadPlayers(request("GET", "/api/presence", null), own); }
                        catch (Exception ex) { Once(log, "presence fetch failed", ex); }
                    }
                    if (connected || installed)
                    {
                        try { Apply(labels); installed = connected; }
                        catch (Exception ex) { Once(log, "Steam friends debugger unavailable", ex); }
                    }
                }
                catch (Exception ex) { Once(log, "presence failed", ex); }
                finally { busy = false; }
            });
        }
        internal static Dictionary<string, string> ReadPlayers(object response, uint own)
        {
            var result = new Dictionary<string, string>(); object players;
            if (!CloudClient.Map(response).TryGetValue("players", out players)) return result;
            var list = players as IEnumerable; if (list == null) return result;
            foreach (object entry in list)
            {
                var row = CloudClient.Map(entry); object account, name, updated;
                uint id; long time;
                if (!row.TryGetValue("accountId", out account) || !uint.TryParse(Convert.ToString(account, CultureInfo.InvariantCulture), out id) || id == 0 || id == own) continue;
                if (!row.TryGetValue("updatedAt", out updated) || !long.TryParse(Convert.ToString(updated, CultureInfo.InvariantCulture), out time) || time < CloudClient.Now() - 180000) continue;
                if (!row.TryGetValue("name", out name)) continue;
                string label = CleanName(name as string); if (label.Length != 0) result[id.ToString(CultureInfo.InvariantCulture)] = label;
            }
            return result;
        }
        internal static string BuildScript(Dictionary<string, string> labels)
        {
            // Only serialized data enters the script. App 480 and its join metadata stay intact.
            return @"(function(labels){
var store=window.g_FriendsUIApp&&g_FriendsUIApp.FriendStore;
if(!store)throw new Error('Cabinet: friends store unavailable');
var s=window.__cabinetLabels;
if(!s){
 s=window.__cabinetLabels={labels:{},written:new Map(),patches:new Map(),expires:0};
 s.clear=function(p){var v=s.written.get(p);if(v){if(p.m_strGameExtraInfo===v.value)p.m_strGameExtraInfo=v.before;s.written.delete(p);}};
 s.one=function(p,id){var name=s.labels[String(id)];if(p.m_unGamePlayedAppID===480&&name){var v=s.written.get(p);if(!v||p.m_strGameExtraInfo!==v.value){s.written.set(p,{before:p.m_strGameExtraInfo,value:name});}else{v.value=name;}p.m_strGameExtraInfo=name;}else{s.clear(p);}};
 s.apply=function(){
  if(Date.now()>s.expires)s.labels={};
  var current=window.g_FriendsUIApp&&g_FriendsUIApp.FriendStore;if(!current)return;
  var friends=Array.from(current.all_friends),ids=new Map();
  friends.forEach(function(f){if(f.persona)ids.set(f.persona,f.accountid);});
  s.written.forEach(function(v,p){if(!ids.has(p))s.clear(p);});
  friends.forEach(function(f){var p=f.persona;if(!p)return;
   var proto=Object.getPrototypeOf(p);
   if(proto&&typeof proto.UpdateFromMessage==='function'&&!s.patches.has(proto)){
    var original=proto.UpdateFromMessage;
    var wrapped=function(){s.clear(this);var r=original.apply(this,arguments);s.apply();return r;};
    s.patches.set(proto,{original:original,wrapped:wrapped});proto.UpdateFromMessage=wrapped;
   }
   s.one(p,f.accountid);
  });
 };
 s.timer=setInterval(s.apply,5000);
}
s.labels=labels;s.expires=Date.now()+180000;s.apply();return true;
})(" + new JavaScriptSerializer().Serialize(labels) + ")";
        }
        internal static Uri SelectTarget(string json)
        {
            object parsed; try { parsed = new JavaScriptSerializer().DeserializeObject(json); } catch { return null; }
            var targets = parsed as object[]; if (targets == null) return null;
            foreach (object entry in targets)
            {
                var row = CloudClient.Map(entry); object title, socket; Uri uri;
                if (row.TryGetValue("title", out title) && (title as string) == "SharedJSContext" && row.TryGetValue("webSocketDebuggerUrl", out socket)
                    && Uri.TryCreate(socket as string, UriKind.Absolute, out uri) && uri.Scheme == "ws" && uri.IsLoopback && uri.Port == 8080 && uri.UserInfo == "") return uri;
            }
            return null;
        }
        internal static void Apply(Dictionary<string, string> labels)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8080/json");
            req.Timeout = 3000; req.ReadWriteTimeout = 3000; req.Proxy = null; req.AllowAutoRedirect = false;
            string json;
            using (var response = req.GetResponse()) using (var stream = response.GetResponseStream()) using (var reader = new StreamReader(stream))
            { char[] buffer = new char[1024 * 1024 + 1]; int length = reader.ReadBlock(buffer, 0, buffer.Length); if (length == buffer.Length) throw new IOException("Debugger target list too large"); json = new string(buffer, 0, length); }
            var target = SelectTarget(json); if (target == null) throw new IOException("SharedJSContext target missing");
            Evaluate(target, BuildScript(labels));
        }
        internal static object Evaluate(Uri target, string script)
        {
            using (var timeout = new CancellationTokenSource(5000)) using (var ws = new ClientWebSocket())
            {
                ws.Options.Proxy = null;
                ws.ConnectAsync(target, timeout.Token).GetAwaiter().GetResult();
                byte[] command = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new { id = 1, method = "Runtime.evaluate", @params = new { expression = script, returnByValue = true } }));
                ws.SendAsync(new ArraySegment<byte>(command), WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult();
                byte[] buffer = new byte[8192];
                for (int messages = 0; messages < 32; messages++)
                {
                    using (var body = new MemoryStream())
                    {
                        WebSocketReceiveResult part;
                        do
                        {
                            part = ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).GetAwaiter().GetResult();
                            if (part.MessageType == WebSocketMessageType.Close) throw new IOException("Debugger socket closed");
                            body.Write(buffer, 0, part.Count); if (body.Length > 1024 * 1024) throw new IOException("Debugger reply too large");
                        } while (!part.EndOfMessage);
                        var reply = CloudClient.Map(new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(body.ToArray())));
                        object id; if (!reply.TryGetValue("id", out id) || Convert.ToInt32(id) != 1) continue;
                        if (reply.ContainsKey("error")) throw new IOException("Debugger rejected evaluation");
                        var result = CloudClient.Map(reply["result"]); if (result.ContainsKey("exceptionDetails")) throw new IOException("Steam friends script failed");
                        return CloudClient.Map(result["result"]);
                    }
                }
                throw new IOException("Debugger did not answer evaluation");
            }
        }
    }
}
