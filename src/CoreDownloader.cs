using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace Hydra
{
    // Автоматическая загрузка ядра sing-box с официальной страницы релизов.
    // Архив проверяется по SHA-256, опубликованному GitHub для этого файла.
    public static class CoreDownloader
    {
        public const string Version = "1.14.4";
        public const string ZipSha256 = "99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df";
        public static readonly string Url = "https://github.com/SagerNet/sing-box/releases/download/v" + Version + "/sing-box-" + Version + "-windows-amd64.zip";
        public const string ReleasesPage = "https://github.com/SagerNet/sing-box/releases/tag/v" + Version;

        // progress: процент 0..100 (или -1, если размер неизвестен) и скачанные байты
        public static string Download(Action<int, long> progress)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Directory.CreateDirectory(AppPaths.CoresDir);
            string tmpDir = Path.Combine(Path.GetTempPath(), "hydra-core-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                string zip = Path.Combine(tmpDir, "sing-box.zip");
                var req = (HttpWebRequest)WebRequest.Create(Url);
                req.UserAgent = "Hydra/" + Version;
                req.Timeout = 30000;
                req.ReadWriteTimeout = 60000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var input = resp.GetResponseStream())
                using (var output = File.Create(zip))
                {
                    long total = resp.ContentLength, done = 0;
                    var buf = new byte[81920];
                    int n, last = -2;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        output.Write(buf, 0, n);
                        done += n;
                        int pct = total > 0 ? (int)(done * 100 / total) : -1;
                        if (pct != last && progress != null) { last = pct; progress(pct, done); }
                    }
                }

                string hash;
                using (var sha = SHA256.Create())
                using (var f = File.OpenRead(zip))
                    hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                if (hash != ZipSha256)
                    throw new Exception(I18n.L("контрольная сумма архива не совпала — файл повреждён или подменён"));

                string target = Path.Combine(AppPaths.CoresDir, "sing-box.exe");
                using (var archive = ZipFile.OpenRead(zip))
                {
                    ZipArchiveEntry exe = null;
                    foreach (var e in archive.Entries)
                        if (e.Name.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase)) { exe = e; break; }
                    if (exe == null) throw new Exception(I18n.L("в архиве нет sing-box.exe"));
                    string tmpExe = Path.Combine(tmpDir, "sing-box.exe");
                    exe.ExtractToFile(tmpExe, true);
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmpExe, target);
                }
                return target;
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }
    }
}
