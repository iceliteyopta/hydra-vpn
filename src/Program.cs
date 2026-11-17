using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Hydra
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--selftest") return SelfTest.Run(args[1]);
            if (args.Length >= 2 && args[0] == "--make-icon")
            {
                Branding.WriteIco(args[1]);
                return 0;
            }
            if (args.Length >= 2 && args[0] == "--update-test")
            {
                // Проверка самообновления без окна: найти релиз новее, скачать, подменить exe
                try
                {
                    var r = Updater.CheckLatest();
                    if (r == null) { File.WriteAllText(args[1], "NO UPDATE (current " + Updater.CurrentVersion + ")"); return 0; }
                    string exe = Updater.DownloadAndReplace(r, null);
                    File.WriteAllText(args[1], "UPDATED to " + r.Version + " sha256=" + r.ExeSha256 + " -> " + exe);
                    return 0;
                }
                catch (Exception ex) { File.WriteAllText(args[1], "FAIL " + ex.Message); return 1; }
            }
            if (args.Length >= 2 && args[0] == "--fetch-core")
            {
                // Скачать ядро sing-box в cores\ рядом с exe; результат — в файл args[1]
                try { File.WriteAllText(args[1], "OK " + CoreDownloader.Download(null)); return 0; }
                catch (Exception ex) { File.WriteAllText(args[1], "FAIL " + ex.Message); return 1; }
            }
            if (args.Length >= 2 && args[0] == "--screenshots")
            {
                // Снимки страниц окна в PNG (без запуска VPN): --screenshots <папка> [ru|en]
                I18n.Init(args.Length > 2 ? args[2] : ProfileStore.Load().Profile.Language);
                Application.EnableVisualStyles();
                var f = new MainForm();
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(-3000, 0);
                f.ShowInTaskbar = false;
                f.Shown += (s, e) =>
                {
                    foreach (var page in new[] { "home", "tunnels", "rules", "protect", "settings", "history", "home-connected" })
                    {
                        if (page == "home-connected") { f.DemoTraffic("connected"); f.ShowPageForTest("home"); }
                        else f.ShowPageForTest(page);
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(150);
                        Application.DoEvents();
                        using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                            bmp.Save(Path.Combine(args[1], page + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    f.ForceClose();
                };
                Application.Run(f);
                return 0;
            }
            if (args.Length >= 2 && args[0] == "--ui-smoke")
            {
                // Создаёт окно, через 2 секунды закрывает и пишет результат в файл
                try
                {
                    Application.EnableVisualStyles();
                    var f = new MainForm();
                    var tm = new System.Windows.Forms.Timer { Interval = 2000 };
                    tm.Tick += (s, e) => { tm.Stop(); f.ForceClose(); };
                    tm.Start();
                    Application.Run(f);
                    File.WriteAllText(args[1], "OK");
                    return 0;
                }
                catch (Exception ex) { File.WriteAllText(args[1], "FAIL " + ex); return 1; }
            }
            // Импорт без окна: --add-link <ссылка> <имя> | --add-ovpn <файл> <имя> | --add-rule <тип> <значения> <куда> | --set-final <куда>
            if (args.Length >= 2 && (args[0] == "--add-link" || args[0] == "--add-ovpn" || args[0] == "--add-rule" || args[0] == "--add-rule-top" || args[0] == "--set-final" || args[0] == "--set-dns" || args[0] == "--set-ruleset-via" || args[0] == "--set-killswitch"))
            {
                var st = ProfileStore.Load();
                var p = st.Profile;
                string res;
                try
                {
                    switch (args[0])
                    {
                        case "--add-link":
                            {
                                var pl = LinkParser.Parse(args[1]);
                                var t = new Tunnel { Name = p.UniqueName(args.Length > 2 ? args[2] : pl.Name), Type = pl.Type, Link = args[1], Server = pl.Server };
                                p.Tunnels.Add(t);
                                res = "OK tunnel " + t.Name + " " + t.Type;
                                break;
                            }
                        case "--add-ovpn":
                            {
                                var t = new Tunnel { Type = TunnelTypes.OpenVpn, Name = p.UniqueName(args.Length > 2 ? args[2] : Path.GetFileNameWithoutExtension(args[1])) };
                                t.ConfigFile = OvpnConf.Import(args[1], t.Dir());
                                var remotes = OvpnConf.Remotes(File.ReadAllText(t.ConfigPath()));
                                t.Server = string.Join(", ", remotes.ToArray());
                                p.Tunnels.Add(t);
                                res = "OK tunnel " + t.Name + " openvpn";
                                break;
                            }
                        case "--add-rule":
                            p.Rules.Add(new Rule { Kind = args[1], Values = args[2], Target = args[3] });
                            res = "OK rule " + args[1] + " -> " + args[3];
                            break;
                        case "--add-rule-top":
                            p.Rules.Insert(0, new Rule { Kind = args[1], Values = args[2], Target = args[3] });
                            res = "OK rule (top) " + args[1] + " -> " + args[3];
                            break;
                        case "--set-dns":
                            p.DefaultDns = args[1];
                            res = "OK dns " + args[1];
                            break;
                        case "--set-ruleset-via":
                            p.RuleSetDetour = args[1];
                            res = "OK ruleset-via " + args[1];
                            break;
                        case "--set-killswitch":
                            p.KillSwitch = args[1] == "on";
                            res = "OK killswitch " + (p.KillSwitch ? "on" : "off");
                            break;
                        default:
                            p.Final = args[1];
                            res = "OK final " + args[1];
                            break;
                    }
                    st.Save();
                }
                catch (Exception ex) { res = "FAIL " + ex.Message; }
                File.AppendAllText(Path.Combine(AppPaths.DataDir, "cli.log"), res + Environment.NewLine, new UTF8Encoding(false));
                return res.StartsWith("OK") ? 0 : 1;
            }
            if (args.Length >= 2 && args[0] == "--gen-config")
            {
                BuildResult res;
                string path = Engine.WriteConfig(ProfileStore.Load().Profile, false, out res);
                File.Copy(path, args[1], true);
                return 0;
            }

            I18n.Init(ProfileStore.Load().Profile.Language);

            // TUN-адаптеры и маршруты требуют прав администратора
            if (!IsAdmin())
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath) { Verb = "runas", UseShellExecute = true });
                }
                catch
                {
                    MessageBox.Show(I18n.L("Для работы VPN нужны права администратора."), "Hydra", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return 0;
            }

            Updater.CleanupOld();
            bool updated = Array.IndexOf(args, "--updated") >= 0;
            bool created;
            using (var m = new Mutex(true, "Global\\Hydra-single-instance", out created))
            {
                // После самообновления старый процесс ещё может завершаться — ждём его
                if (!created && updated)
                {
                    try { created = m.WaitOne(20000); } catch (AbandonedMutexException) { created = true; }
                    Updater.CleanupOld();
                }
                if (!created) { MessageBox.Show(I18n.L("Hydra уже запущена (иконка в трее)."), "Hydra"); return 0; }

                // Сбои: в UI-потоке — отчёт и продолжаем работу; в других потоках — отчёт перед падением
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) =>
                {
                    string path = CrashReport.ForException(e.Exception, "UI");
                    MessageBox.Show(I18n.L("В Hydra произошла ошибка, но работа продолжится.\n\n") + e.Exception.Message +
                        (path != null ? I18n.L("\n\nОтчёт сохранён:\n") + path : ""), "Hydra", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    var ex = e.ExceptionObject as Exception;
                    if (ex != null) CrashReport.ForException(ex, I18n.L("фоновый поток"));
                    FileLog.Close();
                };

                History.MarkStarted();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                History.MarkExited();
                FileLog.Close();
            }
            return 0;
        }

        static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    // Проверка разбора ссылок и генерации конфига без запуска туннелей
    static class SelfTest
    {
        public static int Run(string outDir)
        {
            Directory.CreateDirectory(outDir);
            Environment.SetEnvironmentVariable("HYDRA_DATA", Path.Combine(outDir, "data"));
            var log = new StringBuilder();
            int fails = 0;

            var links = new[] {
                "vless://11111111-2222-3333-4444-555555555555@203.0.113.10:443?encryption=none&security=reality&sni=www.microsoft.com&fp=chrome&pbk=SbVKOEMjK0sIlbwg4akyBg5mL5KZwwB-ed4eEE7YnRc&sid=6ba85179e30d4fc2&type=tcp&flow=xtls-rprx-vision#NL%20Reality",
                "vless://11111111-2222-3333-4444-555555555555@example.com:443?security=tls&type=ws&path=%2Fws%3Fed%3D2048&host=cdn.example.com#WS",
                "vless://11111111-2222-3333-4444-555555555555@example.com:443?security=tls&type=grpc&serviceName=grpcsvc#gRPC",
                "hy2://s3cret@198.51.100.7:8443?sni=hy.example.com&obfs=salamander&obfs-password=xyz&insecure=1&mport=20000-30000#Hysteria2",
                "hysteria2://pass@[2001:db8::1]:443/?sni=a.example#HY2v6",
                "hysteria://198.51.100.8:36712?protocol=udp&auth=abc&peer=h.example&upmbps=30&downmbps=200#Hy1",
                "trojan://pw@trojan.example:443?security=tls&sni=trojan.example&type=ws&path=/tj#Trojan",
                "ss://Y2hhY2hhMjAtaWV0Zi1wb2x5MTMwNTpwYXNzd29yZA@192.0.2.5:8388#SS",
                "ss://2022-blake3-aes-128-gcm:YWJjZGVmZ2hpamtsbW5vcA%3D%3D@192.0.2.6:8388#SS2022",
                "tuic://11111111-2222-3333-4444-555555555555:pw@192.0.2.9:443?congestion_control=bbr&alpn=h3&sni=t.example#TUIC",
                "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"v\":\"2\",\"ps\":\"VM\",\"add\":\"vm.example\",\"port\":\"443\",\"id\":\"11111111-2222-3333-4444-555555555555\",\"aid\":\"0\",\"net\":\"ws\",\"path\":\"/vm\",\"host\":\"vm.example\",\"tls\":\"tls\"}")),
            };

            var p = new Profile();
            foreach (var l in links)
            {
                try
                {
                    var pl = LinkParser.Parse(l);
                    p.Tunnels.Add(new Tunnel { Name = p.UniqueName(pl.Name), Type = pl.Type, Link = l, Server = pl.Server });
                    log.AppendLine("OK   " + pl.Type + "  " + pl.Name + "  " + pl.Server);
                }
                catch (Exception ex) { fails++; log.AppendLine("FAIL " + l.Substring(0, Math.Min(50, l.Length)) + " : " + ex.Message); }
            }

            // AmneziaWG + обычный WireGuard + OpenVPN
            string awg = "[Interface]\nPrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=\nAddress = 10.8.1.2/32\nDNS = 1.1.1.1\nJc = 4\nJmin = 40\nJmax = 70\nS1 = 0\nS2 = 0\nH1 = 1\nH2 = 2\nH3 = 3\nH4 = 4\n\n[Peer]\nPublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=\nAllowedIPs = 0.0.0.0/0\nEndpoint = 192.0.2.20:51820\nPersistentKeepalive = 25\n";
            string wgc = awg.Replace("Jc = 4\nJmin = 40\nJmax = 70\n", "").Replace("Endpoint = 192.0.2.20", "Endpoint = 192.0.2.21");
            foreach (var pair in new[] { new[] { "AWG Office", awg }, new[] { "WG Home", wgc } })
            {
                var wg = WgConf.Parse(pair[1]);
                var t = new Tunnel { Name = pair[0], Type = wg.IsAmnezia() ? TunnelTypes.AmneziaWg : TunnelTypes.WireGuard, ConfigFile = "tunnel.conf" };
                Directory.CreateDirectory(t.Dir());
                File.WriteAllText(t.ConfigPath(), pair[1]);
                p.Tunnels.Add(t);
                log.AppendLine("OK   " + t.Type + "  " + t.Name);
            }
            string ovpnDir = Path.Combine(outDir, "ovpn-src");
            Directory.CreateDirectory(ovpnDir);
            File.WriteAllText(Path.Combine(ovpnDir, "ca.crt"), "dummy");
            File.WriteAllText(Path.Combine(ovpnDir, "work.ovpn"), "client\ndev tun\nproto udp\nremote 192.0.2.30 1194\nca ca.crt\nauth-user-pass\nredirect-gateway def1\nblock-outside-dns\ndhcp-option DNS 10.0.0.1\n<tls-crypt>\nroute 1.2.3.4\n</tls-crypt>\n");
            var ov = new Tunnel { Name = "Работа", Type = TunnelTypes.OpenVpn, OvpnUser = "test", OvpnPassProtected = Secret.Protect("test") };
            ov.ConfigFile = OvpnConf.Import(Path.Combine(ovpnDir, "work.ovpn"), ov.Dir());
            p.Tunnels.Add(ov);
            string san = OvpnConf.Sanitize(File.ReadAllText(ov.ConfigPath()), true);
            File.WriteAllText(Path.Combine(outDir, "sanitized.ovpn"), san);
            bool caCopied = File.Exists(Path.Combine(ov.Dir(), "ca.crt"));
            bool inlineKept = san.Contains("\nroute 1.2.3.4\n");
            bool dropped = san.Contains("# [Hydra] redirect-gateway") && san.Contains("# [Hydra] auth-user-pass");
            log.AppendLine((caCopied && inlineKept && dropped ? "OK   " : "FAIL ") + "openvpn import/sanitize  ca=" + caCopied + " inline=" + inlineKept + " dropped=" + dropped);
            if (!(caCopied && inlineKept && dropped)) fails++;

            p.Rules.Add(new Rule { Kind = "domain_suffix", Values = "youtube.com, googlevideo.com, *.ytimg.com", Target = "NL Reality" });
            p.Rules.Add(new Rule { Kind = "domain_suffix", Values = "corp.local, jira.company.ru", Target = "Работа" });
            p.Rules.Add(new Rule { Kind = "ip_cidr", Values = "10.0.0.0/8", Target = "Работа" });
            p.Rules.Add(new Rule { Kind = "geosite", Values = "openai", Target = "Hysteria2" });
            p.Rules.Add(new Rule { Kind = "process_name", Values = "Telegram", Target = "AWG Office" });
            p.Rules.Add(new Rule { Kind = "domain_suffix", Values = "netflix.com", Target = "WG Home" });
            p.Rules.Add(new Rule { Kind = "geosite", Values = "category-ads-all", Target = "block" });
            p.Rules.Add(new Rule { Kind = "domain_suffix", Values = "ru, рф", Target = "direct" });
            p.Rules.Add(new Rule { Kind = "domain_suffix", Values = "x.com", Target = "Нет такого" });
            p.Final = "direct";
            p.DefaultDns = "https://1.1.1.1/dns-query";

            var res = ConfigBuilder.Build(p, true);
            foreach (var w in res.Warnings) log.AppendLine("WARN " + w);
            log.AppendLine("externals: " + res.Externals.Count);
            File.WriteAllText(Path.Combine(outDir, "config.json"), Json.Serialize(res.Config), new UTF8Encoding(false));

            var store = new ProfileStore { Profile = p };
            store.Save();
            var back = ProfileStore.Load().Profile;
            bool rt = back.Tunnels.Count == p.Tunnels.Count && back.Rules.Count == p.Rules.Count;
            log.AppendLine((rt ? "OK   " : "FAIL ") + "profile round-trip");
            if (!rt) fails++;

            log.AppendLine("fails: " + fails);
            File.WriteAllText(Path.Combine(outDir, "selftest.txt"), log.ToString(), new UTF8Encoding(false));
            return fails == 0 ? 0 : 1;
        }
    }
}
