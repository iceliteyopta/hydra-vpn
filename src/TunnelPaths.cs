using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hydra
{
    internal sealed class TunnelPaths
    {
        internal readonly string Group, Direct, Relay;

        internal TunnelPaths(string group, HashSet<string> reserved)
        {
            Group = group;
            Direct = Reserve(group + " · direct", reserved);
            Relay = Reserve(group + " · relay", reserved);
        }

        static string Reserve(string tag, HashSet<string> reserved)
        {
            string candidate = tag;
            for (int n = 2; !reserved.Add(candidate); n++) candidate = tag + " " + n;
            return candidate;
        }

        internal void AddOutbounds(Dictionary<string, object> direct, string server, List<object> outbounds)
        {
            var relay = new Dictionary<string, object>(direct);
            relay["server"] = server;
            // Authenticate the VPN server's certificate even when its link uses an IP literal.
            var tls = new Dictionary<string, object>((Dictionary<string, object>)direct["tls"]);
            if (!tls.ContainsKey("server_name")) tls["server_name"] = direct["server"];
            relay["tls"] = tls;
            direct["tag"] = Direct;
            relay["tag"] = Relay;
            outbounds.Add(D.New("type", "selector", "tag", Group, "outbounds", new[] { Relay, Direct },
                "default", Relay, "interrupt_exist_connections", true));
            outbounds.Add(relay);
            outbounds.Add(direct);
        }
    }

    // A controller and its credentials belong to exactly one sing-box process.
    internal sealed class PathRecovery : IDisposable
    {
        readonly TunnelPaths paths;
        readonly int port;
        readonly string secret;
        readonly Func<bool> enabled;
        readonly Action<string> log;
        Timer timer;
        int running;

        internal PathRecovery(TunnelPaths paths, int port, string secret, Func<bool> enabled, Action<string> log)
        {
            this.paths = paths;
            this.port = port;
            this.secret = secret;
            this.enabled = enabled;
            this.log = log;
        }

        internal void StartMonitoring()
        {
            // An established QUIC stream can stall without tagged dial errors.
            timer = new Timer(_ => Check(true), null, 45000, 45000);
        }

        public void Dispose() { if (timer != null) timer.Dispose(); }

        internal void Check(bool periodic = false)
        {
            if (!enabled() || Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (periodic && Probe(Selected(), ProbeSites[0])) return;
                    CheckPaths();
                }
                catch (Exception ex) { if (enabled()) Report(I18n.L("Ошибка проверки пути") + " (" + ex.Message + ")"); }
                finally { Interlocked.Exchange(ref running, 0); }
            });
        }

        static readonly string[] ProbeSites = { "https://1.1.1.1/cdn-cgi/trace", "https://www.gstatic.com/generate_204" };

        void CheckPaths()
        {
            int attempt = 0;
            while (enabled())
            {
                string current = Selected();
                string spare = current == paths.Relay ? paths.Direct : paths.Relay;
                string site = ProbeSites[attempt++ % ProbeSites.Length];
                var checkSpare = Task.Factory.StartNew(() => Probe(spare, site));
                bool currentOk = Probe(current, site);
                bool spareOk = checkSpare.Result;
                if (!enabled()) return;
                if (currentOk)
                {
                    Report(I18n.L("Текущий путь отвечает") + " (" + Label(current) + ")");
                    return;
                }
                if (spareOk)
                {
                    if (Selected() != current || !enabled()) return;
                    Request(paths.Group, "", Json.Serialize(D.New("name", spare)));
                    if (Selected() != spare) throw new InvalidOperationException(I18n.L("Не удалось подтвердить переключение"));
                    string msg = I18n.L("Путь переключён") + " (" + Label(current) + " → " + Label(spare) + ")";
                    Report(msg);
                    History.Add(History.PathSwitch, paths.Group + " · " + msg);
                    return;
                }
                if (attempt == 1) Report(I18n.L("Оба пути не ответили. Повтор через 30 с."));
                for (int i = 0; i < 30 && enabled(); i++) Thread.Sleep(1000);
            }
        }

        void Report(string message) { log(paths.Group + " · " + message); }
        string Label(string tag) { return I18n.L(tag == paths.Direct ? "без ретранслятора" : "через ретранслятор"); }

        string Selected()
        {
            var status = Json.Deserialize<Dictionary<string, object>>(Request(paths.Group, "", null));
            string tag = Convert.ToString(status["now"]);
            if (tag != paths.Direct && tag != paths.Relay) throw new InvalidDataException(I18n.L("Неизвестный активный путь"));
            return tag;
        }

        bool Probe(string tag, string site)
        {
            try
            {
                var result = Json.Deserialize<Dictionary<string, int>>(Request(tag,
                    "/delay?timeout=6000&url=" + Uri.EscapeDataString(site), null));
                return result["delay"] > 0;
            }
            catch (WebException ex)
            {
                if (ex.Response != null) ex.Response.Close();
                return false;
            }
        }

        string Request(string tag, string suffix, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port +
                "/proxies/" + Uri.EscapeDataString(tag) + suffix);
            req.Proxy = null;
            req.ConnectionGroupName = paths.Group;
            req.AllowAutoRedirect = false;
            req.Timeout = req.ReadWriteTimeout = 8500;
            req.Headers["Authorization"] = "Bearer " + secret;
            if (body != null)
            {
                req.Method = "PUT";
                req.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream())) return sr.ReadToEnd();
        }
    }
}
