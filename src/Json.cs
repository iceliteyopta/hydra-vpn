using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace Hydra
{
    static class Json
    {
        static JavaScriptSerializer S()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 256;
            return s;
        }

        public static string Serialize(object o) { return Pretty(S().Serialize(o)); }
        public static T Deserialize<T>(string s) { return S().Deserialize<T>(s); }
        public static object DeserializeObject(string s) { return S().DeserializeObject(s); }

        public static string Pretty(string json)
        {
            var sb = new StringBuilder(json.Length * 2);
            int ind = 0;
            bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); }
                    else if (c == '"') inStr = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inStr = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        sb.Append(c);
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(json[++i]); break; }
                        ind++; NewLine(sb, ind); break;
                    case '}':
                    case ']':
                        ind--; NewLine(sb, ind); sb.Append(c); break;
                    case ',': sb.Append(c); NewLine(sb, ind); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        static void NewLine(StringBuilder sb, int ind)
        {
            sb.Append('\n');
            sb.Append(' ', Math.Max(0, ind) * 2);
        }
    }

    static class D
    {
        public static Dictionary<string, object> New(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2)
                if (kv[i + 1] != null) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }
}
