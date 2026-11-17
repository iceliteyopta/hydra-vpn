using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Hydra
{
    // Логотип Hydra: значок для exe (.ico), окна, панели задач и трея
    public static class Branding
    {
        // state: "on" — подключено, "off" — нет, "warn" — сбой / kill switch держит
        public static Bitmap Draw(int size, string state)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size;
                float pad = Math.Max(0.5f, s * 0.04f);
                var r = new RectangleF(pad, pad, s - pad * 2, s - pad * 2);

                Color a, b;
                if (state == "on" || state == "brand") { a = Theme.Accent; b = Theme.Accent2; }
                else if (state == "warn") { a = Theme.Warn; b = Color.FromArgb(234, 88, 12); }
                else { a = Color.FromArgb(148, 163, 184); b = Color.FromArgb(100, 116, 139); }

                // фон: тёмный скруглённый квадрат (как у современных приложений) с градиентной рамкой
                using (var path = Theme.Round(r, s * 0.24f))
                {
                    using (var br = new LinearGradientBrush(r, Color.FromArgb(20, 26, 44), Color.FromArgb(9, 12, 20), 90f)) g.FillPath(br, path);
                    if (size >= 24)
                        using (var br = new LinearGradientBrush(r, a, b, 45f))
                        using (var pen = new Pen(br, Math.Max(1f, s * 0.035f))) g.DrawPath(pen, path);
                }

                // три «шеи» гидры из одного основания, на концах — «головы» (узлы туннелей)
                bool tiny = size <= 20;
                float w = Math.Max(1.3f, s * (tiny ? 0.10f : 0.075f));
                float headR = Math.Max(1.4f, s * (tiny ? 0.10f : 0.085f));
                var basePt = new PointF(s * 0.5f, s * 0.80f);
                var heads = new[] { new PointF(s * 0.27f, s * 0.34f), new PointF(s * 0.5f, s * 0.22f), new PointF(s * 0.73f, s * 0.34f) };
                using (var br = new LinearGradientBrush(new RectangleF(0, 0, s, s), a, b, 60f))
                using (var pen = new Pen(br, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    // левая и правая шеи изгибаются наружу, средняя — прямая
                    g.DrawBezier(pen, basePt, new PointF(s * 0.5f, s * 0.58f), new PointF(s * 0.22f, s * 0.56f), heads[0]);
                    g.DrawLine(pen, basePt, heads[1]);
                    g.DrawBezier(pen, basePt, new PointF(s * 0.5f, s * 0.58f), new PointF(s * 0.78f, s * 0.56f), heads[2]);
                    foreach (var h in heads) g.FillEllipse(br, h.X - headR, h.Y - headR, headR * 2, headR * 2);
                    float bd = Math.Max(1.5f, s * 0.07f);
                    g.FillEllipse(br, basePt.X - bd, basePt.Y - bd, bd * 2, bd * 2);
                }
            }
            return bmp;
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(int eventId, int flags, string item1, IntPtr item2);

        static Icon bigIcon, smallIcon;

        // Явно ставим окну большой (панель задач, Alt+Tab) и маленький (заголовок) значки нужного под DPI размера
        public static void SetWindowIcons(IntPtr hwnd)
        {
            try
            {
                int big = Math.Max(32, GetSystemMetrics(11));   // SM_CXICON
                int small = Math.Max(16, GetSystemMetrics(49)); // SM_CXSMICON
                if (bigIcon == null) bigIcon = MakeIcon(big, "brand");
                if (smallIcon == null) smallIcon = MakeIcon(small, "brand");
                SendMessage(hwnd, 0x0080, (IntPtr)1, bigIcon.Handle);   // WM_SETICON, ICON_BIG
                SendMessage(hwnd, 0x0080, (IntPtr)0, smallIcon.Handle); // WM_SETICON, ICON_SMALL
            }
            catch { }
        }

        // Windows кэширует значок exe по пути; после обновления на месте просим оболочку перечитать его
        public static void RefreshShellIcon(string exePath)
        {
            try
            {
                SHChangeNotify(0x00002000, 0x0005, exePath, IntPtr.Zero); // SHCNE_UPDATEITEM, SHCNF_PATHW
                SHChangeNotify(0x08000000, 0x0000, null, IntPtr.Zero);    // SHCNE_ASSOCCHANGED
            }
            catch { }
        }

        public static Icon MakeIcon(int size, string state)
        {
            using (var bmp = Draw(size, state))
            {
                IntPtr h = bmp.GetHicon();
                var tmp = Icon.FromHandle(h);
                var icon = (Icon)tmp.Clone();
                DestroyIcon(h);
                return icon;
            }
        }

        // Многоразмерный .ico (PNG внутри) — для ресурсов exe
        public static void WriteIco(string path)
        {
            using (var fs = File.Create(path)) WriteIco(fs);
        }

        // Значок окна (много размеров: панель задач, Alt+Tab, заголовок)
        public static Icon AppIcon()
        {
            var ms = new MemoryStream();
            WriteIco(ms);
            ms.Position = 0;
            return new Icon(ms);
        }

        public static void WriteIco(Stream fs)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
            var images = new List<byte[]>();
            foreach (int sz in sizes)
                using (var bmp = Draw(sz, "brand"))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    images.Add(ms.ToArray());
                }
            var w = new BinaryWriter(fs);
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
                w.Flush();
            }
        }
    }
}
