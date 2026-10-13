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
                    var labels = new Dictionary<string, Label>();
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
        internal static Dictionary<string, Label> ReadPlayers(object response, uint own)
        {
            var result = new Dictionary<string, Label>(); object players;
            if (!CloudClient.Map(response).TryGetValue("players", out players)) return result;
            var list = players as IEnumerable; if (list == null) return result;
            foreach (object entry in list)
            {
                var row = CloudClient.Map(entry); object account, name, updated, app;
                uint id; long time;
                if (!row.TryGetValue("accountId", out account) || !uint.TryParse(Convert.ToString(account, CultureInfo.InvariantCulture), out id) || id == 0 || id == own) continue;
                if (!row.TryGetValue("updatedAt", out updated) || !long.TryParse(Convert.ToString(updated, CultureInfo.InvariantCulture), out time) || time < CloudClient.Now() - 180000) continue;
                if (!row.TryGetValue("name", out name) || !row.TryGetValue("appId", out app)) continue;
                string label = CleanName(name as string), appId = app as string;
                if (label.Length != 0 && appId != null && Regex.IsMatch(appId, @"^[1-9][0-9]{0,9}\z"))
                    result[id.ToString(CultureInfo.InvariantCulture)] = new Label { name = label, app = appId };
            }
            return result;
        }
        internal sealed class Label { public string name { get; set; } public string app { get; set; } }
        internal static string BuildScript(Dictionary<string, Label> labels)
        {
            // Only serialized data enters the script. App 480, the lobby and the connect
            // string stay intact so Join Game still works. The game id is what Launch Game
            // runs, so it points at the real game; the icon comes from the real game too.
            // Avatar badges read app 480's icon directly, not the friend's: Spacewar's
            // icon (broken anyway) becomes the shared real game's, else Cabinet's own.
            return @"(function(labels,fallback){
var store=window.g_FriendsUIApp&&g_FriendsUIApp.FriendStore;
if(!store)throw new Error('Cabinet: friends store unavailable');
var s=window.__cabinetLabels;
if(s&&s.version!==3){
 s.labels={};s.written.forEach(function(v,p){s.clear(p);});clearInterval(s.timer);
 s.patches.forEach(function(v,p){if(v.wrapped){if(p.UpdateFromMessage===v.wrapped)p.UpdateFromMessage=v.original;}
  else Object.keys(v).forEach(function(n){if(p[n]===v[n].wrapped)p[n]=v[n].original;});});
 if(s.spacewarInfo)delete s.spacewarInfo.icon_url;
 s=null;
}
if(!s){
 s=window.__cabinetLabels={version:3,labels:{},written:new Map(),patches:new Map(),expires:0,fallback:''};
 s.spacewarIcon=function(){var apps=window.g_FriendsUIApp&&g_FriendsUIApp.AppInfoStore,current=window.g_FriendsUIApp&&g_FriendsUIApp.FriendStore;
  if(!apps||!current)return null;
  var games=new Set(),unknown=false;
  Array.from(current.all_friends).forEach(function(f){var p=f.persona;if(!p||p.m_unGamePlayedAppID!==480)return;
   var v=s.written.get(p);if(v&&p.m_gameid===v.app)games.add(v.app);else unknown=true;});
  if(games.size===1&&!unknown){var info=apps.GetAppInfo(Number(games.values().next().value));if(info&&info.m_unAppID!==480&&info.icon_url)return info.icon_url;}
  return s.fallback||null;};
 s.patchSpacewar=function(){var apps=window.g_FriendsUIApp&&g_FriendsUIApp.AppInfoStore;if(!apps||s.spacewarInfo)return;
  var a=apps.GetAppInfo(480),d=a&&Object.getOwnPropertyDescriptor(Object.getPrototypeOf(a),'icon_url');if(!d||!d.get)return;
  Object.defineProperty(a,'icon_url',{configurable:true,get:function(){return s.spacewarIcon()||d.get.call(this);}});s.spacewarInfo=a;};
 s.clear=function(p){var v=s.written.get(p);if(!v)return;
  if(p.m_strGameExtraInfo===v.name)p.m_strGameExtraInfo=v.beforeName;
  if(p.m_gameid===v.app)p.m_gameid=v.beforeGame;
  s.written.delete(p);};
 s.one=function(p,id){var l=s.labels[String(id)];
  if(p.m_unGamePlayedAppID!==480||!l){s.clear(p);return;}
  var v=s.written.get(p);
  if(!v||p.m_strGameExtraInfo!==v.name||p.m_gameid!==v.app){v={beforeName:p.m_strGameExtraInfo,beforeGame:p.m_gameid};s.written.set(p,v);}
  v.name=l.name;v.app=l.app;p.m_strGameExtraInfo=l.name;p.m_gameid=l.app;};
 s.icon=function(p){var v=s.written.get(p),apps=window.g_FriendsUIApp&&g_FriendsUIApp.AppInfoStore;
  if(!v||p.m_gameid!==v.app||!apps)return null;
  var info=apps.GetAppInfo(Number(v.app));return info&&info.icon_url?info.icon_url:null;};
 s.patch=function(proto,name,make){var m=s.patches.get(proto);if(!m){m={};s.patches.set(proto,m);}
  if(m[name]||typeof proto[name]!=='function')return;
  var original=proto[name],wrapped=make(original);m[name]={original:original,wrapped:wrapped};proto[name]=wrapped;};
 s.apply=function(){
  if(Date.now()>s.expires)s.labels={};
  var current=window.g_FriendsUIApp&&g_FriendsUIApp.FriendStore;if(!current)return;
  var friends=Array.from(current.all_friends),ids=new Map();
  friends.forEach(function(f){if(f.persona)ids.set(f.persona,f.accountid);});
  s.written.forEach(function(v,p){if(!ids.has(p))s.clear(p);});
  friends.forEach(function(f){var p=f.persona;if(!p)return;
   var proto=Object.getPrototypeOf(p);
   if(proto){
    s.patch(proto,'UpdateFromMessage',function(o){return function(){s.clear(this);var r=o.apply(this,arguments);s.apply();return r;};});
    s.patch(proto,'GetCurrentGameIconURL',function(o){return function(){return s.icon(this)||o.apply(this,arguments);};});
   }
   s.one(p,f.accountid);
  });
  s.patchSpacewar();
 };
 s.timer=setInterval(s.apply,5000);
}
s.fallback=fallback;s.labels=labels;s.expires=Date.now()+180000;s.apply();return true;
})(" + new JavaScriptSerializer().Serialize(labels) + "," + new JavaScriptSerializer().Serialize(FallbackIcon()) + ")";
        }
        static string fallbackIcon;
        internal static string FallbackIcon()
        {
            if (fallbackIcon != null) return fallbackIcon;
            using (var stream = typeof(FriendLabels).Assembly.GetManifestResourceStream("Cabinet.Icon32"))
            {
                if (stream == null) return fallbackIcon = "";
                var bytes = new byte[stream.Length]; stream.Read(bytes, 0, bytes.Length);
                return fallbackIcon = "data:image/png;base64," + Convert.ToBase64String(bytes);
            }
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
        internal static void Apply(Dictionary<string, Label> labels)
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
