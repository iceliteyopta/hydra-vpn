using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Hydra
{
    public class HistoryEvent
    {
        public string Time { get; set; }     // ISO 8601, локальное время
        public string Kind { get; set; }     // см. History.Kinds
        public string Text { get; set; }
        public string Details { get; set; }
        public long Duration { get; set; }   // секунды (для сессий)
        public string File { get; set; }     // путь к отчёту, если есть

        public DateTime When()
        {
            DateTime t;
            return DateTime.TryParse(Time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t) ? t : DateTime.MinValue;
        }
    }

    // История событий: подключения, отключения, обрывы, ошибки, краши. Хранится в data\history.jsonl (строка = событие).
    public static class History
    {
        public const string AppStart = "app_start", AppExit = "app_exit", Connected = "connected", Disconnected = "disconnected",
            Dropped = "dropped", Error = "error", TunnelError = "tunnel_error", Crash = "crash",
            KillHold = "kill_hold", KillRelease = "kill_release", Update = "update", Reconnect = "reconnect";

        static readonly object sync = new object();
        public static event Action<HistoryEvent> Added;
        const int MaxEvents = 5000;

        static string FilePath { get { return Path.Combine(AppPaths.DataDir, "history.jsonl"); } }

        public static HistoryEvent Add(string kind, string text, string details = null, long duration = 0, string file = null)
        {
            var e = new HistoryEvent { Time = DateTime.Now.ToString("o", CultureInfo.InvariantCulture), Kind = kind, Text = text, Details = details, Duration = duration, File = file };
            try
            {
                lock (sync)
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    string line = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(e);
                    System.IO.File.AppendAllText(FilePath, line + "\n", new UTF8Encoding(false));
                }
            }
            catch { }
            FileLog.Write("[history] " + kind + ": " + text + (string.IsNullOrEmpty(details) ? "" : " — " + details));
            var h = Added; if (h != null) try { h(e); } catch { }
            return e;
        }

        // Последние события, новые первыми
        public static List<HistoryEvent> Load()
        {
            var list = new List<HistoryEvent>();
            try
            {
                lock (sync)
                {
                    if (!System.IO.File.Exists(FilePath)) return list;
                    var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                    var lines = System.IO.File.ReadAllLines(FilePath, Encoding.UTF8);
                    int start = Math.Max(0, lines.Length - MaxEvents);
                    for (int i = lines.Length - 1; i >= start; i--)
                    {
                        if (lines[i].Length == 0) continue;
                        try { list.Add(ser.Deserialize<HistoryEvent>(lines[i])); } catch { }
                    }
                    // Обрезаем файл, если разросся
                    if (lines.Length > MaxEvents * 2)
                    {
                        var keep = new string[MaxEvents];
                        Array.Copy(lines, lines.Length - MaxEvents, keep, 0, MaxEvents);
                        System.IO.File.WriteAllLines(FilePath, keep, new UTF8Encoding(false));
                    }
                }
            }
            catch { }
            return list;
        }

        public static void Clear()
        {
            lock (sync) { try { System.IO.File.Delete(FilePath); } catch { } }
        }

        // ---- аварийное завершение: флаг-файл живёт, пока Hydra работает ----
        static string FlagPath { get { return Path.Combine(AppPaths.DataDir, "running.flag"); } }

        public static void MarkStarted()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                if (System.IO.File.Exists(FlagPath))
                {
                    string since = System.IO.File.ReadAllText(FlagPath);
                    Add(Crash, I18n.L("Hydra была закрыта аварийно"), I18n.L("процесс завершили, произошёл сбой или выключился компьютер. Запуск был: ") + since);
                }
                System.IO.File.WriteAllText(FlagPath, DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));
            }
            catch { }
            Add(AppStart, I18n.L("Hydra запущена"), I18n.L("версия ") + Updater.CurrentVersion);
        }

        public static void MarkExited()
        {
            Add(AppExit, I18n.L("Hydra закрыта"));
            try { System.IO.File.Delete(FlagPath); } catch { }
        }

        public static string FormatDuration(long sec)
        {
            if (sec <= 0) return "";
            var t = TimeSpan.FromSeconds(sec);
            if (t.TotalHours >= 1) return string.Format(I18n.L("{0} ч {1} мин"), (int)t.TotalHours, t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format(I18n.L("{0} мин {1} с"), t.Minutes, t.Seconds);
            return string.Format(I18n.L("{0} с"), t.Seconds);
        }
    }

    // Журнал на диске: data\logs\hydra-YYYY-MM-DD.log, хранится 14 дней. Плюс кольцевой буфер последних строк для отчётов о сбоях.
    public static class FileLog
    {
        static readonly object sync = new object();
        static StreamWriter writer;
        static string currentDay;
        static readonly Queue<string> recent = new Queue<string>();
        const int RecentMax = 300;
        const long MaxDayBytes = 50L * 1024 * 1024;

        public static string Dir { get { return Path.Combine(AppPaths.DataDir, "logs"); } }

        public static string PathFor(DateTime day) { return Path.Combine(Dir, "hydra-" + day.ToString("yyyy-MM-dd") + ".log"); }

        public static void Write(string line)
        {
            lock (sync)
            {
                recent.Enqueue(line);
                while (recent.Count > RecentMax) recent.Dequeue();
                try
                {
                    string day = DateTime.Now.ToString("yyyy-MM-dd");
                    if (writer == null || day != currentDay)
                    {
                        if (writer != null) writer.Dispose();
                        Directory.CreateDirectory(Dir);
                        writer = new StreamWriter(new FileStream(PathFor(DateTime.Now), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                        currentDay = day;
                        Cleanup();
                    }
                    if (writer.BaseStream.Length < MaxDayBytes)
                        writer.WriteLine(line.StartsWith(DateTime.Now.ToString("HH:")) || line.Length > 8 && line[2] == ':' && line[5] == ':' ? line : DateTime.Now.ToString("HH:mm:ss") + "  " + line);
                }
                catch { }
            }
        }

        public static string[] Recent()
        {
            lock (sync) return recent.ToArray();
        }

        public static void Close()
        {
            lock (sync) { if (writer != null) { writer.Dispose(); writer = null; } }
        }

        static void Cleanup()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Dir, "*.*"))
                    if (System.IO.File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) System.IO.File.Delete(f);
            }
            catch { }
        }
    }

    // Отчёты о сбоях: data\logs\crash-YYYYMMDD-HHMMSS.txt
    public static class CrashReport
    {
        public static string Write(string title, string body)
        {
            try
            {
                Directory.CreateDirectory(FileLog.Dir);
                string path = Path.Combine(FileLog.Dir, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                var sb = new StringBuilder();
                sb.AppendLine("Hydra " + Updater.CurrentVersion + " — " + title);
                sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "   Windows " + Environment.OSVersion.Version + (Environment.Is64BitProcess ? " x64" : " x86"));
                sb.AppendLine();
                sb.AppendLine(body);
                sb.AppendLine();
                sb.AppendLine("---- " + I18n.L("последние строки журнала") + " ----");
                foreach (var l in FileLog.Recent()) sb.AppendLine(l);
                System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch { return null; }
        }

        public static string ForException(Exception ex, string where)
        {
            string path = Write(I18n.L("сбой: ") + where, ex.ToString());
            History.Add(History.Crash, I18n.L("Сбой Hydra: ") + ex.GetType().Name, ex.Message, 0, path);
            return path;
        }
    }
}
