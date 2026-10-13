using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using Cabinet;
class FriendLabelChecks
{
    static int checks;
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--live")
        {
            var target = FriendLabels.SelectTarget(new WebClient().DownloadString("http://127.0.0.1:8080/json"));
            string live = @"(function(){
if(window.__cabinetLabels)throw new Error('Existing Cabinet labels: refusing to disturb them');
var friends=Array.from(g_FriendsUIApp.FriendStore.all_friends),before=friends.map(function(f){return {p:f.persona,name:f.persona.m_strGameExtraInfo,app:f.persona.m_unGamePlayedAppID,game:f.persona.m_gameid};});
var result={playing:friends.filter(function(f){return !!f.persona.m_unGamePlayedAppID;}).length};
try{
" + FriendLabels.BuildScript(new Dictionary<string, FriendLabels.Label>()) + @";
var s=window.__cabinetLabels;result.installed=!!s;result.patches=s.patches.size;
result.wrapped=Array.from(s.patches).every(function(e){return e[0].UpdateFromMessage===e[1].UpdateFromMessage.wrapped&&e[0].GetCurrentGameIconURL===e[1].GetCurrentGameIconURL.wrapped;});
var friend=friends.find(function(f){return f.persona.m_unGamePlayedAppID===480;});
result.realSpacewarFriend=!!friend;
if(friend){var p=friend.persona,original=p.m_strGameExtraInfo,game=p.m_gameid;
 s.labels[String(friend.accountid)]={name:'Cabinet local label check',app:'2868840'};s.apply();
 result.realLabel=p.GetCurrentGameName()==='Cabinet local label check';result.appPreserved=p.m_unGamePlayedAppID===480;result.launchesReal=p.m_gameid==='2868840';
 result.icon=p.GetCurrentGameIconURL();
 s.labels={};s.apply();result.labelCleared=p.m_strGameExtraInfo===original&&p.m_gameid===game;
}
}finally{
var s=window.__cabinetLabels;if(s){s.labels={};s.written.forEach(function(v,p){s.clear(p);});clearInterval(s.timer);s.patches.forEach(function(m,p){Object.keys(m).forEach(function(n){if(p[n]===m[n].wrapped)p[n]=m[n].original;});});delete window.__cabinetLabels;}
}
result.restored=before.every(function(v){return v.p.m_strGameExtraInfo===v.name&&v.p.m_unGamePlayedAppID===v.app&&v.p.m_gameid===v.game;});result.stateRemoved=!window.__cabinetLabels;
return result;
})()";
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            Console.WriteLine(json.Serialize(FriendLabels.Evaluate(target, live)));
            Console.WriteLine(json.Serialize(FriendLabels.Evaluate(target, "({stateRemoved:!window.__cabinetLabels})"))); return;
        }
        if (args.Length > 0 && args[0] == "--inspect")
        {
            string targets = new WebClient().DownloadString("http://127.0.0.1:8080/json");
            var result = FriendLabels.Evaluate(FriendLabels.SelectTarget(targets), "JSON.stringify(Array.from(g_FriendsUIApp.FriendStore.all_friends).slice(0,4).map(function(f){return {keys:Object.keys(f),accountid:f.accountid,app:f.persona.m_unGamePlayedAppID,hasUpdate:typeof Object.getPrototypeOf(f.persona).UpdateFromMessage};}))");
            Console.WriteLine(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(result)); return;
        }
        Check(FriendLabels.CleanName("a\n\t\u007f\u0085b") == "ab", "control characters removed");
        Check(FriendLabels.CleanName(null) == "" && FriendLabels.CleanName("   ") == "", "empty names");
        Check(FriendLabels.CleanName(new string('a', 99) + "\ud83d\ude00").Length == 99, "truncation preserves surrogate pairs");
        Check(FriendLabels.CleanName(new string('a', 120)).Length == 100, "name limit");
        foreach (string json in new[] { "", "{", "null", "{}", "[]", "[null,1,{}]", "[{\"title\":\"SharedJSContext\"}]", "[{\"title\":\"SharedJSContext\",\"webSocketDebuggerUrl\":\"ws://evil.test:8080/x\"}]" })
            Check(FriendLabels.SelectTarget(json) == null, "missing or odd target: " + json);
        string good = "{\"title\":\"SharedJSContext\",\"webSocketDebuggerUrl\":\"ws://127.0.0.1:8080/devtools/page/abc\"}";
        Check(FriendLabels.SelectTarget("[{\"title\":\"Other\"}," + good + "]").AbsolutePath.EndsWith("abc"), "exact SharedJSContext target");
        var labels = new Dictionary<string, FriendLabels.Label> { { "123", new FriendLabels.Label { name = "quoted \" \\ </script> é ☃", app = "2868840" } } };
        string script = FriendLabels.BuildScript(labels);
        Check(script.Contains("\\\"") && script.Contains("\\\\"), "JSON escapes names");
        string path = Path.Combine(Path.GetTempPath(), "cabinet-label-check-" + Guid.NewGuid().ToString("N") + ".js");
        try
        {
            string mock = @"
const assert=require('node:assert/strict');
global.window=global;let timers=[];global.setInterval=(fn)=>{timers.push(fn);return 1;};
class Persona {constructor(app,name){this.m_unGamePlayedAppID=app;this.m_gameid=String(app);this.m_strGameExtraInfo=name;} UpdateFromMessage(app,name){this.m_unGamePlayedAppID=app;this.m_gameid=String(app);this.m_strGameExtraInfo=name;return 42;} GetCurrentGameIconURL(){return 'icon-'+this.m_unGamePlayedAppID;}}
let p=new Persona(480,'Spacewar'),other=new Persona(100,'Other'),self=new Persona(480,'Self');
global.g_FriendsUIApp={FriendStore:{all_friends:new Set([{accountid:123,persona:p},{accountid:456,persona:other}]),self:{persona:self}},AppInfoStore:{GetAppInfo:(id)=>({icon_url:'real-icon-'+id})}};
const original=Persona.prototype.UpdateFromMessage;
";
            string behavior = @"
const patched=Persona.prototype.UpdateFromMessage;
assert.notEqual(original,patched);assert.equal(p.m_strGameExtraInfo,'quoted '+String.fromCharCode(34)+' '+String.fromCharCode(92)+' </script> é ☃');assert.equal(p.m_unGamePlayedAppID,480);assert.equal(other.m_strGameExtraInfo,'Other');assert.equal(self.m_strGameExtraInfo,'Self');
assert.equal(p.m_gameid,'2868840');assert.equal(p.GetCurrentGameIconURL(),'real-icon-2868840');assert.equal(other.GetCurrentGameIconURL(),'icon-100');assert.equal(other.m_gameid,'100');
assert.equal(p.UpdateFromMessage(480,'Server name'),42);assert.equal(p.m_strGameExtraInfo,__cabinetLabels.labels['123'].name);assert.equal(p.m_gameid,'2868840');
";
            string tail = @"
assert.equal(Persona.prototype.UpdateFromMessage,patched);assert.equal(timers.length,1);
__cabinetLabels.labels={};__cabinetLabels.apply();assert.equal(p.m_strGameExtraInfo,'Server name');assert.equal(p.m_gameid,'480');assert.equal(p.GetCurrentGameIconURL(),'icon-480');
__cabinetLabels.labels={'123':{name:'New',app:'2868840'}};__cabinetLabels.apply();p.UpdateFromMessage(100,'Real Steam game');assert.equal(p.m_strGameExtraInfo,'Real Steam game');assert.equal(p.m_gameid,'100');
p.UpdateFromMessage(480,'Server again');assert.equal(p.m_strGameExtraInfo,'New');p.m_strGameExtraInfo='External';p.m_gameid='999';__cabinetLabels.labels={};__cabinetLabels.apply();assert.equal(p.m_strGameExtraInfo,'External');assert.equal(p.m_gameid,'999');
__cabinetLabels.labels={'123':{name:'Expiry',app:'2868840'}};__cabinetLabels.apply();__cabinetLabels.expires=0;timers[0]();assert.equal(p.m_strGameExtraInfo,'External');assert.equal(p.m_gameid,'999');
__cabinetLabels.expires=Date.now()+180000;__cabinetLabels.labels={'123':{name:'Removed',app:'2868840'}};__cabinetLabels.apply();g_FriendsUIApp.FriendStore.all_friends.clear();__cabinetLabels.apply();assert.equal(p.m_strGameExtraInfo,'External');
console.log('PASS JS reapply, clear, external ownership, expiry, friend removal, idempotence, unchanged app/self, launch id, icon');
";
            string upgrade = @"
delete global.__cabinetLabels;Persona.prototype.UpdateFromMessage=original;
let q=new Persona(480,'Spacewar');g_FriendsUIApp.FriendStore.all_friends=new Set([{accountid:123,persona:q}]);
const v1=function(){};global.__cabinetLabels={labels:{},written:new Map([[q,{}]]),patches:new Map([[Persona.prototype,{original:original,wrapped:v1}]]),timer:7,clear:function(x){this.written.delete(x);}};
Persona.prototype.UpdateFromMessage=v1;let cleared=0;global.clearInterval=()=>{cleared++;};
";
            string upgradeCheck = @"
assert.equal(__cabinetLabels.version,2);assert.equal(cleared,1);assert.notEqual(Persona.prototype.UpdateFromMessage,v1);assert.equal(q.m_gameid,'2868840');
console.log('PASS older injected script replaced');
";
            File.WriteAllText(path, mock + script + ";\n" + behavior + script + ";\n" + tail + upgrade + script + ";\n" + upgradeCheck);
            using (var process = Process.Start(new ProcessStartInfo("bun", "\"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true }))
            { if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("Script checks timed out"); } Check(process.ExitCode == 0, "script behavior"); }
        }
        finally { File.Delete(path); }
        var players = new Dictionary<string, object> { { "players", new object[] {
            new Dictionary<string, object> { { "accountId", 1 }, { "appId", "10" }, { "name", "Own" }, { "updatedAt", CloudClient.Now() } },
            new Dictionary<string, object> { { "accountId", 2 }, { "appId", "2868840" }, { "name", "Friend\nGame" }, { "updatedAt", CloudClient.Now() } },
            new Dictionary<string, object> { { "accountId", 3 }, { "appId", "10" }, { "name", "Stale" }, { "updatedAt", 1 } },
            new Dictionary<string, object> { { "accountId", 4 }, { "appId", "x1" }, { "name", "Bad app" }, { "updatedAt", CloudClient.Now() } } } } };
        var map = FriendLabels.ReadPlayers(players, 1);
        Check(map.Count == 1 && map["2"].name == "FriendGame" && map["2"].app == "2868840", "own, stale and invalid-app presence excluded");
        Console.WriteLine(checks + " friend label checks passed.");
    }
}
