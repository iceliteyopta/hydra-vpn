using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Hydra
{
    // Базовый контрол с двойной буферизацией и прозрачным фоном родителя
    public class PaintControl : Control
    {
        public PaintControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected Color ParentBack()
        {
            Control p = Parent;
            while (p != null && p.BackColor == Color.Transparent) p = p.Parent;
            return p != null ? p.BackColor : Theme.Bg;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(ParentBack())) e.Graphics.FillRectangle(b, ClientRectangle);
        }
    }

    public enum ButtonKind { Primary, Secondary, Ghost, Danger }

    public class FlatButton : PaintControl
    {
        bool hover, down;
        public ButtonKind Kind = ButtonKind.Secondary;
        public string Glyph;

        public FlatButton(string text, string glyph, ButtonKind kind)
        {
            Text = text; Glyph = glyph; Kind = kind;
            Font = Theme.UiSemi(9.5f);
            Cursor = Cursors.Hand;
            Height = 36;
            Margin = new Padding(0, 0, 8, 8);
            AutoSizeToText();
        }

        public void AutoSizeToText()
        {
            using (var g = CreateGraphics())
            {
                int w = string.IsNullOrEmpty(Text) ? 0 : (int)Math.Ceiling(g.MeasureString(Text, Font).Width);
                Width = (Glyph != null ? 26 : 0) + w + (string.IsNullOrEmpty(Text) ? 20 : 30);
            }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fg = Theme.Text;
            using (var path = Theme.Round(r, 9))
            {
                switch (Kind)
                {
                    case ButtonKind.Primary:
                        {
                            Color a = Theme.Accent, b = Theme.Accent2;
                            if (hover) { a = Theme.Mix(a, Color.White, 0.15f); b = Theme.Mix(b, Color.White, 0.15f); }
                            if (down) { a = Theme.Mix(a, Color.Black, 0.2f); b = Theme.Mix(b, Color.Black, 0.2f); }
                            using (var br = new LinearGradientBrush(new RectangleF(0, 0, Width, Height), a, b, LinearGradientMode.Horizontal)) g.FillPath(br, path);
                            fg = Color.FromArgb(8, 10, 18);
                            break;
                        }
                    case ButtonKind.Danger:
                        using (var br = new SolidBrush(Theme.Alpha(Theme.Bad, hover ? 60 : 35))) g.FillPath(br, path);
                        using (var pen = new Pen(Theme.Alpha(Theme.Bad, 140))) g.DrawPath(pen, path);
                        fg = Theme.Bad;
                        break;
                    case ButtonKind.Ghost:
                        if (hover) using (var br = new SolidBrush(Theme.Surface2)) g.FillPath(br, path);
                        fg = hover ? Theme.Text : Theme.Muted;
                        break;
                    default:
                        using (var br = new SolidBrush(hover ? Theme.Mix(Theme.Surface2, Theme.Accent, 0.08f) : Theme.Surface2)) g.FillPath(br, path);
                        using (var pen = new Pen(hover ? Theme.Alpha(Theme.Accent, 120) : Theme.Border)) g.DrawPath(pen, path);
                        break;
                }
            }
            if (!Enabled) fg = Theme.Alpha(fg, 90);

            float x = string.IsNullOrEmpty(Text) ? (Width - 16) / 2f : 14;
            if (Glyph != null)
            {
                using (var f = Theme.Icon(11f))
                using (var br = new SolidBrush(fg))
                {
                    var sz = g.MeasureString(Glyph, f);
                    g.DrawString(Glyph, f, br, x - 2, (Height - sz.Height) / 2f + 1);
                }
                x += 24;
            }
            if (!string.IsNullOrEmpty(Text))
                using (var br = new SolidBrush(fg))
                {
                    var sz = g.MeasureString(Text, Font);
                    if (Glyph == null) x = (Width - sz.Width) / 2f;
                    g.DrawString(Text, Font, br, x, (Height - sz.Height) / 2f);
                }
        }
    }

    public class NavButton : PaintControl
    {
        bool hover;
        bool active;
        public string Glyph;
        public bool Active { get { return active; } set { active = value; Invalidate(); } }

        public NavButton(string text, string glyph)
        {
            Text = text; Glyph = glyph;
            Font = Theme.UiSemi(10f);
            Height = 42; Cursor = Cursors.Hand;
            Margin = new Padding(0, 0, 0, 4);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            if (active || hover)
            {
                using (var path = Theme.Round(r, 10))
                {
                    if (active)
                        using (var br = new LinearGradientBrush(r, Theme.Alpha(Theme.Accent, 38), Theme.Alpha(Theme.Accent2, 14), LinearGradientMode.Horizontal))
                            g.FillPath(br, path);
                    else using (var br = new SolidBrush(Theme.Surface)) g.FillPath(br, path);
                }
            }
            if (active)
            {
                using (var bar = Theme.Round(new RectangleF(0, 10, 3.5f, Height - 21), 2))
                using (var br = new LinearGradientBrush(new RectangleF(0, 8, 4, Height - 16), Theme.Accent, Theme.Accent2, LinearGradientMode.Vertical))
                    g.FillPath(br, bar);
            }
            Color fg = active ? Theme.Text : hover ? Theme.Text : Theme.Muted;
            using (var f = Theme.Icon(12f))
            using (var br = new SolidBrush(active ? Theme.Accent : fg))
                g.DrawString(Glyph, f, br, 16, (Height - 18) / 2f);
            using (var br = new SolidBrush(fg))
            {
                var sz = g.MeasureString(Text, Font);
                g.DrawString(Text, Font, br, 46, (Height - sz.Height) / 2f);
            }
        }
    }

    public class ToggleSwitch : PaintControl
    {
        bool isOn;
        float pos;
        readonly Timer anim = new Timer { Interval = 15 };
        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            Size = new Size(46, 26);
            Cursor = Cursors.Hand;
            anim.Tick += (s, e) =>
            {
                float target = isOn ? 1 : 0;
                pos += (target - pos) * 0.35f;
                if (Math.Abs(target - pos) < 0.02f) { pos = target; anim.Stop(); }
                Invalidate();
            };
        }

        public bool Checked
        {
            get { return isOn; }
            set
            {
                if (isOn == value) return;
                isOn = value;
                if (IsHandleCreated && Visible) anim.Start(); else { pos = isOn ? 1 : 0; Invalidate(); }
                var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty);
            }
        }

        public void SetSilently(bool v) { isOn = v; pos = v ? 1 : 0; Invalidate(); }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var path = Theme.Round(r, r.Height / 2))
            {
                if (pos > 0.01f)
                    using (var br = new LinearGradientBrush(r, Theme.Alpha(Theme.Accent, (int)(255 * pos)), Theme.Alpha(Theme.Accent2, (int)(255 * pos)), LinearGradientMode.Horizontal))
                    {
                        using (var off = new SolidBrush(Theme.Surface2)) g.FillPath(off, path);
                        g.FillPath(br, path);
                    }
                else using (var off = new SolidBrush(Theme.Surface2)) g.FillPath(off, path);
                using (var pen = new Pen(pos > 0.5f ? Theme.Alpha(Theme.Accent, 150) : Theme.Border)) g.DrawPath(pen, path);
            }
            float d = r.Height - 6;
            float x = r.X + 3 + (r.Width - d - 6) * pos;
            using (var br = new SolidBrush(pos > 0.5f ? Color.White : Theme.Muted)) g.FillEllipse(br, x, r.Y + 3, d, d);
        }
    }

    // Большая кнопка питания на главной
    public class PowerButton : PaintControl
    {
        string state = "stopped";
        bool hover, down;
        // плавные значения 0..1, догоняют цель с экспоненциальным сглаживанием
        float onT, busyT, hoverT, downT;
        float phase, arcLen = 110f;
        readonly Timer anim = new Timer { Interval = 15 };
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        double lastT;

        public PowerButton()
        {
            Size = new Size(230, 230);
            Cursor = Cursors.Hand;
            anim.Tick += (s, e) => Step();
            anim.Start();
        }

        public string State
        {
            get { return state; }
            set { state = value; Kick(); }
        }

        void Kick() { lastT = clock.Elapsed.TotalSeconds; if (!anim.Enabled) anim.Start(); }

        static float Approach(float v, float target, float dt, float speed)
        {
            return v + (target - v) * (float)(1 - Math.Exp(-speed * dt));
        }

        void Step()
        {
            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.05, Math.Max(0.001, now - lastT));
            lastT = now;
            bool on = state == "connected", busy = state == "connecting" || state == "stopping";
            onT = Approach(onT, on ? 1 : 0, dt, 5f);
            busyT = Approach(busyT, busy ? 1 : 0, dt, 6f);
            hoverT = Approach(hoverT, hover ? 1 : 0, dt, 10f);
            downT = Approach(downT, down ? 1 : 0, dt, 18f);
            phase += dt;
            // дуга «дышит»: длина плавно меняется, пока идёт подключение
            arcLen = 110f + 70f * (float)Math.Sin(phase * 2.2);
            Invalidate();
            bool settled = Math.Abs(onT - (on ? 1 : 0)) < 0.002f && Math.Abs(busyT - (busy ? 1 : 0)) < 0.002f
                && Math.Abs(hoverT - (hover ? 1 : 0)) < 0.002f && Math.Abs(downT - (down ? 1 : 0)) < 0.002f;
            // в покое (не подключено, не наведено) — таймер стоит, процессор отдыхает
            if (settled && !on && !busy) anim.Stop();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Kick(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Kick(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Kick(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Kick(); base.OnMouseUp(e); }

        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            float cx = Width / 2f, cy = Height / 2f;
            float scale = 1f - 0.035f * downT;
            float R = (Math.Min(Width, Height) / 2f - 20) * scale;
            float pulse = (float)(Math.Sin(phase * 2.0) * 0.5 + 0.5);
            Color accent = Theme.Mix(Theme.Accent, Theme.Accent2, busyT * (1 - onT));

            // мягкое радиальное свечение
            float glowI = Math.Max(onT * (0.75f + 0.25f * pulse), Math.Max(busyT * 0.6f, hoverT * 0.3f));
            if (glowI > 0.01f)
            {
                float gr = R + 18 + 3 * pulse * onT;
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(cx - gr, cy - gr, gr * 2, gr * 2);
                    using (var pb = new PathGradientBrush(path))
                    {
                        pb.CenterPoint = new PointF(cx, cy);
                        pb.CenterColor = Theme.Alpha(accent, (int)(90 * glowI));
                        pb.SurroundColors = new[] { Theme.Alpha(accent, 0) };
                        var blend = new ColorBlend(3);
                        blend.Colors = new[] { Theme.Alpha(accent, 0), Theme.Alpha(accent, (int)(70 * glowI)), Theme.Alpha(accent, (int)(90 * glowI)) };
                        blend.Positions = new[] { 0f, 0.22f, 1f };
                        pb.InterpolationColors = blend;
                        g.FillPath(pb, path);
                    }
                }
            }

            // внутренний круг
            var inner = new RectangleF(cx - R, cy - R, R * 2, R * 2);
            using (var br = new LinearGradientBrush(inner, Theme.Mix(Theme.Surface2, accent, 0.03f + 0.12f * onT + 0.04f * hoverT), Theme.Surface, 60f))
                g.FillEllipse(br, inner);

            // базовое кольцо
            using (var pen = new Pen(Theme.Mix(Theme.Border, accent, 0.25f * hoverT), 3f)) g.DrawEllipse(pen, inner);

            // полное градиентное кольцо (подключено) — медленно вращается
            if (onT > 0.01f)
                using (var br = new LinearGradientBrush(inner, Theme.Alpha(Theme.Accent, (int)(255 * onT)), Theme.Alpha(Theme.Accent2, (int)(255 * onT)), 45f + phase * 25f))
                using (var pen = new Pen(br, 4f)) g.DrawEllipse(pen, inner);

            // дуга-спиннер (подключение/отключение)
            if (busyT > 0.01f)
                using (var br = new LinearGradientBrush(inner, Theme.Alpha(Theme.Accent, (int)(255 * busyT)), Theme.Alpha(Theme.Accent2, (int)(255 * busyT)), 45f))
                using (var pen = new Pen(br, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, inner, (phase * 260f) % 360f, arcLen);

            // внутреннее тонкое кольцо
            var inner2 = RectangleF.Inflate(inner, -16, -16);
            using (var pen = new Pen(Theme.Alpha(Theme.Mix(Theme.Muted, Theme.Accent, onT), (int)(30 + 40 * onT)), 1f)) g.DrawEllipse(pen, inner2);

            // значок питания
            Color ic = Theme.Mix(Theme.Mix(Theme.Muted, Theme.Accent, Math.Max(hoverT, busyT)), Color.White, onT);
            using (var f = Theme.Icon(46f * scale))
            using (var br = new SolidBrush(ic))
            {
                var sz = g.MeasureString(Theme.IcPower, f);
                g.DrawString(Theme.IcPower, f, br, cx - sz.Width / 2 + 2, cy - sz.Height / 2 - 10 * scale);
            }
            bool isOn = state == "connected", isBusy = state == "connecting" || state == "stopping";
            string label = isOn ? I18n.L("ОТКЛЮЧИТЬ") : isBusy ? I18n.L("ПОДОЖДИТЕ") : I18n.L("ПОДКЛЮЧИТЬ");
            using (var f = Theme.UiSemi(8.5f))
            using (var br = new SolidBrush(Theme.Mix(Theme.Muted, Theme.Accent, Math.Max(onT, hoverT * 0.6f))))
            {
                var sz = g.MeasureString(label, f);
                g.DrawString(label, f, br, cx - sz.Width / 2, cy + 30 * scale);
            }
        }
    }

    // Скруглённая карточка-контейнер
    public class Card : Panel
    {
        public Color Fill = Theme.Surface;
        public bool Highlight;

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Padding = new Padding(18);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Control p = Parent;
            using (var b = new SolidBrush(p != null ? p.BackColor : Theme.Bg)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Round(r, 14))
            {
                using (var br = new SolidBrush(Fill)) g.FillPath(br, path);
                if (Highlight)
                    using (var br = new LinearGradientBrush(r, Theme.Alpha(Theme.Accent, 200), Theme.Alpha(Theme.Accent2, 200), LinearGradientMode.Horizontal))
                    using (var pen = new Pen(br, 1.5f)) g.DrawPath(pen, path);
                else using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            }
        }
    }

    // Карточка туннеля в списке
    public class TunnelCard : Card
    {
        public readonly Tunnel Tunnel;
        public readonly ToggleSwitch Toggle = new ToggleSwitch();
        public readonly FlatButton MenuButton = new FlatButton("", Theme.IcMore, ButtonKind.Ghost);
        public string Status = "";
        public string Protocol = "";
        bool hover;

        public TunnelCard(Tunnel t, string protocol)
        {
            Tunnel = t; Protocol = protocol;
            Height = 76;
            Padding = new Padding(0);
            Margin = new Padding(0, 0, 0, 10);
            Cursor = Cursors.Hand;
            Toggle.SetSilently(t.Enabled);
            MenuButton.Width = 36;
            Controls.Add(Toggle);
            Controls.Add(MenuButton);
            Resize += (s, e) => Layout_();
            Layout_();
        }

        void Layout_()
        {
            MenuButton.Location = new Point(Width - MenuButton.Width - 14, (Height - MenuButton.Height) / 2);
            Toggle.Location = new Point(MenuButton.Left - Toggle.Width - 12, (Height - Toggle.Height) / 2);
        }

        protected override void OnMouseEnter(EventArgs e) { SetHover(true); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e)
        {
            if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) SetHover(false);
            base.OnMouseLeave(e);
        }

        void SetHover(bool h)
        {
            if (hover == h) return;
            hover = h;
            Fill = h ? Theme.Mix(Theme.Surface, Theme.Surface2, 0.6f) : Theme.Surface;
            BackColor = Fill;
            Invalidate(true);
        }

        public static Color StatusColor(string st)
        {
            if (st == I18n.L("подключён") || st == I18n.L("готов")) return Theme.Good;
            if (st == I18n.L("ошибка")) return Theme.Bad;
            if (st == I18n.L("подключение…") || st == I18n.L("ожидание")) return Theme.Warn;
            return Theme.Muted;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics; Theme.Hq(g);
            bool en = Tunnel.Enabled;
            // точка статуса
            Color dot = string.IsNullOrEmpty(Status) ? (en ? Theme.Alpha(Theme.Accent, 160) : Theme.Border) : StatusColor(Status);
            using (var br = new SolidBrush(Theme.Alpha(dot, 50))) g.FillEllipse(br, 14, Height / 2f - 10, 20, 20);
            using (var br = new SolidBrush(dot)) g.FillEllipse(br, 19, Height / 2f - 5, 10, 10);

            using (var f = Theme.UiSemi(11.5f))
            using (var br = new SolidBrush(en ? Theme.Text : Theme.Muted))
                g.DrawString(Tunnel.Name, f, br, 46, 14);
            float nameW;
            using (var f = Theme.UiSemi(11.5f)) nameW = g.MeasureString(Tunnel.Name, f).Width;

            // плашка протокола
            using (var f = Theme.UiSemi(8f))
            {
                var sz = g.MeasureString(Protocol, f);
                var pr = new RectangleF(46 + nameW + 8, 17, sz.Width + 12, 20);
                using (var path = Theme.Round(pr, 10))
                using (var br = new SolidBrush(Theme.Alpha(Theme.Accent2, 40))) g.FillPath(br, path);
                using (var br = new SolidBrush(Theme.Mix(Theme.Accent2, Color.White, 0.35f))) g.DrawString(Protocol, f, br, pr.X + 6, pr.Y + 2);
            }

            string sub = (Tunnel.Server ?? "") + (string.IsNullOrEmpty(Status) ? "" : "   ·   " + Status);
            using (var f = Theme.Ui(9f))
            using (var br = new SolidBrush(Theme.Muted))
                g.DrawString(sub, f, br, new RectangleF(46, 42, Math.Max(10, Toggle.Left - 56), 20), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
        }
    }

    // Тёмный выпадающий список (стандартный ComboBox в режиме DropDownList игнорирует цвета)
    public class DarkCombo : ComboBox
    {
        bool hover;

        public DarkCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            ItemHeight = 24;
            BackColor = Theme.Surface2;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9.5f);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var br = new SolidBrush(sel ? Theme.Mix(Theme.Surface2, Theme.Accent, 0.25f) : Theme.Surface2)) e.Graphics.FillRectangle(br, e.Bounds);
            using (var br = new SolidBrush(Theme.Text))
                e.Graphics.DrawString(Convert.ToString(Items[e.Index]), Font, br, e.Bounds.X + 6, e.Bounds.Y + 3);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F) // WM_PAINT: перерисовываем рамку и кнопку целиком
            {
                using (var g = Graphics.FromHwnd(Handle))
                {
                    Theme.Hq(g);
                    var parentBack = Parent != null ? Parent.BackColor : Theme.Bg;
                    if (parentBack == Color.Transparent) parentBack = Theme.Surface;
                    using (var br = new SolidBrush(parentBack)) g.FillRectangle(br, ClientRectangle);
                    var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                    using (var path = Theme.Round(r, 7))
                    {
                        using (var br = new SolidBrush(hover || Focused ? Theme.Mix(Theme.Surface2, Theme.Accent, 0.06f) : Theme.Surface2)) g.FillPath(br, path);
                        using (var pen = new Pen(Focused || DroppedDown ? Theme.Alpha(Theme.Accent, 160) : Theme.Border)) g.DrawPath(pen, path);
                    }
                    string text = SelectedItem != null ? Convert.ToString(SelectedItem) : "";
                    using (var br = new SolidBrush(Enabled ? Theme.Text : Theme.Muted))
                        g.DrawString(text, Font, br, new RectangleF(9, (Height - Font.Height) / 2f - 1, Width - 36, Font.Height + 4), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                    using (var f = Theme.Icon(8f))
                    using (var br = new SolidBrush(Theme.Muted))
                        g.DrawString(Theme.IcDown, f, br, Width - 24, (Height - 12) / 2f);
                }
            }
        }
    }

    // Карточка скорости туннеля на главной: ↓/↑ и график за последнюю минуту
    public class SpeedCard : PaintControl
    {
        public string Title = "", Sub = "";
        public Color Dot = Theme.Muted;
        public long Up, Down;
        readonly float[] histDown = new float[60], histUp = new float[60];

        public SpeedCard() { Size = new Size(204, 110); Margin = new Padding(0, 0, 10, 10); }

        public void Push(long up, long down)
        {
            Up = up; Down = down;
            Array.Copy(histDown, 1, histDown, 0, histDown.Length - 1);
            Array.Copy(histUp, 1, histUp, 0, histUp.Length - 1);
            histDown[histDown.Length - 1] = down;
            histUp[histUp.Length - 1] = up;
            Invalidate();
        }

        public void Reset() { Array.Clear(histDown, 0, histDown.Length); Array.Clear(histUp, 0, histUp.Length); Up = Down = 0; Invalidate(); }

        public static string Rate(long bps)
        {
            if (bps < 1024) return bps + I18n.L(" Б/с");
            if (bps < 1024 * 1024) return (bps / 1024.0).ToString(bps < 10 * 1024 ? "0.0" : "0") + I18n.L(" КБ/с");
            return (bps / 1048576.0).ToString(bps < 10 * 1048576 ? "0.00" : "0.0") + I18n.L(" МБ/с");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Round(r, 14))
            {
                using (var br = new SolidBrush(Theme.Surface)) g.FillPath(br, path);

                // график: скорость скачивания за 60 секунд
                float max = 16 * 1024;
                for (int i = 0; i < histDown.Length; i++) max = Math.Max(max, Math.Max(histDown[i], histUp[i]));
                float gx = 1, gw = Width - 2, gTop = Height - 34, gh = 32;
                var pts = new PointF[histDown.Length];
                for (int i = 0; i < histDown.Length; i++)
                    pts[i] = new PointF(gx + gw * i / (histDown.Length - 1), gTop + gh - gh * histDown[i] / max);
                g.SetClip(path);
                using (var area = new GraphicsPath())
                {
                    area.AddLines(pts);
                    area.AddLine(pts[pts.Length - 1], new PointF(gx + gw, Height));
                    area.AddLine(new PointF(gx + gw, Height), new PointF(gx, Height));
                    area.CloseFigure();
                    using (var br = new LinearGradientBrush(new RectangleF(0, gTop - 1, Width, gh + 2), Theme.Alpha(Dot, 70), Theme.Alpha(Dot, 0), LinearGradientMode.Vertical))
                        g.FillPath(br, area);
                }
                using (var pen = new Pen(Theme.Alpha(Dot, 200), 1.6f)) g.DrawLines(pen, pts);
                g.ResetClip();
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            }

            using (var br = new SolidBrush(Theme.Alpha(Dot, 60))) g.FillEllipse(br, 14, 14, 14, 14);
            using (var br = new SolidBrush(Dot)) g.FillEllipse(br, 17.5f, 17.5f, 7, 7);
            var nf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using (var f = Theme.UiSemi(10f)) using (var br = new SolidBrush(Theme.Text))
                g.DrawString(Title, f, br, new RectangleF(34, 10, Width - 44, 20), nf);
            using (var f = Theme.Ui(8f)) using (var br = new SolidBrush(Theme.Muted))
                g.DrawString(Sub, f, br, new RectangleF(34, 29, Width - 44, 16), nf);
            using (var f = Theme.UiSemi(10f))
            {
                using (var br = new SolidBrush(Theme.Text)) g.DrawString("↓ " + Rate(Down), f, br, 14, 50);
                using (var br = new SolidBrush(Theme.Muted)) g.DrawString("↑ " + Rate(Up), f, br, new RectangleF(Width / 2f, 50, Width / 2f - 12, 20), new StringFormat { Alignment = StringAlignment.Far });
            }
        }
    }

    // Маленькая «таблетка» со статусом на главной
    public class Chip : PaintControl
    {
        public Color Dot = Theme.Muted;
        public Chip(string text, Color dot)
        {
            Text = text; Dot = dot;
            Font = Theme.UiSemi(9f);
            Height = 32;
            Margin = new Padding(0, 0, 8, 8);
            using (var g = CreateGraphics()) Width = (int)g.MeasureString(text, Font).Width + 44;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Round(r, r.Height / 2))
            {
                using (var br = new SolidBrush(Theme.Surface)) g.FillPath(br, path);
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            }
            using (var br = new SolidBrush(Theme.Alpha(Dot, 60))) g.FillEllipse(br, 10, Height / 2f - 7, 14, 14);
            using (var br = new SolidBrush(Dot)) g.FillEllipse(br, 13.5f, Height / 2f - 3.5f, 7, 7);
            using (var br = new SolidBrush(Theme.Text)) g.DrawString(Text, Font, br, 30, (Height - g.MeasureString(Text, Font).Height) / 2f);
        }
    }

    // Логотип с градиентным текстом
    public class Logo : PaintControl
    {
        public Logo() { Height = 74; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            // значок приложения — тот же, что у exe и в трее
            using (var icon = Branding.Draw(40, "brand")) g.DrawImage(icon, 16, 16, 40, 40);
            using (var path = new GraphicsPath())
            {
                using (var ff = new FontFamily("Segoe UI"))
                    path.AddString("HYDRA", ff, (int)FontStyle.Bold, 24f, new PointF(64, 14), StringFormat.GenericTypographic);
                using (var br = new LinearGradientBrush(new RectangleF(64, 14, 110, 30), Theme.Accent, Theme.Accent2, LinearGradientMode.Horizontal))
                    g.FillPath(br, path);
            }
            using (var f = Theme.Ui(8f))
            using (var br = new SolidBrush(Theme.Muted))
                g.DrawString("MULTI-TUNNEL VPN", f, br, 66, 44);
        }
    }
}
