using System;
using System.Drawing;
using System.Windows.Forms;

namespace Hydra
{
    static class Dialogs
    {
        static Form MakeForm(string title, int w, int h)
        {
            var f = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(w, h),
                Font = Theme.Ui(9.5f),
                AutoScaleMode = AutoScaleMode.Font,
                BackColor = Theme.Bg,
                ForeColor = Theme.Text,
                ShowIcon = false,
            };
            f.HandleCreated += (s, e) => Theme.DarkTitleBar(f);
            return f;
        }

        static void AddButtons(Form f, int y)
        {
            var ok = new FlatButton(I18n.L("Готово"), null, ButtonKind.Primary) { Width = 110 };
            var cancel = new FlatButton(I18n.L("Отмена"), null, ButtonKind.Secondary) { Width = 100 };
            ok.Location = new Point(f.ClientSize.Width - 230, y);
            cancel.Location = new Point(f.ClientSize.Width - 112, y);
            ok.Click += (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); };
            cancel.Click += (s, e) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
            f.Controls.Add(ok); f.Controls.Add(cancel);
            f.KeyPreview = true;
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { f.DialogResult = DialogResult.Cancel; f.Close(); } };
        }

        static Label Caption(string text, int x, int y, int w, int h)
        {
            return new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), ForeColor = Theme.Muted, BackColor = Theme.Bg };
        }

        public static string MultiLine(IWin32Window owner, string title, string hint)
        {
            using (var f = MakeForm(title, 600, 330))
            {
                f.Controls.Add(Caption(hint, 16, 14, 568, 40));
                var tb = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(16, 60), Size = new Size(568, 206), WordWrap = false, Font = Theme.Mono(9f) };
                try { if (Clipboard.ContainsText() && Clipboard.GetText().Contains("://")) tb.Text = Clipboard.GetText(); } catch { }
                f.Controls.Add(tb);
                AddButtons(f, 280);
                Theme.Apply(f);
                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
            }
        }

        public static string Input(IWin32Window owner, string title, string label, string value)
        {
            using (var f = MakeForm(title, 460, 150))
            {
                f.Controls.Add(Caption(label, 16, 14, 428, 40));
                var tb = new TextBox { Text = value ?? "", Location = new Point(16, 58), Width = 428 };
                f.Controls.Add(tb);
                AddButtons(f, 100);
                f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { f.DialogResult = DialogResult.OK; f.Close(); } };
                Theme.Apply(f);
                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }

        public static bool Credentials(IWin32Window owner, string title, ref string user, out string pass)
        {
            pass = null;
            using (var f = MakeForm(title, 420, 200))
            {
                f.Controls.Add(Caption(I18n.L("Логин"), 16, 18, 80, 20));
                var tu = new TextBox { Text = user ?? "", Location = new Point(100, 14), Width = 304 };
                f.Controls.Add(Caption(I18n.L("Пароль"), 16, 56, 80, 20));
                var tp = new TextBox { UseSystemPasswordChar = true, Location = new Point(100, 52), Width = 304 };
                f.Controls.Add(tu); f.Controls.Add(tp);
                f.Controls.Add(Caption(I18n.L("Пароль хранится зашифрованным (Windows DPAPI)."), 16, 96, 388, 20));
                AddButtons(f, 148);
                Theme.Apply(f);
                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                user = tu.Text.Trim(); pass = tp.Text;
                return true;
            }
        }
    }

    // Тёмные выпадающие меню
    class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Surface2; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Surface2; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Surface2; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Surface2; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.Alpha(Theme.Accent, 120); } }
        public override Color MenuItemSelected { get { return Theme.Mix(Theme.Surface2, Theme.Accent, 0.18f); } }
        public override Color MenuItemSelectedGradientBegin { get { return MenuItemSelected; } }
        public override Color MenuItemSelectedGradientEnd { get { return MenuItemSelected; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
    }

    static class DarkMenu
    {
        public static ContextMenuStrip Create()
        {
            var m = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()), ForeColor = Theme.Text, Font = Theme.Ui(9.5f), ShowImageMargin = false };
            m.ItemAdded += (s, e) => { e.Item.ForeColor = Theme.Text; e.Item.Padding = new Padding(6, 4, 6, 4); };
            return m;
        }
    }
}
