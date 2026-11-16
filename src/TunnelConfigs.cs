using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Hydra
{
    // Конфиг WireGuard / AmneziaWG (.conf)
    public class WgConf
    {
        public Dictionary<string, string> Iface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<Dictionary<string, string>> Peers = new List<Dictionary<string, string>>();

        // Параметры обфускации AmneziaWG (1.x и 2.0)
        public static readonly string[] AwgKeys = { "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4", "I1", "I2", "I3", "I4", "I5", "J1", "J2", "J3", "Itime" };

        public static WgConf Parse(string text)
        {
            var c = new WgConf();
            Dictionary<string, string> cur = null;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int hash = line.IndexOf('#');
                if (hash == 0 || line.Length == 0) continue;
                if (line.StartsWith("["))
                {
                    string sec = line.Trim('[', ']').Trim().ToLowerInvariant();
                    if (sec == "interface") cur = c.Iface;
                    else if (sec == "peer") { cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); c.Peers.Add(cur); }
                    else cur = null;
                    continue;
                }
                if (cur == null) continue;
                int e = line.IndexOf('=');
                if (e <= 0) continue;
                string k = line.Substring(0, e).Trim();
                string v = line.Substring(e + 1).Trim();
                string old;
                if (cur.TryGetValue(k, out old) && (k.Equals("Address", StringComparison.OrdinalIgnoreCase) || k.Equals("AllowedIPs", StringComparison.OrdinalIgnoreCase) || k.Equals("DNS", StringComparison.OrdinalIgnoreCase)))
                    v = old + "," + v;
                cur[k] = v;
            }
            if (!c.Iface.ContainsKey("PrivateKey")) throw new FormatException(I18n.L("В конфиге нет [Interface] PrivateKey"));
            if (c.Peers.Count == 0) throw new FormatException(I18n.L("В конфиге нет секции [Peer]"));
            return c;
        }

        public bool IsAmnezia()
        {
            foreach (var k in AwgKeys)
            {
                string v;
                if (!Iface.TryGetValue(k, out v)) continue;
                // H1..H4 = 1..4 и нули в остальных — это обычный WireGuard
                if (k.Length == 2 && k[0] == 'H' && v == k.Substring(1)) continue;
                if (v == "0" || v == "") continue;
                return true;
            }
            return false;
        }

        public static List<string> SplitList(string s)
        {
            var l = new List<string>();
            if (string.IsNullOrEmpty(s)) return l;
            foreach (var p in s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (p.Trim().Length > 0) l.Add(p.Trim());
            return l;
        }

        public List<string> Addresses() { return SplitList(Get(Iface, "Address")); }
        public List<string> Dns() { return SplitList(Get(Iface, "DNS")); }

        public int Mtu()
        {
            int m;
            return int.TryParse(Get(Iface, "MTU"), out m) && m > 0 ? m : 1420;
        }

        public static string Get(Dictionary<string, string> d, string k)
        {
            string v;
            return d.TryGetValue(k, out v) ? v : null;
        }

        public void Endpoint(int peer, out string host, out int port)
        {
            string ep = Get(Peers[peer], "Endpoint");
            if (string.IsNullOrEmpty(ep)) throw new FormatException(I18n.L("У [Peer] нет Endpoint"));
            string extra;
            LinkParser.SplitHostPort(ep, out host, out port, out extra);
        }

        public static string KeyHex(string b64)
        {
            var b = Convert.FromBase64String(b64.Trim());
            if (b.Length != 32) throw new FormatException(I18n.L("Неверная длина ключа WireGuard"));
            var sb = new StringBuilder(64);
            foreach (var x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    // Работа с .ovpn: копирование вместе с файлами ключей, очистка опций маршрутизации
    public static class OvpnConf
    {
        static readonly string[] FileDirectives = { "ca", "cert", "key", "tls-auth", "tls-crypt", "tls-crypt-v2", "pkcs12", "crl-verify", "extra-certs", "secret", "dh", "http-proxy-user-pass" };

        // Опции, которые убираем: маршрутизацией и DNS управляет sing-box
        static readonly string[] DropDirectives = {
            "redirect-gateway", "redirect-private", "block-outside-dns", "dhcp-option", "dns", "route", "route-ipv6",
            "dev-node", "windows-driver", "management", "management-hold", "management-query-passwords",
            "log", "log-append", "daemon", "service", "register-dns", "route-metric", "route-delay", "route-method", "route-nopull",
            "up", "down", "script-security", "route-up", "route-pre-down", "ipchange", "user", "group"
        };

        public static IEnumerable<string[]> Directives(string text)
        {
            string block = null;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (block != null)
                {
                    if (line.Equals("</" + block + ">", StringComparison.OrdinalIgnoreCase)) block = null;
                    continue;
                }
                if (line.StartsWith("<") && line.EndsWith(">") && !line.StartsWith("</"))
                {
                    string tag = line.Trim('<', '>');
                    if (tag != "connection") block = tag; // remote внутри <connection> тоже нужны
                    continue;
                }
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line.StartsWith("</")) continue;
                yield return Tokenize(line);
            }
        }

        static string[] Tokenize(string line)
        {
            var l = new List<string>();
            var sb = new StringBuilder();
            bool q = false;
            foreach (char ch in line)
            {
                if (ch == '"') { q = !q; continue; }
                if (!q && char.IsWhiteSpace(ch)) { if (sb.Length > 0) { l.Add(sb.ToString()); sb.Clear(); } continue; }
                sb.Append(ch);
            }
            if (sb.Length > 0) l.Add(sb.ToString());
            return l.ToArray();
        }

        // Достаёт содержимое inline-блоков <ca>…</ca>, <tls-crypt>…</tls-crypt> и т.п.
        public static Dictionary<string, string> InlineBlocks(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string block = null;
            var sb = new StringBuilder();
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                string t = line.Trim();
                if (block != null)
                {
                    if (t.Equals("</" + block + ">", StringComparison.OrdinalIgnoreCase)) { d[block] = sb.ToString().Trim() + "\n"; block = null; sb.Clear(); }
                    else sb.Append(line).Append('\n');
                    continue;
                }
                if (t.StartsWith("<") && t.EndsWith(">") && !t.StartsWith("</"))
                {
                    string tag = t.Trim('<', '>');
                    if (tag != "connection") block = tag;
                }
            }
            return d;
        }

        // Переводит .ovpn в endpoint "openvpn-client" встроенного OpenVPN sing-box (1.14+)
        public static Dictionary<string, object> ToSingBoxEndpoint(string text, string dir, string tag, string user, string pass)
        {
            var inl = InlineBlocks(text);
            var ep = D.New("type", "openvpn-client", "tag", tag);
            var tls = new Dictionary<string, object>();
            var servers = new List<object>();
            string network = "udp", defPort = "1194", keyDirection = null;
            object[] wrap = null; // {type, directiveArgs}

            Func<string, string, string> content = (directive, arg) =>
            {
                if (arg == null || arg == "[inline]")
                {
                    string v;
                    if (inl.TryGetValue(directive, out v)) return v;
                    throw new Exception(I18n.L("нет содержимого для ") + directive);
                }
                string f = Path.IsPathRooted(arg) ? arg : Path.Combine(dir, arg);
                if (!File.Exists(f)) throw new Exception(I18n.L("не найден файл ") + arg);
                return File.ReadAllText(f);
            };

            var remotes = new List<string[]>();
            foreach (var d in Directives(text))
            {
                string k = d[0].ToLowerInvariant();
                string a1 = d.Length > 1 ? d[1] : null, a2 = d.Length > 2 ? d[2] : null;
                switch (k)
                {
                    case "remote": if (a1 != null) remotes.Add(d); break;
                    case "proto": network = Net(a1); break;
                    case "port": case "rport": if (a1 != null) defPort = a1; break;
                    case "remote-random": ep["remote_random"] = true; break;
                    case "dev":
                        if (a1 != null && a1.StartsWith("tap", StringComparison.OrdinalIgnoreCase))
                            throw new Exception(I18n.L("режим dev tap не поддерживается встроенным OpenVPN — выберите драйвер dco/wintun/tap"));
                        break;
                    case "ca": tls["certificate"] = content("ca", a1); break;
                    case "cert": tls["client_certificate"] = content("cert", a1); break;
                    case "key": tls["client_key"] = content("key", a1); break;
                    case "pkcs12": throw new Exception(I18n.L("PKCS#12 (pkcs12) не поддерживается встроенным OpenVPN — выберите драйвер dco/wintun/tap"));
                    case "tls-auth": wrap = new object[] { "tls_auth", content("tls-auth", a1), a2 }; break;
                    case "tls-crypt": wrap = new object[] { "tls_crypt", content("tls-crypt", a1), null }; break;
                    case "tls-crypt-v2": wrap = new object[] { "tls_crypt_v2", content("tls-crypt-v2", a1), null }; break;
                    case "key-direction": keyDirection = a1; break;
                    case "secret":
                        ep["mode"] = "static_key";
                        ep["static_key"] = content("secret", a1);
                        if (a2 != null) keyDirection = a2;
                        break;
                    case "verify-x509-name":
                        if (a1 != null) tls["server_name"] = a1;
                        if (a2 != null) tls["server_name_type"] = a2;
                        break;
                    case "remote-cert-tls": if (a1 != null) tls["remote_certificate_tls"] = a1; break;
                    case "tls-version-min": if (a1 != null) tls["version_min"] = a1; break;
                    case "tls-cipher": if (a1 != null) tls["cipher"] = a1; break;
                    case "cipher": if (a1 != null) ep["cipher"] = a1; break;
                    case "data-ciphers":
                    case "ncp-ciphers":
                        if (a1 != null) { var l = new List<object>(); foreach (var c in a1.Split(':')) if (c.Length > 0) l.Add(c); ep["data_ciphers"] = l; }
                        break;
                    case "data-ciphers-fallback": if (a1 != null) ep["data_ciphers_fallback"] = a1; break;
                    case "auth": if (a1 != null) ep["auth"] = a1; break;
                    case "comp-lzo": ep["compression_lzo"] = a1 ?? "adaptive"; break;
                    case "compress": ep["compression"] = a1 ?? "stub"; break;
                    case "allow-compression": if (a1 != null) ep["allow_compression"] = a1; break;
                    case "tun-mtu": { int m; if (int.TryParse(a1, out m)) ep["mtu"] = m; break; }
                    case "fragment": { int m; if (int.TryParse(a1, out m)) ep["fragment"] = m; break; }
                    case "mssfix": { int m; if (int.TryParse(a1, out m)) ep["mss_fix"] = m; break; }
                }
            }

            if (!tls.ContainsKey("certificate") && inl.ContainsKey("ca")) tls["certificate"] = inl["ca"];
            if (!tls.ContainsKey("client_certificate") && inl.ContainsKey("cert")) tls["client_certificate"] = inl["cert"];
            if (!tls.ContainsKey("client_key") && inl.ContainsKey("key")) tls["client_key"] = inl["key"];
            if (wrap == null && inl.ContainsKey("tls-crypt-v2")) wrap = new object[] { "tls_crypt_v2", inl["tls-crypt-v2"], null };
            if (wrap == null && inl.ContainsKey("tls-crypt")) wrap = new object[] { "tls_crypt", inl["tls-crypt"], null };
            if (wrap == null && inl.ContainsKey("tls-auth")) wrap = new object[] { "tls_auth", inl["tls-auth"], null };
            if (!ep.ContainsKey("static_key") && inl.ContainsKey("secret")) { ep["mode"] = "static_key"; ep["static_key"] = inl["secret"]; }
            if (inl.ContainsKey("pkcs12")) throw new Exception(I18n.L("PKCS#12 не поддерживается встроенным OpenVPN — выберите драйвер dco/wintun/tap"));

            foreach (var r in remotes)
            {
                int port;
                if (!int.TryParse(r.Length > 2 ? r[2] : defPort, out port)) port = 1194;
                var s = D.New("server", r[1], "server_port", port);
                if (r.Length > 3) s["network"] = Net(r[3]);
                servers.Add(s);
            }
            if (servers.Count == 0) throw new Exception(I18n.L("в конфиге нет строки remote"));
            ep["servers"] = servers;
            ep["network"] = network;

            if (wrap != null)
            {
                var cw = D.New("type", wrap[0], "key", wrap[1]);
                string dirArg = (string)wrap[2] ?? keyDirection;
                if ((string)wrap[0] == "tls_auth" && dirArg != null) cw["direction"] = dirArg == "0" ? "server" : "client";
                tls["control_wrap"] = cw;
            }
            if (keyDirection != null && ep.ContainsKey("static_key")) ep["key_direction"] = keyDirection == "0" ? "server" : "client";
            if (tls.Count > 0 && !ep.ContainsKey("static_key")) ep["tls"] = tls;
            // В TLS-режиме директива cipher означает запасной шифр (как в OpenVPN 2.6)
            if (!ep.ContainsKey("static_key") && ep.ContainsKey("cipher"))
            {
                if (!ep.ContainsKey("data_ciphers_fallback")) ep["data_ciphers_fallback"] = ep["cipher"];
                ep.Remove("cipher");
            }

            if (!string.IsNullOrEmpty(user)) { ep["username"] = user; ep["password"] = pass ?? ""; }
            else if (NeedsAuth(text)) throw new Exception(I18n.L("конфиг требует логин и пароль — задайте их кнопкой «Логин OpenVPN»"));

            // Шлюз по умолчанию и блокировку DNS от сервера игнорируем: куда слать трафик, решают правила
            ep["pull_filters"] = new List<object> {
                D.New("action", "ignore", "text", "redirect-gateway"),
                D.New("action", "ignore", "text", "redirect-private"),
                D.New("action", "ignore", "text", "block-outside-dns"),
                D.New("action", "ignore", "text", "block-ipv6"),
            };
            return ep;
        }

        static string Net(string proto)
        {
            proto = (proto ?? "udp").ToLowerInvariant();
            return proto.StartsWith("tcp") ? "tcp" : "udp";
        }

        public static bool NeedsAuth(string text)
        {
            foreach (var d in Directives(text))
                if (d[0].Equals("auth-user-pass", StringComparison.OrdinalIgnoreCase) && d.Length == 1) return true;
            return false;
        }

        public static List<string> Remotes(string text)
        {
            var l = new List<string>();
            foreach (var d in Directives(text))
                if (d[0].Equals("remote", StringComparison.OrdinalIgnoreCase) && d.Length >= 2) l.Add(d[1]);
            return l;
        }

        // Копирует .ovpn и файлы, на которые он ссылается, в destDir. Возвращает имя файла конфига.
        public static string Import(string srcPath, string destDir)
        {
            Directory.CreateDirectory(destDir);
            string text = File.ReadAllText(srcPath);
            string srcDir = Path.GetDirectoryName(Path.GetFullPath(srcPath));
            foreach (var d in Directives(text))
            {
                if (d.Length < 2 || Array.IndexOf(FileDirectives, d[0].ToLowerInvariant()) < 0) continue;
                if (d[1] == "[inline]") continue;
                string f = Path.IsPathRooted(d[1]) ? d[1] : Path.Combine(srcDir, d[1]);
                if (File.Exists(f))
                {
                    string rel = Path.IsPathRooted(d[1]) ? Path.GetFileName(d[1]) : d[1];
                    string dst = Path.Combine(destDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(f, dst, true);
                    if (Path.IsPathRooted(d[1])) text = text.Replace(d[1], rel);
                }
            }
            File.WriteAllText(Path.Combine(destDir, "client.ovpn"), text);
            return "client.ovpn";
        }

        // Возвращает текст конфига без опций маршрутизации/DNS. keepAuth=false убирает auth-user-pass.
        public static string Sanitize(string text, bool dropAuth)
        {
            var sb = new StringBuilder();
            string block = null;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                string t = line.Trim();
                if (block != null)
                {
                    sb.Append(line).Append('\n');
                    if (t.Equals("</" + block + ">", StringComparison.OrdinalIgnoreCase)) block = null;
                    continue;
                }
                if (t.StartsWith("<") && t.EndsWith(">") && !t.StartsWith("</"))
                {
                    string tag = t.Trim('<', '>');
                    if (tag != "connection") block = tag;
                    sb.Append(line).Append('\n');
                    continue;
                }
                if (t.Length > 0 && t[0] != '#' && t[0] != ';' && !t.StartsWith("<"))
                {
                    var tok = Tokenize(t);
                    string k = tok[0].ToLowerInvariant();
                    if (Array.IndexOf(DropDirectives, k) >= 0) { sb.Append("# [Hydra] ").Append(line).Append('\n'); continue; }
                    if (k == "auth-user-pass" && dropAuth) { sb.Append("# [Hydra] ").Append(line).Append('\n'); continue; }
                }
                sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }
    }
}
