using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Hydra
{
    // Все дочерние процессы помещаются в Job: если клиент упадёт, они тоже завершатся
    static class Job
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int type, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint len);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic;
            public IO_COUNTERS Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        static IntPtr handle = IntPtr.Zero;
        static readonly object sync = new object();

        public static void Add(Process p)
        {
            try
            {
                lock (sync)
                {
                    if (handle == IntPtr.Zero)
                    {
                        handle = CreateJobObject(IntPtr.Zero, null);
                        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                        info.Basic.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
                        SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
                    }
                    AssignProcessToJobObject(handle, p.Handle);
                }
            }
            catch { }
        }
    }

    static class Proc
    {
        public static Process Start(string exe, string args, string workDir, Action<string> onLine, Dictionary<string, string> env)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir ?? Path.GetDirectoryName(exe),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler h = (s, e) => { if (e.Data != null && onLine != null) onLine(e.Data); };
            p.OutputDataReceived += h;
            p.ErrorDataReceived += h;
            p.Start();
            Job.Add(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        public static int Run(string exe, string args, out string output, int timeoutMs)
        {
            var sb = new StringBuilder();
            var p = Start(exe, args, null, l => { lock (sb) sb.AppendLine(l); }, null);
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = sb.ToString(); return -1; }
            p.WaitForExit();
            lock (sb) output = sb.ToString();
            return p.ExitCode;
        }

        public static void Kill(Process p, int graceMs)
        {
            if (p == null) return;
            try
            {
                if (!p.HasExited && graceMs > 0) p.WaitForExit(graceMs);
                if (!p.HasExited) p.Kill();
                p.WaitForExit(3000);
            }
            catch { }
        }

        public static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public static bool WaitInterface(string name, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    if (string.Equals(ni.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
                Thread.Sleep(300);
            }
            return false;
        }

        public static string Netsh(string args, Action<string> log)
        {
            string outp;
            int code = Run("netsh.exe", args, out outp, 15000);
            if (code != 0 && log != null) log("netsh " + args + " → " + outp.Trim());
            return outp;
        }
    }

    public interface IRunner
    {
        string Name { get; }
        void Start();
        void Stop();
    }

    // ---------------- OpenVPN ----------------
    public class OpenVpnRunner : IRunner
    {
        readonly Profile p; readonly ExternalTunnel ext; readonly Action<string> log;
        Process proc; int mgmtPort; string authFile;

        public OpenVpnRunner(Profile p, ExternalTunnel ext, Action<string> log) { this.p = p; this.ext = ext; this.log = log; }
        public string Name { get { return ext.Tunnel.Name; } }

        public void Start()
        {
            var t = ext.Tunnel;
            string exe = AppPaths.OpenVpn(p);
            if (exe == null) throw new Exception(I18n.L("openvpn.exe не найден. Установите OpenVPN 2.6+ или укажите путь в настройках"));
            string dir = t.Dir();
            string text = File.ReadAllText(t.ConfigPath());
            bool haveCreds = !string.IsNullOrEmpty(t.OvpnUser);
            if (OvpnConf.NeedsAuth(text) && !haveCreds) throw new Exception(I18n.L("конфиг требует логин и пароль — задайте их кнопкой «Логин OpenVPN»"));

            string driver = (t.OvpnDriver ?? "dco").ToLowerInvariant();
            EnsureAdapter(exe, ext.Interface, driver);

            string runCfg = Path.Combine(dir, "run.ovpn");
            File.WriteAllText(runCfg, OvpnConf.Sanitize(text, haveCreds));
            mgmtPort = Proc.FreePort();

            var args = new StringBuilder();
            args.AppendFormat("--config \"{0}\" --cd \"{1}\" --dev-node \"{2}\" ", runCfg, dir.TrimEnd('\\'), ext.Interface);
            args.Append("--route-nopull --route 0.0.0.0 0.0.0.0 vpn_gateway 9999 ");
            args.AppendFormat("--management 127.0.0.1 {0} --verb 3 --auth-nocache --auth-retry none ", mgmtPort);
            if (driver == "wintun") args.Append("--windows-driver wintun ");
            else if (driver == "tap") args.Append("--windows-driver tap-windows6 ");
            if (haveCreds)
            {
                authFile = Path.Combine(dir, "auth.tmp");
                File.WriteAllText(authFile, t.OvpnUser + "\n" + Secret.Unprotect(t.OvpnPassProtected) + "\n");
                args.AppendFormat("--auth-user-pass \"{0}\" ", authFile);
            }

            var ready = new ManualResetEvent(false);
            string fail = null;
            proc = Proc.Start(exe, args.ToString(), dir, line =>
            {
                log("[" + Name + "] " + line);
                if (line.Contains("Initialization Sequence Completed")) ready.Set();
                else if (line.Contains("AUTH_FAILED")) { fail = I18n.L("неверный логин или пароль"); ready.Set(); }
                else if (line.Contains("Exiting due to fatal error")) { fail = I18n.L("OpenVPN завершился с ошибкой (см. лог)"); ready.Set(); }
            }, null);
            proc.Exited += (s, e) => ready.Set();

            if (!ready.WaitOne(90000)) { Stop(); throw new Exception(I18n.L("OpenVPN не подключился за 90 секунд")); }
            if (fail != null || proc.HasExited) { Stop(); throw new Exception(fail ?? I18n.L("OpenVPN завершился (см. лог)")); }
        }

        void EnsureAdapter(string exe, string name, string driver)
        {
            string tapctl = Path.Combine(Path.GetDirectoryName(exe), "tapctl.exe");
            if (!File.Exists(tapctl)) { log("[" + Name + I18n.L("] tapctl.exe не найден, адаптер ") + name + I18n.L(" должен существовать")); return; }
            string outp;
            Proc.Run(tapctl, "list", out outp, 15000);
            foreach (var l in outp.Split('\n'))
                if (l.Trim().EndsWith("\t" + name) || l.Trim().EndsWith(" " + name) || l.Trim() == name) return;
            string hwid = driver == "wintun" ? "wintun" : driver == "tap" ? "root\\tap0901" : "ovpn-dco";
            log("[" + Name + I18n.L("] создаю сетевой адаптер ") + name + " (" + hwid + ")");
            int code = Proc.Run(tapctl, "create --hwid " + hwid + " --name " + name, out outp, 30000);
            if (code != 0) throw new Exception(I18n.L("не удалось создать адаптер: ") + outp.Trim());
        }

        public void Stop()
        {
            if (proc != null && !proc.HasExited)
            {
                try
                {
                    using (var c = new TcpClient())
                    {
                        c.Connect(IPAddress.Loopback, mgmtPort);
                        var b = Encoding.ASCII.GetBytes("signal SIGTERM\r\n");
                        c.GetStream().Write(b, 0, b.Length);
                    }
                }
                catch { }
                Proc.Kill(proc, 4000);
            }
            proc = null;
            if (authFile != null) { try { File.Delete(authFile); } catch { } authFile = null; }
        }
    }

    // ---------------- AmneziaWG (amneziawg-go + UAPI) ----------------
    public class AwgRunner : IRunner
    {
        readonly Profile p; readonly ExternalTunnel ext; readonly Action<string> log;
        Process proc;

        public AwgRunner(Profile p, ExternalTunnel ext, Action<string> log) { this.p = p; this.ext = ext; this.log = log; }
        public string Name { get { return ext.Tunnel.Name; } }

        public void Start()
        {
            string exe = AppPaths.AwgGo(p);
            if (exe == null) throw new Exception(I18n.L("amneziawg-go.exe не найден. Положите его (и wintun.dll) в cores\\amneziawg\\"));
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(exe), "wintun.dll")))
                log("[" + Name + I18n.L("] внимание: рядом с amneziawg-go.exe нет wintun.dll"));

            string ifname = ext.Interface;
            var env = new Dictionary<string, string> { { "LOG_LEVEL", "error" } };
            proc = Proc.Start(exe, ifname, Path.GetDirectoryName(exe), l => log("[" + Name + "] " + l), env);

            string pipe = WaitPipe(ifname, 20000);
            if (pipe == null) { Stop(); throw new Exception(I18n.L("amneziawg-go не создал управляющий канал (запущено от администратора?)")); }

            string resp = Uapi(pipe, BuildUapi(ext.Wg, ext.AwgEndpoint));
            if (!resp.Contains("errno=0")) { Stop(); throw new Exception(I18n.L("amneziawg-go отклонил конфиг: ") + resp.Trim()); }

            if (!Proc.WaitInterface(ifname, 15000)) { Stop(); throw new Exception(I18n.L("адаптер ") + ifname + I18n.L(" не появился")); }
            ConfigureInterface(ifname);
            log("[" + Name + I18n.L("] туннель поднят, endpoint ") + ext.AwgEndpoint);
        }

        string BuildUapi(WgConf wg, string endpoint)
        {
            var sb = new StringBuilder();
            sb.Append("set=1\n");
            sb.Append("private_key=").Append(WgConf.KeyHex(WgConf.Get(wg.Iface, "PrivateKey"))).Append('\n');
            string lp = WgConf.Get(wg.Iface, "ListenPort");
            if (!string.IsNullOrEmpty(lp)) sb.Append("listen_port=").Append(lp).Append('\n');
            foreach (var k in WgConf.AwgKeys)
            {
                string v = WgConf.Get(wg.Iface, k);
                if (!string.IsNullOrEmpty(v)) sb.Append(k.ToLowerInvariant()).Append('=').Append(v).Append('\n');
            }
            sb.Append("replace_peers=true\n");
            for (int i = 0; i < wg.Peers.Count; i++)
            {
                var pr = wg.Peers[i];
                sb.Append("public_key=").Append(WgConf.KeyHex(WgConf.Get(pr, "PublicKey"))).Append('\n');
                string psk = WgConf.Get(pr, "PresharedKey");
                if (!string.IsNullOrEmpty(psk)) sb.Append("preshared_key=").Append(WgConf.KeyHex(psk)).Append('\n');
                if (i == 0) sb.Append("endpoint=").Append(endpoint).Append('\n');
                string ka = WgConf.Get(pr, "PersistentKeepalive");
                if (!string.IsNullOrEmpty(ka)) sb.Append("persistent_keepalive_interval=").Append(ka).Append('\n');
                sb.Append("replace_allowed_ips=true\n");
                var allowed = WgConf.SplitList(WgConf.Get(pr, "AllowedIPs"));
                if (allowed.Count == 0) allowed.Add("0.0.0.0/0");
                foreach (var a in allowed) sb.Append("allowed_ip=").Append(a).Append('\n');
            }
            sb.Append('\n');
            return sb.ToString();
        }

        static string WaitPipe(string ifname, int timeoutMs)
        {
            var known = new[] { @"ProtectedPrefix\Administrators\AmneziaWG\" + ifname, @"ProtectedPrefix\Administrators\WireGuard\" + ifname };
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    foreach (var f in Directory.GetFiles(@"\\.\pipe\"))
                        if (f.EndsWith("\\" + ifname, StringComparison.OrdinalIgnoreCase))
                            return f.Substring(@"\\.\pipe\".Length);
                }
                catch { }
                foreach (var k in known)
                {
                    try
                    {
                        using (var c = new NamedPipeClientStream(".", k, PipeDirection.InOut)) { c.Connect(100); return k; }
                    }
                    catch { }
                }
                Thread.Sleep(300);
            }
            return null;
        }

        static string Uapi(string pipe, string request)
        {
            using (var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut))
            {
                c.Connect(5000);
                var b = Encoding.UTF8.GetBytes(request);
                c.Write(b, 0, b.Length);
                c.Flush();
                var r = new StreamReader(c, Encoding.UTF8);
                var sb = new StringBuilder();
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.Length == 0) break;
                    sb.AppendLine(line);
                }
                return sb.ToString();
            }
        }

        void ConfigureInterface(string ifname)
        {
            bool first = true;
            foreach (var a in ext.Wg.Addresses())
            {
                if (a.Contains(":")) continue; // IPv6 не настраиваем: DNS работает в режиме ipv4_only
                string ip = a, mask = "255.255.255.255";
                int sl = a.IndexOf('/');
                if (sl >= 0) { ip = a.Substring(0, sl); mask = PrefixToMask(int.Parse(a.Substring(sl + 1))); }
                if (first) Proc.Netsh("interface ipv4 set address name=\"" + ifname + "\" source=static address=" + ip + " mask=" + mask, log);
                else Proc.Netsh("interface ipv4 add address name=\"" + ifname + "\" address=" + ip + " mask=" + mask, log);
                first = false;
            }
            Proc.Netsh("interface ipv4 set subinterface \"" + ifname + "\" mtu=" + ext.Wg.Mtu() + " store=active", log);
            // Маршрут по умолчанию с огромной метрикой: системе он не мешает,
            // но нужен, чтобы sing-box мог отправлять трафик через этот адаптер (bind_interface).
            Proc.Netsh("interface ipv4 add route prefix=0.0.0.0/0 interface=\"" + ifname + "\" metric=9999 store=active", log);
        }

        static string PrefixToMask(int prefix)
        {
            uint m = prefix <= 0 ? 0 : 0xFFFFFFFFu << (32 - Math.Min(prefix, 32));
            return string.Format("{0}.{1}.{2}.{3}", m >> 24, (m >> 16) & 255, (m >> 8) & 255, m & 255);
        }

        public void Stop()
        {
            Proc.Kill(proc, 0);
            proc = null;
        }
    }
}
