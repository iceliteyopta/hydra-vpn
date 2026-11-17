using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Hydra
{
    // Порядок запуска: sing-box (TUN + маршрутизация) → OpenVPN / AmneziaWG.
    // Процессы openvpn.exe и amneziawg-go.exe sing-box пускает напрямую, а их серверы исключены из TUN.
    public class Engine
    {
        public event Action<string> Log;
        public event Action<string, string> TunnelStatus;
        public event Action<string> StateChanged; // "connecting" | "connected" | "stopping" | "stopped"

        readonly object sync = new object();
        Process singbox;
        readonly List<IRunner> runners = new List<IRunner>();
        public string State = "stopped";
        public event Action<bool> KillChanged;
        public bool KillActive;

        void SetKill(bool v) { KillActive = v; var h = KillChanged; if (h != null) h(v); }

        // Скорость: тег туннеля → {вверх, вниз} байт/с; ключ "" — всего
        public event Action<Dictionary<string, long[]>> Traffic;
        // Туннель много раз подряд выдаёт ошибки — похоже, сервер не отвечает
        public event Action<string, string> TunnelProblem;
        readonly Dictionary<string, List<DateTime>> tunnelErrors = new Dictionary<string, List<DateTime>>();
        readonly Dictionary<string, DateTime> tunnelAlerted = new Dictionary<string, DateTime>();
        HashSet<string> tunnelTags = new HashSet<string>();

        void WatchLine(string line)
        {
            if (line.IndexOf("ERROR", StringComparison.Ordinal) < 0) return;
            foreach (var tag in tunnelTags)
            {
                if (line.IndexOf("[" + tag + "]", StringComparison.Ordinal) < 0) continue;
                List<DateTime> list;
                lock (tunnelErrors)
                {
                    if (!tunnelErrors.TryGetValue(tag, out list)) tunnelErrors[tag] = list = new List<DateTime>();
                    var now = DateTime.Now;
                    list.Add(now);
                    list.RemoveAll(t => (now - t).TotalSeconds > 60);
                    DateTime last;
                    if (list.Count >= 8 && (!tunnelAlerted.TryGetValue(tag, out last) || (now - last).TotalMinutes > 10))
                    {
                        tunnelAlerted[tag] = now;
                        string msg = line.Length > 200 ? line.Substring(line.Length - 200) : line;
                        History.Add(History.TunnelError, I18n.L("Туннель «") + tag + I18n.L("» не отвечает"), msg.Trim());
                        var h = TunnelProblem; if (h != null) h(tag, msg);
                    }
                }
            }
        }

        // Опрос локального API sing-box раз в секунду
        void StatsLoop()
        {
            var prev = new Dictionary<string, long[]>();
            long prevUp = -1, prevDown = -1;
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            while (State == "connected" || State == "connecting")
            {
                Thread.Sleep(1000);
                if (ConfigBuilder.StatsPort <= 0) continue;
                try
                {
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://127.0.0.1:" + ConfigBuilder.StatsPort + "/connections");
                    req.Headers["Authorization"] = "Bearer " + ConfigBuilder.StatsSecret;
                    req.Timeout = 2000;
                    req.Proxy = null;
                    string json;
                    using (var resp = req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream())) json = sr.ReadToEnd();
                    var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                    if (o == null) continue;

                    var rates = new Dictionary<string, long[]>();
                    var seen = new Dictionary<string, long[]>();
                    var conns = o.ContainsKey("connections") ? o["connections"] as object[] : null;
                    if (conns != null)
                        foreach (var co in conns)
                        {
                            var c = co as Dictionary<string, object>;
                            if (c == null) continue;
                            string id = Convert.ToString(c["id"]);
                            long up = Convert.ToInt64(c["upload"]), down = Convert.ToInt64(c["download"]);
                            var chains = c.ContainsKey("chains") ? c["chains"] as object[] : null;
                            string tag = chains != null && chains.Length > 0 ? Convert.ToString(chains[0]) : "direct";
                            long[] p;
                            long du = 0, dd = 0;
                            if (prev.TryGetValue(id, out p)) { du = Math.Max(0, up - p[0]); dd = Math.Max(0, down - p[1]); }
                            else if (prevUp >= 0) { du = up; dd = down; } // новое соединение после первого опроса
                            seen[id] = new[] { up, down };
                            long[] r;
                            if (!rates.TryGetValue(tag, out r)) rates[tag] = r = new long[2];
                            r[0] += du; r[1] += dd;
                        }
                    prev = seen;

                    long totUp = Convert.ToInt64(o["uploadTotal"]), totDown = Convert.ToInt64(o["downloadTotal"]);
                    if (prevUp >= 0) rates[""] = new[] { Math.Max(0, totUp - prevUp), Math.Max(0, totDown - prevDown) };
                    prevUp = totUp; prevDown = totDown;
                    var h = Traffic; if (h != null) h(rates);
                }
                catch { }
            }
            var hh = Traffic; if (hh != null) hh(new Dictionary<string, long[]>());
        }

        // для истории: начало сессии и последние строки sing-box
        DateTime sessionStart;
        bool sessionActive;
        readonly Queue<string> singboxTail = new Queue<string>();

        long SessionSeconds() { return sessionActive ? (long)(DateTime.Now - sessionStart).TotalSeconds : 0; }

        // Снять блокировку kill switch вручную (когда VPN уже не работает)
        public void ReleaseKill()
        {
            var th = new Thread(() => { KillSwitch.Disable(L); SetKill(false); History.Add(History.KillRelease, I18n.L("Блокировка kill switch снята вручную")); });
            th.IsBackground = true;
            th.Start();
        }

        public void SyncKillState()
        {
            var th = new Thread(() => { int n = KillSwitch.ActiveRules(); if (n > 0 && State == "stopped") { L(I18n.L("Kill switch удерживает блокировку с прошлого раза (") + n + I18n.L(" правил).")); SetKill(true); } });
            th.IsBackground = true;
            th.Start();
        }

        void L(string s) { var h = Log; if (h != null) h(DateTime.Now.ToString("HH:mm:ss") + "  " + s); }
        void Status(string name, string st) { var h = TunnelStatus; if (h != null) h(name, st); }
        void SetState(string s) { State = s; var h = StateChanged; if (h != null) h(s); }

        public void Start(Profile p)
        {
            lock (sync)
            {
                if (State != "stopped") return;
                SetState("connecting");
            }
            var th = new Thread(() =>
            {
                try
                {
                    DoStart(p);
                    lock (sync) { if (State == "connecting") SetState("connected"); }
                }
                catch (Exception ex)
                {
                    L(I18n.L("ОШИБКА: ") + ex.Message);
                    History.Add(History.Error, I18n.L("Не удалось подключиться"), ex.Message);
                    DoStop(false);
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        public void Stop()
        {
            var th = new Thread(() => DoStop(false));
            th.IsBackground = true;
            th.Start();
        }

        public static string WriteConfig(Profile p, bool resolve, out BuildResult res)
        {
            res = ConfigBuilder.Build(p, resolve);
            Directory.CreateDirectory(AppPaths.RunDir);
            string path = Path.Combine(AppPaths.RunDir, "config.json");
            File.WriteAllText(path, Json.Serialize(res.Config), new UTF8Encoding(false));
            return path;
        }

        void DoStart(Profile p)
        {
            string sb = AppPaths.SingBox(p);
            if (sb == null) throw new Exception(I18n.L("sing-box.exe не найден. Положите его в папку cores\\ или укажите путь в настройках"));

            foreach (var t in p.Tunnels) Status(t.Name, t.Enabled ? I18n.L("ожидание") : I18n.L("выключен"));

            L(I18n.L("Собираю конфигурацию…"));
            ConfigBuilder.StatsPort = Proc.FreePort();
            ConfigBuilder.StatsSecret = Guid.NewGuid().ToString("N");
            var tags = new HashSet<string>();
            foreach (var t in p.Tunnels) if (t.Enabled) tags.Add(t.Name);
            tunnelTags = tags;
            lock (tunnelErrors) { tunnelErrors.Clear(); tunnelAlerted.Clear(); }
            BuildResult res;
            string cfg = WriteConfig(p, true, out res);
            foreach (var w in res.Warnings) L("! " + w);

            string outp;
            int code = Proc.Run(sb, "check -c \"" + cfg + "\" --disable-color", out outp, 30000);
            if (code != 0) throw new Exception(I18n.L("sing-box отклонил конфигурацию:\n") + outp.Trim());

            // Kill switch: пути программ и IP сервисов определяем до запуска TUN
            List<string> killApps = null, killIps = null;
            if (p.KillSwitch)
            {
                if (!KillSwitch.FirewallEnabled()) L(I18n.L("! Брандмауэр Windows выключен в одном из профилей — kill switch не сработает"));
                killApps = KillSwitch.ExpandApps(p.KillApps ?? new List<string>());
                killIps = KillSwitch.ResolveDomains(ConfigBuilder.SplitValues(p.KillDomains));
                L(I18n.L("Kill switch: программ найдено ") + killApps.Count + I18n.L(", адресов ") + killIps.Count);
                foreach (var a in killApps) L("   · " + a);
            }

            L(I18n.L("Запускаю sing-box (TUN «") + ConfigBuilder.TunName + I18n.L("»)…"));
            var started = new ManualResetEvent(false);
            lock (singboxTail) singboxTail.Clear();
            singbox = Proc.Start(sb, "run -c \"" + cfg + "\" -D \"" + AppPaths.RunDir.TrimEnd('\\') + "\" --disable-color", AppPaths.RunDir, line =>
            {
                L("[sing-box] " + line);
                lock (singboxTail) { singboxTail.Enqueue(line); while (singboxTail.Count > 80) singboxTail.Dequeue(); }
                WatchLine(line);
                if (line.Contains("sing-box started")) started.Set();
            }, null);
            var proc = singbox;
            singbox.Exited += (s, e) =>
            {
                started.Set();
                if (State == "connected")
                {
                    int exitCode = -1;
                    try { exitCode = proc.ExitCode; } catch { }
                    L(I18n.L("sing-box неожиданно завершился"));
                    string tail;
                    lock (singboxTail) tail = string.Join(Environment.NewLine, singboxTail.ToArray());
                    string lastErr = "";
                    foreach (var l in tail.Split('\n')) if (l.Contains("FATAL") || l.Contains("ERROR") || l.Contains("panic")) lastErr = l.Trim();
                    string report = CrashReport.Write(I18n.L("обрыв VPN: sing-box завершился с кодом ") + exitCode, tail);
                    History.Add(History.Dropped, I18n.L("VPN оборвался: sing-box завершился с кодом ") + exitCode,
                        lastErr.Length > 0 ? lastErr : null, SessionSeconds(), report);
                    sessionActive = false;
                    DoStop(true);
                }
            };
            if (!started.WaitOne(20000) || singbox.HasExited) throw new Exception(I18n.L("sing-box не запустился (см. лог выше)"));
            // В конфиге могут быть пароли — после запуска он не нужен
            try { File.Delete(cfg); } catch { }
            var stats = new Thread(StatsLoop) { IsBackground = true };
            stats.Start();

            if (killApps != null)
            {
                int n = KillSwitch.Enable(killApps, killIps, L);
                L(I18n.L("Kill switch включён: ") + n + I18n.L(" правил брандмауэра. Без VPN эти программы и адреса не выйдут в интернет."));
                SetKill(n > 0);
            }

            foreach (var t in p.Tunnels)
                if (t.Enabled && (TunnelTypes.IsLink(t.Type) || t.Type == TunnelTypes.WireGuard || t.Type == TunnelTypes.OpenVpn && ConfigBuilder.IsNativeOvpn(t)))
                    Status(t.Name, I18n.L("готов"));

            foreach (var ext in res.Externals)
            {
                if (State != "connecting") return;
                IRunner r = ext.Kind == TunnelTypes.OpenVpn ? (IRunner)new OpenVpnRunner(p, ext, L) : new AwgRunner(p, ext, L);
                Status(r.Name, I18n.L("подключение…"));
                L(I18n.L("Поднимаю ") + ext.Kind + I18n.L(" «") + r.Name + I18n.L("» на адаптере ") + ext.Interface);
                try
                {
                    r.Start();
                    lock (sync) runners.Add(r);
                    Status(r.Name, I18n.L("подключён"));
                }
                catch (Exception ex)
                {
                    Status(r.Name, I18n.L("ошибка"));
                    L(I18n.L("Туннель «") + r.Name + I18n.L("»: ") + ex.Message);
                    History.Add(History.TunnelError, I18n.L("Туннель «") + r.Name + I18n.L("» не поднялся"), ex.Message);
                }
            }
            L(I18n.L("Готово. Трафик распределяется по правилам."));
            var names = new List<string>();
            foreach (var t in p.Tunnels) if (t.Enabled) names.Add(t.Name);
            sessionStart = DateTime.Now;
            sessionActive = true;
            History.Add(History.Connected, I18n.L("VPN подключён"), string.Join(", ", names.ToArray()) + (KillActive ? "  ·  kill switch" : ""));
        }

        // keepKill = true: VPN упал сам — блокировку kill switch оставляем
        void DoStop(bool keepKill)
        {
            lock (sync)
            {
                if (State == "stopping" || State == "stopped") return;
                SetState("stopping");
            }
            List<IRunner> rs;
            lock (sync) { rs = new List<IRunner>(runners); runners.Clear(); }
            rs.Reverse();
            foreach (var r in rs)
            {
                try { r.Stop(); Status(r.Name, I18n.L("отключён")); } catch (Exception ex) { L(r.Name + ": " + ex.Message); }
            }
            if (singbox != null)
            {
                Proc.Kill(singbox, 0);
                singbox = null;
            }
            if (KillActive)
            {
                if (keepKill)
                {
                    L(I18n.L("Kill switch держит блокировку: защищённые программы не выйдут в интернет, пока VPN не поднимется или вы не снимете блокировку."));
                    History.Add(History.KillHold, I18n.L("Kill switch заблокировал защищённые программы"));
                }
                else { KillSwitch.Disable(L); SetKill(false); }
            }
            if (!keepKill && sessionActive)
                History.Add(History.Disconnected, I18n.L("VPN отключён"), null, SessionSeconds());
            sessionActive = false;
            L(I18n.L("Отключено."));
            SetState("stopped");
        }

        public void StopBlocking() { DoStop(false); }
    }
}
