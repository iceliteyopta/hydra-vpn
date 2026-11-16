using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Hydra
{
    // Тёмная «неоновая» тема Hydra
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(9, 12, 20);
        public static readonly Color Sidebar = Color.FromArgb(13, 17, 28);
        public static readonly Color Surface = Color.FromArgb(18, 23, 37);
        public static readonly Color Surface2 = Color.FromArgb(25, 31, 49);
        public static readonly Color Border = Color.FromArgb(40, 48, 72);
        public static readonly Color Text = Color.FromArgb(230, 236, 255);
        public static readonly Color Muted = Color.FromArgb(128, 140, 175);
        public static readonly Color Accent = Color.FromArgb(34, 211, 238);   // cyan
        public static readonly Color Accent2 = Color.FromArgb(168, 85, 247);  // violet
        public static readonly Color Good = Color.FromArgb(52, 211, 153);
        public static readonly Color Warn = Color.FromArgb(251, 191, 36);
        public static readonly Color Bad = Color.FromArgb(248, 113, 113);

        public static Font Ui(float size) { return new Font("Segoe UI", size, FontStyle.Regular); }
        public static Font UiSemi(float size) { return new Font("Segoe UI Semibold", size, FontStyle.Regular); }
        public static Font UiBold(float size) { return new Font("Segoe UI", size, FontStyle.Bold); }
        public static Font Mono(float size)
        {
            var f = new Font("Cascadia Mono", size);
            if (f.Name == "Cascadia Mono") return f;
            f.Dispose();
            return new Font("Consolas", size);
        }

        static string iconFamily;
        public static Font Icon(float size)
        {
            if (iconFamily == null)
            {
                iconFamily = "Segoe MDL2 Assets";
                using (var f = new Font("Segoe Fluent Icons", 10f)) if (f.Name == "Segoe Fluent Icons") iconFamily = "Segoe Fluent Icons";
            }
            return new Font(iconFamily, size);
        }

        // Глифы Segoe Fluent Icons / MDL2
        public const string IcHome = "", IcGlobe = "", IcList = "", IcShield = "", IcSettings = "",
            IcLog = "", IcAdd = "", IcDelete = "", IcEdit = "", IcLink = "", IcPower = "",
            IcUp = "", IcDown = "", IcMore = "", IcKey = "", IcFolder = "", IcCode = "",
            IcFile = "", IcLock = "", IcUnlock = "", IcCopy = "", IcClear = "", IcApp = "",
            IcTemplate = "", IcNetwork = "";

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Max(1, radius * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Hq(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c); }

        // ---- тёмный заголовок окна (Windows 10 20H1+ / 11) ----
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void DarkTitleBar(Form f)
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(f.Handle, 20, ref on, 4);
                int caption = ColorTranslator.ToWin32(Bg);
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
                int text = ColorTranslator.ToWin32(Text);
                DwmSetWindowAttribute(f.Handle, 36, ref text, 4);
            }
            catch { }
        }

        // Покраска стандартных контролов
        public static void Apply(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is TextBox)
                {
                    var t = (TextBox)c;
                    if (t.BorderStyle != BorderStyle.None)
                    {
                        t.BackColor = Surface2; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle;
                    }
                }
                else if (c is ComboBox)
                {
                    var cb = (ComboBox)c;
                    cb.BackColor = Surface2; cb.ForeColor = Text; cb.FlatStyle = FlatStyle.Flat;
                }
                else if (c is CheckBox || c is Label)
                {
                    if (c.ForeColor == SystemColors.ControlText) c.ForeColor = Text;
                    c.BackColor = Color.Transparent;
                }
                else if (c is ListBox)
                {
                    var lb = (ListBox)c;
                    lb.BackColor = Surface2; lb.ForeColor = Text; lb.BorderStyle = BorderStyle.None;
                }
                Apply(c);
            }
        }

        public static void StyleGrid(DataGridView g)
        {
            g.BackgroundColor = Surface;
            g.BorderStyle = BorderStyle.None;
            g.GridColor = Border;
            g.EnableHeadersVisualStyles = false;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.ColumnHeadersDefaultCellStyle.BackColor = Surface;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
            g.ColumnHeadersDefaultCellStyle.Font = UiSemi(9f);
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Surface;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
            g.ColumnHeadersHeight = 36;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.DefaultCellStyle.BackColor = Surface;
            g.DefaultCellStyle.ForeColor = Text;
            g.DefaultCellStyle.SelectionBackColor = Alpha(Accent, 45);
            g.DefaultCellStyle.SelectionForeColor = Text;
            g.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            g.DefaultCellStyle.Font = Ui(9.5f);
            g.RowTemplate.Height = 38;
            g.RowHeadersVisible = false;
            g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(21, 26, 42);
        }
    }
}
