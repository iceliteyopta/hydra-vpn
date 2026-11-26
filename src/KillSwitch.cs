using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Hydra
{
    // Kill switch на брандмауэре Windows.
    // Для выбранных программ и IP-адресов блокируется исходящий трафик с любого локального адреса,
    // кроме выбранных адресов TUN-адаптера Hydra и loopback.
    // Пока VPN работает, их трафик идёт через TUN и проходит. Если VPN упал, трафик пошёл бы
    // через Wi-Fi/Ethernet с другим локальным адресом — и брандмауэр его режет.
    public static class KillSwitch
    {
        public const string RuleName = "Hydra KillSwitch";

        public static readonly string[] DefaultApps = {
            @"C:\Program Files\WindowsApps\Claude_*\app\Claude.exe",
            @"%LOCALAPPDATA%\Claude-Data\claude_*\claude-code\*\*\claude.exe",
            @"%LOCALAPPDATA%\AnthropicClaude\app-*\claude.exe",
        };
        public const string DefaultDomains = "claude.ai, claude.com, anthropic.com, claudeusercontent.com, clau.de";

        // Разворачивает шаблоны с * и %ПЕРЕМЕННЫМИ% в реальные пути .exe (по диску и по запущенным процессам)
        public static List<string> ExpandApps(IEnumerable<string> patterns)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var running = new List<string>();
            foreach (var p in Process.GetProcesses())
            {
                try { string f = p.MainModule.FileName; if (!string.IsNullOrEmpty(f)) running.Add(f); } catch { }
            }
            foreach (var raw in patterns)
            {
                string pat = Environment.ExpandEnvironmentVariables((raw ?? "").Trim().Trim('"'));
                if (pat.Length == 0) continue;
                var rx = new Regex("^" + Regex.Escape(pat).Replace(@"\*", @"[^\\]*").Replace(@"\?", @"[^\\]") + "$", RegexOptions.IgnoreCase);
                foreach (var f in Walk(pat)) if (seen.Add(f)) found.Add(f);
                foreach (var f in running) if (rx.IsMatch(f) && seen.Add(f)) found.Add(f);
            }
            return found;
        }

        static IEnumerable<string> Walk(string pattern)
        {
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
            {
                if (File.Exists(pattern)) yield return pattern;
                yield break;
            }
            var parts = pattern.Split('\\');
            var current = new List<string> { parts[0] + "\\" };
            for (int i = 1; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1;
                var next = new List<string>();
                foreach (var dir in current)
                {
                    try
                    {
                        if (last)
                        {
                            foreach (var f in Directory.GetFiles(dir, parts[i])) next.Add(f);
                        }
                        else if (parts[i].IndexOf('*') >= 0 || parts[i].IndexOf('?') >= 0)
                        {
                            foreach (var d in Directory.GetDirectories(dir, parts[i])) next.Add(d);
                        }
                        else
                        {
                            string d = Path.Combine(dir, parts[i]);
                            if (Directory.Exists(d)) next.Add(d);
                        }
                    }
                    catch { } // например, WindowsApps закрыт даже для администратора
                }
                current = next;
            }
            foreach (var f in current) yield return f;
        }

        // Имена процессов для правил sing-box
        public static List<string> ProcessNames(IEnumerable<string> patterns)
        {
            var l = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in patterns)
            {
                string n = Path.GetFileName(Environment.ExpandEnvironmentVariables((raw ?? "").Trim().Trim('"')));
                if (string.IsNullOrEmpty(n) || n.IndexOf('*') >= 0) continue;
                if (seen.Add(n)) l.Add(n);
                if (seen.Add(n.ToLowerInvariant())) l.Add(n.ToLowerInvariant());
            }
            return l;
        }

        public static List<string> ResolveDomains(IEnumerable<string> domains)
        {
            var l = new List<string>();
            var seen = new HashSet<string>();
            foreach (var d in domains)
            {
                foreach (var h in new[] { d, "www." + d, "api." + d })
                {
                    try
                    {
                        foreach (var a in Dns.GetHostAddresses(h))
                            if ((a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6) && !IsFake(a) && seen.Add(a.ToString()))
                                l.Add(a.ToString());
                    }
                    catch { }
                }
            }
            return l;
        }

        // fake-IP других VPN-клиентов (198.18.0.0/15) и служебные адреса — не настоящие
        static bool IsFake(IPAddress a)
        {
            var b = a.GetAddressBytes();
            if (b.Length == 4) return (b[0] == 198 && (b[1] == 18 || b[1] == 19)) || b[0] == 0 || b[0] == 127;
            return IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal;
        }

        internal static int Enable(List<string> exePaths, List<string> ips, TunAddresses addresses, Action<string> log)
        {
            string LocalV4 = addresses.BlockedIPv4;
            string LocalV6 = addresses.BlockedIPv6;
            Disable(null);
            int n = 0;
            foreach (var exe in exePaths)
            {
                n += Add("program=\"" + exe + "\" localip=" + LocalV4, log);
                n += Add("program=\"" + exe + "\" localip=" + LocalV6, log);
            }
            // Адреса сервисов: блок для всех программ, если трафик идёт не через TUN
            for (int i = 0; i < ips.Count; i += 50)
            {
                var chunk = ips.GetRange(i, Math.Min(50, ips.Count - i));
                var v4 = chunk.FindAll(a => !a.Contains(":"));
                var v6 = chunk.FindAll(a => a.Contains(":"));
                if (v4.Count > 0) n += Add("remoteip=" + string.Join(",", v4.ToArray()) + " localip=" + LocalV4, log);
                if (v6.Count > 0) n += Add("remoteip=" + string.Join(",", v6.ToArray()) + " localip=" + LocalV6, log);
            }
            return n;
        }

        static int Add(string spec, Action<string> log)
        {
            string outp;
            int code = Proc.Run("netsh.exe", "advfirewall firewall add rule name=\"" + RuleName + "\" dir=out action=block enable=yes profile=any " + spec, out outp, 15000);
            if (code != 0) { if (log != null) log(I18n.L("Kill switch: не удалось добавить правило (") + spec + "): " + outp.Trim()); return 0; }
            return 1;
        }

        public static void Disable(Action<string> log)
        {
            string outp;
            Proc.Run("netsh.exe", "advfirewall firewall delete rule name=\"" + RuleName + "\"", out outp, 15000);
            if (log != null) log(I18n.L("Kill switch снят: правила брандмауэра удалены."));
        }

        public static int ActiveRules()
        {
            string outp;
            int code = Proc.Run("powershell.exe", "-NoProfile -NonInteractive -Command \"@(Get-NetFirewallRule -DisplayName '" + RuleName + "' -ErrorAction SilentlyContinue).Count\"", out outp, 20000);
            int n;
            return code == 0 && int.TryParse(outp.Trim(), out n) ? n : 0;
        }

        public static bool FirewallEnabled()
        {
            string outp;
            int code = Proc.Run("powershell.exe", "-NoProfile -NonInteractive -Command \"@(Get-NetFirewallProfile | Where-Object { -not $_.Enabled }).Count\"", out outp, 20000);
            int n;
            return code != 0 || !int.TryParse(outp.Trim(), out n) || n == 0;
        }
    }
}
