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

        // Снять блокировку kill switch вручную (когда VPN уже не работает)
        public void ReleaseKill()
        {
            var th = new Thread(() => { KillSwitch.Disable(L); SetKill(false); });
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
                catch (Exception ex) { L(I18n.L("ОШИБКА: ") + ex.Message); DoStop(false); }
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
            singbox = Proc.Start(sb, "run -c \"" + cfg + "\" -D \"" + AppPaths.RunDir.TrimEnd('\\') + "\" --disable-color", AppPaths.RunDir, line =>
            {
                L("[sing-box] " + line);
                if (line.Contains("sing-box started")) started.Set();
            }, null);
            singbox.Exited += (s, e) =>
            {
                started.Set();
                if (State == "connected")
                {
                    L(I18n.L("sing-box неожиданно завершился"));
                    DoStop(true);
                }
            };
            if (!started.WaitOne(20000) || singbox.HasExited) throw new Exception(I18n.L("sing-box не запустился (см. лог выше)"));
            // В конфиге могут быть пароли — после запуска он не нужен
            try { File.Delete(cfg); } catch { }

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
                }
            }
            L(I18n.L("Готово. Трафик распределяется по правилам."));
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
                if (keepKill) L(I18n.L("Kill switch держит блокировку: защищённые программы не выйдут в интернет, пока VPN не поднимется или вы не снимете блокировку."));
                else { KillSwitch.Disable(L); SetKill(false); }
            }
            L(I18n.L("Отключено."));
            SetState("stopped");
        }

        public void StopBlocking() { DoStop(false); }
    }
}
