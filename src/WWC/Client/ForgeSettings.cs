#if WWC_FORGE
// WWC's settings in the F12 menu, all under WWC's one entry: every part's config file (settings-schema.json, written by the release
// build from the shipped defaults) next to the client parts' own settings. Values are read from each part's config file under
// SPT_Runtime\user\mods\Widowz-WWC.Server; a change is written straight back into that file, replacing only the characters of the
// value, so every note in it stays. The parts read their files when SPT starts, so a change applies at the next start.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace WWC.Client.Settings
{
    internal sealed class SchemaEntry
    {
        public string[] Path;
        public string Kind, Def, Label, Group, Desc;
        public bool Adv;
        public string[] Options;
        public double? Min, Max;
    }

    internal sealed class SchemaFile
    {
        public string File, Label;
        public readonly List<SchemaEntry> Entries = new List<SchemaEntry>();
        public string[] FileOnly;
    }

    internal static class SettingsCore
    {
        static string gameRoot;
        static Saver saver;
        static bool binding;

        /// <summary>Adds every setting in settings-schema.json to WWC's own F12 entry. Never throws.</summary>
        public static void Bind(ConfigFile cfg, ManualLogSource log)
        {
            try
            {
                gameRoot = BepInEx.Paths.GameRootPath;
                string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(SettingsCore).Assembly.Location) ?? "", "settings-schema.json");
                if (!File.Exists(path)) { log.LogInfo("WWC settings: no settings-schema.json, so the F12 menu only shows the game-side settings"); return; }
                var files = Load(path);
                saver = new GameObject("WWCSettingsSaver").AddComponent<Saver>();
                UnityEngine.Object.DontDestroyOnLoad(saver.gameObject);
                saver.gameObject.hideFlags = HideFlags.HideAndDontSave;
                saver.Log = log;
                int order = 0, count = 0;
                foreach (var f in files) count += BindFile(cfg, f, log, ref order);
                log.LogInfo("WWC settings: " + count + " settings from " + files.Count + " config files in the F12 menu");
            }
            catch (Exception ex) { log.LogError("WWC settings menu is off: " + ex); }
        }

        static List<SchemaFile> Load(string path)
        {
            var root = JsoncDoc.Parse(File.ReadAllText(path, Encoding.UTF8)).Root;
            var list = new List<SchemaFile>();
            foreach (var m in Arr(root, "mods"))
                foreach (var f in Arr(m, "files"))
                {
                    var sf = new SchemaFile { File = Str(f, "file"), Label = Str(f, "label"), FileOnly = Strs(f, "fileOnly") };
                    foreach (var e in Arr(f, "entries"))
                        sf.Entries.Add(new SchemaEntry
                        {
                            Path = Strs(e, "path"), Kind = Str(e, "kind"), Def = Str(e, "def"), Label = Str(e, "label"), Group = Str(e, "group"),
                            Desc = Str(e, "desc"), Adv = Bool(e, "adv"), Options = Strs(e, "options"), Min = Num(e, "min"), Max = Num(e, "max")
                        });
                    list.Add(sf);
                }
            return list;
        }

        static int BindFile(ConfigFile cfg, SchemaFile f, ManualLogSource log, ref int order)
        {
            string full = System.IO.Path.Combine(gameRoot, f.File);
            string prefix = string.IsNullOrEmpty(f.Label) ? "" : f.Label + " - ";
            if (!File.Exists(full))
            {
                Info(cfg, prefix + "General", "Status", "Missing: " + f.File, "This file isn't there, so its settings can't be shown. Reinstall WWC.", order++);
                return 0;
            }
            JsoncDoc doc;
            try { doc = JsoncDoc.Parse(File.ReadAllText(full, Encoding.UTF8)); }
            catch (JsoncException ex)
            {
                Info(cfg, prefix + "General", "Status", "The file has a mistake", System.IO.Path.GetFileName(full) + ": " + ex.Message + ". Fix it in " + f.File + " with a text editor, then restart the game. Until then that part uses its normal values.", order++);
                return 0;
            }
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var e in f.Entries)
            {
                try { BindEntry(cfg, f, full, doc, e, prefix, used, order++); n++; }
                catch (Exception ex) { log.LogWarning("WWC settings: " + f.Label + " " + string.Join(".", e.Path) + " left out of the menu (" + ex.Message + ")"); }
            }
            if (f.FileOnly != null && f.FileOnly.Length > 0)
                Info(cfg, prefix + "General", "Only in the file", string.Join(", ", f.FileOnly),
                    "Lists and groups the menu can't show. Change them in " + f.File + " with a text editor; the notes in the file explain each one.", order++);
            return n;
        }

        static void Info(ConfigFile cfg, string section, string key, string value, string desc, int order)
        {
            // The F12 menu lists a higher Order first, so file order is kept with negative numbers.
            var tags = new ConfigurationManagerAttributes { ReadOnly = true, HideDefaultButton = true, Order = -order };
            var entry = cfg.Bind(new ConfigDefinition(Clean(section), Clean(key)), value, new ConfigDescription(desc, null, tags));
            binding = true; entry.Value = value; binding = false;
        }

        static void BindEntry(ConfigFile cfg, SchemaFile f, string full, JsoncDoc doc, SchemaEntry e, string prefix, HashSet<string> used, int order)
        {
            string section = Clean(prefix + (string.IsNullOrEmpty(e.Group) ? "General" : e.Group));
            string key = Clean(e.Label);
            string unique = key; int n = 2;
            while (!used.Add(section + "|" + unique)) unique = key + " (" + n++ + ")";
            var def = new ConfigDefinition(section, unique);
            JNode node = doc.At(e.Path);
            JNode defNode = JsoncDoc.Parse(e.Def).Root;
            string where = string.Join(".", e.Path);
            var desc = new StringBuilder((e.Desc ?? "").Trim());
            bool readOnly = false;
            if (node == null) Para(desc, "Not in your file yet, so WWC uses this default. Changing it adds it to the file.");
            Para(desc, "Saved in " + f.File + " (" + where + "). Takes effect the next time you start SPT (server and game).");
            var tags = new ConfigurationManagerAttributes { Order = -order, IsAdvanced = e.Adv };
            string label = f.Label + ": " + e.Label;

            switch (e.Kind)
            {
                case "bool":
                {
                    bool dv = defNode.Kind == JKind.True;
                    bool cur = dv;
                    if (node != null) { if (node.Kind == JKind.True || node.Kind == JKind.False) cur = node.Kind == JKind.True; else readOnly = true; }
                    Finish(cfg, def, dv, cur, null, desc, tags, readOnly, full, e.Path, v => v ? "true" : "false", label);
                    break;
                }
                case "int":
                {
                    int dv = (int)Math.Round(defNode.Num);
                    int cur = dv;
                    if (node != null) { if (node.Kind == JKind.Number) cur = (int)Math.Round(node.Num); else readOnly = true; }
                    AcceptableValueBase range = null;
                    if (e.Min.HasValue && e.Max.HasValue && cur >= e.Min.Value && cur <= e.Max.Value && dv >= e.Min.Value && dv <= e.Max.Value)
                        range = new AcceptableValueRange<int>((int)e.Min.Value, (int)e.Max.Value);
                    Finish(cfg, def, dv, cur, range, desc, tags, readOnly, full, e.Path, v => JsoncDoc.Number(v, true), label);
                    break;
                }
                case "float":
                {
                    double dv = defNode.Num;
                    double cur = dv;
                    if (node != null) { if (node.Kind == JKind.Number) cur = node.Num; else readOnly = true; }
                    AcceptableValueBase range = null;
                    if (e.Min.HasValue && e.Max.HasValue && cur >= e.Min.Value && cur <= e.Max.Value && dv >= e.Min.Value && dv <= e.Max.Value)
                        range = new AcceptableValueRange<double>(e.Min.Value, e.Max.Value);
                    tags.ObjToStr = o => ((double)o).ToString("R", CultureInfo.InvariantCulture);
                    tags.StrToObj = s => { double r; return double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? (object)r : (object)cur; };
                    Finish(cfg, def, dv, cur, range, desc, tags, readOnly, full, e.Path, v => JsoncDoc.Number(v, false), label);
                    break;
                }
                case "string":
                {
                    string dv = defNode.Str ?? "";
                    string cur = dv;
                    if (node != null) { if (node.Kind == JKind.String) cur = node.Str; else readOnly = true; }
                    AcceptableValueBase list = null;
                    if (e.Options != null && e.Options.Length > 1 && Array.IndexOf(e.Options, cur) >= 0 && Array.IndexOf(e.Options, dv) >= 0)
                        list = new AcceptableValueList<string>(e.Options);
                    Finish(cfg, def, dv, cur, list, desc, tags, readOnly, full, e.Path, v => JsoncDoc.Quote(v ?? ""), label);
                    break;
                }
                case "list":
                {
                    string dv = JoinList(defNode);
                    string cur = dv;
                    if (node != null) { if (node.Kind == JKind.Array) cur = JoinList(node); else readOnly = true; }
                    Para(desc, "Several values are separated by commas.");
                    Finish(cfg, def, dv, cur, null, desc, tags, readOnly, full, e.Path, ListJson, label);
                    break;
                }
            }
        }

        static void Finish<T>(ConfigFile cfg, ConfigDefinition def, T defaultValue, T current, AcceptableValueBase acceptable, StringBuilder desc,
            ConfigurationManagerAttributes tags, bool readOnly, string file, string[] path, Func<T, string> toJson, string label)
        {
            if (readOnly) { tags.ReadOnly = true; Para(desc, "Your file has a different kind of value here, so the menu only shows it. Change it in the file."); }
            var entry = cfg.Bind(def, defaultValue, new ConfigDescription(desc.ToString(), acceptable, tags));
            binding = true;
            try { entry.Value = current; }
            finally { binding = false; }
            if (readOnly) return;
            entry.SettingChanged += (s, a) =>
            {
                if (binding) return;
                saver.Queue(file, path, toJson(entry.Value), label);
            };
        }

        static void Para(StringBuilder sb, string s) { if (sb.Length > 0) sb.Append("\n\n"); sb.Append(s); }

        static string JoinList(JNode arr)
        {
            var parts = new List<string>();
            if (arr.Items != null) foreach (var it in arr.Items) if (it.Kind == JKind.String) parts.Add(it.Str);
            return string.Join(", ", parts.ToArray());
        }

        static string ListJson(string text)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var raw in (text ?? "").Split(','))
            {
                string v = raw.Trim();
                if (v.Length == 0) continue;
                if (!first) sb.Append(", ");
                sb.Append(JsoncDoc.Quote(v));
                first = false;
            }
            return sb.Append(']').ToString();
        }

        // BepInEx refuses these characters in section and setting names.
        static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") sb.Append("=\n\t\\\"'[]".IndexOf(c) >= 0 ? ' ' : c);
            return sb.ToString().Replace("  ", " ").Trim();
        }

        static JNode Get(JNode o, string k) { var p = o == null ? null : o.Find(k); return p == null ? null : p.Value; }
        static string Str(JNode o, string k) { var v = Get(o, k); return v != null && v.Kind == JKind.String ? v.Str : null; }
        static bool Bool(JNode o, string k) { var v = Get(o, k); return v != null && v.Kind == JKind.True; }
        static double? Num(JNode o, string k) { var v = Get(o, k); return v != null && v.Kind == JKind.Number ? (double?)v.Num : null; }
        static IEnumerable<JNode> Arr(JNode o, string k)
        {
            var v = Get(o, k);
            if (v == null) yield break;
            if (v.Kind == JKind.Array) { foreach (var it in v.Items) yield return it; }
            else if (v.Kind == JKind.Object) yield return v;
        }
        static string[] Strs(JNode o, string k)
        {
            var v = Get(o, k);
            if (v == null) return null;
            if (v.Kind == JKind.String) return new[] { v.Str };
            if (v.Kind != JKind.Array) return null;
            var list = new List<string>();
            foreach (var it in v.Items) if (it.Kind == JKind.String) list.Add(it.Str);
            return list.ToArray();
        }
    }

    /// <summary>Writes changes into the parts' files a moment after the last edit, and shows a short note on screen.</summary>
    internal sealed class Saver : MonoBehaviour
    {
        sealed class Pending
        {
            public string File;
            public readonly List<KeyValuePair<string[], string>> Changes = new List<KeyValuePair<string[], string>>();
            public readonly List<string> Labels = new List<string>();
            public float Due;
        }

        public ManualLogSource Log;
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string toast;
        bool toastError;
        float toastUntil;
        GUIStyle style;

        public void Queue(string file, string[] path, string json, string label)
        {
            Pending p;
            if (!pending.TryGetValue(file, out p)) { p = new Pending { File = file }; pending[file] = p; }
            p.Changes.RemoveAll(c => string.Join("\u0001", c.Key) == string.Join("\u0001", path));
            p.Changes.Add(new KeyValuePair<string[], string>(path, json));
            if (!p.Labels.Contains(label)) p.Labels.Add(label);
            p.Due = Time.realtimeSinceStartup + 0.8f;   // typing 12000 is one save, not five
        }

        void Update()
        {
            if (pending.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            List<string> ready = null;
            foreach (var kv in pending) if (kv.Value.Due <= now) { if (ready == null) ready = new List<string>(); ready.Add(kv.Key); }
            if (ready == null) return;
            foreach (var k in ready) { var p = pending[k]; pending.Remove(k); Write(p); }
        }

        void OnApplicationQuit()
        {
            foreach (var p in new List<Pending>(pending.Values)) Write(p);
            pending.Clear();
        }

        void Write(Pending p)
        {
            string name = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(p.File)) + "\\" + System.IO.Path.GetFileName(p.File);
            try
            {
                // Read the file again now, so an edit made in a text editor while the game runs is kept.
                string text = File.ReadAllText(p.File, Encoding.UTF8);
                string updated = text;
                foreach (var c in p.Changes) updated = JsoncDoc.SetRaw(updated, c.Key, c.Value);
                JsoncDoc.Parse(updated);   // never write a file WWC couldn't read
                if (updated == text) return;
                if (backedUp.Add(p.File)) File.Copy(p.File, p.File + ".bak", true);   // how the file was before this game's first change
                string tmp = p.File + ".tmp";
                File.WriteAllText(tmp, updated, new UTF8Encoding(false));
                File.Copy(tmp, p.File, true);
                File.Delete(tmp);
                foreach (var c in p.Changes) Log.LogInfo("WWC settings: " + name + " " + string.Join(".", c.Key) + " = " + c.Value);
                Show("Saved: " + string.Join(", ", p.Labels.ToArray()) + ".\nRestart SPT (server and game) to use it.", false);
            }
            catch (Exception ex)
            {
                Log.LogWarning("WWC settings: could not save " + name + ": " + ex.Message);
                Show("Couldn't save " + name + ": " + ex.Message, true);
            }
        }

        void Show(string text, bool error) { toast = text; toastError = error; toastUntil = Time.realtimeSinceStartup + 5f; }

        void OnGUI()
        {
            if (toast == null || Time.realtimeSinceStartup > toastUntil) return;
            GUI.depth = -1000;
            if (style == null) style = new GUIStyle(GUI.skin.box) { fontSize = 15, wordWrap = true, alignment = TextAnchor.MiddleCenter };
            style.normal.textColor = toastError ? new Color(1f, 0.65f, 0.6f) : Color.white;
            float w = Mathf.Min(760f, Screen.width - 40f);
            GUI.Box(new Rect((Screen.width - w) / 2f, Screen.height - 110f, w, 60f), toast, style);
        }
    }

    // The tag class the F12 menu (BepInEx ConfigurationManager) looks for by name on a setting's description; it reads these fields by
    // reflection, so names and types must match its own.
#pragma warning disable 0649
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? ShowRangeAsPercent;
        public bool? Browsable;
        public string Category;
        public object DefaultValue;
        public bool? HideDefaultButton;
        public bool? HideSettingName;
        public string Description;
        public string DispName;
        public int? Order;
        public bool? ReadOnly;
        public bool? IsAdvanced;
        public System.Func<object, string> ObjToStr;
        public System.Func<string, object> StrToObj;
    }
#pragma warning restore 0649
}
#endif
