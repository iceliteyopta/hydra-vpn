using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace Hydra
{
    public class ParsedLink
    {
        public string Type;
        public string Name;
        public string Server;
        public Dictionary<string, object> Outbound;
    }

    // Разбор ссылок vless:// vmess:// trojan:// ss:// hy2:// hysteria2:// hysteria:// tuic://
    // в outbound формата sing-box.
    public static class LinkParser
    {
        public static ParsedLink Parse(string link)
        {
            link = (link ?? "").Trim();
            int se = link.IndexOf("://", StringComparison.Ordinal);
            if (se <= 0) throw new FormatException(I18n.L("Не похоже на ссылку: ") + Short(link));
            string scheme = link.Substring(0, se).ToLowerInvariant();
            string rest = link.Substring(se + 3);

            string name = null;
            int h = rest.IndexOf('#');
            if (h >= 0) { name = Unesc(rest.Substring(h + 1)); rest = rest.Substring(0, h); }

            if (scheme == "vmess") return Vmess(rest, name);
            if (scheme == "ss") return Shadowsocks(rest, name);

            string query = "";
            int qi = rest.IndexOf('?');
            if (qi >= 0) { query = rest.Substring(qi + 1); rest = rest.Substring(0, qi); }
            rest = rest.TrimEnd('/');
            var q = ParseQuery(query);

            string user = null;
            int at = rest.LastIndexOf('@');
            if (at >= 0) { user = Unesc(rest.Substring(0, at)); rest = rest.Substring(at + 1); }

            string host, extraPorts; int port;
            SplitHostPort(rest, out host, out port, out extraPorts);

            Dictionary<string, object> ob;
            string type;
            switch (scheme)
            {
                case "vless":
                    {
                        type = "vless";
                        if (string.IsNullOrEmpty(user)) throw new FormatException(I18n.L("В ссылке vless нет UUID"));
                        ob = D.New("type", "vless", "server", host, "server_port", port, "uuid", user);
                        string flow = Get(q, "flow");
                        if (!Empty(flow)) ob["flow"] = flow;
                        string sec = (Get(q, "security") ?? "none").ToLowerInvariant();
                        if (sec == "tls" || sec == "reality" || sec == "xtls") ob["tls"] = Tls(q, host, sec == "reality", false);
                        AddTransport(ob, q, Get(q, "type") ?? "tcp");
                        ob["packet_encoding"] = "xudp";
                        break;
                    }
                case "trojan":
                    {
                        type = "trojan";
                        ob = D.New("type", "trojan", "server", host, "server_port", port, "password", user ?? "");
                        string sec = (Get(q, "security") ?? "tls").ToLowerInvariant();
                        if (sec != "none") ob["tls"] = Tls(q, host, sec == "reality", false);
                        AddTransport(ob, q, Get(q, "type") ?? "tcp");
                        break;
                    }
                case "hy2":
                case "hysteria2":
                    {
                        type = "hysteria2";
                        ob = D.New("type", "hysteria2", "server", host, "server_port", port, "password", user ?? Get(q, "auth") ?? "");
                        string mport = Get(q, "mport") ?? extraPorts;
                        if (!Empty(mport)) ob["server_ports"] = PortRanges(mport);
                        string obfs = Get(q, "obfs");
                        if (!Empty(obfs) && obfs != "none")
                            ob["obfs"] = D.New("type", obfs, "password", Get(q, "obfs-password", "obfs_password") ?? "");
                        AddMbps(ob, q);
                        ob["tls"] = Tls(q, host, false, true);
                        break;
                    }
                case "hysteria":
                    {
                        type = "hysteria";
                        string proto = (Get(q, "protocol") ?? "udp").ToLowerInvariant();
                        if (proto != "udp") throw new NotSupportedException(I18n.L("Hysteria v1: режим ") + proto + I18n.L(" не поддерживается sing-box (нужен udp)"));
                        ob = D.New("type", "hysteria", "server", host, "server_port", port);
                        ob["up_mbps"] = ToInt(Get(q, "upmbps", "up"), 50);
                        ob["down_mbps"] = ToInt(Get(q, "downmbps", "down"), 100);
                        string auth = Get(q, "auth") ?? user;
                        if (!Empty(auth)) ob["auth_str"] = auth;
                        string obfs = Get(q, "obfsParam", "obfs");
                        if (!Empty(obfs)) ob["obfs"] = obfs;
                        string mport = Get(q, "mport") ?? extraPorts;
                        if (!Empty(mport)) ob["server_ports"] = PortRanges(mport);
                        ob["tls"] = Tls(q, host, false, true);
                        break;
                    }
                case "tuic":
                    {
                        type = "tuic";
                        string uuid = user ?? "", pass = "";
                        int c = uuid.IndexOf(':');
                        if (c >= 0) { pass = uuid.Substring(c + 1); uuid = uuid.Substring(0, c); }
                        ob = D.New("type", "tuic", "server", host, "server_port", port, "uuid", uuid, "password", pass);
                        ob["congestion_control"] = Get(q, "congestion_control", "congestion") ?? "bbr";
                        string relay = Get(q, "udp_relay_mode");
                        if (!Empty(relay)) ob["udp_relay_mode"] = relay;
                        var tls = Tls(q, host, false, true);
                        if (!tls.ContainsKey("alpn")) tls["alpn"] = new List<object> { "h3" };
                        ob["tls"] = tls;
                        break;
                    }
                default:
                    throw new NotSupportedException(I18n.L("Протокол ") + scheme + I18n.L(":// не поддерживается"));
            }

            return new ParsedLink { Type = type, Name = Empty(name) ? type + " " + host : name, Server = host + ":" + port, Outbound = ob };
        }

        // ---- VMess (vmess://base64(json)) ----
        static ParsedLink Vmess(string rest, string name)
        {
            var o = Json.DeserializeObject(B64(rest)) as Dictionary<string, object>;
            if (o == null) throw new FormatException(I18n.L("Не удалось разобрать vmess-ссылку"));
            Func<string, string> g = k =>
            {
                object v;
                if (o.TryGetValue(k, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
                return null;
            };
            string host = g("add");
            int port = ToInt(g("port"), 443);
            var ob = D.New("type", "vmess", "server", host, "server_port", port, "uuid", g("id"),
                "security", Empty(g("scy")) ? "auto" : g("scy"), "alter_id", ToInt(g("aid"), 0));
            var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            q["host"] = g("host"); q["path"] = g("path"); q["serviceName"] = g("path");
            q["sni"] = g("sni"); q["alpn"] = g("alpn"); q["fp"] = g("fp"); q["headerType"] = g("type");
            if ((g("tls") ?? "").ToLowerInvariant() == "tls") ob["tls"] = Tls(q, host, false, false);
            AddTransport(ob, q, g("net") ?? "tcp");
            ob["packet_encoding"] = "xudp";
            string nm = Empty(name) ? g("ps") : name;
            return new ParsedLink { Type = "vmess", Name = Empty(nm) ? "vmess " + host : nm, Server = host + ":" + port, Outbound = ob };
        }

        // ---- Shadowsocks (SIP002 и старый формат) ----
        static ParsedLink Shadowsocks(string rest, string name)
        {
            string qs = "";
            int qi = rest.IndexOf('?');
            if (qi >= 0) { qs = rest.Substring(qi + 1); rest = rest.Substring(0, qi); }
            rest = rest.TrimEnd('/');
            var q = ParseQuery(qs);
            if (!Empty(Get(q, "plugin"))) throw new NotSupportedException(I18n.L("Плагины Shadowsocks (obfs/v2ray-plugin) не поддерживаются"));

            string userinfo, hp;
            int at = rest.LastIndexOf('@');
            if (at < 0)
            {
                string dec = B64(rest);
                at = dec.LastIndexOf('@');
                if (at < 0) throw new FormatException(I18n.L("Не удалось разобрать ss-ссылку"));
                userinfo = dec.Substring(0, at); hp = dec.Substring(at + 1);
            }
            else
            {
                userinfo = Unesc(rest.Substring(0, at)); hp = rest.Substring(at + 1);
                if (userinfo.IndexOf(':') < 0) userinfo = B64(userinfo);
            }
            int c = userinfo.IndexOf(':');
            if (c < 0) throw new FormatException(I18n.L("В ss-ссылке нет метода шифрования"));
            string host, extra; int port;
            SplitHostPort(hp, out host, out port, out extra);
            var ob = D.New("type", "shadowsocks", "server", host, "server_port", port,
                "method", userinfo.Substring(0, c), "password", userinfo.Substring(c + 1));
            return new ParsedLink { Type = "shadowsocks", Name = Empty(name) ? "ss " + host : name, Server = host + ":" + port, Outbound = ob };
        }

        // ---- общие куски ----
        static Dictionary<string, object> Tls(Dictionary<string, string> q, string host, bool reality, bool quic)
        {
            var t = D.New("enabled", true);
            string sni = Get(q, "sni", "peer", "servername");
            if (Empty(sni) && !IsIp(host)) sni = host;
            if (!Empty(sni)) t["server_name"] = sni;
            string ins = (Get(q, "allowInsecure", "insecure", "allow_insecure", "skip-cert-verify") ?? "").ToLowerInvariant();
            if (ins == "1" || ins == "true") t["insecure"] = true;
            string alpn = Get(q, "alpn");
            if (!Empty(alpn)) t["alpn"] = SplitList(alpn);
            if (!quic)
            {
                string fp = Get(q, "fp", "fingerprint");
                if (reality && Empty(fp)) fp = "chrome";
                if (!Empty(fp) && fp != "none") t["utls"] = D.New("enabled", true, "fingerprint", fp);
            }
            if (reality)
            {
                string pbk = Get(q, "pbk", "publicKey", "public-key");
                if (Empty(pbk)) throw new FormatException(I18n.L("Для REALITY нужен параметр pbk (публичный ключ)"));
                var r = D.New("enabled", true, "public_key", pbk);
                string sid = Get(q, "sid", "shortId", "short-id");
                if (!Empty(sid)) r["short_id"] = sid;
                t["reality"] = r;
            }
            return t;
        }

        static void AddTransport(Dictionary<string, object> ob, Dictionary<string, string> q, string type)
        {
            type = (type ?? "tcp").ToLowerInvariant();
            string path = Get(q, "path");
            string host = Get(q, "host");
            switch (type)
            {
                case "":
                case "tcp":
                case "raw":
                    if ((Get(q, "headerType") ?? "") == "http")
                    {
                        var ht = D.New("type", "http");
                        if (!Empty(host)) ht["host"] = SplitList(host);
                        if (!Empty(path)) ht["path"] = path;
                        ob["transport"] = ht;
                    }
                    return;
                case "ws":
                    {
                        var ws = D.New("type", "ws");
                        if (!Empty(path))
                        {
                            int i = path.IndexOf("?ed=", StringComparison.Ordinal);
                            if (i >= 0)
                            {
                                ws["max_early_data"] = ToInt(path.Substring(i + 4), 0);
                                ws["early_data_header_name"] = "Sec-WebSocket-Protocol";
                                path = path.Substring(0, i);
                            }
                            ws["path"] = path;
                        }
                        if (!Empty(host)) ws["headers"] = D.New("Host", host);
                        ob["transport"] = ws;
                        return;
                    }
                case "grpc":
                    ob["transport"] = D.New("type", "grpc", "service_name", Get(q, "serviceName", "service_name") ?? "");
                    return;
                case "httpupgrade":
                    {
                        var hu = D.New("type", "httpupgrade");
                        if (!Empty(host)) hu["host"] = host;
                        if (!Empty(path)) hu["path"] = path;
                        ob["transport"] = hu;
                        return;
                    }
                case "http":
                case "h2":
                    {
                        var ht = D.New("type", "http");
                        if (!Empty(host)) ht["host"] = SplitList(host);
                        if (!Empty(path)) ht["path"] = path;
                        ob["transport"] = ht;
                        return;
                    }
                case "quic":
                    ob["transport"] = D.New("type", "quic");
                    return;
                case "xhttp":
                case "splithttp":
                    throw new NotSupportedException(I18n.L("Транспорт xhttp не поддерживается ядром sing-box"));
                default:
                    throw new NotSupportedException(I18n.L("Неизвестный транспорт: ") + type);
            }
        }

        static void AddMbps(Dictionary<string, object> ob, Dictionary<string, string> q)
        {
            int up = ToInt(Get(q, "upmbps", "up"), 0), down = ToInt(Get(q, "downmbps", "down"), 0);
            if (up > 0) ob["up_mbps"] = up;
            if (down > 0) ob["down_mbps"] = down;
        }

        static List<object> PortRanges(string spec)
        {
            var l = new List<object>();
            foreach (var p in spec.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = p.Trim().Replace('-', ':');
                if (s.IndexOf(':') < 0) s = s + ":" + s;
                l.Add(s);
            }
            return l;
        }

        public static void SplitHostPort(string hp, out string host, out int port, out string extraPorts)
        {
            extraPorts = null;
            string portStr = null;
            hp = hp.Trim();
            if (hp.StartsWith("["))
            {
                int e = hp.IndexOf(']');
                if (e < 0) throw new FormatException(I18n.L("Некорректный адрес: ") + hp);
                host = hp.Substring(1, e - 1);
                if (e + 1 < hp.Length && hp[e + 1] == ':') portStr = hp.Substring(e + 2);
            }
            else
            {
                int c = hp.IndexOf(':');
                if (c >= 0 && hp.IndexOf(':', c + 1) < 0) { host = hp.Substring(0, c); portStr = hp.Substring(c + 1); }
                else host = hp;
            }
            port = 443;
            if (!Empty(portStr))
            {
                int comma = portStr.IndexOf(',');
                string first = comma >= 0 ? portStr.Substring(0, comma) : portStr;
                if (comma >= 0) extraPorts = portStr.Substring(comma + 1);
                if (first.IndexOf('-') >= 0) { extraPorts = Empty(extraPorts) ? first : first + "," + extraPorts; first = first.Split('-')[0]; }
                port = ToInt(first, 443);
            }
            if (Empty(host)) throw new FormatException(I18n.L("В ссылке нет адреса сервера"));
        }

        public static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in (query ?? "").Split('&'))
            {
                if (part.Length == 0) continue;
                int e = part.IndexOf('=');
                string k = Unesc(e < 0 ? part : part.Substring(0, e));
                string v = e < 0 ? "" : Unesc(part.Substring(e + 1));
                d[k] = v;
            }
            return d;
        }

        static string Get(Dictionary<string, string> q, params string[] keys)
        {
            foreach (var k in keys)
            {
                string v;
                if (q.TryGetValue(k, out v) && !Empty(v)) return v;
            }
            return null;
        }

        static List<object> SplitList(string s)
        {
            var l = new List<object>();
            foreach (var p in s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) l.Add(p.Trim());
            return l;
        }

        public static string B64(string s)
        {
            s = s.Trim().Replace('-', '+').Replace('_', '/').Replace("\r", "").Replace("\n", "");
            int pad = s.Length % 4;
            if (pad == 2) s += "=="; else if (pad == 3) s += "=";
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }

        public static bool TryB64(string s, out string result)
        {
            result = null;
            try { result = B64(s); return true; } catch { return false; }
        }

        static string Unesc(string s)
        {
            try { return Uri.UnescapeDataString(s); } catch { return s; }
        }

        static int ToInt(string s, int def)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        static bool IsIp(string h) { IPAddress a; return IPAddress.TryParse(h, out a); }
        static bool Empty(string s) { return string.IsNullOrEmpty(s); }
        static string Short(string s) { return s.Length > 40 ? s.Substring(0, 40) + "…" : s; }
    }
}
