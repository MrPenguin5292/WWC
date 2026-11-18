#if WWC_FORGE
// Jsonc.cs - reads and edits the commented JSON files the mods use for settings, without losing a single note.
// Used by WWC's F12 settings menu (ForgeSettings.cs). Written in C# 5 (no string interpolation, no ?. or nameof) so build scripts
// can compile it too.
//
// What it understands: // and /* */ comments, a trailing comma after the last item, a UTF-8 byte-order mark.
// What it keeps: everything. An edit replaces only the characters of the one value it changes; a new key is
// written next to its siblings with their indentation and line ending.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WWC.Client.Settings
{
    public enum JKind { Object, Array, String, Number, True, False, Null }

    public sealed class JProp
    {
        public string Key;
        public int KeyStart;      // index of the key's opening quote
        public JNode Value;
        public string Comment;    // the notes right above the key and after its value on the same line
        public int CommaEnd;      // index just past the comma that follows the value, or -1 when there is none
    }

    public sealed class JNode
    {
        public JKind Kind;
        public int Start;         // first character of the value
        public int End;           // one past its last character
        public string Str;        // unescaped text of a string
        public double Num;
        public bool IsInteger;    // a number written without a dot or exponent
        public List<JProp> Props; // object members in file order
        public List<JNode> Items; // array items

        public JProp Find(string key)
        {
            if (Props == null) return null;
            for (int i = 0; i < Props.Count; i++) if (Props[i].Key == key) return Props[i];
            return null;
        }

        public bool IsScalar { get { return Kind != JKind.Object && Kind != JKind.Array; } }
    }

    public sealed class JsoncException : Exception
    {
        public int Line;
        public JsoncException(string message, int line) : base(message + " (line " + line + ")") { Line = line; }
    }

    public sealed class JsoncDoc
    {
        public string Text;
        public JNode Root;
        public bool HadBom;

        const char Bom = (char)0xFEFF;
        string t;
        int p;
        int lastTokenEnd;

        sealed class Note { public string Text; public bool SameLine; }

        public static JsoncDoc Parse(string text)
        {
            var d = new JsoncDoc();
            if (text == null) throw new JsoncException("no text", 0);
            if (text.Length > 0 && text[0] == Bom) { d.HadBom = true; text = text.Substring(1); }
            d.Text = text; d.t = text; d.p = 0; d.lastTokenEnd = 0;
            d.Skip(null);
            d.Root = d.Value();
            d.Skip(null);
            if (d.p < d.t.Length) throw new JsoncException("unexpected text after the end", d.LineAt(d.p));
            return d;
        }

        public int LineAt(int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < t.Length; i++) if (t[i] == '\n') line++;
            return line;
        }

        // Skips spaces and comments. Each comment is reported with whether it sits on the same line as the token before it.
        void Skip(List<Note> notes)
        {
            while (p < t.Length)
            {
                char c = t[p];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { p++; continue; }
                if (c == '/' && p + 1 < t.Length && (t[p + 1] == '/' || t[p + 1] == '*'))
                {
                    int start = p;
                    string body;
                    if (t[p + 1] == '/')
                    {
                        int e = t.IndexOf('\n', p);
                        if (e < 0) e = t.Length;
                        body = t.Substring(p + 2, e - p - 2).Trim();
                        p = e;
                    }
                    else
                    {
                        int e = t.IndexOf("*/", p + 2, StringComparison.Ordinal);
                        if (e < 0) throw new JsoncException("a /* note is never closed", LineAt(p));
                        body = t.Substring(p + 2, e - p - 2).Trim();
                        p = e + 2;
                    }
                    if (notes != null)
                    {
                        bool same = t.IndexOf('\n', lastTokenEnd, start - lastTokenEnd) < 0;
                        var n = new Note(); n.Text = body; n.SameLine = same; notes.Add(n);
                    }
                    continue;
                }
                break;
            }
        }

        JNode Value()
        {
            if (p >= t.Length) throw new JsoncException("the file ends where a value should be", LineAt(p));
            char c = t[p];
            if (c == '{') return ObjectValue();
            if (c == '[') return ArrayValue();
            if (c == '"') { var n = new JNode(); n.Kind = JKind.String; n.Start = p; n.Str = StringToken(); n.End = p; lastTokenEnd = p; return n; }
            if (c == '-' || (c >= '0' && c <= '9')) return NumberValue();
            if (Word("true")) { var n = new JNode(); n.Kind = JKind.True; n.Start = p - 4; n.End = p; lastTokenEnd = p; return n; }
            if (Word("false")) { var n = new JNode(); n.Kind = JKind.False; n.Start = p - 5; n.End = p; lastTokenEnd = p; return n; }
            if (Word("null")) { var n = new JNode(); n.Kind = JKind.Null; n.Start = p - 4; n.End = p; lastTokenEnd = p; return n; }
            throw new JsoncException("unexpected character '" + c + "'", LineAt(p));
        }

        bool Word(string w)
        {
            if (string.CompareOrdinal(t, p, w, 0, w.Length) != 0) return false;
            int e = p + w.Length;
            if (e < t.Length && (char.IsLetterOrDigit(t[e]) || t[e] == '_')) return false;
            p = e;
            return true;
        }

        JNode NumberValue()
        {
            int s = p;
            if (t[p] == '-') p++;
            while (p < t.Length && char.IsDigit(t[p])) p++;
            bool integer = true;
            if (p < t.Length && t[p] == '.') { integer = false; p++; while (p < t.Length && char.IsDigit(t[p])) p++; }
            if (p < t.Length && (t[p] == 'e' || t[p] == 'E'))
            {
                integer = false; p++;
                if (p < t.Length && (t[p] == '+' || t[p] == '-')) p++;
                while (p < t.Length && char.IsDigit(t[p])) p++;
            }
            string raw = t.Substring(s, p - s);
            double v;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) throw new JsoncException("'" + raw + "' is not a number", LineAt(s));
            var n = new JNode(); n.Kind = JKind.Number; n.Start = s; n.End = p; n.Num = v; n.IsInteger = integer;
            lastTokenEnd = p;
            return n;
        }

        string StringToken()
        {
            int s = p;
            p++;
            var sb = new StringBuilder();
            while (true)
            {
                if (p >= t.Length) throw new JsoncException("a string is never closed", LineAt(s));
                char c = t[p++];
                if (c == '"') break;
                if (c == '\n') throw new JsoncException("a string runs past the end of its line", LineAt(s));
                if (c != '\\') { sb.Append(c); continue; }
                if (p >= t.Length) throw new JsoncException("a string is never closed", LineAt(s));
                char e = t[p++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (p + 4 > t.Length) throw new JsoncException("a \\u escape is cut short", LineAt(s));
                        sb.Append((char)int.Parse(t.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        p += 4; break;
                    default: throw new JsoncException("unknown escape \\" + e, LineAt(s));
                }
            }
            return sb.ToString();
        }

        JNode ObjectValue()
        {
            var node = new JNode(); node.Kind = JKind.Object; node.Start = p; node.Props = new List<JProp>();
            p++; lastTokenEnd = p;
            var pending = new List<Note>();
            JProp prev = null;
            while (true)
            {
                var notes = new List<Note>();
                Skip(notes);
                foreach (var n in notes)
                {
                    if (n.SameLine && prev != null) prev.Comment = Join(prev.Comment, n.Text);
                    else pending.Add(n);
                }
                if (p >= t.Length) throw new JsoncException("an object is never closed", LineAt(node.Start));
                if (t[p] == '}') { p++; node.End = p; lastTokenEnd = p; return node; }
                if (t[p] != '"') throw new JsoncException("expected a \"key\" here", LineAt(p));
                var prop = new JProp(); prop.KeyStart = p; prop.CommaEnd = -1;
                prop.Key = StringToken(); lastTokenEnd = p;
                Skip(null);
                if (p >= t.Length || t[p] != ':') throw new JsoncException("expected ':' after \"" + prop.Key + "\"", LineAt(p));
                p++; lastTokenEnd = p;
                Skip(null);
                prop.Value = Value();
                string lead = null;
                foreach (var n in pending) lead = Join(lead, n.Text);
                pending.Clear();
                prop.Comment = lead;
                node.Props.Add(prop);
                prev = prop;
                notes = new List<Note>();
                Skip(notes);
                foreach (var n in notes) { if (n.SameLine) prop.Comment = Join(prop.Comment, n.Text); else pending.Add(n); }
                if (p < t.Length && t[p] == ',') { p++; prop.CommaEnd = p; lastTokenEnd = p; continue; }
                if (p < t.Length && t[p] == '}') continue;
                throw new JsoncException("expected ',' or '}' after \"" + prop.Key + "\"", LineAt(p));
            }
        }

        JNode ArrayValue()
        {
            var node = new JNode(); node.Kind = JKind.Array; node.Start = p; node.Items = new List<JNode>();
            p++; lastTokenEnd = p;
            while (true)
            {
                Skip(null);
                if (p >= t.Length) throw new JsoncException("a list is never closed", LineAt(node.Start));
                if (t[p] == ']') { p++; node.End = p; lastTokenEnd = p; return node; }
                node.Items.Add(Value());
                Skip(null);
                if (p < t.Length && t[p] == ',') { p++; lastTokenEnd = p; continue; }
                if (p < t.Length && t[p] == ']') continue;
                throw new JsoncException("expected ',' or ']' in a list", LineAt(p));
            }
        }

        static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(b)) return a;
            if (string.IsNullOrEmpty(a)) return b;
            return a + " " + b;
        }

        // ---------------------------------------------------------------- lookups

        public JNode At(IList<string> path)
        {
            JNode n = Root;
            foreach (var k in path)
            {
                if (n == null || n.Kind != JKind.Object) return null;
                var pr = n.Find(k);
                if (pr == null) return null;
                n = pr.Value;
            }
            return n;
        }

        public string RawOf(JNode n) { return Text.Substring(n.Start, n.End - n.Start); }

        public string Newline { get { return Text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n"; } }

        // ---------------------------------------------------------------- writing values

        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        public static string Number(double v, bool integer)
        {
            if (integer) return Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
            string s = v.ToString("R", CultureInfo.InvariantCulture);
            return s;
        }

        public static string Bool(bool v) { return v ? "true" : "false"; }

        // Sets the value at path to the given JSON text and returns the new file text. Keys that are missing are added
        // (with any missing parent objects) next to their siblings. The rest of the file is left exactly as it was.
        public static string SetRaw(string text, IList<string> path, string json)
        {
            var doc = Parse(text);
            string result = doc.SetRawIn(path, json);
            return doc.HadBom ? new string(Bom, 1) + result : result;
        }

        string SetRawIn(IList<string> path, string json)
        {
            if (path == null || path.Count == 0) throw new ArgumentException("empty path");
            if (Root.Kind != JKind.Object) throw new JsoncException("the file is not an object", 1);
            JNode parent = Root;
            for (int i = 0; i < path.Count; i++)
            {
                var pr = parent.Find(path[i]);
                if (pr == null)
                {
                    // Build the rest of the path as nested objects and add it to this parent.
                    string value = json;
                    for (int j = path.Count - 1; j > i; j--) value = "{ " + Quote(path[j]) + ": " + value + " }";
                    return InsertProp(parent, path[i], value);
                }
                if (i == path.Count - 1)
                    return Text.Substring(0, pr.Value.Start) + json + Text.Substring(pr.Value.End);
                if (pr.Value.Kind != JKind.Object)
                {
                    string value = json;
                    for (int j = path.Count - 1; j > i; j--) value = "{ " + Quote(path[j]) + ": " + value + " }";
                    return Text.Substring(0, pr.Value.Start) + value + Text.Substring(pr.Value.End);
                }
                parent = pr.Value;
            }
            return Text;
        }

        string InsertProp(JNode obj, string key, string json)
        {
            string nl = Newline;
            string member = Quote(key) + ": " + json;
            int close = obj.End - 1; // the '}'
            if (obj.Props.Count == 0)
            {
                bool multi = Text.IndexOf('\n', obj.Start, obj.End - obj.Start) >= 0;
                if (!multi) return Text.Substring(0, obj.Start + 1) + " " + member + " " + Text.Substring(close);
                // A multi-line empty object (it may hold commented-out examples): the new member goes first, one step in.
                return Text.Substring(0, obj.Start + 1) + nl + IndentOfLine(obj.Start) + "  " + member + Text.Substring(obj.Start + 1);
            }
            var last = obj.Props[obj.Props.Count - 1];
            bool oneLine = Text.IndexOf('\n', obj.Start, obj.End - obj.Start) < 0;
            if (oneLine)
            {
                string before = Text.Substring(0, last.Value.End);
                string after = Text.Substring(last.CommaEnd >= 0 ? last.CommaEnd : last.Value.End);
                return before + ", " + member + (last.CommaEnd >= 0 ? "," : "") + after;
            }
            string indent = IndentOfLine(last.KeyStart);
            // Put the new member on its own line after the last one (after its trailing note, if any).
            int insertAt = last.CommaEnd >= 0 ? last.CommaEnd : last.Value.End;
            int lineEnd = Text.IndexOf('\n', insertAt);
            if (lineEnd < 0 || lineEnd > close) lineEnd = -1;
            if (last.CommaEnd >= 0)
            {
                if (lineEnd < 0) return Text.Substring(0, insertAt) + nl + indent + member + "," + Text.Substring(insertAt);
                int cut = Text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
                return Text.Substring(0, cut) + nl + indent + member + "," + Text.Substring(cut);
            }
            string withComma = Text.Substring(0, last.Value.End) + "," + Text.Substring(last.Value.End);
            int shift = 1;
            int le = lineEnd < 0 ? -1 : lineEnd + shift;
            if (le < 0) return withComma.Substring(0, last.Value.End + 1) + nl + indent + member + withComma.Substring(last.Value.End + 1);
            int cut2 = withComma[le - 1] == '\r' ? le - 1 : le;
            return withComma.Substring(0, cut2) + nl + indent + member + withComma.Substring(cut2);
        }

        string IndentOfLine(int index)
        {
            int ls = Text.LastIndexOf('\n', Math.Max(0, index - 1));
            ls = ls < 0 ? 0 : ls + 1;
            int e = ls;
            while (e < Text.Length && (Text[e] == ' ' || Text[e] == '\t')) e++;
            return Text.Substring(ls, e - ls);
        }

        // ---------------------------------------------------------------- comparing

        // A form of a value that ignores spacing, notes, key order and number spelling (1 and 1.0 are the same).
        public string Canon(JNode n)
        {
            var sb = new StringBuilder();
            CanonInto(n, sb);
            return sb.ToString();
        }

        void CanonInto(JNode n, StringBuilder sb)
        {
            switch (n.Kind)
            {
                case JKind.Object:
                    var keys = new List<JProp>(n.Props);
                    keys.Sort(delegate(JProp a, JProp b) { return string.CompareOrdinal(a.Key, b.Key); });
                    sb.Append('{');
                    for (int i = 0; i < keys.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Quote(keys[i].Key)).Append(':'); CanonInto(keys[i].Value, sb); }
                    sb.Append('}');
                    break;
                case JKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < n.Items.Count; i++) { if (i > 0) sb.Append(','); CanonInto(n.Items[i], sb); }
                    sb.Append(']');
                    break;
                case JKind.String: sb.Append(Quote(n.Str)); break;
                case JKind.Number: sb.Append(n.Num.ToString("R", CultureInfo.InvariantCulture)); break;
                case JKind.True: sb.Append("true"); break;
                case JKind.False: sb.Append("false"); break;
                default: sb.Append("null"); break;
            }
        }
    }

    // Three-way settings merge for updates: the new version's file (with its notes and any new settings) gets the player's values.
    public static class SettingsMerge
    {
        // mine: the player's current file. fresh: the new version's default file. bases: every default file earlier versions
        // shipped. A value the player never changed (it equals what some earlier version shipped) takes the new default;
        // anything else keeps the player's value. Keys the player added that no version knows are carried over.
        // Returns the merged text, or null when the player's file can't be read (then leave it alone).
        public static string Merge(string mine, string fresh, IList<string> bases, List<string> report)
        {
            JsoncDoc m;
            try { m = JsoncDoc.Parse(mine); }
            catch (JsoncException ex) { report.Add("your file has a mistake (" + ex.Message + "), so it was left exactly as it was"); return null; }
            var baseDocs = new List<JsoncDoc>();
            if (bases != null) foreach (var b in bases) { try { baseDocs.Add(JsoncDoc.Parse(b)); } catch (JsoncException) { } }
            var freshDoc = JsoncDoc.Parse(fresh);
            if (m.Root.Kind != JKind.Object || freshDoc.Root.Kind != JKind.Object)
            {
                // A file that is one list (items.json): it is either untouched (take the new one) or the player's (keep it whole).
                string mc = m.Canon(m.Root);
                if (mc == freshDoc.Canon(freshDoc.Root)) return mine;
                foreach (var b in baseDocs) if (b.Canon(b.Root) == mc) { report.Add("took the new file (yours was an earlier default)"); return fresh; }
                report.Add("kept your whole file (it is one list, and you changed it)");
                return mine;
            }
            string text = fresh;
            var path = new List<string>();
            text = Walk(m, m.Root, JsoncDoc.Parse(fresh), baseDocs, path, text, report);
            return text;
        }

        static string Walk(JsoncDoc m, JNode mineNode, JsoncDoc freshDoc, List<JsoncDoc> bases, List<string> path, string text, List<string> report)
        {
            var freshNode = JsoncDoc.Parse(text).At(path);
            if (freshNode == null || freshNode.Kind != JKind.Object || mineNode.Kind != JKind.Object) return text;
            foreach (var mp in mineNode.Props)
            {
                path.Add(mp.Key);
                var cur = JsoncDoc.Parse(text);
                var fNode = cur.At(path);
                string where = string.Join(".", path.ToArray());
                if (fNode == null)
                {
                    bool known = false;
                    foreach (var b in bases) if (b.At(path) != null) { known = true; break; }
                    if (!known) { text = JsoncDoc.SetRaw(text, path, m.RawOf(mp.Value)); report.Add("kept your extra setting " + where); }
                    // A key an earlier version had and this one dropped is left out on purpose.
                }
                else if (fNode.Kind == JKind.Object && mp.Value.Kind == JKind.Object)
                {
                    text = Walk(m, mp.Value, null, bases, path, text, report);
                }
                else
                {
                    string mineCanon = m.Canon(mp.Value);
                    if (mineCanon != cur.Canon(fNode))
                    {
                        bool untouched = false;
                        foreach (var b in bases) { var bn = b.At(path); if (bn != null && b.Canon(bn) == mineCanon) { untouched = true; break; } }
                        if (!untouched) { text = JsoncDoc.SetRaw(text, path, m.RawOf(mp.Value)); report.Add("kept your value for " + where); }
                        else report.Add("took the new default for " + where);
                    }
                }
                path.RemoveAt(path.Count - 1);
            }
            return text;
        }

        // Settings the new file has and the player's file doesn't (dotted paths), so a report can name them.
        public static List<string> NewKeys(string mine, string merged)
        {
            var list = new List<string>();
            try { Missing(JsoncDoc.Parse(mine).Root, JsoncDoc.Parse(merged).Root, "", list); }
            catch (JsoncException) { }
            return list;
        }

        static void Missing(JNode mine, JNode merged, string prefix, List<string> list)
        {
            if (merged.Kind != JKind.Object) return;
            foreach (var p in merged.Props)
            {
                if (p.Key.StartsWith("_")) continue;
                var mp = mine != null && mine.Kind == JKind.Object ? mine.Find(p.Key) : null;
                if (mp == null) { list.Add(prefix + p.Key); continue; }
                if (p.Value.Kind == JKind.Object) Missing(mp.Value, p.Value, prefix + p.Key + ".", list);
            }
        }

        // True when both files hold the same settings (ignoring notes and spacing).
        public static bool SameSettings(string a, string b)
        {
            try { var x = JsoncDoc.Parse(a); var y = JsoncDoc.Parse(b); return x.Canon(x.Root) == y.Canon(y.Root); }
            catch (JsoncException) { return false; }
        }
    }
}
#endif
