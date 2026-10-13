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
        // Each problem is logged when it starts or changes, and again when it clears.
        static readonly Dictionary<string, string> problems = new Dictionary<string, string>();
        static void Report(Action<string> log, string kind, string problem)
        {
            string last; bool had = problems.TryGetValue(kind, out last);
            if (problem == null) { if (had) { problems.Remove(kind); log(kind + ": recovered"); } return; }
            if (!had || last != problem) { problems[kind] = problem; log(kind + ": " + problem); }
        }
        static void Once(Action<string> log, string kind, Exception ex) { Report(log, kind, ex.Message); }
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
                        // A failed fetch keeps the labels already in Steam (they expire on their own)
                        // instead of wiping them until the next successful round.
                        try { labels = ReadPlayers(request("GET", "/api/presence", null), own); Report(log, "presence fetch failed", null); }
                        catch (Exception ex) { Once(log, "presence fetch failed", ex); return; }
                    }
                    if (connected || installed)
                    {
                        try
                        {
                            string errors = Apply(labels); installed = connected;
                            Report(log, "Steam friends debugger unavailable", null);
                            Report(log, "friend labels script errors", string.IsNullOrEmpty(errors) || errors == "{}" ? null : errors);
                        }
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
if(s&&s.version!==6){
 try{s.labels={};s.written.forEach(function(v,p){s.clear(p);});}catch(e){}
 clearInterval(s.timer);if(s.pending)clearTimeout(s.pending);
 s.patches.forEach(function(v,p){if(v.wrapped){if(p.UpdateFromMessage===v.wrapped)p.UpdateFromMessage=v.original;}
  else Object.keys(v).forEach(function(n){if(p[n]===v[n].wrapped)p[n]=v[n].original;});});
 var w=s.spacewarInfo;
 if(w&&w.copy){if(w.map.get(480)===w.copy)w.map.set(480,w.original);}else if(w)delete w.icon_url;
 if(s.unpatchGroups)try{s.unpatchGroups();}catch(e){}
 // Version 5 stopped halfway on Steam's MobX store and left these behind.
 var gs=store.FriendGroupStore;
 if(gs)['GetGameGroup','GetCountFriendsInGame','games_with_friends_playing'].forEach(function(n){var o=Object.getOwnPropertyDescriptor(gs,n);
  if(o&&o.configurable&&/s\.moved|g\.GetGameGroup/.test(String(o.value||o.get)))delete gs[n];});
 s=null;
}
if(!s){
 s=window.__cabinetLabels={version:6,labels:{},written:new Map(),patches:new Map(),friends:new Map(),errors:{},expires:0,fallback:'',sig:''};
 // Every hook runs inside Steam's own code: a failure is recorded and Steam's own result is used.
 s.fail=function(n,e){s.errors[n]=String(e&&e.message||e).slice(0,200);};
 s.step=function(n,f){try{f();delete s.errors[n];}catch(e){s.fail(n,e);}};
 // Friends relabeled to a game in this library are counted under that game (library
 // page, green badge, home carousel) instead of Spacewar. Not in the library: Spacewar.
 s.owned=function(app){var st=window.appStore;try{return !!(st&&st.GetAppOverviewByAppID&&st.GetAppOverviewByAppID(Number(app)));}catch(e){return false;}};
 s.moved=function(){var by={};s.written.forEach(function(v,p){
   if(v.friend&&p.m_unGamePlayedAppID===480&&p.m_gameid===v.app&&s.owned(v.app))(by[v.app]=by[v.app]||[]).push(v.friend);});return by;};
 s.group=function(list){return {member_list:list,member_list_unsorted:list,unfiltered_count:list.length,member_count:list.length};};
 s.patchGroups=function(){var g=store.FriendGroupStore;if(!g||(s.groupStore&&s.groupStore.store===g))return;
  if(s.groupStore)s.unpatchGroups();
  var proto=Object.getPrototypeOf(g),map=g.m_mapGameGroups,d=null,get=proto.GetGameGroup,count=proto.GetCountFriendsInGame;
  for(var o=proto;o&&!d;o=Object.getPrototypeOf(o))d=Object.getOwnPropertyDescriptor(o,'games_with_friends_playing');
  if(!map||typeof map.get!=='function'||typeof get!=='function'||!d||!d.get)return;
  var u={store:g};
  var keys=function(k){try{var r=k.filter(function(x){return Number(x)!==480||!!g.GetGameGroup(480);});
    Object.keys(s.moved()).forEach(function(a){if(r.indexOf(Number(a))<0)r.push(Number(a));});return r;}catch(e){s.fail('games',e);return k;}};
  g.GetGameGroup=function(e){var base=get.call(g,e);try{var by=s.moved(),extra=by[String(e)];
    if(Number(e)===480&&base){var all=[];Object.keys(by).forEach(function(k){all=all.concat(by[k]);});
     if(!all.length)return base;var left=base.member_list.filter(function(f){return all.indexOf(f)<0;});return left.length?s.group(left):undefined;}
    return extra?s.group((base?base.member_list:[]).concat(extra)):base;}catch(x){s.fail('GetGameGroup',x);return base;}};
  g.GetCountFriendsInGame=function(e){try{var r=g.GetGameGroup(e);return r?r.unfiltered_count||0:0;}
    catch(x){s.fail('count',x);return typeof count==='function'?count.call(g,e):0;}};
  var own=Object.getOwnPropertyDescriptor(g,'games_with_friends_playing');
  if(own&&!own.configurable){
   // Steam's store is MobX: the property is a non-configurable computed, so its derivation is wrapped.
   var sym=Object.getOwnPropertySymbols(g).filter(function(x){return String(x)==='Symbol(mobx administration)';})[0],adm=sym&&g[sym],
    cv=adm&&adm.values_&&typeof adm.values_.get==='function'&&adm.values_.get('games_with_friends_playing');
   if(cv&&typeof cv.derivation==='function'){var od=cv.derivation;cv.derivation=function(){return keys(od.call(this));};u.computed={cv:cv,original:od,wrapped:cv.derivation};}
  }else Object.defineProperty(g,'games_with_friends_playing',{configurable:true,get:function(){return keys(d.get.call(this));}});
  s.groupStore=u;};
 s.unpatchGroups=function(){var u=s.groupStore;if(!u)return;s.groupStore=null;delete u.store.GetGameGroup;delete u.store.GetCountFriendsInGame;
  var o=Object.getOwnPropertyDescriptor(u.store,'games_with_friends_playing');if(o&&o.configurable)delete u.store.games_with_friends_playing;
  if(u.computed&&u.computed.cv.derivation===u.computed.wrapped)u.computed.cv.derivation=u.computed.original;};
 // Components already rendered observe map keys, not our patch: touch the affected keys.
 s.nudge=function(){var w=s.groupStore&&s.groupStore.store.m_mapGameGroups;if(!w)return;var by=s.moved(),sig=JSON.stringify(Object.keys(by).map(function(k){return k+':'+by[k].length;}));
  if(sig===s.sig)return;var keys=[480].concat(Object.keys(by).map(Number),s.sigKeys||[]);s.sig=sig;s.sigKeys=Object.keys(by).map(Number);
  var any=w.get(480)||Array.from(w.values())[0];
  keys.forEach(function(k){if(w.has(k)){var x=w.get(k);w.delete(k);w.set(k,x);}else if(any){w.set(k,any);w.delete(k);}});};
 s.spacewarIcon=function(){var apps=window.g_FriendsUIApp&&g_FriendsUIApp.AppInfoStore,current=window.g_FriendsUIApp&&g_FriendsUIApp.FriendStore;
  if(!apps||!current)return null;
  var games=new Set(),unknown=false;
  Array.from(current.all_friends).forEach(function(f){var p=f.persona;if(!p||p.m_unGamePlayedAppID!==480)return;
   var v=s.written.get(p);if(v&&p.m_gameid===v.app)games.add(v.app);else unknown=true;});
  if(games.size===1&&!unknown){var info=apps.GetAppInfo(Number(games.values().next().value));if(info&&info.m_unAppID!==480&&info.icon_url)return info.icon_url;}
  return s.fallback||null;};
 // AppInfo objects aren't observable; the store's map is. Swapping in a copy with the
 // icon override makes rendered badges refresh; restoring the original undoes it.
 s.patchSpacewar=function(){var apps=window.g_FriendsUIApp&&g_FriendsUIApp.AppInfoStore,map=apps&&apps.m_mapAppInfo;
  if(!map||typeof map.set!=='function')return;apps.GetAppInfo(480);var a=map.get(480);
  if(!a||(s.spacewarInfo&&a===s.spacewarInfo.copy))return;
  var d=Object.getOwnPropertyDescriptor(Object.getPrototypeOf(a),'icon_url');if(!d||!d.get)return;
  var c=Object.create(Object.getPrototypeOf(a));Object.getOwnPropertyNames(a).forEach(function(k){c[k]=a[k];});
  Object.defineProperty(c,'icon_url',{configurable:true,get:function(){try{var i=s.spacewarIcon();if(i)return i;}catch(e){s.fail('spacewarIcon',e);}return d.get.call(this);}});
  s.spacewarInfo={map:map,original:a,copy:c};map.set(480,c);};
 s.clear=function(p){var v=s.written.get(p);if(!v)return;
  if(p.m_strGameExtraInfo===v.name)p.m_strGameExtraInfo=v.beforeName;
  if(p.m_gameid===v.app)p.m_gameid=v.beforeGame;
  s.written.delete(p);};
 s.one=function(p,id,f){var l=s.labels[String(id)];
  if(p.m_unGamePlayedAppID!==480||!l){s.clear(p);return;}
  var v=s.written.get(p);
  if(!v||p.m_strGameExtraInfo!==v.name||p.m_gameid!==v.app){v={beforeName:p.m_strGameExtraInfo,beforeGame:p.m_gameid};s.written.set(p,v);}
  v.name=l.name;v.app=l.app;v.friend=f;p.m_strGameExtraInfo=l.name;p.m_gameid=l.app;};
 s.icon=function(p){var v=s.written.get(p),apps=window.g_FriendsUIApp&&g_FriendsUIApp.AppInfoStore;
  if(!v||p.m_gameid!==v.app||!apps)return null;
  var info=apps.GetAppInfo(Number(v.app));return info&&info.icon_url?info.icon_url:null;};
 s.patch=function(proto,name,make){var m=s.patches.get(proto);if(!m){m={};s.patches.set(proto,m);}
  if(m[name]||typeof proto[name]!=='function')return;
  var original=proto[name],wrapped=make(original);m[name]={original:original,wrapped:wrapped};proto[name]=wrapped;};
 s.labelAll=function(){
  if(Date.now()>s.expires)s.labels={};
  var current=window.g_FriendsUIApp&&g_FriendsUIApp.FriendStore;if(!current)return;
  var friends=Array.from(current.all_friends),ids=new Map();
  friends.forEach(function(f){if(f.persona)ids.set(f.persona,f);});
  s.written.forEach(function(v,p){if(!ids.has(p))s.clear(p);});s.friends=ids;
  friends.forEach(function(f){var p=f.persona;if(!p)return;
   var proto=Object.getPrototypeOf(p);
   if(proto){
    // The updated persona is relabeled at once; store-wide work runs once per burst.
    s.patch(proto,'UpdateFromMessage',function(o){return function(){
     try{s.clear(this);}catch(e){s.fail('clear',e);}
     var r=o.apply(this,arguments);
     try{var f=s.friends.get(this);if(f)s.one(this,f.accountid,f);}catch(e){s.fail('update',e);}
     s.later();return r;};});
    s.patch(proto,'GetCurrentGameIconURL',function(o){return function(){try{var i=s.icon(this);if(i)return i;}catch(e){s.fail('icon',e);}return o.apply(this,arguments);};});
   }
   s.one(p,f.accountid,f);
  });
 };
 s.later=function(){if(s.pending)return;try{s.pending=setTimeout(function(){s.pending=0;s.apply();},250);}catch(e){s.pending=0;}};
 s.apply=function(){s.step('labels',s.labelAll);s.step('spacewar',s.patchSpacewar);s.step('groups',s.patchGroups);s.step('nudge',s.nudge);};
 s.timer=setInterval(s.apply,5000);
}
s.fallback=fallback;s.labels=labels;s.expires=Date.now()+180000;s.apply();return JSON.stringify(s.errors);
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
        // Returns the script's own error report ("{}" when every step worked).
        internal static string Apply(Dictionary<string, Label> labels)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8080/json");
            req.Timeout = 3000; req.ReadWriteTimeout = 3000; req.Proxy = null; req.AllowAutoRedirect = false;
            string json;
            using (var response = req.GetResponse()) using (var stream = response.GetResponseStream()) using (var reader = new StreamReader(stream))
            { char[] buffer = new char[1024 * 1024 + 1]; int length = reader.ReadBlock(buffer, 0, buffer.Length); if (length == buffer.Length) throw new IOException("Debugger target list too large"); json = new string(buffer, 0, length); }
            var target = SelectTarget(json); if (target == null) throw new IOException("SharedJSContext target missing");
            object value; return CloudClient.Map(Evaluate(target, BuildScript(labels))).TryGetValue("value", out value) ? value as string : null;
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
