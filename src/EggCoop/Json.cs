using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EggCoop
{
    // Minimal JSON reader for Twitch replies (Unity-free, checked by src/SelfTest). Unity's JsonUtility leaves nested
    // objects empty for classes from a plugin assembly loaded at runtime, and Twitch's messages are all nested.
    // Objects become Dictionary<string, object>, arrays List<object>, numbers double.
    public static class Json
    {
        public static object Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = 0;
            try { return Value(s, ref i); } catch (Exception) { return null; }
        }

        // Path lookup: Get(o, "payload", "event", "user_name"); a number in the path indexes an array.
        public static object Get(object o, params object[] path)
        {
            foreach (var p in path)
            {
                if (p is string k && o is Dictionary<string, object> d) o = d.TryGetValue(k, out var v) ? v : null;
                else if (p is int n && o is List<object> l) o = n >= 0 && n < l.Count ? l[n] : null;
                else return null;
            }
            return o;
        }
        public static string Str(object o, params object[] path) => Get(o, path) is string s ? s : null;
        public static int Int(object o, params object[] path) => Get(o, path) is double d ? (int)d : 0;
        public static bool Bool(object o, params object[] path) => Get(o, path) is bool b && b;
        public static List<object> List(object o, params object[] path) => Get(o, path) as List<object> ?? new List<object>();

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = Text(s, ref i);
                    Ws(s, ref i); i++;   // ':'
                    d[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (s[i++] == '}') return d;   // else ','
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (s[i++] == ']') return l;
                }
            }
            if (c == '"') return Text(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        static string Text(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;   // opening quote
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;   // \" \\ \/
                }
            }
            i++;
            return sb.ToString();
        }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
    }
}
