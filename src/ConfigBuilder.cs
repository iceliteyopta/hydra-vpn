using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Hydra
{
    // Внешний туннель, который поднимает отдельный процесс (OpenVPN / AmneziaWG)
    public class ExternalTunnel
    {
        public Tunnel Tunnel;
        public string Kind;
        public string Interface;
        public WgConf Wg;
        public string AwgEndpoint; // ip:port после резолва
    }

    public class BuildResult
    {
        internal TunAddresses Addresses;
        internal readonly List<TunnelPaths> PathPairs = new List<TunnelPaths>();
        public Dictionary<string, object> Config;
        public List<ExternalTunnel> Externals = new List<ExternalTunnel>();
        public List<string> Warnings = new List<string>();
    }

    public static class RuleKinds
    {
        static RuleKinds() { } // инициализация строго после I18n.Init
        public static readonly string[][] All = {
            new[] { "domain_suffix", I18n.L("Домен и поддомены") },
            new[] { "domain", I18n.L("Домен (точно)") },
            new[] { "domain_keyword", I18n.L("Слово в домене") },
            new[] { "geosite", I18n.L("Категория geosite") },
            new[] { "ip_cidr", I18n.L("IP / подсеть") },
            new[] { "geoip", I18n.L("Страна geoip") },
            new[] { "process_name", I18n.L("Программа (.exe)") },
            new[] { "process_folder", I18n.L("Папка программ") },
            new[] { "port", I18n.L("Порт") },
        };

        public static string Display(string kind)
        {
            foreach (var k in All) if (k[0] == kind) return k[1];
            return All[0][1];
        }

        public static string FromDisplay(string disp)
        {
            foreach (var k in All) if (k[1] == disp || k[0] == disp) return k[0];
            return "domain_suffix";
        }

        public static bool IsDomain(string kind)
        {
            return kind == "domain_suffix" || kind == "domain" || kind == "domain_keyword" || kind == "geosite";
        }
    }

    public static class ConfigBuilder
    {
        public const string TunName = "Hydra";
        // Задаются движком перед запуском: порт и секрет локального API статистики sing-box
        public static int StatsPort;
        public static string StatsSecret = "";

        public static BuildResult Build(Profile p, bool resolveHosts)
        {
            var res = new BuildResult();
            var outbounds = new List<object>();
            var endpoints = new List<object>();
            var tags = new HashSet<string>();
            var excludes = new List<object>();
            var dnsServers = new List<object>();
            var dnsRules = new List<object>();
            var tunnelDns = new Dictionary<string, string>();
            var processBypass = new List<object>();
            var nativeOvpn = new List<string>();
            var wifiNetworks = new Lazy<HashSet<string>>(delegate { return WifiNetworks.Connected(res.Warnings); });
            var reserved = new HashSet<string> { "direct", "block" };
            foreach (var t in p.Tunnels) reserved.Add(t.Name);

            outbounds.Add(D.New("type", "direct", "tag", "direct"));
            dnsServers.Add(D.New("type", "local", "tag", "dns-local"));

            foreach (var t in p.Tunnels)
            {
                if (!t.Enabled) continue;
                try
                {
                    if (TunnelTypes.IsLink(t.Type))
                    {
                        var pl = LinkParser.Parse(t.Link);
                        if (pl.Type == "hysteria2" && t.HasWifiRelay() && wifiNetworks.Value.Contains(t.WifiNetworkName) &&
                            !string.Equals((string)pl.Outbound["server"], t.WifiServer.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            var pair = new TunnelPaths(t.Name, reserved);
                            pair.AddOutbounds(pl.Outbound, t.WifiServer.Trim(), outbounds);
                            res.PathPairs.Add(pair);
                        }
                        else
                        {
                            pl.Outbound["tag"] = t.Name;
                            outbounds.Add(pl.Outbound);
                        }
                    }
                    else if (t.Type == TunnelTypes.WireGuard)
                    {
                        var wg = WgConf.Parse(File.ReadAllText(t.ConfigPath()));
                        endpoints.Add(WireGuardEndpoint(t.Name, wg));
                        if (string.IsNullOrEmpty(t.DnsServer) && wg.Dns().Count > 0) tunnelDns[t.Name] = wg.Dns()[0];
                    }
                    else if (t.Type == TunnelTypes.AmneziaWg)
                    {
                        var wg = WgConf.Parse(File.ReadAllText(t.ConfigPath()));
                        string host; int port;
                        wg.Endpoint(0, out host, out port);
                        string ip = host;
                        if (resolveHosts)
                        {
                            var ips = Resolve(host);
                            if (ips.Count == 0) throw new Exception(I18n.L("не удалось определить IP сервера ") + host);
                            ip = ips[0];
                            foreach (var a in ips) excludes.Add(HostPrefix(a));
                        }
                        res.Externals.Add(new ExternalTunnel { Tunnel = t, Kind = t.Type, Interface = t.InterfaceName(), Wg = wg, AwgEndpoint = (ip.Contains(":") ? "[" + ip + "]" : ip) + ":" + port });
                        outbounds.Add(D.New("type", "direct", "tag", t.Name, "bind_interface", t.InterfaceName()));
                        if (string.IsNullOrEmpty(t.DnsServer) && wg.Dns().Count > 0) tunnelDns[t.Name] = wg.Dns()[0];
                    }
                    else if (t.Type == TunnelTypes.OpenVpn && IsNativeOvpn(t))
                    {
                        // Встроенный OpenVPN sing-box 1.14+: отдельная программа не нужна
                        string text = File.ReadAllText(t.ConfigPath());
                        endpoints.Add(OvpnConf.ToSingBoxEndpoint(text, t.Dir(), t.Name, t.OvpnUser, Secret.Unprotect(t.OvpnPassProtected)));
                        nativeOvpn.Add(t.Name);
                    }
                    else if (t.Type == TunnelTypes.OpenVpn)
                    {
                        string text = File.ReadAllText(t.ConfigPath());
                        if (resolveHosts)
                            foreach (var h in OvpnConf.Remotes(text))
                                foreach (var a in Resolve(h)) excludes.Add(HostPrefix(a));
                        res.Externals.Add(new ExternalTunnel { Tunnel = t, Kind = t.Type, Interface = t.InterfaceName() });
                        outbounds.Add(D.New("type", "direct", "tag", t.Name, "bind_interface", t.InterfaceName()));
                    }
                    else throw new Exception(I18n.L("неизвестный тип ") + t.Type);
                    tags.Add(t.Name);
                    if (!string.IsNullOrEmpty(t.DnsServer)) tunnelDns[t.Name] = t.DnsServer;
                }
                catch (Exception ex)
                {
                    res.Warnings.Add(I18n.L("Туннель «") + t.Name + I18n.L("» пропущен: ") + ex.Message);
                }
            }

            // Процессы-клиенты VPN всегда ходят напрямую, иначе получится петля
            processBypass.Add("openvpn.exe");
            string awg = AppPaths.AwgGo(p);
            processBypass.Add(awg != null ? Path.GetFileName(awg) : "amneziawg-go.exe");

            // ---- правила маршрутизации ----
            var routeRules = new List<object>();
            routeRules.Add(D.New("action", "sniff"));
            routeRules.Add(D.New("protocol", "dns", "action", "hijack-dns"));
            routeRules.Add(D.New("process_name", processBypass, "outbound", "direct"));

            var ruleSets = new Dictionary<string, object>();
            var usedDnsTunnels = new HashSet<string>();

            // Kill switch: выбранные приложения и домены всегда идут в туннель, никогда напрямую
            string killTag = KillTarget(p, tags);
            if (p.KillSwitch)
            {
                if (killTag == null) res.Warnings.Add(I18n.L("Kill switch: нет включённого туннеля — выберите туннель на вкладке «Защита»"));
                else
                {
                    var procs = KillSwitch.ProcessNames(p.KillApps ?? new List<string>());
                    if (procs.Count > 0) routeRules.Add(D.New("process_name", new List<object>(procs), "outbound", killTag));
                    var doms = new List<object>();
                    foreach (var d in SplitValues(p.KillDomains)) doms.Add(CleanDomain(d));
                    if (doms.Count > 0)
                    {
                        routeRules.Add(D.New("domain_suffix", doms, "outbound", killTag));
                        dnsRules.Add(D.New("domain_suffix", doms, "server", "dns-" + killTag));
                        usedDnsTunnels.Add(killTag);
                    }
                }
            }
            int n = 0;
            foreach (var r in p.Rules)
            {
                n++;
                if (!r.Enabled) continue;
                // В именах программ и папках бывают пробелы — делим только по запятой/; /переводу строки
                bool keepSpaces = r.Kind == "process_name" || r.Kind == "process_folder";
                var vals = keepSpaces ? SplitList(r.Values) : SplitValues(r.Values);
                if (vals.Count == 0) continue;
                string target = r.Target ?? "direct";
                if (target != "direct" && target != "block" && !tags.Contains(target))
                {
                    res.Warnings.Add(I18n.L("Правило ") + n + I18n.L(" пропущено: туннель «") + target + I18n.L("» отключён или не найден"));
                    continue;
                }
                Dictionary<string, object> m;
                try { m = Matcher(r.Kind, vals, ruleSets, p.RuleSetDetour, tags); }
                catch (Exception ex) { res.Warnings.Add(I18n.L("Правило ") + n + I18n.L(" пропущено: ") + ex.Message); continue; }

                var rr = new Dictionary<string, object>(m);
                if (target == "block") rr["action"] = "reject";
                else rr["outbound"] = target;
                routeRules.Add(rr);

                // Домены, идущие в туннель, резолвим DNS-ом через этот же туннель
                if (RuleKinds.IsDomain(r.Kind) && target != "direct" && target != "block")
                {
                    var dr = new Dictionary<string, object>(m);
                    dr["server"] = "dns-" + target;
                    dnsRules.Add(dr);
                    usedDnsTunnels.Add(target);
                }
                // Домены, идущие напрямую, резолвим системным DNS (российские сайты получат российские адреса)
                else if (RuleKinds.IsDomain(r.Kind) && target == "direct")
                {
                    var dr = new Dictionary<string, object>(m);
                    dr["server"] = "dns-local";
                    dnsRules.Add(dr);
                }
            }
            // Подсети, которые отдал OpenVPN-сервер, идут в OpenVPN — но ПОСЛЕ правил пользователя:
            // сервер может прислать чужие сети (например, Telegram), и явные правила должны быть главнее
            foreach (var tag in nativeOvpn)
                routeRules.Add(D.New("preferred_by", new List<object> { tag }, "outbound", tag));
            routeRules.Add(D.New("ip_is_private", true, "outbound", "direct"));

            // ---- DNS ----
            // DNS-серверы, которые отдал OpenVPN-сервер, и его внутренние домены (DOMAIN-ROUTE)
            var ovpnDnsRules = new List<object>();
            foreach (var tag in nativeOvpn)
            {
                if (tunnelDns.ContainsKey(tag)) continue;
                dnsServers.Add(D.New("type", "openvpn", "tag", "dns-" + tag, "endpoint", tag,
                    "accept_default_resolvers", true, "accept_search_domain", true));
                ovpnDnsRules.Add(D.New("preferred_by", new List<object> { "dns-" + tag }, "action", "route", "server", "dns-" + tag));
                usedDnsTunnels.Remove(tag);
            }
            dnsRules.InsertRange(0, ovpnDnsRules);
            foreach (var tag in usedDnsTunnels)
            {
                string spec;
                if (!tunnelDns.TryGetValue(tag, out spec)) spec = "https://1.1.1.1/dns-query";
                var s = DnsServer(spec, "dns-" + tag);
                s["detour"] = tag;
                dnsServers.Add(s);
            }
            string dnsFinal = "dns-local";
            string def = (p.DefaultDns ?? "local").Trim();
            if (def != "" && def != "local")
            {
                var s = DnsServer(def, "dns-default");
                dnsServers.Add(s);
                dnsFinal = "dns-default";
            }
            if (p.Final != "direct" && dnsFinal == "dns-default")
                res.Warnings.Add(I18n.L("DNS по умолчанию будет ходить через «") + p.Final + I18n.L("» (туннель по умолчанию)"));

            string final = p.Final ?? "direct";
            if (final != "direct" && !tags.Contains(final))
            {
                res.Warnings.Add(I18n.L("Туннель по умолчанию «") + final + I18n.L("» недоступен — остальной трафик пойдёт напрямую"));
                final = "direct";
            }

            // ---- сборка ----
            res.Addresses = TunAddresses.Detect();
            var tun = D.New("type", "tun", "tag", "tun-in", "interface_name", TunName,
                "address", new List<object> { res.Addresses.IPv4 + "/30", res.Addresses.IPv6 + "/126" },
                "mtu", 9000, "auto_route", true, "strict_route", p.StrictRoute, "stack", "mixed");
            // Prefer Hydra over a competing IPv4 default TUN, independent of its metric.
            if (res.Addresses.ForeignTunDefault)
                tun["route_address"] = new List<object> { "0.0.0.0/1", "128.0.0.0/1", "::/0" };
            if (excludes.Count > 0) tun["route_exclude_address"] = Dedup(excludes);

            var route = D.New("rules", routeRules, "final", final, "auto_detect_interface", true,
                "find_process", true, "default_domain_resolver", "dns-local");
            if (ruleSets.Count > 0) route["rule_set"] = new List<object>(ruleSets.Values);

            var cfg = new Dictionary<string, object>();
            cfg["log"] = D.New("level", string.IsNullOrEmpty(p.LogLevel) ? "info" : p.LogLevel, "timestamp", true);
            cfg["dns"] = D.New("servers", dnsServers, "rules", dnsRules, "final", dnsFinal, "strategy", "ipv4_only");
            cfg["inbounds"] = new List<object> { tun };
            cfg["outbounds"] = outbounds;
            if (endpoints.Count > 0) cfg["endpoints"] = endpoints;
            cfg["route"] = route;
            var exp = D.New("cache_file", D.New("enabled", true, "path", "cache.db"));
            // Локальный API статистики (только 127.0.0.1, с секретом) — скорость по туннелям в главном окне
            if (StatsPort > 0)
                exp["clash_api"] = D.New("external_controller", "127.0.0.1:" + StatsPort, "secret", StatsSecret);
            cfg["experimental"] = exp;
            res.Config = cfg;
            return res;
        }

        static Dictionary<string, object> Matcher(string kind, List<string> vals, Dictionary<string, object> ruleSets, string rsDetour, HashSet<string> tags)
        {
            var list = new List<object>();
            switch (kind)
            {
                case "domain_suffix":
                case "domain":
                case "domain_keyword":
                    foreach (var v in vals) list.Add(CleanDomain(v));
                    return D.New(kind, list);
                case "ip_cidr":
                    foreach (var v in vals) list.Add(v.Contains("/") ? v : (v.Contains(":") ? v + "/128" : v + "/32"));
                    return D.New("ip_cidr", list);
                case "process_name":
                    foreach (var v in vals) list.Add(v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? v : v + ".exe");
                    return D.New("process_name", list);
                case "process_folder":
                    // Всё, что запущено из этих папок (и подпапок): регулярка по полному пути процесса
                    foreach (var v in vals)
                    {
                        string dir = Environment.ExpandEnvironmentVariables(v.Trim().Trim('"')).TrimEnd('\\', '/');
                        list.Add("(?i)^" + GoRegexEscape(dir) + "\\\\");
                    }
                    return D.New("process_path_regex", list);
                case "port":
                    foreach (var v in vals)
                    {
                        int x;
                        if (!int.TryParse(v, out x) || x <= 0 || x > 65535) throw new Exception(I18n.L("неверный порт ") + v);
                        list.Add(x);
                    }
                    return D.New("port", list);
                case "geosite":
                case "geoip":
                    foreach (var v in vals)
                    {
                        string name = v.ToLowerInvariant();
                        string tag = kind + "-" + name;
                        if (!ruleSets.ContainsKey(tag))
                        {
                            string repo = kind == "geosite" ? "sing-geosite" : "sing-geoip";
                            var rs = D.New("type", "remote", "tag", tag, "format", "binary",
                                "url", "https://raw.githubusercontent.com/SagerNet/" + repo + "/rule-set/" + tag + ".srs");
                            string det = string.IsNullOrEmpty(rsDetour) ? "direct" : rsDetour;
                            rs["download_detour"] = (det == "direct" || tags.Contains(det)) ? det : "direct";
                            ruleSets[tag] = rs;
                        }
                        list.Add(tag);
                    }
                    return D.New("rule_set", list);
                default:
                    throw new Exception(I18n.L("неизвестный тип правила ") + kind);
            }
        }

        // Туннель для kill switch: выбранный, иначе туннель по умолчанию
        public static string KillTarget(Profile p, HashSet<string> tags)
        {
            if (!string.IsNullOrEmpty(p.KillTunnel) && tags.Contains(p.KillTunnel)) return p.KillTunnel;
            if (!string.IsNullOrEmpty(p.Final) && tags.Contains(p.Final)) return p.Final;
            return null;
        }

        public static bool IsNativeOvpn(Tunnel t)
        {
            string d = (t.OvpnDriver ?? "").ToLowerInvariant();
            return d == "" || d == "native";
        }

        static Dictionary<string, object> WireGuardEndpoint(string tag, WgConf wg)
        {
            var addrs = new List<object>();
            foreach (var a in wg.Addresses()) addrs.Add(a.Contains("/") ? a : (a.Contains(":") ? a + "/128" : a + "/32"));
            var peers = new List<object>();
            for (int i = 0; i < wg.Peers.Count; i++)
            {
                var pr = wg.Peers[i];
                string host; int port;
                wg.Endpoint(i, out host, out port);
                var allowed = new List<object>();
                foreach (var a in WgConf.SplitList(WgConf.Get(pr, "AllowedIPs"))) allowed.Add(a);
                if (allowed.Count == 0) { allowed.Add("0.0.0.0/0"); allowed.Add("::/0"); }
                var peer = D.New("address", host, "port", port, "public_key", WgConf.Get(pr, "PublicKey"), "allowed_ips", allowed);
                string psk = WgConf.Get(pr, "PresharedKey");
                if (!string.IsNullOrEmpty(psk)) peer["pre_shared_key"] = psk;
                int ka;
                if (int.TryParse(WgConf.Get(pr, "PersistentKeepalive"), out ka) && ka > 0) peer["persistent_keepalive_interval"] = ka;
                peers.Add(peer);
            }
            return D.New("type", "wireguard", "tag", tag, "system", false, "mtu", wg.Mtu(), "address", addrs,
                "private_key", WgConf.Get(wg.Iface, "PrivateKey"), "peers", peers);
        }

        // "local", "1.1.1.1", "udp://8.8.8.8", "tls://1.1.1.1", "https://dns.google/dns-query"
        public static Dictionary<string, object> DnsServer(string spec, string tag)
        {
            spec = spec.Trim();
            if (spec == "local") return D.New("type", "local", "tag", tag);
            string type = "udp", rest = spec;
            int i = spec.IndexOf("://", StringComparison.Ordinal);
            if (i > 0) { type = spec.Substring(0, i).ToLowerInvariant(); rest = spec.Substring(i + 3); }
            string path = null;
            int sl = rest.IndexOf('/');
            if (sl >= 0) { path = rest.Substring(sl); rest = rest.Substring(0, sl); }
            string host, extra; int port;
            LinkParser.SplitHostPort(rest, out host, out port, out extra);
            bool hasPort = rest.StartsWith("[") ? rest.Contains("]:") : (rest.IndexOf(':') >= 0 && rest.IndexOf(':') == rest.LastIndexOf(':'));
            var s = D.New("type", type, "tag", tag, "server", host);
            if (hasPort) s["server_port"] = port;
            if (type == "https" && !string.IsNullOrEmpty(path) && path != "/dns-query") s["path"] = path;
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip)) s["domain_resolver"] = "dns-local";
            return s;
        }

        // Экранирование для RE2 (Go): только спецсимволы, пробелы оставляем как есть
        static string GoRegexEscape(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                if ("\\.+*?()|[]{}^$".IndexOf(c) >= 0) sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static List<string> SplitList(string s)
        {
            var l = new List<string>();
            if (string.IsNullOrEmpty(s)) return l;
            foreach (var p in s.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                if (p.Trim().Length > 0) l.Add(p.Trim());
            return l;
        }

        public static List<string> SplitValues(string s)
        {
            var l = new List<string>();
            if (string.IsNullOrEmpty(s)) return l;
            foreach (var p in s.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (p.Trim().Length > 0) l.Add(p.Trim());
            return l;
        }

        static string CleanDomain(string v)
        {
            v = v.Trim().ToLowerInvariant();
            int i = v.IndexOf("://", StringComparison.Ordinal);
            if (i >= 0) v = v.Substring(i + 3);
            int sl = v.IndexOf('/');
            if (sl >= 0) v = v.Substring(0, sl);
            if (v.StartsWith("*.")) v = v.Substring(2);
            v = v.TrimStart('.');
            if (v == I18n.L("рф")) return "xn--p1ai";
            try { v = new IdnMapping().GetAscii(v); } catch { }
            return v;
        }

        static List<string> Resolve(string host)
        {
            var l = new List<string>();
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) { l.Add(ip.ToString()); return l; }
            try
            {
                foreach (var a in Dns.GetHostAddresses(host))
                    if (a.AddressFamily == AddressFamily.InterNetwork) l.Add(a.ToString());
            }
            catch { }
            return l;
        }

        static string HostPrefix(string ip) { return ip + (ip.Contains(":") ? "/128" : "/32"); }

        static List<object> Dedup(List<object> l)
        {
            var seen = new HashSet<string>();
            var r = new List<object>();
            foreach (var o in l) if (seen.Add((string)o)) r.Add(o);
            return r;
        }
    }
}
