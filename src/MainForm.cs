using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace Hydra
{
    public class MainForm : Form
    {
        public const string AppName = "Hydra";
        static readonly string DirectLabel = I18n.L("Напрямую");
        static readonly string BlockLabel = I18n.L("Блокировать");
        static MainForm() { } // статические поля — строго после I18n.Init

        readonly ProfileStore store;
        Profile P { get { return store.Profile; } }
        readonly Engine engine = new Engine();
        readonly Dictionary<string, string> statuses = new Dictionary<string, string>();
        bool loading;
        string state = "stopped";

        Panel content;
        readonly Dictionary<string, Control> pages = new Dictionary<string, Control>();
        readonly Dictionary<string, NavButton> navs = new Dictionary<string, NavButton>();

        // главная
        PowerButton power;
        Label lblStatus, lblSub;
        FlowLayoutPanel homeChips;
        Card killBanner;
        Label killBannerText;
        // загрузка ядра sing-box
        Card coreBanner;
        Label coreText;
        PaintControl coreBar;
        FlatButton coreRetry, coreManual;
        int corePct = -1;
        bool coreBusy;
        // обновление Hydra
        Card updBanner;
        Label updText;
        FlatButton updNow, updNotes, updLater;
        Updater.Release pendingUpdate;
        bool updBusy;
        // туннели
        FlowLayoutPanel tunnelList;
        Label tunnelsEmpty;
        // правила
        DataGridView grid;
        DataGridViewComboBoxColumn colTarget;
        ComboBox cbFinal;
        // защита
        ToggleSwitch tgKill;
        ComboBox cbKillTunnel;
        ListBox lbApps;
        TextBox tbKillDomains;
        Label lblKillState;
        // настройки
        TextBox tbSingBox, tbOpenVpn, tbAwg, tbDns;
        ToggleSwitch tgStrict;
        ComboBox cbLogLevel, cbRuleSetDetour;
        // журнал
        TextBox txtLog;
        // боковая панель
        Label sideStatus;
        PaintControl sideDot;
        Color sideDotColor = Theme.Muted;
        NotifyIcon tray;

        static readonly string[][] Presets = {
            new[] { "YouTube", "domain_suffix", "youtube.com, youtu.be, googlevideo.com, ytimg.com, ggpht.com, youtube-nocookie.com, youtubei.googleapis.com, yt.be, youtubekids.com" },
            new[] { I18n.L("Telegram (домены)"), "domain_suffix", "telegram.org, t.me, telegram.me, telesco.pe, tdesktop.com, telegra.ph, cdn-telegram.org" },
            new[] { I18n.L("Telegram (IP-сети)"), "ip_cidr", "91.108.4.0/22, 91.108.8.0/22, 91.108.12.0/22, 91.108.16.0/22, 91.108.20.0/22, 91.108.56.0/22, 149.154.160.0/20, 185.76.151.0/24, 95.161.64.0/20" },
            new[] { "Discord", "domain_suffix", "discord.com, discord.gg, discordapp.com, discordapp.net, discord.media, discordcdn.com, discord.dev" },
            new[] { "Instagram / Facebook / WhatsApp", "domain_suffix", "instagram.com, cdninstagram.com, facebook.com, fbcdn.net, fb.com, messenger.com, whatsapp.com, whatsapp.net" },
            new[] { "X / Twitter", "domain_suffix", "x.com, twitter.com, twimg.com, t.co" },
            new[] { I18n.L("ИИ-сервисы (ChatGPT, Claude, Gemini)"), "domain_suffix", "openai.com, chatgpt.com, oaistatic.com, oaiusercontent.com, anthropic.com, claude.ai, claude.com, gemini.google.com" },
            new[] { I18n.L("Российские домены (.ru .рф .su)"), "domain_suffix", I18n.L("ru, рф, su") },
            new[] { I18n.L("Российские IP (geoip-ru)"), "geoip", "ru" },
            new[] { I18n.L("Реклама (geosite category-ads-all)"), "geosite", "category-ads-all" },
        };

        public MainForm()
        {
            store = ProfileStore.Load();
            Text = AppName;
            Font = Theme.Ui(9.5f);
            ForeColor = Theme.Text;
            BackColor = Theme.Bg;
            AutoScaleMode = AutoScaleMode.Font;
            Size = new Size(1180, 760);
            MinimumSize = new Size(960, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Branding.AppIcon();
            DoubleBuffered = true;

            BuildUi();
            loading = true;
            LoadTunnels();
            LoadRules();
            LoadKill();
            LoadSettings();
            loading = false;

            engine.Log += s => { FileLog.Write(s); Ui(() => AppendLogUi(s)); };
            History.Added += ev => Ui(() =>
            {
                OnHistoryEvent(ev);
                if (pages.ContainsKey("history") && pages["history"].Visible) RefreshHistory();
            });
            engine.Traffic += rates => Ui(() => OnTraffic(rates));
            engine.TunnelProblem += (tag, msg) => Ui(() => AutoHeal(tag));
            engine.TunnelStatus += (n, st) => Ui(() => { statuses[n] = st; RefreshStatuses(); });
            engine.StateChanged += st => Ui(() => OnState(st));
            engine.KillChanged += on => Ui(RefreshKill);
            OnState("stopped");
            ShowPage("home");

            HandleCreated += (s, e) => { Theme.DarkTitleBar(this); Branding.SetWindowIcons(Handle); };
            // Первый запуск новой версии: просим Windows перечитать значок exe (иначе на панели задач — старый из кэша)
            if (P.LastRunVersion != Updater.CurrentVersion)
            {
                Branding.RefreshShellIcon(Application.ExecutablePath);
                P.LastRunVersion = Updater.CurrentVersion;
                Save();
            }
            Shown += (s, e) => engine.SyncKillState();

            // Нет ядра — скачиваем сами сразу после открытия окна
            Shown += (s, e) => { if (AppPaths.SingBox(P) == null) StartCoreDownload(); };
            // Проверка обновлений на GitHub
            Shown += (s, e) => { if (P.CheckUpdates) CheckForUpdates(false); };
        }

        // ======================= обновления =======================
        void CheckForUpdates(bool manual)
        {
            var th = new System.Threading.Thread(() =>
            {
                try
                {
                    var r = Updater.CheckLatest();
                    Ui(() =>
                    {
                        if (r == null)
                        {
                            if (manual) MessageBox.Show(this, I18n.L("У вас последняя версия Hydra ") + Updater.CurrentVersion + ".", AppName);
                            return;
                        }
                        if (!manual && r.Version == P.SkippedVersion) return;
                        pendingUpdate = r;
                        updText.Text = I18n.L("Доступна новая версия Hydra ") + r.Version + I18n.L(" (у вас ") + Updater.CurrentVersion + ")";
                        updText.ForeColor = Theme.Text;
                        updNow.Text = I18n.L("Обновить"); updNow.Enabled = true;
                        updBanner.Visible = true;
                        AppendLog(I18n.L("Доступна новая версия Hydra ") + r.Version + ": " + r.HtmlUrl);
                        RefreshHome();
                        if (manual) ShowPage("home");
                    });
                }
                catch (Exception ex)
                {
                    Ui(() =>
                    {
                        AppendLog(I18n.L("Не удалось проверить обновления: ") + ex.Message);
                        if (manual) MessageBox.Show(this, I18n.L("Не удалось проверить обновления: ") + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        void InstallUpdate()
        {
            if (pendingUpdate == null || updBusy) return;
            if (engine.State != "stopped" &&
                MessageBox.Show(this, I18n.L("Для обновления VPN будет отключён, Hydra перезапустится. Продолжить?"), AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            updBusy = true;
            updNow.Enabled = false; updLater.Visible = false;
            var r = pendingUpdate;
            var th = new System.Threading.Thread(() =>
            {
                try
                {
                    string exe = Updater.DownloadAndReplace(r, pct => Ui(() => { updNow.Text = I18n.L("Скачиваю… ") + pct + "%"; updNow.AutoSizeToText(); LayoutUpdBanner(); }));
                    Ui(() =>
                    {
                        AppendLog(I18n.L("Hydra обновлена до ") + r.Version + I18n.L(", перезапуск…"));
                        History.Add(History.Update, I18n.L("Hydra обновлена до ") + r.Version, Updater.CurrentVersion + " → " + r.Version);
                        if (engine.State != "stopped") engine.StopBlocking();
                        tray.Visible = false;
                        Updater.Restart(exe);
                        Close();
                    });
                }
                catch (Exception ex)
                {
                    Ui(() =>
                    {
                        updBusy = false;
                        updText.Text = I18n.L("Не удалось обновить Hydra: ") + ex.Message;
                        updText.ForeColor = Theme.Warn;
                        updNow.Text = I18n.L("Повторить"); updNow.Enabled = true; updNow.AutoSizeToText();
                        LayoutUpdBanner();
                    });
                }
            });
            th.IsBackground = true;
            th.Start();
        }


        void LayoutUpdBanner()
        {
            int w = updBanner.Width;
            updNow.Location = new Point(w - updNow.Width - 14, 14);
            updNotes.Location = new Point(updNow.Left - updNotes.Width - 8, 14);
            updLater.Location = new Point(updNotes.Left - updLater.Width - 8, 14);
            int left = updLater.Visible ? updLater.Left : updNotes.Left;
            updText.MaximumSize = new Size(Math.Max(200, left - 30), 0);
        }

        void LayoutCoreBanner()
        {
            int w = coreBanner.Width;
            coreBar.Width = w - 36;
            coreRetry.Location = new Point(w - coreRetry.Width - 14, 14);
            coreManual.Location = new Point(coreRetry.Left - coreManual.Width - 8, 14);
            int textW = coreRetry.Visible ? coreManual.Left - 30 : w - 36;
            coreText.MaximumSize = new Size(Math.Max(200, textW), 0);
        }

        void StartCoreDownload()
        {
            if (coreBusy) return;
            coreBusy = true; corePct = 0;
            coreText.Text = I18n.L("Скачиваю ядро sing-box ") + CoreDownloader.Version + I18n.L(" с GitHub… Это нужно один раз.");
            coreText.ForeColor = Theme.Text;
            coreRetry.Visible = false; coreManual.Visible = false; coreBar.Visible = true;
            coreBanner.Visible = true;
            RefreshHome();
            AppendLog(I18n.L("Ядро sing-box не найдено — скачиваю ") + CoreDownloader.Url);
            var th = new System.Threading.Thread(() =>
            {
                try
                {
                    string path = CoreDownloader.Download((pct, bytes) => Ui(() => { corePct = pct; coreBar.Invalidate(); }));
                    Ui(() =>
                    {
                        coreBusy = false;
                        coreBanner.Visible = false;
                        AppendLog(I18n.L("Ядро sing-box установлено: ") + path);
                        RefreshHome();
                    });
                }
                catch (Exception ex)
                {
                    Ui(() =>
                    {
                        coreBusy = false;
                        coreText.Text = I18n.L("Не удалось скачать sing-box: ") + ex.Message;
                        coreText.ForeColor = Theme.Warn;
                        coreBar.Visible = false; coreRetry.Visible = true; coreManual.Visible = true;
                        AppendLog(I18n.L("Не удалось скачать sing-box: ") + ex.Message);
                        LayoutHome(power.Parent);
                    });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        void Ui(Action a)
        {
            if (IsDisposed) return;
            try { if (InvokeRequired) BeginInvoke(a); else a(); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
        }

        // Значок в трее: серый — не подключено, цветной — подключено, оранжевый — сбой / kill switch держит
        string trayIconState;
        void SetTrayIcon(string st)
        {
            if (st == trayIconState) return;
            trayIconState = st;
            var old = tray.Icon;
            tray.Icon = Branding.MakeIcon(SystemInformation.SmallIconSize.Width, st);
            if (old != null) old.Dispose();
        }

        // ======================= каркас =======================
        void BuildUi()
        {
            var sidebar = new Panel { Dock = DockStyle.Left, Width = 236, BackColor = Theme.Sidebar, Padding = new Padding(14, 8, 14, 14) };
            sidebar.Paint += (s, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height); };

            var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Sidebar, Padding = new Padding(0, 12, 0, 0) };
            AddNav(nav, "home", I18n.L("Главная"), Theme.IcHome);
            AddNav(nav, "tunnels", I18n.L("Туннели"), Theme.IcGlobe);
            AddNav(nav, "rules", I18n.L("Правила"), Theme.IcList);
            AddNav(nav, "protect", I18n.L("Защита"), Theme.IcShield);
            AddNav(nav, "settings", I18n.L("Настройки"), Theme.IcSettings);
            AddNav(nav, "history", I18n.L("История"), "");
            AddNav(nav, "log", I18n.L("Журнал"), Theme.IcLog);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Theme.Sidebar };
            sideDot = new PaintControl { Size = new Size(14, 14), Location = new Point(6, 14) };
            sideDot.Paint += (s, e) =>
            {
                Theme.Hq(e.Graphics);
                using (var br = new SolidBrush(Theme.Alpha(sideDotColor, 60))) e.Graphics.FillEllipse(br, 0, 0, 13, 13);
                using (var br = new SolidBrush(sideDotColor)) e.Graphics.FillEllipse(br, 3.5f, 3.5f, 6, 6);
            };
            sideStatus = new Label { AutoSize = true, Location = new Point(24, 12), ForeColor = Theme.Text, Font = Theme.UiSemi(9.5f), BackColor = Theme.Sidebar };
            var ver = new Label { AutoSize = true, Location = new Point(24, 34), ForeColor = Theme.Muted, Font = Theme.Ui(8f), Text = "Hydra " + Updater.CurrentVersion + " · sing-box", BackColor = Theme.Sidebar };
            bottom.Controls.Add(sideDot); bottom.Controls.Add(sideStatus); bottom.Controls.Add(ver);

            sidebar.Controls.Add(nav);
            sidebar.Controls.Add(bottom);
            sidebar.Controls.Add(new Logo { Dock = DockStyle.Top });

            content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            pages["home"] = BuildHome();
            pages["tunnels"] = BuildTunnels();
            pages["rules"] = BuildRules();
            pages["protect"] = BuildProtect();
            pages["settings"] = BuildSettings();
            pages["history"] = BuildHistory();
            pages["log"] = BuildLog();
            foreach (var p in pages.Values) { p.Visible = false; content.Controls.Add(p); }
            Theme.Apply(content);

            Controls.Add(content);
            Controls.Add(sidebar);

            tray = new NotifyIcon { Text = AppName, Visible = true };
            SetTrayIcon("off");
            var menu = DarkMenu.Create();
            menu.Opening += (s, e) => BuildTrayMenu(menu);
            BuildTrayMenu(menu);
            tray.ContextMenuStrip = menu;
            // Левый клик — открыть/спрятать окно
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) { if (Visible && WindowState != FormWindowState.Minimized) Hide(); else RestoreWindow(); } };
            tray.BalloonTipClicked += (s, e) => { RestoreWindow(); if (balloonPage != null) ShowPage(balloonPage); };
            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) Hide(); };
            FormClosing += OnClosing;
        }

        bool exiting;
        bool trayHintShown;
        string balloonPage;

        // Меню трея собирается заново при каждом открытии: состояние, туннели со скоростью, действия
        void BuildTrayMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();
            bool on = state == "connected", busy = state == "connecting" || state == "stopping";
            var head = menu.Items.Add("●  " + (on ? I18n.L("Защищено") : busy ? sideStatus.Text : I18n.L("Не подключено")));
            head.Enabled = false;
            head.Font = Theme.UiSemi(9.5f);
            if (on)
            {
                foreach (var t in P.Tunnels)
                {
                    if (!t.Enabled) continue;
                    long[] r;
                    string speed = lastRates.TryGetValue(t.Name, out r) ? "   ↓ " + SpeedCard.Rate(r[1]) + "  ↑ " + SpeedCard.Rate(r[0]) : "";
                    var it = menu.Items.Add("     " + t.Name + "  ·  " + (StatusOf(t).Length > 0 ? StatusOf(t) : TypeLabel(t)) + speed);
                    it.Enabled = false;
                }
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(I18n.L("Открыть Hydra"), null, (s, e) => RestoreWindow());
            if (on || state == "connecting") menu.Items.Add(I18n.L("Отключить"), null, (s, e) => engine.Stop());
            else if (state == "stopped") menu.Items.Add(I18n.L("Подключить"), null, (s, e) => Connect());
            if (engine.KillActive && state == "stopped") menu.Items.Add(I18n.L("Снять блокировку"), null, (s, e) => engine.ReleaseKill());
            menu.Items.Add(I18n.L("История"), null, (s, e) => { RestoreWindow(); ShowPage("history"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(I18n.L("Выход"), null, (s, e) => { exiting = true; Close(); });
        }

        // Автопочинка: туннель перестал отвечать → переподключаем VPN.
        // Не чаще раза в 3 минуты; после трёх попыток за 30 минут — останавливаемся и только сообщаем.
        readonly List<DateTime> healAttempts = new List<DateTime>();
        void AutoHeal(string tag)
        {
            if (!P.AutoReconnect || state != "connected") return;
            var now = DateTime.Now;
            healAttempts.RemoveAll(t => (now - t).TotalMinutes > 30);
            if (healAttempts.Count > 0 && (now - healAttempts[healAttempts.Count - 1]).TotalMinutes < 3) return;
            if (healAttempts.Count >= 3)
            {
                Notify(I18n.L("Туннель «") + tag + I18n.L("» не отвечает"), I18n.L("Автопереподключение уже не помогло 3 раза за 30 минут. Проверьте сервер или переподключитесь вручную."), ToolTipIcon.Warning, "log");
                return;
            }
            healAttempts.Add(now);
            SaveRules();
            engine.Restart(P, I18n.L("туннель «") + tag + I18n.L("» перестал отвечать"));
        }

        void Notify(string title, string text, ToolTipIcon icon, string page)
        {
            balloonPage = page;
            try { tray.ShowBalloonTip(6000, title, text, icon); } catch { }
        }

        // Уведомления о сбоях из истории событий
        void OnHistoryEvent(HistoryEvent ev)
        {
            if (!P.NotifyFailures) return;
            string k = ev.Kind;
            if (k == History.Dropped) Notify(I18n.L("VPN оборвался"), ev.Text + (engine.KillActive ? I18n.L("\nKill switch держит защищённые программы офлайн.") : ""), ToolTipIcon.Error, "history");
            else if (k == History.Error) Notify(I18n.L("Не удалось подключиться"), ev.Details ?? ev.Text, ToolTipIcon.Error, "log");
            else if (k == History.TunnelError) Notify(ev.Text, ev.Details ?? "", ToolTipIcon.Warning, "history");
            else if (k == History.Crash) Notify(ev.Text, ev.Details ?? "", ToolTipIcon.Error, "history");
            else if (k == History.KillHold) Notify(I18n.L("Kill switch сработал"), I18n.L("Защищённые программы отрезаны от интернета, пока VPN не поднимется."), ToolTipIcon.Warning, "home");
            else if (k == History.Reconnect) Notify(I18n.L("Переподключаю VPN"), ev.Details ?? "", ToolTipIcon.Info, "history");
            else if (k == History.Connected && !Visible) Notify(I18n.L("VPN подключён"), ev.Details ?? "", ToolTipIcon.Info, "home");
        }

        void AddNav(FlowLayoutPanel nav, string key, string text, string glyph)
        {
            var b = new NavButton(text, glyph) { Width = 206 };
            b.Click += (s, e) => ShowPage(key);
            navs[key] = b;
            nav.Controls.Add(b);
        }

        void ShowPage(string key)
        {
            foreach (var kv in pages) kv.Value.Visible = kv.Key == key;
            foreach (var kv in navs) kv.Value.Active = kv.Key == key;
            if (key == "home") RefreshHome();
            if (key == "protect") RefreshKill();
            if (key == "history") RefreshHistory();
        }

        public void ShowPageForTest(string key) { ShowPage(key); }

        // Настоящий выход (а не «спрятать в трей»): для тестовых режимов
        public void ForceClose() { exiting = true; Close(); }

        // Для скриншотов: показать карточки скорости с демо-данными
        public void DemoTraffic(string st)
        {
            OnState(st);
            var rnd = new Random(7);
            for (int i = 0; i < 60; i++)
            {
                var rates = new Dictionary<string, long[]>();
                foreach (var t in P.Tunnels)
                    if (t.Enabled) rates[t.Name] = new long[] { rnd.Next(20, 120) * 1024, (long)((Math.Sin(i / 6.0 + t.Name.Length) + 1.3) * rnd.Next(300, 900) * 1024) };
                rates["direct"] = new long[] { rnd.Next(5, 40) * 1024, rnd.Next(50, 400) * 1024 };
                OnTraffic(rates);
            }
        }

        void RestoreWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            BringToFront();
            Activate();
        }

        // Страница: заголовок + (панель инструментов) + тело
        Panel Page(string title, string subtitle, Control toolbar, Control body)
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(34, 26, 34, 26) };
            var header = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = Theme.Bg };
            header.Controls.Add(new Label { Text = title, Font = Theme.UiBold(20f), ForeColor = Theme.Text, AutoSize = true, Location = new Point(0, 0), BackColor = Theme.Bg });
            header.Controls.Add(new Label { Text = subtitle, Font = Theme.Ui(9.5f), ForeColor = Theme.Muted, AutoSize = true, Location = new Point(2, 42), BackColor = Theme.Bg });
            body.Dock = DockStyle.Fill;
            page.Controls.Add(body);
            if (toolbar != null) page.Controls.Add(toolbar);
            page.Controls.Add(header);
            return page;
        }

        FlowLayoutPanel Toolbar(params Control[] items)
        {
            var tb = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, BackColor = Theme.Bg, WrapContents = false };
            tb.Controls.AddRange(items);
            return tb;
        }

        static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Ui(9f), BackColor = Color.Transparent, MaximumSize = new Size(560, 0) };
        }

        // ======================= главная =======================
        Control BuildHome()
        {
            var body = new Panel { BackColor = Theme.Bg };

            killBanner = new Card { Height = 60, Fill = Theme.Mix(Theme.Surface, Theme.Warn, 0.10f), Visible = false };
            killBannerText = new Label { AutoSize = true, ForeColor = Theme.Warn, Font = Theme.UiSemi(9.5f), Location = new Point(18, 20), BackColor = Color.Transparent };
            var release = new FlatButton(I18n.L("Снять блокировку"), Theme.IcUnlock, ButtonKind.Secondary);
            release.Click += (s, e) => engine.ReleaseKill();
            killBanner.Controls.Add(killBannerText);
            killBanner.Controls.Add(release);
            killBanner.Resize += (s, e) => release.Location = new Point(killBanner.Width - release.Width - 14, 12);

            power = new PowerButton();
            power.Click += (s, e) => { if (state == "connected" || state == "connecting") engine.Stop(); else if (state == "stopped") Connect(); };
            lblStatus = new Label { AutoSize = true, Font = Theme.UiBold(22f), ForeColor = Theme.Text, BackColor = Theme.Bg };
            lblSub = new Label { AutoSize = true, Font = Theme.Ui(10f), ForeColor = Theme.Muted, BackColor = Theme.Bg };
            homeChips = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Bg, MaximumSize = new Size(760, 0) };
            speedPanel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Bg, MaximumSize = new Size(760, 0) };

            coreBanner = new Card { Height = 64, Fill = Theme.Mix(Theme.Surface, Theme.Accent, 0.08f), Visible = false };
            coreText = new Label { AutoSize = true, ForeColor = Theme.Text, Font = Theme.UiSemi(9.5f), Location = new Point(18, 12), BackColor = Color.Transparent };
            coreBar = new PaintControl { Height = 6, Location = new Point(18, 40) };
            coreBar.Paint += (s, e) =>
            {
                var g = e.Graphics; Theme.Hq(g);
                var r = new RectangleF(0, 0, coreBar.Width - 1, coreBar.Height - 1);
                using (var path = Theme.Round(r, 3)) using (var br = new SolidBrush(Theme.Surface2)) g.FillPath(br, path);
                float pct = corePct < 0 ? 30 : corePct;
                var fr = new RectangleF(0, 0, Math.Max(6, r.Width * pct / 100f), r.Height);
                using (var path = Theme.Round(fr, 3)) using (var br = new LinearGradientBrush(new RectangleF(0, 0, coreBar.Width, 6), Theme.Accent, Theme.Accent2, LinearGradientMode.Horizontal)) g.FillPath(br, path);
            };
            coreRetry = new FlatButton(I18n.L("Повторить"), Theme.IcDown, ButtonKind.Primary) { Visible = false };
            coreRetry.Click += (s, e) => StartCoreDownload();
            coreManual = new FlatButton(I18n.L("Скачать вручную"), Theme.IcLink, ButtonKind.Secondary) { Visible = false };
            coreManual.Click += (s, e) => { try { Process.Start(CoreDownloader.ReleasesPage); } catch { } };
            coreBanner.Controls.Add(coreText); coreBanner.Controls.Add(coreBar); coreBanner.Controls.Add(coreRetry); coreBanner.Controls.Add(coreManual);
            coreBanner.Resize += (s, e) => LayoutCoreBanner();
            coreRetry.VisibleChanged += (s, e) => LayoutCoreBanner();

            updBanner = new Card { Height = 64, Fill = Theme.Mix(Theme.Surface, Theme.Accent2, 0.10f), Visible = false };
            updText = new Label { AutoSize = true, ForeColor = Theme.Text, Font = Theme.UiSemi(9.5f), Location = new Point(18, 22), BackColor = Color.Transparent };
            updNow = new FlatButton(I18n.L("Обновить"), Theme.IcDown, ButtonKind.Primary);
            updNow.Click += (s, e) => InstallUpdate();
            updNotes = new FlatButton(I18n.L("Что нового"), Theme.IcLink, ButtonKind.Secondary);
            updNotes.Click += (s, e) => { try { if (pendingUpdate != null) Process.Start(pendingUpdate.HtmlUrl); } catch { } };
            updLater = new FlatButton(I18n.L("Позже"), null, ButtonKind.Ghost);
            updLater.Click += (s, e) =>
            {
                if (pendingUpdate != null) { P.SkippedVersion = pendingUpdate.Version; Save(); }
                updBanner.Visible = false; RefreshHome();
            };
            updBanner.Controls.Add(updText); updBanner.Controls.Add(updNow); updBanner.Controls.Add(updNotes); updBanner.Controls.Add(updLater);
            updBanner.Resize += (s, e) => LayoutUpdBanner();

            body.Controls.Add(updBanner);
            body.Controls.Add(coreBanner);
            body.Controls.Add(killBanner);
            body.Controls.Add(power);
            body.Controls.Add(lblStatus);
            body.Controls.Add(lblSub);
            body.Controls.Add(homeChips);
            body.Controls.Add(speedPanel);
            body.Resize += (s, e) => LayoutHome(body);
            homeChips.SizeChanged += (s, e) => LayoutHome(body);
            speedPanel.SizeChanged += (s, e) => LayoutHome(body);
            lblStatus.SizeChanged += (s, e) => LayoutHome(body);
            lblSub.SizeChanged += (s, e) => LayoutHome(body);

            return Page(I18n.L("Главная"), I18n.L("Несколько туннелей одновременно — трафик идёт по вашим правилам"), null, body);
        }

        void LayoutHome(Control body)
        {
            int w = body.ClientSize.Width;
            int top = 0;
            updBanner.Width = w; updBanner.Location = new Point(0, top);
            if (updBanner.Visible) top += updBanner.Height + 12;
            coreBanner.Width = w; coreBanner.Location = new Point(0, top);
            if (coreBanner.Visible) top += coreBanner.Height + 12;
            killBanner.Width = w; killBanner.Location = new Point(0, top);
            if (killBanner.Visible) top += killBanner.Height + 16;
            if (top == 0) top = 10;
            power.Location = new Point((w - power.Width) / 2, top);
            lblStatus.Location = new Point((w - lblStatus.Width) / 2, power.Bottom + 6);
            lblSub.Location = new Point((w - lblSub.Width) / 2, lblStatus.Bottom + 4);
            int y = lblSub.Bottom + 20;
            speedPanel.MaximumSize = new Size(Math.Max(260, w), 0);
            homeChips.MaximumSize = new Size(Math.Max(260, w), 0);
            if (speedPanel.Visible && speedPanel.Controls.Count > 0)
            {
                speedPanel.Location = new Point(Math.Max(0, (w - speedPanel.Width) / 2), y);
                y = speedPanel.Bottom + 4;
            }
            homeChips.Location = new Point(Math.Max(0, (w - homeChips.Width) / 2), y);
        }

        // ======================= скорость по туннелям =======================
        FlowLayoutPanel speedPanel;
        readonly Dictionary<string, SpeedCard> speedCards = new Dictionary<string, SpeedCard>();
        Dictionary<string, long[]> lastRates = new Dictionary<string, long[]>();
        string speedKey = "";

        // Карточки: по одной на включённый туннель + «Напрямую»
        void SyncSpeedCards()
        {
            var names = new List<string>();
            foreach (var t in P.Tunnels) if (t.Enabled) names.Add(t.Name);
            string key = string.Join("\n", names.ToArray());
            if (key != speedKey)
            {
                speedKey = key;
                speedPanel.SuspendLayout();
                speedPanel.Controls.Clear();
                speedCards.Clear();
                foreach (var n in names)
                {
                    var c = new SpeedCard { Title = n };
                    speedCards[n] = c;
                    speedPanel.Controls.Add(c);
                }
                var d = new SpeedCard { Title = DirectLabel, Sub = I18n.L("без VPN"), Dot = Theme.Muted };
                speedCards["direct"] = d;
                speedPanel.Controls.Add(d);
                speedPanel.ResumeLayout();
            }
            foreach (var t in P.Tunnels)
            {
                SpeedCard c;
                if (!t.Enabled || !speedCards.TryGetValue(t.Name, out c)) continue;
                string st = StatusOf(t);
                c.Sub = TypeLabel(t) + (st.Length > 0 ? "  ·  " + st : "");
                c.Dot = st.Length > 0 ? TunnelCard.StatusColor(st) : Theme.Accent;
                c.Invalidate();
            }
            speedPanel.Visible = state == "connected" || state == "connecting";
        }

        void OnTraffic(Dictionary<string, long[]> rates)
        {
            lastRates = rates;
            foreach (var kv in speedCards)
            {
                long[] r;
                if (rates.TryGetValue(kv.Key, out r)) kv.Value.Push(r[0], r[1]);
                else kv.Value.Push(0, 0);
            }
            long[] tot;
            if (rates.TryGetValue("", out tot) && state == "connected")
            {
                string tip = AppName + "  ↓ " + SpeedCard.Rate(tot[1]) + "  ↑ " + SpeedCard.Rate(tot[0]);
                tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            }
            if (rates.Count == 0) foreach (var c in speedCards.Values) c.Reset();
        }

        void RefreshHome()
        {
            bool on = state == "connected";
            lblStatus.Text = on ? I18n.L("Защищено") : state == "connecting" ? I18n.L("Подключение…") : state == "stopping" ? I18n.L("Отключение…") : I18n.L("Не подключено");
            lblStatus.ForeColor = on ? Theme.Accent : Theme.Text;
            string fin = ToTargetLabel(P.Final ?? "direct");
            lblSub.Text = on ? I18n.L("Остальной трафик: ") + fin + (P.KillSwitch ? I18n.L("   ·   Kill switch активен") : "")
                             : I18n.L("Нажмите на кнопку, чтобы поднять все включённые туннели");

            homeChips.SuspendLayout();
            homeChips.Controls.Clear();
            foreach (var t in P.Tunnels)
            {
                if (!t.Enabled) continue;
                string st = StatusOf(t);
                homeChips.Controls.Add(new Chip(t.Name + "  ·  " + TypeLabel(t) + (st.Length > 0 ? "  ·  " + st : ""), st.Length > 0 ? TunnelCard.StatusColor(st) : Theme.Border));
            }
            if (P.KillSwitch)
                homeChips.Controls.Add(new Chip("Kill switch" + (engine.KillActive ? I18n.L(" · блокирует без VPN") : I18n.L(" · включится при подключении")), engine.KillActive ? Theme.Accent : Theme.Border));
            homeChips.ResumeLayout();
            // Пока подключено, туннели показаны карточками скорости — таблетки туннелей прячем
            SyncSpeedCards();
            if (speedPanel.Visible)
                for (int i = homeChips.Controls.Count - 1; i >= 0; i--)
                    if (!homeChips.Controls[i].Text.StartsWith("Kill switch")) homeChips.Controls.RemoveAt(i);

            killBanner.Visible = engine.KillActive && state == "stopped";
            killBannerText.Text = I18n.L("VPN не работает, а kill switch держит блокировку: защищённые программы и адреса без VPN не выйдут в интернет.");
            LayoutHome(power.Parent);
        }

        // ======================= туннели =======================
        Control BuildTunnels()
        {
            var add = new FlatButton(I18n.L("Добавить по ссылке"), Theme.IcLink, ButtonKind.Primary);
            add.Click += (s, e) => AddLinks();
            var ovpn = new FlatButton("OpenVPN (.ovpn)", Theme.IcFile, ButtonKind.Secondary);
            ovpn.Click += (s, e) => ImportOvpn();
            var wg = new FlatButton("WireGuard / AmneziaWG (.conf)", Theme.IcFile, ButtonKind.Secondary);
            wg.Click += (s, e) => ImportWg();

            var body = new Panel { BackColor = Theme.Bg };
            tunnelList = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 0) };
            tunnelList.Resize += (s, e) => SizeCards();
            tunnelsEmpty = new Label { Text = I18n.L("Туннелей пока нет. Добавьте ссылку vless://, hy2://, … или импортируйте .ovpn / .conf."), AutoSize = true, ForeColor = Theme.Muted, Location = new Point(4, 10), BackColor = Theme.Bg };
            body.Controls.Add(tunnelList);
            body.Controls.Add(tunnelsEmpty);
            tunnelsEmpty.BringToFront();
            return Page(I18n.L("Туннели"), I18n.L("Все включённые туннели поднимаются одновременно"), Toolbar(add, ovpn, wg), body);
        }

        void SizeCards()
        {
            int w = tunnelList.ClientSize.Width - 6;
            foreach (Control c in tunnelList.Controls) c.Width = Math.Max(300, w);
        }

        void LoadTunnels()
        {
            tunnelList.SuspendLayout();
            tunnelList.Controls.Clear();
            foreach (var t in P.Tunnels)
            {
                var card = new TunnelCard(t, TypeLabel(t)) { Status = StatusOf(t) };
                var tun = t;
                card.Toggle.CheckedChanged += (s, e) =>
                {
                    if (loading) return;
                    tun.Enabled = card.Toggle.Checked; Save(); card.Invalidate(); RefreshTargets();
                };
                card.MenuButton.Click += (s, e) => TunnelMenu(tun).Show(card.MenuButton, new Point(0, card.MenuButton.Height));
                card.DoubleClick += (s, e) => RenameTunnel(tun);
                tunnelList.Controls.Add(card);
            }
            tunnelList.ResumeLayout();
            SizeCards();
            tunnelsEmpty.Visible = P.Tunnels.Count == 0;
            RefreshTargets();
            if (homeChips != null) RefreshHome();
        }

        ContextMenuStrip TunnelMenu(Tunnel t)
        {
            var m = DarkMenu.Create();
            m.Items.Add(I18n.L("Переименовать…"), null, (s, e) => RenameTunnel(t));
            if (t.Type == TunnelTypes.OpenVpn)
            {
                m.Items.Add(I18n.L("Логин и пароль OpenVPN…"), null, (s, e) => SetOvpnCreds(t));
                m.Items.Add(I18n.L("Драйвер OpenVPN…"), null, (s, e) => SetOvpnDriver(t));
            }
            m.Items.Add(I18n.L("DNS внутри туннеля…"), null, (s, e) => SetTunnelDns(t));
            m.Items.Add(new ToolStripSeparator());
            var del = m.Items.Add(I18n.L("Удалить"), null, (s, e) => DeleteTunnel(t));
            del.ForeColor = Theme.Bad;
            return m;
        }

        void RefreshStatuses()
        {
            foreach (Control c in tunnelList.Controls)
            {
                var card = c as TunnelCard;
                if (card == null) continue;
                card.Status = StatusOf(card.Tunnel);
                card.Invalidate();
            }
            RefreshHome();
        }

        string StatusOf(Tunnel t)
        {
            string s;
            return statuses.TryGetValue(t.Name, out s) ? s : "";
        }

        static string TypeLabel(Tunnel t)
        {
            switch (t.Type)
            {
                case "vless": return "VLESS";
                case "vmess": return "VMess";
                case "trojan": return "Trojan";
                case "shadowsocks": return "Shadowsocks";
                case "hysteria2": return "Hysteria2";
                case "hysteria": return "Hysteria";
                case "tuic": return "TUIC";
                case TunnelTypes.WireGuard: return "WireGuard";
                case TunnelTypes.AmneziaWg: return "AmneziaWG";
                case TunnelTypes.OpenVpn: return ConfigBuilder.IsNativeOvpn(t) ? "OpenVPN" : "OpenVPN · " + t.OvpnDriver;
            }
            return t.Type;
        }

        // ======================= правила =======================
        Control BuildRules()
        {
            var add = new FlatButton(I18n.L("Добавить правило"), Theme.IcAdd, ButtonKind.Primary);
            add.Click += (s, e) => AddRule(new Rule { Target = FirstTunnelOrDirect() });
            var presets = new FlatButton(I18n.L("Шаблоны"), Theme.IcTemplate, ButtonKind.Secondary);
            var pm = DarkMenu.Create();
            foreach (var pr in Presets)
            {
                var preset = pr;
                pm.Items.Add(preset[0], null, (s, e) =>
                {
                    bool ru = preset[0].StartsWith(I18n.L("Российские"));
                    bool ads = preset[2] == "category-ads-all";
                    AddRule(new Rule { Kind = preset[1], Values = preset[2], Target = ads ? "block" : ru ? "direct" : FirstTunnelOrDirect() });
                });
            }
            presets.Click += (s, e) => pm.Show(presets, new Point(0, presets.Height + 2));
            var up = new FlatButton(I18n.L("Выше"), Theme.IcUp, ButtonKind.Secondary);
            up.Click += (s, e) => MoveRule(-1);
            var down = new FlatButton(I18n.L("Ниже"), Theme.IcDown, ButtonKind.Secondary);
            down.Click += (s, e) => MoveRule(1);
            var del = new FlatButton(I18n.L("Удалить"), Theme.IcDelete, ButtonKind.Danger);
            del.Click += (s, e) => { if (grid.CurrentRow != null) { grid.Rows.Remove(grid.CurrentRow); SaveRules(); } };

            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToResizeRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
            };
            Theme.StyleGrid(grid);
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Width = 48 });
            var colKind = new DataGridViewComboBoxColumn { HeaderText = I18n.L("ЧТО"), Width = 190, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing };
            foreach (var k in RuleKinds.All) colKind.Items.Add(k[1]);
            grid.Columns.Add(colKind);
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = I18n.L("ЗНАЧЕНИЯ (ЧЕРЕЗ ЗАПЯТУЮ)"), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            colTarget = new DataGridViewComboBoxColumn { HeaderText = I18n.L("КУДА"), Width = 190, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing };
            grid.Columns.Add(colTarget);
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty && !(grid.CurrentCell is DataGridViewTextBoxCell)) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (s, e) => SaveRules();
            grid.UserDeletedRow += (s, e) => SaveRules();
            grid.DataError += (s, e) => { e.ThrowException = false; };
            grid.EditingControlShowing += (s, e) =>
            {
                e.Control.BackColor = Theme.Surface2; e.Control.ForeColor = Theme.Text;
                var cb = e.Control as ComboBox;
                if (cb != null) cb.FlatStyle = FlatStyle.Flat;
            };
            grid.CellPainting += PaintRuleCell;

            var gridCard = new Card { Padding = new Padding(10) };
            gridCard.Controls.Add(grid);

            var bottom = new Card { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(18, 0, 18, 0) };
            var lbl = new Label { Text = I18n.L("Весь остальной трафик"), AutoSize = true, Font = Theme.UiSemi(10f), ForeColor = Theme.Text, Location = new Point(18, 24), BackColor = Color.Transparent };
            cbFinal = new DarkCombo { Width = 240, Location = new Point(228, 20) };
            cbFinal.SelectedIndexChanged += (s, e) =>
            {
                if (loading || cbFinal.SelectedItem == null) return;
                P.Final = FromTargetLabel((string)cbFinal.SelectedItem);
                Save(); RefreshHome();
            };
            var hint = Hint(I18n.L("Проверяются сверху вниз, срабатывает первое подходящее правило."));
            hint.Location = new Point(490, 18);
            bottom.Resize += (s, e) => hint.MaximumSize = new Size(Math.Max(120, bottom.Width - 510), 0);
            bottom.Controls.Add(lbl); bottom.Controls.Add(cbFinal); bottom.Controls.Add(hint);

            var body = new Panel { BackColor = Theme.Bg };
            gridCard.Dock = DockStyle.Fill;
            body.Controls.Add(gridCard);
            body.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Theme.Bg });
            body.Controls.Add(bottom);
            return Page(I18n.L("Правила"), I18n.L("Что куда направлять: домены, IP, программы, порты"), Toolbar(add, presets, up, down, del), body);
        }

        void PaintRuleCell(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex == 0)
            {
                e.PaintBackground(e.CellBounds, true);
                bool on = e.Value is bool && (bool)e.Value;
                var g = e.Graphics; Theme.Hq(g);
                var r = new RectangleF(e.CellBounds.X + (e.CellBounds.Width - 18) / 2f, e.CellBounds.Y + (e.CellBounds.Height - 18) / 2f, 18, 18);
                using (var path = Theme.Round(r, 5))
                {
                    if (on) using (var br = new LinearGradientBrush(r, Theme.Accent, Theme.Accent2, 45f)) g.FillPath(br, path);
                    else using (var br = new SolidBrush(Theme.Surface2)) g.FillPath(br, path);
                    using (var pen = new Pen(on ? Theme.Accent : Theme.Border)) g.DrawPath(pen, path);
                }
                if (on)
                    using (var pen = new Pen(Color.FromArgb(8, 10, 18), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(pen, new[] { new PointF(r.X + 4.5f, r.Y + 9.5f), new PointF(r.X + 7.8f, r.Y + 12.8f), new PointF(r.X + 13.8f, r.Y + 5.8f) });
                e.Handled = true;
            }
            else if (e.ColumnIndex == 3)
            {
                // «Куда» рисуем плашкой
                e.PaintBackground(e.CellBounds, true);
                string v = e.Value as string ?? "";
                var g = e.Graphics; Theme.Hq(g);
                Color c = v == DirectLabel ? Theme.Good : v == BlockLabel ? Theme.Bad : Theme.Accent;
                using (var f = Theme.UiSemi(9f))
                {
                    var sz = g.MeasureString(v, f);
                    var r = new RectangleF(e.CellBounds.X + 8, e.CellBounds.Y + (e.CellBounds.Height - 24) / 2f, Math.Min(sz.Width + 18, e.CellBounds.Width - 16), 24);
                    using (var path = Theme.Round(r, 12))
                    using (var br = new SolidBrush(Theme.Alpha(c, 38))) g.FillPath(br, path);
                    using (var br = new SolidBrush(Theme.Mix(c, Color.White, 0.25f))) g.DrawString(v, f, br, new RectangleF(r.X + 9, r.Y + 4, r.Width - 12, 18), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                }
                e.Handled = true;
            }
        }

        // ======================= защита (kill switch) =======================
        Control BuildProtect()
        {
            var body = new Panel { BackColor = Theme.Bg };

            var top = new Card { Dock = DockStyle.Top, Height = 150 };
            tgKill = new ToggleSwitch { Location = new Point(20, 24) };
            tgKill.CheckedChanged += (s, e) => { if (loading) return; P.KillSwitch = tgKill.Checked; Save(); RefreshKill(); RefreshHome(); };
            top.Controls.Add(tgKill);
            top.Controls.Add(new Label { Text = I18n.L("Kill switch для выбранных программ и сайтов"), Font = Theme.UiSemi(12f), ForeColor = Theme.Text, AutoSize = true, Location = new Point(80, 20), BackColor = Color.Transparent });
            var desc = Hint(I18n.L("Пока VPN включён, эти программы и адреса ходят только через туннель. Если VPN упадёт, брандмауэр Windows не выпустит их в интернет напрямую — до переподключения или пока вы не снимете блокировку."));
            desc.Location = new Point(82, 50);
            top.Resize += (s, e) => desc.MaximumSize = new Size(Math.Max(200, top.Width - 110), 0);
            top.Controls.Add(desc);
            lblKillState = new Label { AutoSize = true, Font = Theme.UiSemi(9f), Location = new Point(82, 114), BackColor = Color.Transparent };
            top.Controls.Add(lblKillState);

            var route = new Card { Dock = DockStyle.Top, Height = 70 };
            route.Controls.Add(new Label { Text = I18n.L("Через туннель"), Font = Theme.UiSemi(10f), ForeColor = Theme.Text, AutoSize = true, Location = new Point(20, 24), BackColor = Color.Transparent });
            cbKillTunnel = new DarkCombo { Width = 260, Location = new Point(170, 20) };
            cbKillTunnel.SelectedIndexChanged += (s, e) =>
            {
                if (loading || cbKillTunnel.SelectedItem == null) return;
                string v = (string)cbKillTunnel.SelectedItem;
                P.KillTunnel = v.StartsWith(I18n.L("Как «остальной")) ? "" : v; Save();
            };
            route.Controls.Add(cbKillTunnel);
            var rh = Hint(I18n.L("Защищённый трафик никогда не идёт напрямую, даже если совпадёт с правилом «Напрямую»."));
            rh.Location = new Point(452, 17);
            route.Resize += (s, e) => rh.MaximumSize = new Size(Math.Max(120, route.Width - 470), 0);
            route.Controls.Add(rh);

            // приложения + домены
            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Theme.Bg };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));

            var appsCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
            var appsHead = new Label { Text = I18n.L("Программы"), Font = Theme.UiSemi(11f), ForeColor = Theme.Text, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.Transparent };
            var appsHint = new Label { Text = I18n.L("Пути к .exe, в пути можно использовать *"), ForeColor = Theme.Muted, AutoSize = false, Height = 26, Dock = DockStyle.Top, BackColor = Color.Transparent };
            lbApps = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 34, IntegralHeight = false };
            lbApps.DrawItem += DrawAppItem;
            var appsBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, BackColor = Theme.Surface, Padding = new Padding(0, 8, 0, 0), WrapContents = false };
            var addExe = new FlatButton(I18n.L("Файл .exe"), Theme.IcApp, ButtonKind.Secondary);
            addExe.Click += (s, e) => AddKillExe();
            var addPat = new FlatButton(I18n.L("Шаблон"), Theme.IcAdd, ButtonKind.Secondary);
            addPat.Click += (s, e) =>
            {
                string v = Dialogs.Input(this, I18n.L("Шаблон пути"), I18n.L("Например: %LOCALAPPDATA%\\Programs\\App\\app.exe или C:\\Program Files\\WindowsApps\\App_*\\app.exe"), "");
                if (!string.IsNullOrEmpty(v)) { P.KillApps.Add(v); Save(); LoadKill(); }
            };
            var delApp = new FlatButton("", Theme.IcDelete, ButtonKind.Danger) { Width = 40 };
            delApp.Click += (s, e) => { if (lbApps.SelectedIndex >= 0) { P.KillApps.RemoveAt(lbApps.SelectedIndex); Save(); LoadKill(); } };
            var defApps = new FlatButton("Claude", Theme.IcShield, ButtonKind.Ghost);
            defApps.Click += (s, e) =>
            {
                foreach (var a in KillSwitch.DefaultApps) if (!P.KillApps.Contains(a)) P.KillApps.Add(a);
                Save(); LoadKill();
            };
            appsBtns.Controls.AddRange(new Control[] { addExe, addPat, defApps, delApp });
            appsCard.Controls.Add(lbApps);
            appsCard.Controls.Add(appsBtns);
            appsCard.Controls.Add(appsHint);
            appsCard.Controls.Add(appsHead);

            var domCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0) };
            var domHead = new Label { Text = I18n.L("Сайты и адреса"), Font = Theme.UiSemi(11f), ForeColor = Theme.Text, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.Transparent };
            var domHint = new Label { Text = I18n.L("Через запятую, вместе с поддоменами"), ForeColor = Theme.Muted, AutoSize = false, Height = 26, Dock = DockStyle.Top, BackColor = Color.Transparent };
            tbKillDomains = new TextBox { Dock = DockStyle.Fill, Multiline = true, BorderStyle = BorderStyle.None, BackColor = Theme.Surface2, ForeColor = Theme.Text, Font = Theme.Mono(9.5f), ScrollBars = ScrollBars.Vertical };
            tbKillDomains.TextChanged += (s, e) => { if (!loading) { P.KillDomains = tbKillDomains.Text; Save(); } };
            var domWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface2, Padding = new Padding(10) };
            domWrap.Controls.Add(tbKillDomains);
            domCard.Controls.Add(domWrap);
            domCard.Controls.Add(domHint);
            domCard.Controls.Add(domHead);

            split.Controls.Add(appsCard, 0, 0);
            split.Controls.Add(domCard, 1, 0);

            body.Controls.Add(split);
            body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Theme.Bg });
            body.Controls.Add(route);
            body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Theme.Bg });
            body.Controls.Add(top);
            return Page(I18n.L("Защита"), I18n.L("Kill switch: без VPN — без интернета для важных программ"), null, body);
        }

        void DrawAppItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics; Theme.Hq(g);
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var br = new SolidBrush(sel ? Theme.Mix(Theme.Surface, Theme.Accent, 0.15f) : Theme.Surface)) g.FillRectangle(br, e.Bounds);
            string path = (string)lbApps.Items[e.Index];
            string name = Path.GetFileName(path);
            using (var f = Theme.Icon(11f)) using (var br = new SolidBrush(Theme.Accent)) g.DrawString(Theme.IcApp, f, br, e.Bounds.X + 6, e.Bounds.Y + 9);
            using (var f = Theme.UiSemi(9.5f)) using (var br = new SolidBrush(Theme.Text)) g.DrawString(name, f, br, e.Bounds.X + 30, e.Bounds.Y + 1);
            using (var f = Theme.Ui(8f)) using (var br = new SolidBrush(Theme.Muted))
                g.DrawString(path, f, br, new RectangleF(e.Bounds.X + 30, e.Bounds.Y + 17, e.Bounds.Width - 34, 16), new StringFormat { Trimming = StringTrimming.EllipsisPath, FormatFlags = StringFormatFlags.NoWrap });
        }

        void AddKillExe()
        {
            using (var d = new OpenFileDialog { Filter = I18n.L("Программа (*.exe)|*.exe") })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!P.KillApps.Contains(d.FileName)) P.KillApps.Add(d.FileName);
                Save(); LoadKill();
            }
        }

        void LoadKill()
        {
            bool was = loading; loading = true;
            tgKill.SetSilently(P.KillSwitch);
            lbApps.Items.Clear();
            foreach (var a in P.KillApps) lbApps.Items.Add(a);
            tbKillDomains.Text = P.KillDomains ?? "";
            loading = was;
            RefreshKill();
        }

        void RefreshKill()
        {
            if (lblKillState == null) return;
            if (engine.KillActive && state != "connected") { lblKillState.Text = I18n.L("●  Блокировка активна: VPN не работает, защищённые программы отрезаны от интернета"); lblKillState.ForeColor = Theme.Warn; }
            else if (engine.KillActive) { lblKillState.Text = I18n.L("●  Работает: правила брандмауэра установлены"); lblKillState.ForeColor = Theme.Good; }
            else if (P.KillSwitch) { lblKillState.Text = I18n.L("●  Включится при подключении VPN"); lblKillState.ForeColor = Theme.Accent; }
            else { lblKillState.Text = I18n.L("●  Выключен"); lblKillState.ForeColor = Theme.Muted; }
            if (homeChips != null && pages.ContainsKey("home") && pages["home"].Visible) RefreshHome();
        }

        // ======================= настройки =======================
        Control BuildSettings()
        {
            var card = new Card { Padding = new Padding(22, 18, 22, 18), AutoScroll = true };
            var tl = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, BackColor = Theme.Surface };
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));

            tbSingBox = PathRow(tl, "sing-box.exe", I18n.L("пусто — cores\\sing-box.exe"));
            tbOpenVpn = PathRow(tl, "openvpn.exe", I18n.L("нужен только для драйверов dco / wintun / tap"));
            tbAwg = PathRow(tl, "amneziawg-go.exe", I18n.L("пусто — cores\\amneziawg\\amneziawg-go.exe"));

            tbDns = new TextBox { Dock = DockStyle.Fill };
            tbDns.TextChanged += (s, e) => { if (!loading) { P.DefaultDns = tbDns.Text.Trim(); Save(); } };
            AddRow(tl, I18n.L("DNS по умолчанию"), tbDns, I18n.L("local — системный; или 77.88.8.8, tls://1.1.1.1, https://1.1.1.1/dns-query"));

            cbRuleSetDetour = new DarkCombo { Width = 260 };
            cbRuleSetDetour.SelectedIndexChanged += (s, e) =>
            {
                if (loading || cbRuleSetDetour.SelectedItem == null) return;
                P.RuleSetDetour = FromTargetLabel((string)cbRuleSetDetour.SelectedItem); Save();
            };
            AddRow(tl, I18n.L("Скачивать geosite / geoip через"), cbRuleSetDetour, I18n.L("GitHub бывает недоступен напрямую — лучше через туннель"));

            cbLogLevel = new DarkCombo { Width = 140 };
            cbLogLevel.Items.AddRange(new object[] { "error", "warn", "info", "debug" });
            cbLogLevel.SelectedIndexChanged += (s, e) => { if (!loading && cbLogLevel.SelectedItem != null) { P.LogLevel = (string)cbLogLevel.SelectedItem; Save(); } };
            AddRow(tl, I18n.L("Подробность журнала"), cbLogLevel, I18n.L("warn — только проблемы, info — каждое соединение"));

            tgStrict = new ToggleSwitch();
            tgStrict.CheckedChanged += (s, e) => { if (!loading) { P.StrictRoute = tgStrict.Checked; Save(); } };
            AddRow(tl, I18n.L("Строгая маршрутизация"), tgStrict, I18n.L("strict_route: защита от утечек DNS. Если что-то сломалось — выключите"));

            var cbLang = new DarkCombo { Width = 200 };
            cbLang.Items.AddRange(new object[] { "Auto / Авто", "Русский", "English" });
            string curLang = (P.Language ?? "auto").ToLowerInvariant();
            cbLang.SelectedIndex = curLang == "ru" ? 1 : curLang == "en" ? 2 : 0;
            cbLang.SelectedIndexChanged += (s, e) =>
            {
                P.Language = cbLang.SelectedIndex == 1 ? "ru" : cbLang.SelectedIndex == 2 ? "en" : "auto";
                Save();
                MessageBox.Show(this, I18n.L("Перезапустите Hydra, чтобы сменить язык."), AppName);
            };
            AddRow(tl, I18n.L("Язык") + " / Language", cbLang, null);

            var updRow = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Margin = new Padding(0) };
            var tgUpd = new ToggleSwitch { Margin = new Padding(0, 4, 14, 0) };
            tgUpd.SetSilently(P.CheckUpdates);
            tgUpd.CheckedChanged += (s, e) => { P.CheckUpdates = tgUpd.Checked; Save(); };
            var checkNow = new FlatButton(I18n.L("Проверить сейчас"), Theme.IcDown, ButtonKind.Secondary) { Margin = new Padding(0) };
            checkNow.Click += (s, e) => CheckForUpdates(true);
            updRow.Controls.Add(tgUpd); updRow.Controls.Add(checkNow);
            AddRow(tl, I18n.L("Проверять обновления"), updRow, I18n.L("При запуске Hydra смотрит последний релиз на GitHub. Текущая версия: ") + Updater.CurrentVersion);

            var tgNotify = new ToggleSwitch();
            tgNotify.SetSilently(P.NotifyFailures);
            tgNotify.CheckedChanged += (s, e) => { P.NotifyFailures = tgNotify.Checked; Save(); };
            AddRow(tl, I18n.L("Уведомления о сбоях"), tgNotify, I18n.L("Всплывающие уведомления Windows: обрыв VPN, туннель не отвечает, ошибка подключения, kill switch, сбой Hydra"));

            var tgHeal = new ToggleSwitch();
            tgHeal.SetSilently(P.AutoReconnect);
            tgHeal.CheckedChanged += (s, e) => { P.AutoReconnect = tgHeal.Checked; Save(); };
            AddRow(tl, I18n.L("Автопереподключение"), tgHeal, I18n.L("Если туннель перестал отвечать, Hydra сама переподключит VPN (kill switch при этом не снимается). Не чаще раза в 3 минуты."));

            var folder = new FlatButton(I18n.L("Открыть папку Hydra"), Theme.IcFolder, ButtonKind.Secondary);
            folder.Click += (s, e) => Process.Start("explorer.exe", "\"" + AppPaths.BaseDir + "\"");
            var preview = new FlatButton(I18n.L("Показать конфиг sing-box"), Theme.IcCode, ButtonKind.Secondary);
            preview.Click += (s, e) => PreviewConfig();
            var btns = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 14, 0, 0) };
            btns.Controls.Add(folder); btns.Controls.Add(preview);
            int row = tl.RowCount++;
            tl.Controls.Add(btns, 1, row);

            card.Controls.Add(tl);
            return Page(I18n.L("Настройки"), I18n.L("Ядра, DNS и прочее"), null, card);
        }

        TextBox PathRow(TableLayoutPanel tl, string label, string hint)
        {
            var tb = new TextBox { Dock = DockStyle.Fill };
            tb.TextChanged += (s, e) => { if (!loading) SaveSettings(); };
            var b = new FlatButton("", Theme.IcFolder, ButtonKind.Secondary) { Width = 40, Height = 30, Margin = new Padding(6, 0, 0, 0) };
            b.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = I18n.L("Программа (*.exe)|*.exe") })
                    if (d.ShowDialog(this) == DialogResult.OK) tb.Text = d.FileName;
            };
            int row = tl.RowCount++;
            tl.Controls.Add(RowLabel(label), 0, row);
            tl.Controls.Add(tb, 1, row);
            tl.Controls.Add(b, 2, row);
            if (hint != null) HintRow(tl, hint);
            return tb;
        }

        void AddRow(TableLayoutPanel tl, string label, Control c, string hint)
        {
            int row = tl.RowCount++;
            tl.Controls.Add(RowLabel(label), 0, row);
            tl.Controls.Add(c, 1, row);
            if (hint != null) HintRow(tl, hint);
        }

        static Label RowLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = Theme.UiSemi(9.5f), ForeColor = Theme.Text, Margin = new Padding(0, 7, 0, 0), BackColor = Color.Transparent };
        }

        void HintRow(TableLayoutPanel tl, string hint)
        {
            int row = tl.RowCount++;
            tl.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Ui(8.5f), Margin = new Padding(3, 2, 0, 6), BackColor = Color.Transparent }, 1, row);
        }

        // ======================= журнал =======================
        Control BuildLog()
        {
            var copy = new FlatButton(I18n.L("Копировать"), Theme.IcCopy, ButtonKind.Secondary);
            copy.Click += (s, e) => { try { if (txtLog.TextLength > 0) Clipboard.SetText(txtLog.Text); } catch { } };
            var clear = new FlatButton(I18n.L("Очистить"), Theme.IcClear, ButtonKind.Secondary);
            clear.Click += (s, e) => txtLog.Clear();
            txtLog = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
                Font = Theme.Mono(9f), BackColor = Color.FromArgb(11, 14, 24), ForeColor = Color.FromArgb(150, 225, 205), BorderStyle = BorderStyle.None,
            };
            var card = new Card { Padding = new Padding(14), Fill = Color.FromArgb(11, 14, 24) };
            card.BackColor = Color.FromArgb(11, 14, 24);
            card.Controls.Add(txtLog);
            return Page(I18n.L("Журнал"), I18n.L("Что происходит внутри: sing-box, туннели, kill switch"), Toolbar(copy, clear), card);
        }

        // Сообщения самого окна: и в журнал на диске, и на экран
        void AppendLog(string s)
        {
            FileLog.Write(s);
            AppendLogUi(s);
        }

        void AppendLogUi(string s)
        {
            if (txtLog.TextLength > 400000) txtLog.Text = txtLog.Text.Substring(200000);
            txtLog.AppendText(s + Environment.NewLine);
        }

        // ======================= история =======================
        ListBox lbHistory;
        DarkCombo cbHistFilter;
        Label statSessions, statDrops, statOnline;
        List<HistoryEvent> historyAll = new List<HistoryEvent>();

        Control BuildHistory()
        {
            cbHistFilter = new DarkCombo { Width = 230, Margin = new Padding(0, 2, 8, 8) };
            cbHistFilter.Items.AddRange(new object[] { I18n.L("Все события"), I18n.L("Подключения и отключения"), I18n.L("Обрывы и ошибки"), I18n.L("Сбои Hydra") });
            cbHistFilter.SelectedIndex = 0;
            cbHistFilter.SelectedIndexChanged += (s, e) => FillHistory();
            var openLogs = new FlatButton(I18n.L("Папка логов"), Theme.IcFolder, ButtonKind.Secondary);
            openLogs.Click += (s, e) => { Directory.CreateDirectory(FileLog.Dir); Process.Start("explorer.exe", "\"" + FileLog.Dir + "\""); };
            var today = new FlatButton(I18n.L("Журнал за сегодня"), Theme.IcFile, ButtonKind.Secondary);
            today.Click += (s, e) => OpenFile(FileLog.PathFor(DateTime.Now));
            var clear = new FlatButton(I18n.L("Очистить историю"), Theme.IcDelete, ButtonKind.Danger);
            clear.Click += (s, e) =>
            {
                if (MessageBox.Show(this, I18n.L("Очистить историю событий? Журналы и отчёты о сбоях останутся в папке логов."), AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                History.Clear(); RefreshHistory();
            };

            var stats = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Bg, WrapContents = false };
            statSessions = StatCard(stats, I18n.L("подключений за 7 дней"), Theme.Accent);
            statDrops = StatCard(stats, I18n.L("обрывов и ошибок за 7 дней"), Theme.Warn);
            statOnline = StatCard(stats, I18n.L("под VPN сегодня"), Theme.Good);

            lbHistory = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 58, IntegralHeight = false };
            lbHistory.DrawItem += DrawHistoryItem;
            lbHistory.DoubleClick += (s, e) =>
            {
                var ev = lbHistory.SelectedItem as HistoryEvent;
                if (ev == null) return;
                if (!string.IsNullOrEmpty(ev.File) && File.Exists(ev.File)) OpenFile(ev.File);
                else OpenFile(FileLog.PathFor(ev.When()));
            };
            var card = new Card { Padding = new Padding(8) };
            card.Controls.Add(lbHistory);

            var body = new Panel { BackColor = Theme.Bg };
            card.Dock = DockStyle.Fill;
            body.Controls.Add(card);
            body.Controls.Add(stats);
            return Page(I18n.L("История"), I18n.L("Подключения, обрывы и сбои. Двойной клик — отчёт или журнал за тот день"), Toolbar(cbHistFilter, openLogs, today, clear), body);
        }

        Label StatCard(FlowLayoutPanel parent, string caption, Color accent)
        {
            var c = new Card { Width = 250, Height = 76, Margin = new Padding(0, 0, 12, 0), Padding = new Padding(0) };
            var value = new Label { Text = "—", Font = Theme.UiBold(18f), ForeColor = accent, AutoSize = true, Location = new Point(18, 10), BackColor = Color.Transparent };
            c.Controls.Add(value);
            c.Controls.Add(new Label { Text = caption, Font = Theme.Ui(9f), ForeColor = Theme.Muted, AutoSize = true, Location = new Point(20, 46), BackColor = Color.Transparent });
            parent.Controls.Add(c);
            return value;
        }

        void RefreshHistory()
        {
            historyAll = History.Load();
            DateTime weekAgo = DateTime.Now.AddDays(-7), today = DateTime.Today;
            int sessions = 0, drops = 0; long online = 0;
            foreach (var e in historyAll)
            {
                var t = e.When();
                if (t >= weekAgo)
                {
                    if (e.Kind == History.Connected) sessions++;
                    if (e.Kind == History.Dropped || e.Kind == History.Error || e.Kind == History.TunnelError || e.Kind == History.Crash) drops++;
                }
                if (t >= today && (e.Kind == History.Disconnected || e.Kind == History.Dropped)) online += e.Duration;
            }
            if (state == "connected")
                foreach (var e in historyAll)
                    if (e.Kind == History.Connected) { var t = e.When(); online += (long)(DateTime.Now - (t > today ? t : today)).TotalSeconds; break; }
            statSessions.Text = sessions.ToString();
            statDrops.Text = drops.ToString();
            statOnline.Text = online > 0 ? History.FormatDuration(online) : "0";
            FillHistory();
        }

        void FillHistory()
        {
            if (lbHistory == null) return;
            int f = cbHistFilter.SelectedIndex;
            lbHistory.BeginUpdate();
            lbHistory.Items.Clear();
            foreach (var e in historyAll)
            {
                bool show = f == 0
                    || f == 1 && (e.Kind == History.Connected || e.Kind == History.Disconnected || e.Kind == History.Dropped)
                    || f == 2 && (e.Kind == History.Dropped || e.Kind == History.Error || e.Kind == History.TunnelError || e.Kind == History.KillHold)
                    || f == 3 && e.Kind == History.Crash;
                if (show) lbHistory.Items.Add(e);
            }
            if (lbHistory.Items.Count == 0) lbHistory.Items.Add(I18n.L("Событий пока нет"));
            lbHistory.EndUpdate();
        }

        static Color KindColor(string k)
        {
            if (k == History.Connected) return Theme.Good;
            if (k == History.Disconnected || k == History.KillRelease) return Theme.Accent;
            if (k == History.Dropped || k == History.KillHold) return Theme.Warn;
            if (k == History.Error || k == History.TunnelError || k == History.Crash) return Theme.Bad;
            if (k == History.Update || k == History.Reconnect) return Theme.Accent2;
            return Theme.Muted;
        }

        void DrawHistoryItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics; Theme.Hq(g);
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var br = new SolidBrush(sel ? Theme.Mix(Theme.Surface, Theme.Accent, 0.12f) : Theme.Surface)) g.FillRectangle(br, e.Bounds);
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, e.Bounds.X + 12, e.Bounds.Bottom - 1, e.Bounds.Right - 12, e.Bounds.Bottom - 1);
            var ev = lbHistory.Items[e.Index] as HistoryEvent;
            if (ev == null)
            {
                using (var br = new SolidBrush(Theme.Muted)) g.DrawString(Convert.ToString(lbHistory.Items[e.Index]), Theme.Ui(9.5f), br, e.Bounds.X + 16, e.Bounds.Y + 18);
                return;
            }
            Color c = KindColor(ev.Kind);
            float cy = e.Bounds.Y + e.Bounds.Height / 2f;
            using (var br = new SolidBrush(Theme.Alpha(c, 50))) g.FillEllipse(br, e.Bounds.X + 14, cy - 9, 18, 18);
            using (var br = new SolidBrush(c)) g.FillEllipse(br, e.Bounds.X + 19, cy - 4, 8, 8);

            var t = ev.When();
            string when = t.Date == DateTime.Today ? t.ToString("HH:mm:ss") : t.ToString("dd.MM HH:mm");
            using (var f = Theme.UiSemi(10f)) using (var br = new SolidBrush(Theme.Text))
                g.DrawString(ev.Text ?? "", f, br, new RectangleF(e.Bounds.X + 44, e.Bounds.Y + 8, e.Bounds.Width - 200, 22), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            using (var f = Theme.Ui(9f)) using (var br = new SolidBrush(Theme.Muted))
                g.DrawString(when, f, br, new RectangleF(e.Bounds.Right - 150, e.Bounds.Y + 10, 136, 18), new StringFormat { Alignment = StringAlignment.Far });

            var sub = new List<string>();
            if (ev.Duration > 0) sub.Add(I18n.L("длительность ") + History.FormatDuration(ev.Duration));
            if (!string.IsNullOrEmpty(ev.Details)) sub.Add(ev.Details);
            if (!string.IsNullOrEmpty(ev.File)) sub.Add(I18n.L("есть отчёт"));
            using (var f = Theme.Ui(8.5f)) using (var br = new SolidBrush(Theme.Muted))
                g.DrawString(string.Join("   ·   ", sub.ToArray()), f, br, new RectangleF(e.Bounds.X + 44, e.Bounds.Y + 32, e.Bounds.Width - 60, 18), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
        }

        void OpenFile(string path)
        {
            if (File.Exists(path)) { try { Process.Start("notepad.exe", "\"" + path + "\""); } catch { } }
            else MessageBox.Show(this, I18n.L("Файл не найден: ") + path, AppName);
        }

        // ======================= данные =======================
        void Save()
        {
            try { store.Save(); } catch (Exception ex) { AppendLog(I18n.L("Не удалось сохранить профиль: ") + ex.Message); }
        }

        string ToTargetLabel(string t) { return t == "direct" ? DirectLabel : t == "block" ? BlockLabel : t; }
        string FromTargetLabel(string l) { return l == DirectLabel ? "direct" : l == BlockLabel ? "block" : l; }

        string FirstTunnelOrDirect()
        {
            foreach (var t in P.Tunnels) if (t.Enabled) return t.Name;
            return "direct";
        }

        void RefreshTargets()
        {
            if (colTarget == null || cbFinal == null) return;
            bool was = loading; loading = true;
            var names = new List<string> { DirectLabel, BlockLabel };
            foreach (var t in P.Tunnels) names.Add(t.Name);
            foreach (var r in P.Rules) { string l = ToTargetLabel(r.Target); if (!names.Contains(l)) names.Add(l); }

            colTarget.Items.Clear();
            foreach (var n in names) colTarget.Items.Add(n);

            cbFinal.Items.Clear();
            cbRuleSetDetour.Items.Clear();
            foreach (var n in names)
            {
                if (n == BlockLabel) continue;
                cbFinal.Items.Add(n);
                cbRuleSetDetour.Items.Add(n);
            }
            string fl = ToTargetLabel(P.Final ?? "direct");
            if (!cbFinal.Items.Contains(fl)) cbFinal.Items.Add(fl);
            cbFinal.SelectedItem = fl;
            string rl = ToTargetLabel(P.RuleSetDetour ?? "direct");
            if (!cbRuleSetDetour.Items.Contains(rl)) cbRuleSetDetour.Items.Add(rl);
            cbRuleSetDetour.SelectedItem = rl;

            if (cbKillTunnel != null)
            {
                cbKillTunnel.Items.Clear();
                string auto = I18n.L("Как «остальной трафик»");
                cbKillTunnel.Items.Add(auto);
                foreach (var t in P.Tunnels) cbKillTunnel.Items.Add(t.Name);
                cbKillTunnel.SelectedItem = string.IsNullOrEmpty(P.KillTunnel) || P.Find(P.KillTunnel) == null ? auto : P.KillTunnel;
            }
            loading = was;
        }

        void LoadRules()
        {
            bool was = loading; loading = true;
            RefreshTargets();
            grid.Rows.Clear();
            foreach (var r in P.Rules)
                grid.Rows.Add(r.Enabled, RuleKinds.Display(r.Kind), r.Values, ToTargetLabel(r.Target));
            loading = was;
        }

        void SaveRules()
        {
            if (loading) return;
            var list = new List<Rule>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                list.Add(new Rule
                {
                    Enabled = row.Cells[0].Value is bool && (bool)row.Cells[0].Value,
                    Kind = RuleKinds.FromDisplay(row.Cells[1].Value as string),
                    Values = (row.Cells[2].Value as string) ?? "",
                    Target = FromTargetLabel((row.Cells[3].Value as string) ?? DirectLabel),
                });
            }
            P.Rules = list;
            Save();
        }

        void AddRule(Rule r)
        {
            P.Rules.Add(r);
            LoadRules();
            Save();
            if (grid.Rows.Count > 0) grid.CurrentCell = grid.Rows[grid.Rows.Count - 1].Cells[2];
        }

        void MoveRule(int d)
        {
            if (grid.CurrentRow == null) return;
            int i = grid.CurrentRow.Index, j = i + d;
            if (j < 0 || j >= P.Rules.Count) return;
            var r = P.Rules[i]; P.Rules[i] = P.Rules[j]; P.Rules[j] = r;
            LoadRules(); Save();
            grid.CurrentCell = grid.Rows[j].Cells[2];
        }

        void LoadSettings()
        {
            tbSingBox.Text = P.SingBoxPath ?? "";
            tbOpenVpn.Text = P.OpenVpnPath ?? "";
            tbAwg.Text = P.AwgGoPath ?? "";
            tbDns.Text = P.DefaultDns ?? "local";
            tgStrict.SetSilently(P.StrictRoute);
            cbLogLevel.SelectedItem = string.IsNullOrEmpty(P.LogLevel) ? "info" : P.LogLevel;
        }

        void SaveSettings()
        {
            P.SingBoxPath = tbSingBox.Text.Trim();
            P.OpenVpnPath = tbOpenVpn.Text.Trim();
            P.AwgGoPath = tbAwg.Text.Trim();
            Save();
        }

        // ======================= действия с туннелями =======================
        void AddLinks()
        {
            string text = Dialogs.MultiLine(this, I18n.L("Добавить по ссылке"), I18n.L("Вставьте одну или несколько ссылок (по одной на строку) или подписку в base64:\nvless://  vmess://  trojan://  ss://  hy2://  hysteria://  tuic://"));
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            string dec;
            if (!text.Contains("://") && LinkParser.TryB64(text, out dec) && dec.Contains("://")) text = dec;

            int ok = 0, before = P.Tunnels.Count;
            var errors = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                try
                {
                    var pl = LinkParser.Parse(line);
                    P.Tunnels.Add(new Tunnel { Name = P.UniqueName(pl.Name), Type = pl.Type, Link = line, Server = pl.Server });
                    ok++;
                }
                catch (Exception ex) { errors.Add(ex.Message); }
            }
            if (ok > 0 && before == 0) AutoFinal(P.Tunnels[0]);
            Save(); LoadTunnels();
            if (errors.Count > 0)
                MessageBox.Show(this, I18n.L("Добавлено: ") + ok + I18n.L("\nНе удалось разобрать: ") + errors.Count + "\n\n" + string.Join("\n", errors.ToArray()), AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void ImportOvpn()
        {
            using (var d = new OpenFileDialog { Filter = I18n.L("OpenVPN (*.ovpn;*.conf)|*.ovpn;*.conf|Все файлы|*.*") })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var t = new Tunnel { Type = TunnelTypes.OpenVpn, Name = P.UniqueName(Path.GetFileNameWithoutExtension(d.FileName)) };
                    t.ConfigFile = OvpnConf.Import(d.FileName, t.Dir());
                    string text = File.ReadAllText(t.ConfigPath());
                    var remotes = OvpnConf.Remotes(text);
                    t.Server = remotes.Count > 0 ? string.Join(", ", remotes.ToArray()) : "";
                    if (OvpnConf.NeedsAuth(text))
                    {
                        string user = ""; string pass;
                        if (Dialogs.Credentials(this, I18n.L("Логин для ") + t.Name, ref user, out pass)) { t.OvpnUser = user; t.OvpnPassProtected = Secret.Protect(pass); }
                    }
                    P.Tunnels.Add(t);
                    if (P.Tunnels.Count == 1) AutoFinal(t);
                    Save(); LoadTunnels();
                }
                catch (Exception ex) { MessageBox.Show(this, I18n.L("Не удалось импортировать: ") + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        void ImportWg()
        {
            using (var d = new OpenFileDialog { Filter = I18n.L("WireGuard / AmneziaWG (*.conf)|*.conf|Все файлы|*.*") })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string text = File.ReadAllText(d.FileName);
                    var wg = WgConf.Parse(text);
                    string host; int port;
                    wg.Endpoint(0, out host, out port);
                    var t = new Tunnel
                    {
                        Type = wg.IsAmnezia() ? TunnelTypes.AmneziaWg : TunnelTypes.WireGuard,
                        Name = P.UniqueName(Path.GetFileNameWithoutExtension(d.FileName)),
                        Server = host + ":" + port,
                        ConfigFile = "tunnel.conf",
                    };
                    Directory.CreateDirectory(t.Dir());
                    File.WriteAllText(t.ConfigPath(), text);
                    P.Tunnels.Add(t);
                    if (P.Tunnels.Count == 1) AutoFinal(t);
                    Save(); LoadTunnels();
                }
                catch (Exception ex) { MessageBox.Show(this, I18n.L("Не удалось импортировать: ") + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        void RenameTunnel(Tunnel t)
        {
            string n = Dialogs.Input(this, I18n.L("Переименовать"), I18n.L("Новое имя туннеля:"), t.Name);
            if (string.IsNullOrEmpty(n) || n == t.Name) return;
            if (P.Find(n) != null || n == "direct" || n == "block" || n == DirectLabel || n == BlockLabel) { MessageBox.Show(this, I18n.L("Такое имя уже занято."), AppName); return; }
            foreach (var r in P.Rules) if (r.Target == t.Name) r.Target = n;
            if (P.Final == t.Name) P.Final = n;
            if (P.RuleSetDetour == t.Name) P.RuleSetDetour = n;
            if (P.KillTunnel == t.Name) P.KillTunnel = n;
            t.Name = n;
            Save(); LoadTunnels(); LoadRules();
        }

        void DeleteTunnel(Tunnel t)
        {
            if (MessageBox.Show(this, I18n.L("Удалить туннель «") + t.Name + I18n.L("»?\nПравила, которые на него ссылаются, будут пропускаться."), AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            P.Tunnels.Remove(t);
            try { if (Directory.Exists(t.Dir())) Directory.Delete(t.Dir(), true); } catch { }
            Save(); LoadTunnels(); LoadRules();
        }

        void SetOvpnCreds(Tunnel t)
        {
            string user = t.OvpnUser; string pass;
            if (!Dialogs.Credentials(this, I18n.L("Логин для ") + t.Name, ref user, out pass)) return;
            t.OvpnUser = user; t.OvpnPassProtected = Secret.Protect(pass);
            Save();
        }

        void SetOvpnDriver(Tunnel t)
        {
            string v = Dialogs.Input(this, I18n.L("Драйвер OpenVPN"), I18n.L("native — встроенный в sing-box (по умолчанию).  dco / wintun / tap — через установленный openvpn.exe"), t.OvpnDriver ?? "native");
            if (v == null) return;
            v = v.ToLowerInvariant();
            if (v != "native" && v != "dco" && v != "wintun" && v != "tap") { MessageBox.Show(this, I18n.L("Допустимо: native, dco, wintun, tap"), AppName); return; }
            t.OvpnDriver = v; Save(); LoadTunnels();
        }

        void SetTunnelDns(Tunnel t)
        {
            string v = Dialogs.Input(this, I18n.L("DNS внутри туннеля"), I18n.L("DNS для доменов, которые идут через «") + t.Name + I18n.L("» (пусто — автоматически):"), t.DnsServer);
            if (v == null) return;
            t.DnsServer = v; Save();
        }

        // ======================= подключение =======================
        void Connect()
        {
            SaveRules();
            if (AppPaths.SingBox(P) == null)
            {
                // Ядра ещё нет: показываем загрузку на главной
                ShowPage("home");
                if (!coreBusy) StartCoreDownload();
                return;
            }
            // Защита от «включил — ничего не изменилось»: весь трафик напрямую и ни одно правило не ведёт в туннель
            if (P.Final == "direct" && !AnyRuleUsesTunnel())
            {
                Tunnel first = null;
                foreach (var t in P.Tunnels) if (t.Enabled) { first = t; break; }
                if (first != null)
                {
                    var ans = MessageBox.Show(this,
                        I18n.L("Сейчас весь трафик идёт напрямую, и ни одно правило не направляет его в туннель — VPN ничего не изменит.\n\nПустить весь трафик через «") + first.Name + I18n.L("»?\n\n(Это можно поменять на вкладке «Правила» → «Весь остальной трафик».)"),
                        AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (ans == DialogResult.Cancel) return;
                    if (ans == DialogResult.Yes) { P.Final = first.Name; Save(); RefreshTargets(); RefreshHome(); }
                }
            }
            statuses.Clear();
            engine.Start(P);
        }

        bool AnyRuleUsesTunnel()
        {
            foreach (var r in P.Rules)
                if (r.Enabled && r.Target != "direct" && r.Target != "block")
                {
                    var t = P.Find(r.Target);
                    if (t != null && t.Enabled) return true;
                }
            return false;
        }

        // Первый туннель сразу становится маршрутом для всего трафика — иначе VPN «ничего не делает»
        void AutoFinal(Tunnel t)
        {
            if (P.Final != "direct" || P.Rules.Count > 0) return;
            P.Final = t.Name;
            AppendLog(I18n.L("Весь трафик теперь идёт через «") + t.Name + I18n.L("». Изменить: «Правила» → «Весь остальной трафик».") );
        }

        void OnState(string st)
        {
            string prev = state;
            state = st;
            power.State = st;
            power.Invalidate();
            bool on = st == "connected";
            sideStatus.Text = on ? I18n.L("Защищено") : st == "connecting" ? I18n.L("Подключение…") : st == "stopping" ? I18n.L("Отключение…") : I18n.L("Не подключено");
            sideDotColor = on ? Theme.Good : st == "stopped" ? (engine.KillActive ? Theme.Warn : Theme.Muted) : Theme.Warn;
            sideDot.Invalidate();
            tray.Text = AppName + I18n.L(" — ") + sideStatus.Text;
            SetTrayIcon(on ? "on" : st == "stopped" && engine.KillActive ? "warn" : "off");
            RefreshHome();
            RefreshKill();
        }

        void PreviewConfig()
        {
            try
            {
                SaveRules();
                BuildResult res;
                string path = Engine.WriteConfig(P, false, out res);
                foreach (var w in res.Warnings) AppendLog("! " + w);
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            // Крестик прячет окно в трей; выход — через меню трея («Выход»), обновление или выключение Windows
            if (!exiting && e.CloseReason == CloseReason.UserClosing && !updBusy)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    Notify(AppName, I18n.L("Hydra работает в трее. Выход — правой кнопкой по значку → «Выход»."), ToolTipIcon.Info, "home");
                }
                return;
            }
            if (engine.State != "stopped")
            {
                if (e.CloseReason == CloseReason.UserClosing && !updBusy &&
                    MessageBox.Show(this, I18n.L("Отключить все туннели и выйти?"), AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                { e.Cancel = true; exiting = false; return; }
                // Выход и выключение Windows: корректно гасим туннели и снимаем правила kill switch
                Cursor = Cursors.WaitCursor;
                engine.StopBlocking();
            }
            tray.Visible = false;
            tray.Dispose();
        }
    }
}
