using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Cabinet;
class ControllerLayoutChecks
{
    static int checks;
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); checks++; Console.WriteLine("PASS " + what); }
    static void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, new UTF8Encoding(false)); }
    static string Layout(string title, string device, bool native) { return "\"controller_mappings\" { \"title\" \"" + title + "\" \"controller_type\" \"" + device + "\" \"group\" { \"binding\" \"" + (native ? "game_action Controls SELECT" : "xinput_button A") + "\" } }"; }
    static string Node(string path, params string[] node) { return new VdfText(File.ReadAllText(path)).RawValue(node); }
    static void Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "cabinet-controller-checks-" + Guid.NewGuid().ToString("N"));
        string steam = Path.Combine(root, "steam"), data = Path.Combine(root, "data");
        string config = Path.Combine(steam, "steamapps", "common", "Steam Controller Configs", "123", "config");
        string xbox = Path.Combine(config, "configset_controller_xboxone.vdf"), ps4 = Path.Combine(config, "configset_controller_ps4.vdf");
        string local = Path.Combine(steam, "userdata", "123", "config", "localconfig.vdf");
        string template = Path.Combine(steam, "controller_base", "templates", "controller_xboxone_gamepad_joystick.vdf");
        var logs = new List<string>(); bool live = true;
        try
        {
            Put(template, Layout("Gamepad", "controller_xboxone", false));
            Put(Path.Combine(steam, "controller_base", "templates", "controller_ps4_gamepad_joystick.vdf"), Layout("PS template", "controller_ps4", false));
            Put(xbox, "\"controller_config\" { // keep this comment\n \"480\" { \"workshop\" \"77\" } \"100\" { \"template\" \"controller_xboxone_gamepad_joystick.vdf\" } \"200\" { \"autosave\" \"1\" } \"999\" { \"untouched\" \"yes\" } }");
            Put(ps4, "\"controller_config\" { \"100\" { \"template\" \"controller_ps4_gamepad_joystick.vdf\" } }");
            Put(local, "\"UserLocalConfigStore\" { \"apps\" { \"480\" { \"UseSteamControllerConfig\" \"2\" \"Other\" \"preserve\" } \"100\" { \"UseSteamControllerConfig\" \"0\" } \"200\" { \"UseSteamControllerConfig\" \"1\" } } \"OtherRoot\" \"keep\" }");
            string originalLayout = Layout("original spare", "controller_xboxone", false);
            string dest = Path.Combine(config, "480", "controller_xboxone.vdf"); Put(dest, originalLayout);
            string source = Path.Combine(config, "200", "controller_xboxone.vdf"); string custom = Layout("Distinctive custom", "controller_xboxone", false); Put(source, custom);
            var engine = new ControllerLayouts(steam, data, delegate { return live; }, logs.Add);
            string untouched = Node(xbox, "controller_config", "999"), real = Node(xbox, "controller_config", "100");
            Check(engine.Apply("100", "123"), "template apply changes spare slot while client running");
            Check(File.ReadAllText(dest).Contains("Gamepad"), "template resolved");
            Check(Node(local, "UserLocalConfigStore", "apps", "480", "UseSteamControllerConfig") == "\"0\"", "forced off mirrored");
            Check(Node(xbox, "controller_config", "999") == untouched && Node(xbox, "controller_config", "100") == real && File.ReadAllText(xbox).Contains("keep this comment"), "other app nodes and comments preserved");
            Check(!engine.Apply("100", "123"), "identical apply performs no Steam writes");
            Check(engine.Apply("200", "123") && File.ReadAllText(dest) == custom && File.ReadAllText(source) == custom, "custom wins and source is unchanged");
            Check(Node(local, "UserLocalConfigStore", "apps", "480", "UseSteamControllerConfig") == "\"1\"", "forced on mirrored");
            Check(!File.Exists(Path.Combine(config, "480", "controller_ps4.vdf")), "previous game's stale autosave removed");
            Put(Path.Combine(config, "300", "controller_xboxone.vdf"), Layout("native", "controller_xboxone", true));
            engine.Apply("300", "123");
            Check(File.ReadAllText(dest).Contains("Gamepad") && logs.Any(l => l.Contains("native game actions")), "native-action layout falls back with evidence log");
            Check(Node(local, "UserLocalConfigStore", "apps", "480", "UseSteamControllerConfig") == null, "absent default override removes previous force setting");
            string communityDir = Path.Combine(steam, "steamapps", "workshop", "content", "241100", "88"); Put(Path.Combine(communityDir, "x_legacy.bin"), Layout("community", "controller_xboxone", false));
            string serial = Path.Combine(config, "configset_TESTSERIAL.vdf"); Put(serial, "\"controller_config\" { \"400\" { \"workshop\" \"88\" } }");
            engine.Apply("400", "123");
            Check(File.ReadAllText(Path.Combine(config, "480", "TESTSERIAL.vdf")).Contains("community") && Node(serial, "controller_config", "480").Contains("autosave"), "physical-device community selection resolved independently");
            engine.Apply("100", "123");
            Check(engine.Tick(new HashSet<string>(new[] { "100", "400" }), "123", "400") != null, "latest launch selected when multiple games are observed");
            Check(File.Exists(Path.Combine(config, "480", "TESTSERIAL.vdf")), "latest game's physical selection retained");
            engine.Apply("100", "123");
            Check(Node(serial, "controller_config", "480") == null && !File.Exists(Path.Combine(config, "480", "TESTSERIAL.vdf")), "physical-device override removed when switching to type defaults");
            Check(logs.Count > 0, "controller changes logged");
            // Simulate a user changing another app after Cabinet's first snapshot.
            var updated = new VdfText(File.ReadAllText(xbox)); updated.SetRawValue("{ \"changed\" \"later\" }", "controller_config", "999"); Put(xbox, updated.Text);
            live = false;
            Check(engine.Tick(new HashSet<string>(), "123", null) == null, "Steam shutdown reapplies last preset silently");
            Check(Node(local, "UserLocalConfigStore", "apps", "480", "UseSteamControllerConfig") == "\"0\"", "last preset remains prepared between sessions");
            engine.Restore();
            Check(File.ReadAllText(dest) == originalLayout && Node(xbox, "controller_config", "480").Contains("77"), "first original layout and selection restored after multiple games");
            Check(Node(xbox, "controller_config", "999").Contains("later") && Node(local, "UserLocalConfigStore", "apps", "480", "Other") == "\"preserve\"", "restore preserves later unrelated edits and sibling settings");
            Check(Node(local, "UserLocalConfigStore", "apps", "480", "UseSteamControllerConfig") == "\"2\"", "original enablement restored");
            Check(!File.Exists(Path.Combine(data, "controller-last-game.json")) && Directory.GetFiles(data, "controller-480-original*").Length == 1, "restore clears preset and keeps one original snapshot");
            Check(Directory.GetFiles(Path.Combine(data, "controller-file-backups")).Length > 0, "Steam files backed up before commits");
            bool malformed = false; try { new VdfText("\"root\" { \"bad\" \"unterminated").SetRawValue("\"1\"", "root", "480"); } catch (InvalidDataException) { malformed = true; }
            Check(malformed, "malformed VDF refused");
            bool invalid = false; try { engine.Apply("../100", "123"); } catch (InvalidDataException) { invalid = true; }
            Check(invalid, "invalid source path rejected");
            Console.WriteLine(checks + " controller checks passed");
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved).TrimEnd(Path.DirectorySeparatorChar) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(resolved).StartsWith("cabinet-controller-checks-")) Directory.Delete(resolved, true);
        }
    }
}
