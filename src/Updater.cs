using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace Hydra
{
    // Проверка обновлений через GitHub Releases и самообновление exe.
    public static class Updater
    {
        public const string Repo = "iceliteyopta/hydra-vpn";
        public const string CurrentVersion = "2.2.0";

        public class Release
        {
            public string Tag, Version, HtmlUrl, ExeUrl, ExeSha256;
        }

        // Возвращает релиз, если на GitHub есть версия новее текущей, иначе null.
        public static Release CheckLatest()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
            req.UserAgent = "Hydra/" + CurrentVersion;
            req.Accept = "application/vnd.github+json";
            req.Timeout = 15000;
            string json;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
                json = sr.ReadToEnd();

            var o = Json.DeserializeObject(json) as Dictionary<string, object>;
            if (o == null) return null;
            string tag = Str(o, "tag_name");
            Version latest, current;
            if (!TryParse(tag, out latest) || !TryParse(CurrentVersion, out current) || latest <= current) return null;

            var r = new Release { Tag = tag, Version = tag.TrimStart('v', 'V'), HtmlUrl = Str(o, "html_url") };
            var assets = o.ContainsKey("assets") ? o["assets"] as IEnumerable : null;
            if (assets != null)
                foreach (var a in assets)
                {
                    var ad = a as Dictionary<string, object>;
                    if (ad == null || !string.Equals(Str(ad, "name"), "Hydra.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    r.ExeUrl = Str(ad, "browser_download_url");
                    string digest = Str(ad, "digest") ?? "";
                    if (digest.StartsWith("sha256:")) r.ExeSha256 = digest.Substring(7).ToLowerInvariant();
                }
            return r;
        }

        // Скачивает новый exe, проверяет SHA-256, подменяет текущий файл. Возвращает путь к exe.
        public static string DownloadAndReplace(Release r, Action<int> progress)
        {
            if (string.IsNullOrEmpty(r.ExeUrl)) throw new Exception(I18n.L("в релизе нет Hydra.exe"));
            if (string.IsNullOrEmpty(r.ExeSha256)) throw new Exception(I18n.L("GitHub не сообщил контрольную сумму файла"));
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            string self = Application.ExecutablePath;
            string dir = Path.GetDirectoryName(self);
            string tmp = Path.Combine(dir, "Hydra.update.exe");
            var req = (HttpWebRequest)WebRequest.Create(r.ExeUrl);
            req.UserAgent = "Hydra/" + CurrentVersion;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var input = resp.GetResponseStream())
            using (var output = File.Create(tmp))
            {
                long total = resp.ContentLength, done = 0;
                var buf = new byte[81920];
                int n, last = -1;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    output.Write(buf, 0, n);
                    done += n;
                    int pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct != last && progress != null) { last = pct; progress(pct); }
                }
            }

            string hash;
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(tmp))
                hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
            if (hash != r.ExeSha256)
            {
                try { File.Delete(tmp); } catch { }
                throw new Exception(I18n.L("контрольная сумма не совпала — обновление отменено"));
            }

            // Запущенный exe нельзя перезаписать, но можно переименовать
            string old = self + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(self, old);
            File.Move(tmp, self);
            return self;
        }

        public static void Restart(string exe)
        {
            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false });
        }

        // Удаляем Hydra.exe.old, оставшийся от прошлого обновления
        public static void CleanupOld()
        {
            try
            {
                string old = Application.ExecutablePath + ".old";
                if (File.Exists(old)) File.Delete(old);
                string tmp = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Hydra.update.exe");
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch { }
        }

        static bool TryParse(string s, out Version v)
        {
            v = null;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.TrimStart('v', 'V');
            int dash = s.IndexOf('-');
            if (dash >= 0) s = s.Substring(0, dash);
            return Version.TryParse(s, out v);
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null;
        }
    }
}
