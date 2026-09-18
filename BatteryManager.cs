using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Management;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BatteryManager
{
    internal sealed class DeviceBattery
    {
        public string Name;
        public string Type;
        public int? Level;
        public string Detail;
        public bool IsSystem;
        public bool Connected = true;
        public bool Enabled = true;
    }

    public sealed class MainForm : Form
    {
        private readonly ScrollList deviceList;
        private readonly Panel detailPanel;
        private readonly Label detailPlaceholder;
        private readonly ComboBox trayCombo;
        private readonly ComboBox pollingCombo;
        private readonly SwitchBox autoUpdateToggle;
        private readonly Label countLabel;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private readonly NotifyIcon trayIcon;
        private readonly SwitchBox startupToggle;
        private readonly HashSet<string> lowBatteryNotified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Dernier palier de 5 % déjà signalé, par appareil (20, 15, 10, 5).
        private readonly Dictionary<string, int> lowBatteryMilestone = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private bool exiting;
        private readonly Color ink = Theme.Ink;
        private readonly Color muted = Theme.Muted;
        private readonly Color line = Theme.Line;
        private readonly Color accent = Theme.Accent;
        private readonly Color green = Theme.Green;
        private readonly Color orange = Theme.Orange;
        private readonly Color red = Theme.Red;
        private int refreshInProgress;
        private DeviceBattery selected;
        private const string AppName = "Battery Manager";

        public MainForm()
        {
            Text = AppName + " v1.0.1";
            MinimumSize = new Size(860, 620);
            Size = new Size(1080, 760);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Canvas;
            Font = new Font("Segoe UI", 9.75F);
            DoubleBuffered = true;
            // Fenêtre sans chrome natif : barre de titre maison et coins arrondis (style macOS).
            FormBorderStyle = FormBorderStyle.None;
            Padding = new Padding(0);
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.BackColor = Theme.Canvas;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));   // en-tête
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // corps
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132)); // réglages
            root.Padding = new Padding(22, 14, 22, 18);
            Controls.Add(root);

            // Barre de titre hors du root : elle touche les bords de la fenêtre.
            var titleBar = BuildTitleBar();
            titleBar.Dock = DockStyle.Top;
            titleBar.Height = 42;
            Controls.Add(titleBar);
            titleBar.BringToFront();

            // Le root laisse la place à la barre de titre en haut.
            root.Padding = new Padding(22, 46, 22, 18);

            // ---- En-tête : titre de l'application ----
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Canvas };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            var titleBlock = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
            var appTitle = LabelFor(AppName, 20F, FontStyle.Bold, ink);
            appTitle.AutoSize = true;
            appTitle.Location = new Point(0, 2);
            titleBlock.Controls.Add(appTitle);
            var subtitle = LabelFor("Battery levels", 9.75F, FontStyle.Regular, muted);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(2, 36);
            titleBlock.Controls.Add(subtitle);
            header.Controls.Add(titleBlock, 0, 0);

            var refresh = new RoundedButton("\u21BB") { Size = new Size(42, 42), Margin = new Padding(0, 6, 0, 0), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            refresh.Click += delegate { RefreshDevices(); };
            header.Controls.Add(refresh, 1, 0);
            root.Controls.Add(header, 0, 0);

            var body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.ColumnCount = 2;
            body.RowCount = 1;
            body.BackColor = Theme.Canvas;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 504));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(body, 0, 1);

            // ---- Carte gauche : liste des appareils ----
            var leftCard = new CardPanel { Dock = DockStyle.Fill };
            var left = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            var devicesTitle = LabelFor("Devices", 13F, FontStyle.Bold, ink);
            devicesTitle.AutoSize = true;
            devicesTitle.Location = new Point(20, 18);
            left.Controls.Add(devicesTitle);
            countLabel = LabelFor("", 9F, FontStyle.Regular, muted);
            countLabel.AutoSize = true;
            countLabel.Location = new Point(22, 44);
            left.Controls.Add(countLabel);

            deviceList = new ScrollList();
            deviceList.Location = new Point(20, 72);
            deviceList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            deviceList.BackColor = Theme.Card;
            // Marge à droite pour laisser la barre de défilement sans chevaucher les cartes.
            deviceList.Padding = new Padding(0, 0, 14, 0);
            left.Resize += delegate
            {
                deviceList.Size = new Size(Math.Max(100, left.ClientSize.Width - 32), Math.Max(60, left.ClientSize.Height - 86));
            };
            left.Controls.Add(deviceList);
            leftCard.Controls.Add(left);
            body.Controls.Add(leftCard, 0, 0);

            // ---- Carte droite : détails de l’appareil sélectionné ----
            var rightCard = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(18, 0, 0, 0) };
            detailPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(26, 22, 26, 22) };
            detailPlaceholder = LabelFor("Select a device to view details", 11.5F, FontStyle.Regular, muted);
            detailPlaceholder.AutoSize = false;
            detailPlaceholder.Dock = DockStyle.Fill;
            detailPlaceholder.TextAlign = ContentAlignment.MiddleCenter;
            detailPanel.Controls.Add(detailPlaceholder);
            rightCard.Controls.Add(detailPanel);
            body.Controls.Add(rightCard, 1, 0);

            // ---- Carte basse : réglages ----
            var footerCard = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 18, 0, 0) };
            var footer = new TableLayoutPanel();
            footer.Dock = DockStyle.Fill;
            footer.BackColor = Color.Transparent;
            footer.ColumnCount = 4;
            footer.RowCount = 2;
            footer.Padding = new Padding(22, 14, 22, 14);
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            footerCard.Controls.Add(footer);
            root.Controls.Add(footerCard, 0, 2);

            footer.Controls.Add(RowLabel("Tray icon shows"), 0, 0);
            trayCombo = ComboFor(new object[] { "Lowest battery (auto)", "All batteries", "Off" }, 0);
            footer.Controls.Add(trayCombo, 1, 0);

            footer.Controls.Add(RowLabel("Polling interval"), 0, 1);
            pollingCombo = ComboFor(new object[] { "30 seconds", "1 minute", "2 minutes", "5 minutes", "10 minutes" }, 3);
            pollingCombo.SelectedIndexChanged += PollingChanged;
            footer.Controls.Add(pollingCombo, 1, 1);

            footer.Controls.Add(RowLabel("Auto-update"), 2, 0);
            autoUpdateToggle = new SwitchBox { Checked = true, Margin = new Padding(0, 7, 0, 7) };
            autoUpdateToggle.CheckedChanged += AutoUpdateChanged;
            footer.Controls.Add(autoUpdateToggle, 3, 0);

            footer.Controls.Add(RowLabel("Start with Windows"), 2, 1);
            startupToggle = new SwitchBox { Checked = StartupEnabled(), Margin = new Padding(0, 7, 0, 7) };
            startupToggle.CheckedChanged += StartupToggleChanged;
            footer.Controls.Add(startupToggle, 3, 1);

            // ---- Zone de notification (tray) ----
            Icon = TrayIcons.AppIcon();
            trayIcon = new NotifyIcon { Icon = TrayIcons.Green(), Visible = true, Text = AppName };
            trayIcon.DoubleClick += delegate { RestoreFromTray(); };
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Ouvrir Battery Manager", null, delegate { RestoreFromTray(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Quitter", null, delegate { ExitApplication(); });
            trayIcon.ContextMenuStrip = trayMenu;

            refreshTimer = new System.Windows.Forms.Timer { Interval = 300000 };
            refreshTimer.Tick += delegate { RefreshDevices(); };
            refreshTimer.Start();
            Shown += delegate { RefreshDevices(); };

            // Réduction en zone de notification à la fermeture.
            FormClosing += OnFormClosing;
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized) HideToTray();
            };
        }

        // Barre de titre maison : boutons de fenêtre Windows à droite, zone de glissement à gauche.
        private Control BuildTitleBar()
        {
            var bar = new TitleBar();
            bar.MouseDown += delegate (object s, MouseEventArgs e) { DragWindow(); };

            // Boutons réduite / agrandir / fermer, alignés à droite (style Windows 11).
            var buttons = new Panel { Dock = DockStyle.Right, Width = 138, BackColor = Color.Transparent };
            var close = new CaptionButton(CaptionGlyph.Close) { Location = new Point(92, 0) };
            close.Click += delegate { Close(); };
            var maximize = new CaptionButton(CaptionGlyph.Maximize) { Location = new Point(46, 0) };
            maximize.Click += delegate { ToggleMaximize(); };
            var minimize = new CaptionButton(CaptionGlyph.Minimize) { Location = new Point(0, 0) };
            minimize.Click += delegate { WindowState = FormWindowState.Minimized; };
            buttons.Controls.Add(minimize);
            buttons.Controls.Add(maximize);
            buttons.Controls.Add(close);
            bar.Controls.Add(buttons);

            var dragZone = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            dragZone.MouseDown += delegate (object s, MouseEventArgs e) { DragWindow(); };
            dragZone.DoubleClick += delegate { ToggleMaximize(); };
            bar.Controls.Add(dragZone);
            dragZone.BringToFront();

            return bar;
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        // Déplace la fenêtre sans chrome : appel à l'API système de glissement.
        [DllImport("user32.dll")]
        private static extern int ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private void DragWindow()
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 0x2, 0); // WM_NCLBUTTONDOWN, HTCAPTION
        }

        // Coins arrondis : API DWM native (Windows 11) avec repli sur une région.
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyRoundedCorners();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState != FormWindowState.Maximized) ApplyRoundedCorners();
        }

        private void ApplyRoundedCorners()
        {
            if (Width <= 0 || Height <= 0) return;
            // Tentative DWM (Windows 11) : coins arrondis gérés par le compositeur.
            try
            {
                var pref = DWMWCP_ROUND;
                if (DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) == 0)
                {
                    Region = null;
                    return;
                }
            }
            catch { }
            // Repli : région arrondie (Windows 10 et antérieurs).
            using (var path = Theme.Rounded(new Rectangle(0, 0, Width, Height), 14))
                Region = new Region(path);
        }

        // Peint le fond gris et un liseré fin sur le pourtour de la fenêtre sans chrome.
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 14))
            using (var pen = new Pen(Color.FromArgb(214, 216, 220), 1f))
                e.Graphics.DrawPath(pen, path);
        }

        // Sans bordure native, on rétablit le redimensionnement par les bords en renvoyant
        // les codes de test de zone (HTLEFT, HTRIGHT, HTTOP, ...) selon la position du curseur.
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        private const int GRIP = 6; // épaisseur de la zone de saisie des bords
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
            {
                var pos = PointToClient(new Point(m.LParam.ToInt32() & 0xFFFF, (m.LParam.ToInt32() >> 16) & 0xFFFF));
                var left = pos.X <= GRIP;
                var right = pos.X >= ClientSize.Width - GRIP;
                var top = pos.Y <= GRIP;
                var bottom = pos.Y >= ClientSize.Height - GRIP;

                if (top && left) m.Result = (IntPtr)HTTOPLEFT;
                else if (top && right) m.Result = (IntPtr)HTTOPRIGHT;
                else if (bottom && left) m.Result = (IntPtr)HTBOTTOMLEFT;
                else if (bottom && right) m.Result = (IntPtr)HTBOTTOMRIGHT;
                else if (left) m.Result = (IntPtr)HTLEFT;
                else if (right) m.Result = (IntPtr)HTRIGHT;
                else if (top) m.Result = (IntPtr)HTTOP;
                else if (bottom) m.Result = (IntPtr)HTBOTTOM;
                else m.Result = (IntPtr)HTCLIENT;
            }
        }

        private static Label RowLabel(string text)
        {
            return new Label { Text = text, Font = new Font("Segoe UI", 9.75F), ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, BackColor = Color.Transparent };
        }

        private static ComboBox ComboFor(object[] items, int selected)
        {
            var combo = new StyledCombo { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 12, 6) };
            combo.Items.AddRange(items);
            combo.SelectedIndex = selected;
            return combo;
        }

        private void PollingChanged(object sender, EventArgs e)
        {
            var values = new[] { 30, 60, 120, 300, 600 };
            refreshTimer.Interval = values[pollingCombo.SelectedIndex] * 1000;
        }

        private void AutoUpdateChanged(object sender, EventArgs e)
        {
            if (autoUpdateToggle.Checked) refreshTimer.Start(); else refreshTimer.Stop();
        }

        // ---- Démarrage automatique avec Windows (clé utilisateur HKCU) ----
        private static bool StartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
                    return key != null && key.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        private void StartupToggleChanged(object sender, EventArgs e)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                {
                    if (key == null) return;
                    if (startupToggle.Checked) key.SetValue(AppName, String.Concat("\"", Application.ExecutablePath, "\" --minimized"));
                    else key.DeleteValue(AppName, false);
                }
            }
            catch { }
        }

        // ---- Zone de notification ----
        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
        }

        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            // La croix réduit dans la zone de notification, sauf si l'utilisateur a choisi "Quitter".
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                if (!lowBatteryNotified.Contains("__tray_hint__"))
                {
                    lowBatteryNotified.Add("__tray_hint__");
                    trayIcon.ShowBalloonTip(3000, AppName, "Battery Manager continue de surveiller en arrière-plan.", ToolTipIcon.Info);
                }
            }
        }

        private void ExitApplication()
        {
            exiting = true;
            trayIcon.Visible = false;
            Close();
        }

        // ---- Notification à chaque palier de 5 % franchi à la baisse (20, 15, 10, 5) ----
        // lowBatteryMilestone mémorise, pour chaque appareil, le dernier palier déjà signalé.
        private void CheckLowBattery(List<DeviceBattery> devices)
        {
            const int threshold = 20;
            const int step = 5;
            foreach (var device in devices)
            {
                if (!device.Level.HasValue || device.IsSystem) continue;
                var level = device.Level.Value;
                if (level <= threshold)
                {
                    // Seuil franchi : multiple de 5 immédiatement supérieur ou égal au niveau,
                    // borné à [5, 20]. On notifie à 20, 15, 10 puis 5 %, sans relance entre les paliers.
                    var milestone = Math.Min(threshold, (int)Math.Ceiling(level / (double)step) * step);
                    int lastNotified;
                    if (!lowBatteryMilestone.TryGetValue(device.Name, out lastNotified) || milestone < lastNotified)
                    {
                        lowBatteryMilestone[device.Name] = milestone;
                        trayIcon.ShowBalloonTip(5000, "Batterie faible",
                            device.Name + " est à " + level + "%.", ToolTipIcon.Warning);
                    }
                }
                else
                {
                    // Réinitialise dès que l'appareil repasse au-dessus du seuil : un retour sous les
                    // 20 % redéclenchera les alertes palier par palier.
                    lowBatteryMilestone.Remove(device.Name);
                }
            }
        }

        private static Label LabelFor(string text, float size, FontStyle style, Color color)
        {
            return new Label { Text = text, Font = new Font("Segoe UI", size, style), ForeColor = color, BackColor = Color.Transparent };
        }

        private Button ButtonFor(string text, Color back, Color fore)
        {
            var button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = fore, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = line;
            return button;
        }

        private void RefreshDevices()
        {
            if (Interlocked.Exchange(ref refreshInProgress, 1) != 0) return;
            Task.Run(delegate { return DiscoverDevices(); }).ContinueWith(delegate (Task<List<DeviceBattery>> task)
            {
                if (IsDisposed || !IsHandleCreated)
                {
                    Interlocked.Exchange(ref refreshInProgress, 0);
                    return;
                }
                BeginInvoke(new Action(delegate
                {
                    try
                    {
                        if (task.Status == TaskStatus.RanToCompletion) RenderDevices(task.Result);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref refreshInProgress, 0);
                    }
                }));
            }, TaskScheduler.Default);
        }

        private void RenderDevices(List<DeviceBattery> devices)
        {
            var previous = selected;
            var shown = devices.Where(d => !d.IsSystem).ToList();
            countLabel.Text = shown.Count + (shown.Count == 1 ? " device" : " devices");

            // Appareils connectés en tête, triés par batterie croissante (plus faible d'abord) ;
            // les appareils sans niveau puis les déconnectés sont relégués en fin de liste.
            var ordered = devices
                .OrderByDescending(d => d.Connected)
                .ThenBy(d => d.Level.HasValue ? 0 : 1)
                .ThenBy(d => d.Level.HasValue ? d.Level.Value : int.MaxValue)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Les cartes précédentes sont retirées : sans cela les anciennes valeurs
            // resteraient empilées sous les nouvelles et la liste semblerait figée.
            deviceList.ClearCards();

            // Ajout dans l'ordre : la première carte ajoutée en Dock.Top reste en haut de la liste.
            deviceList.BeginUpdate();
            foreach (var device in ordered)
            {
                var card = new DeviceCard(device, ink, muted, accent, green, orange, red);
                var captured = device;
                card.Selected2 += delegate { SelectDevice(captured); };
                deviceList.AddCard(card);
            }
            deviceList.EndUpdate();

            if (previous != null)
            {
                var match = devices.FirstOrDefault(d => String.Equals(d.Name, previous.Name, StringComparison.OrdinalIgnoreCase));
                if (match != null) SelectDevice(match);
            }

            UpdateTrayAndNotify(devices);
        }

        // Met à jour l'info-bulle du tray et déclenche les alertes de batterie faible.
        private void UpdateTrayAndNotify(List<DeviceBattery> devices)
        {
            var lowest = devices
                .Where(d => d.Level.HasValue)
                .OrderBy(d => d.Level.Value)
                .FirstOrDefault();
            if (lowest != null)
            {
                var tip = lowest.Name + " : " + lowest.Level.Value + "%";
                trayIcon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            }
            else
            {
                trayIcon.Text = AppName;
            }

            // Icône du tray : rouge si une batterie connectée est sous 20 %, verte sinon.
            var lowBattery = devices.Any(d => !d.IsSystem && d.Connected && d.Level.HasValue && d.Level.Value <= 20);
            var oldIcon = trayIcon.Icon;
            trayIcon.Icon = lowBattery ? TrayIcons.Red() : TrayIcons.Green();
            if (oldIcon != null && oldIcon != trayIcon.Icon) oldIcon.Dispose();

            CheckLowBattery(devices);
        }

        private void SelectDevice(DeviceBattery device)
        {
            selected = device;
            foreach (Control control in deviceList.CardControls)
            {
                var card = control as DeviceCard;
                if (card != null) card.SetSelected(ReferenceEquals(card.Device, device));
            }
            ShowDetails(device);
        }

        private void ShowDetails(DeviceBattery device)
        {
            detailPanel.SuspendLayout();
            detailPanel.Controls.Clear();

            var levelColor = device.Level.HasValue ? (device.Level.Value <= 20 ? red : device.Level.Value <= 40 ? orange : green) : muted;

            // Nom + badge de statut sur une même ligne.
            var title = LabelFor(device.Name, 15F, FontStyle.Bold, ink);
            title.AutoSize = true;
            title.Location = new Point(26, 24);
            detailPanel.Controls.Add(title);

            var badge = new StatusBadge(device.Connected ? "Connected" : "Disconnected", device.Connected ? green : muted) { Location = new Point(28, 58) };
            detailPanel.Controls.Add(badge);

            // Grand pourcentage mis en avant.
            var levelText = device.Level.HasValue ? device.Level.Value + "%" : "N/A";
            var level = LabelFor(levelText, 44F, FontStyle.Bold, levelColor);
            level.AutoSize = true;
            level.Location = new Point(24, 94);
            detailPanel.Controls.Add(level);

            var caption = LabelFor(device.Level.HasValue ? "Current charge" : "No battery level reported", 9.75F, FontStyle.Regular, muted);
            caption.AutoSize = true;
            caption.Location = new Point(28, 162);
            detailPanel.Controls.Add(caption);

            // Barre de charge fine et arrondie, redimensionnable avec la carte.
            var bar = new ProgressBar2(levelColor) { Location = new Point(30, 194), Height = 8, Width = 320 };
            bar.SetValue(device.Level.HasValue ? device.Level.Value : 0);
            detailPanel.Controls.Add(bar);
            detailPanel.Resize += delegate { bar.Width = Math.Max(120, detailPanel.ClientSize.Width - 60); };

            // Séparateur discret.
            var divider = new Panel { Location = new Point(26, 226), Height = 1, Width = 320, BackColor = line };
            detailPanel.Controls.Add(divider);
            detailPanel.Resize += delegate { divider.Width = Math.Max(120, detailPanel.ClientSize.Width - 52); };

            var fields = new[]
            {
                new[] { "Type", device.Type },
                new[] { "Status", device.Connected ? "Connected" : "Disconnected" },
                new[] { "Battery", device.Level.HasValue ? device.Level.Value + "%" : "Not reported" },
                new[] { "Source", device.Detail },
                new[] { "Reporting", device.Enabled ? "Enabled" : "Disabled" }
            };
            var top = 246;
            foreach (var field in fields)
            {
                var label = LabelFor(field[0], 9.75F, FontStyle.Regular, muted);
                label.AutoSize = true;
                label.Location = new Point(26, top);
                detailPanel.Controls.Add(label);
                var value = LabelFor(field[1], 9.75F, FontStyle.Regular, ink);
                value.AutoSize = true;
                value.Location = new Point(150, top);
                detailPanel.Controls.Add(value);
                top += 32;
            }
            detailPanel.ResumeLayout();
        }

        private List<DeviceBattery> DiscoverDevices()
        {
            var devices = new List<DeviceBattery>();
            var power = SystemInformation.PowerStatus;
            if (power.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery)
            {
                devices.Add(new DeviceBattery { Name = "This PC", Type = "System battery", Level = (int)Math.Round(power.BatteryLifePercent * 100), Detail = power.PowerLineStatus == PowerLineStatus.Online ? "Plugged in" : "On battery", IsSystem = true, Connected = true });
            }

            string discoveryError = null;
            var log = new StringBuilder();
            var pendingNative = new List<KeyValuePair<DeviceBattery, string>>();
            var logitechDevices = new List<DeviceBattery>();
            var corsairDevices = new List<DeviceBattery>();
            try
            {
                log.AppendLine("=== BLE/BTH query ===");
                var seen = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);
                var seenAlias = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);
                using (var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, Status FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'BTHENUM%' OR PNPDeviceID LIKE 'BTHLE%'") )
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        var name = Convert.ToString(item["Name"]);
                        var deviceId = Convert.ToString(item["PNPDeviceID"]);
                        var status = Convert.ToString(item["Status"]);
                        if (String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(deviceId) || !deviceId.Contains("\\DEV_") || status == "Error") continue;
                        var alias = NormalizeName(name);
                        DeviceBattery existing;
                        if (seen.TryGetValue(name, out existing) || seenAlias.TryGetValue(alias, out existing))
                        {
                            if (!existing.Level.HasValue) pendingNative.Add(new KeyValuePair<DeviceBattery, string>(existing, deviceId));
                            continue;
                        }

                        // Statut de connexion Bluetooth réel (un appareil appairé mais hors portée reste "Disconnected").
                        ulong address;
                        var connected = TryBluetoothAddress(deviceId, out address) && ReadBluetoothConnected(address);
                        var device = new DeviceBattery { Name = name, Type = TypeFor(name, "Bluetooth device"), Level = null, Detail = "Reading level...", Connected = connected };
                        seen.Add(name, device);
                        seenAlias.Add(alias, device);
                        devices.Add(device);
                        int? gatt0 = connected ? ReadBluetoothBattery(deviceId) : null;
                        device.Level = gatt0;
                        device.Detail = gatt0.HasValue ? "GATT battery service" : (connected ? "Not reported by Windows" : "Hors ligne");
                        pendingNative.Add(new KeyValuePair<DeviceBattery, string>(device, deviceId));
                        log.AppendLine(name + " | id=" + deviceId + " | connected=" + connected + " | gatt=" + (gatt0.HasValue ? gatt0.Value.ToString() : "-"));
                    }
                }

                using (var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, Status FROM Win32_PnPEntity WHERE Name LIKE '%VOID%' OR Name LIKE '%CORSAIR%WIRELESS%' OR Name LIKE 'G915 X LIGHTSPEED' OR Name LIKE 'G502 LIGHTSPEED'") )
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        var name = Convert.ToString(item["Name"]);
                        var deviceId = Convert.ToString(item["PNPDeviceID"]);
                        var status = Convert.ToString(item["Status"]);
                        var alias = NormalizeName(name);
                        if (String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(deviceId) || status == "Error" || seen.ContainsKey(name) || seenAlias.ContainsKey(alias)) continue;
                        var isLogitech = name.IndexOf("G915", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("G502", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("LIGHTSPEED", StringComparison.OrdinalIgnoreCase) >= 0;
                        var isCorsair = name.IndexOf("CORSAIR", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("VOID", StringComparison.OrdinalIgnoreCase) >= 0;
                        var device = new DeviceBattery { Name = name, Type = isLogitech ? "Logitech G device" : (isCorsair ? "Corsair device" : TypeFor(name, "USB device")), Level = null, Detail = "Reading level...", Connected = IsUsbConnected(name) };
                        seen.Add(name, device);
                        seenAlias.Add(alias, device);
                        devices.Add(device);
                        if (isLogitech) logitechDevices.Add(device);
                        else if (isCorsair) corsairDevices.Add(device);
                        else pendingNative.Add(new KeyValuePair<DeviceBattery, string>(device, deviceId));
                    }
                }

                // 1) Logitech : G HUB (settings.db) prioritaire, puis HID++ en repli.
                if (logitechDevices.Count > 0)
                {
                    var ghub = GHubBattery.ReadAll();
                    log.AppendLine("GHUB entries: " + ghub.Count);
                    var hidppLevels = new List<int>();
                    var hidppUsed = false;
                    foreach (var device in logitechDevices)
                    {
                        var fromHub = GHubBattery.Match(ghub, device.Name);
                        if (fromHub.HasValue)
                        {
                            device.Level = fromHub;
                            device.Detail = "Logitech G HUB";
                        }
                        else
                        {
                            if (!hidppUsed) { hidppLevels = ReadLogitechBatteries(); hidppUsed = true; }
                            device.Detail = "Logitech HID++";
                        }
                    }
                    // Attribution HID++ aux appareils restants (dans l'ordre).
                    var pendingLogi = logitechDevices.Where(d => !d.Level.HasValue).ToList();
                    for (var i = 0; i < pendingLogi.Count && i < hidppLevels.Count; i++) pendingLogi[i].Level = hidppLevels[i];
                    foreach (var d in logitechDevices.Where(d => !d.Level.HasValue)) d.Detail = "Niveau non disponible";
                }

                // 2) Corsair : iCUE n'expose pas toujours le niveau sur disque, on tente les fichiers iCUE.
                if (corsairDevices.Count > 0)
                {
                    var icue = IcueBattery.ReadAll();
                    log.AppendLine("iCUE entries: " + icue.Count);
                    foreach (var device in corsairDevices)
                    {
                        var fromIcue = IcueBattery.Match(icue, device.Name);
                        if (fromIcue.HasValue)
                        {
                            device.Level = fromIcue;
                            device.Detail = "Corsair iCUE";
                        }
                        else
                        {
                            device.Detail = "iCUE n'expose pas ce niveau";
                        }
                    }
                }

                // Une seule invocation PowerShell pour tous les niveaux exposés par Windows.
                var ids = pendingNative.Select(pair => pair.Value).ToList();
                var levels = ReadWindowsBatteryLevelsBatch(ids);
                log.AppendLine("batch ids=" + ids.Count + " results=" + levels.Count + " err=" + BatchError);
                foreach (var pending in pendingNative)
                {
                    int level;
                    if (!levels.TryGetValue(pending.Value, out level)) continue;
                    if (!pending.Key.Level.HasValue)
                    {
                        pending.Key.Level = level;
                        pending.Key.Detail = "Windows PnP property";
                    }
                }
            }
            catch (Exception error) { discoveryError = error.Message; log.AppendLine("EXCEPTION: " + error.Message); }

            // Un appareil déconnecté ne doit pas afficher un niveau (valeur périmée) : on le neutralise.
            foreach (var d in devices)
            {
                if (d.IsSystem || d.Connected) continue;
                if (d.Level.HasValue)
                {
                    d.Level = null;
                    d.Detail = "Hors ligne";
                }
            }

            log.AppendLine("HID++ debug: " + LogitDebug);
            log.AppendLine("=== FINAL " + devices.Count + " devices ===");
            foreach (var d in devices) log.AppendLine(d.Name + " => " + (d.Level.HasValue ? d.Level.Value + "%" : "N/A") + " (" + d.Detail + ")");
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "battery-debug.log"), log.ToString()); } catch { }

            if (devices.Count == 0)
            {
                devices.Add(new DeviceBattery { Name = "No device detected", Type = "Bluetooth", Level = null, Detail = discoveryError ?? "Pair a device then refresh", Connected = false });
            }
            return devices;
        }

        // Réduit les noms proches à une clé commune pour éviter les doublons (ex. casque Corsair).
        private static string NormalizeName(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return name;
            var value = name.ToLowerInvariant();
            // Le contenu entre parenthèses porte souvent le vrai nom du produit : on le réunit au nom.
            var open = value.IndexOf("(");
            var close = value.IndexOf(")", open + 1);
            if (open > 0)
            {
                var inside = close > open ? value.Substring(open + 1, close - open - 1) : value.Substring(open + 1);
                value = String.Concat(value.Substring(0, open), inside);
            }
            value = value.Replace("casque pour téléphone", "").Replace("casque pour telephone", "");
            value = value.Replace("gaming headset", "").Replace("headset", "").Replace("casque", "");
            value = value.Replace("wireless", "").Replace("sans fil", "");
            var chars = value.Where(c => Char.IsLetterOrDigit(c)).ToArray();
            return new string(chars);
        }

        private static string TypeFor(string name, string fallback)
        {
            var lower = (name ?? "").ToLowerInvariant();
            if (lower.Contains("airpods")) return "Audio device";
            if (lower.Contains("iphone") || lower.Contains("ipad")) return "Mobile device";
            if (lower.Contains("xbox") || lower.Contains("controller")) return "Gamepad";
            if (lower.Contains("jbl") || lower.Contains("speaker") || lower.Contains("bose")) return "Speaker";
            if (lower.Contains("keyboard")) return "Keyboard";
            if (lower.Contains("mouse")) return "Mouse";
            return fallback;
        }

        // Pour les appareils USB (non Bluetooth) : connecté si une instance PnP est active (Status OK).
        private static bool IsUsbConnected(string name)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Status, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE Name = '" + name.Replace("'", "''") + "'"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        var status = Convert.ToString(item["Status"]);
                        var error = Convert.ToInt32(item["ConfigManagerErrorCode"] ?? 0);
                        // Status OK et aucun code d'erreur Config Manager = périphérique présent et démarré.
                        if (String.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) && error == 0) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static int? ReadWindowsBatteryLevel(string deviceId)
        {
            return PnpProperty.ReadBatteryLevel(deviceId);
        }

        // Lecture groupée via Get-PnpDeviceProperty (une seule invocation PowerShell).
        private static Dictionary<string, int> ReadWindowsBatteryLevelsBatch(IEnumerable<string> deviceIds)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var list = deviceIds.Where(id => !String.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return map;
            try
            {
                var script = new StringBuilder();
                script.Append("$ids = @(");
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0) script.Append(',');
                    script.Append('\'').Append(list[i].Replace("'", "''")).Append('\'');
                }
                script.Append("); foreach ($id in $ids) { $p = Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_BatteryLevel' -ErrorAction SilentlyContinue; if ($null -ne $p.Data) { [Console]::WriteLine($id + '=' + $p.Data) }");
                script.Append(" }");
                var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
                var shell = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                if (!System.IO.File.Exists(shell)) shell = "powershell.exe";
                var info = new ProcessStartInfo(shell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand);
                info.CreateNoWindow = true;
                info.UseShellExecute = false;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                using (var process = Process.Start(info))
                {
                    if (!process.WaitForExit(8000))
                    {
                        try { process.Kill(); } catch { }
                        BatchError = "timeout (shell=" + shell + ")";
                        return map;
                    }
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        var split = line.LastIndexOf('=');
                        if (split <= 0) continue;
                        int level;
                        if (Int32.TryParse(line.Substring(split + 1).Trim(), out level) && level >= 0 && level <= 100)
                            map[line.Substring(0, split).Trim()] = level;
                    }
                    if (map.Count == 0)
                        BatchError = "exit=" + process.ExitCode + " shell=" + shell + " err=" + process.StandardError.ReadToEnd().Trim();
                }
            }
            catch (Exception ex) { BatchError = "exception: " + ex.Message; }
            return map;
        }

        internal static string BatchError;
        internal static string LogitDebug = "";

        // Lecture native de DEVPKEY_Device_BatteryLevel (sans lancer PowerShell).
        private static class PnpProperty
        {
            private static readonly Guid SetupClass = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
            private const uint DevpropTypeUint32 = 0x00007;

            [StructLayout(LayoutKind.Sequential)] private struct DevpropKey { public Guid FmtId; public uint Pid; }
            [StructLayout(LayoutKind.Sequential)] private struct SpDevinfoData { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool SetupDiOpenDeviceInfo(IntPtr set, string instanceId, IntPtr hwnd, uint flags, ref SpDevinfoData data);
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SpDevinfoData data, ref DevpropKey key, out uint type, IntPtr buffer, uint size, out uint required, uint flags);
            [DllImport("setupapi.dll", SetLastError = true)]
            private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

            public static int? ReadBatteryLevel(string instanceId)
            {
                if (String.IsNullOrWhiteSpace(instanceId)) return null;
                var set = SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x04 /* DIGCF_ALLCLASSES */ | 0x02 /* DIGCF_PRESENT */);
                if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;
                try
                {
                    var info = new SpDevinfoData { Size = Marshal.SizeOf(typeof(SpDevinfoData)) };
                    if (!SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref info)) return null;
                    var key = new DevpropKey { FmtId = SetupClass, Pid = 2 };
                    uint type;
                    uint required;
                    var buffer = Marshal.AllocHGlobal(4);
                    try
                    {
                        if (!SetupDiGetDeviceProperty(set, ref info, ref key, out type, buffer, 4, out required, 0)) return null;
                        var value = (int)Marshal.ReadInt32(buffer);
                        return value >= 0 && value <= 100 ? (int?)value : null;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                catch { return null; }
                finally { SetupDiDestroyDeviceInfoList(set); }
            }
        }

        // Lit tous les niveaux HID++ disponibles, un par appareil Logitech (dans l'ordre des récepteurs).
        private static List<int> ReadLogitechBatteries()
        {
            var levels = new List<int>();
            var seenReceivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr infoSet = HidNative.SetupDiGetClassDevs(ref HidNative.HidGuid, IntPtr.Zero, IntPtr.Zero, HidNative.Present | HidNative.DeviceInterface);
            if (infoSet == IntPtr.Zero || infoSet == new IntPtr(-1)) return levels;
            try
            {
                for (uint index = 0; ; index++)
                {
                    var interfaceData = new HidNative.DeviceInterfaceData { Size = Marshal.SizeOf(typeof(HidNative.DeviceInterfaceData)) };
                    if (!HidNative.SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref HidNative.HidGuid, index, ref interfaceData)) break;
                    LogitDebug += "(enum" + index + ")";
                    uint required = 0;
                    HidNative.SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, IntPtr.Zero, 0, ref required, IntPtr.Zero);
                    if (required == 0) continue;
                    var detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!HidNative.SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, detail, required, ref required, IntPtr.Zero)) continue;
                        var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (path != null && path.StartsWith("\\\\?\\")) path = "\\\\.\\" + path.Substring(4);
                        if (path == null) continue;

                        // Un récepteur peut exposer plusieurs interfaces ; on ne lit que la première qui répond.
                        var receiverKey = path;
                        var hash = receiverKey.IndexOf('#');
                        if (hash > 0) receiverKey = receiverKey.Substring(0, hash);
                        if (seenReceivers.Contains(receiverKey)) continue;

                        using (var handle = HidNative.CreateFile(path, HidNative.ReadWrite, HidNative.ShareRead | HidNative.ShareWrite, IntPtr.Zero, HidNative.OpenExisting, 0, IntPtr.Zero))
                        {
                            if (handle.IsInvalid) { LogitDebug += "(openFail:" + Marshal.GetLastWin32Error() + " path=" + path + ")"; continue; }
                            var attributes = new HidNative.Attributes { Size = Marshal.SizeOf(typeof(HidNative.Attributes)) };
                            if (!HidNative.HidD_GetAttributes(handle, ref attributes)) { LogitDebug += "(attrFail)"; continue; }
                            LogitDebug += "(vid=" + attributes.VendorId.ToString("X4") + " pid=" + attributes.ProductId.ToString("X4") + ")";
                            if (attributes.VendorId != 0x046D) continue;
                            if (attributes.ProductId != 0xC547 && attributes.ProductId != 0xC539 && attributes.ProductId != 0xC53A) continue;
                            var preparsed = IntPtr.Zero;
                            if (!HidNative.HidD_GetPreparsedData(handle, ref preparsed)) continue;
                            var caps = new HidNative.Caps();
                            if (HidNative.HidP_GetCaps(preparsed, ref caps) != 0x110000)
                            {
                                HidNative.HidD_FreePreparsedData(preparsed);
                                continue;
                            }
                            var inputLength = Math.Max(7, (int)caps.InputReportByteLength);
                            var outputLength = Math.Max(7, (int)caps.OutputReportByteLength);
                            HidNative.HidD_FreePreparsedData(preparsed);
                            if (outputLength < 7 || inputLength < 7) continue;

                            var receiverLevels = new List<int>();
                            foreach (byte deviceIndex in new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0xFF })
                            {
                                var level = ReadBatteryFeatures(handle, deviceIndex, 0, inputLength, outputLength);
                                LogitDebug += "[" + receiverKey + " dev=" + deviceIndex + " pid=" + attributes.ProductId.ToString("X4") + " level=" + (level.HasValue ? level.Value.ToString() : "-") + "]";
                                if (level.HasValue && !receiverLevels.Contains(level.Value)) receiverLevels.Add(level.Value);
                            }
                            if (receiverLevels.Count > 0)
                            {
                                seenReceivers.Add(receiverKey);
                                levels.AddRange(receiverLevels);
                            }
                        }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { HidNative.SetupDiDestroyDeviceInfoList(infoSet); }
            return levels;
        }

        private static int? ReadBatteryFeatures(SafeFileHandle handle, byte deviceIndex, int reportLength, int inputLength, int outputLength)
        {
            var level = ReadBatteryFeature(handle, deviceIndex, 0x1000, 0x00, reportLength, inputLength, outputLength, 0);
            if (level.HasValue) return level;
            level = ReadBatteryFeature(handle, deviceIndex, 0x1004, 0x10, reportLength, inputLength, outputLength, 0);
            if (level.HasValue) return level;
            return ReadBatteryFeature(handle, deviceIndex, 0x1001, 0x00, reportLength, inputLength, outputLength, 0);
        }

        private static byte[] HidppRegister(SafeFileHandle handle, byte register, byte subRegister, int inputLength, int outputLength)
        {
            var request = new byte[outputLength];
            request[0] = 0x10; request[1] = 0xFF; request[2] = 0x81; request[3] = register; request[4] = subRegister;
            byte[] response;
            return HidppExchange(handle, request, inputLength, out response) ? response : null;
        }

        private static int? ReadBatteryFeature(SafeFileHandle handle, byte deviceIndex, ushort featureId, byte function, int reportLength, int inputLength, int outputLength, int payloadOffset)
        {
            var featureIndex = HidppFeatureIndex(handle, deviceIndex, featureId, reportLength, inputLength, outputLength);
            if (!featureIndex.HasValue) return null;
            var report = new byte[outputLength];
            report[0] = 0x10; report[1] = deviceIndex; report[2] = featureIndex.Value; report[3] = function;
            byte[] response;
            if (!HidppExchange(handle, report, inputLength, out response) || response.Length < 6 + payloadOffset) return null;
            var value = response[5 + payloadOffset];
            if (featureId == 0x1004)
            {
                // Unified battery (0x1004): byte 5 = state of charge (%).
                return value <= 100 ? (int?)value : null;
            }
            if (featureId == 0x1000)
            {
                // Battery level status (0x1000): byte 5 = status (0=discharged .. 4=full).
                value = value >= 4 ? (byte)100 : value == 3 ? (byte)75 : value == 2 ? (byte)50 : value == 1 ? (byte)25 : (byte)0;
                return (int?)value;
            }
            return value <= 100 ? (int?)value : null;
        }

        private static byte? HidppFeatureIndex(SafeFileHandle handle, byte deviceIndex, ushort featureId, int reportLength, int inputLength, int outputLength)
        {
            var report = new byte[outputLength];
            report[0] = 0x10; report[1] = deviceIndex; report[2] = 0x00; report[3] = 0x00;
            report[4] = (byte)(featureId >> 8); report[5] = (byte)featureId;
            byte[] response;
            if (!HidppExchange(handle, report, inputLength, out response)) return null;
            // Reply: [0x10, devIndex, 0x8F, featIdx(echo), func(echo), featureIndex, featureType, ...]
            if (response.Length < 6) return null;
            if (response[1] != deviceIndex) return null;
            return response[5] != 0 ? (byte?)response[5] : null;
        }

        private static bool HidppExchange(SafeFileHandle handle, byte[] request, int inputLength, out byte[] response)
        {
            response = null;
            if (!HidNative.HidD_SetOutputReport(handle, request, request.Length)) return false;
            var buffer = new byte[Math.Max(7, inputLength)];
            buffer[0] = 0x10;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (HidNative.HidD_GetInputReport(handle, buffer, buffer.Length))
                {
                    response = (byte[])buffer.Clone();
                    return true;
                }
                Thread.Sleep(15);
            }
            return false;
        }

        private static class HidNative
        {
            public const uint Present = 0x02, DeviceInterface = 0x10, ReadWrite = 0xC0000000, ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
            public static Guid HidGuid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
            [StructLayout(LayoutKind.Sequential)] public struct DeviceInterfaceData { public int Size; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
            [StructLayout(LayoutKind.Sequential)] public struct Attributes { public int Size; public ushort VendorId; public ushort ProductId; public ushort VersionNumber; }
            [StructLayout(LayoutKind.Sequential)] public struct Caps
            {
                public short Usage;
                public short UsagePage;
                public short InputReportByteLength;
                public short OutputReportByteLength;
                public short FeatureReportByteLength;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public short[] Reserved;
            }
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid guid, uint index, ref DeviceInterfaceData detail);
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData detail, IntPtr buffer, uint size, ref uint required, IntPtr data);
            [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
            [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
            [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, ref IntPtr preparsedData);
            [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
            [DllImport("hid.dll", SetLastError = true)] public static extern int HidP_GetCaps(IntPtr preparsedData, ref Caps capabilities);
            [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] report, int length);
            [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] report, int length);
            [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetOutputReport(SafeFileHandle handle, byte[] report, int length);
            [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetInputReport(SafeFileHandle handle, byte[] report, int length);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        }

        // Extrait l'adresse Bluetooth (12 chiffres hexadécimaux après \DEV_) de l'identifiant PnP.
        private static bool TryBluetoothAddress(string deviceId, out ulong address)
        {
            address = 0;
            if (String.IsNullOrEmpty(deviceId)) return false;
            var marker = deviceId.IndexOf("\\DEV_", StringComparison.OrdinalIgnoreCase);
            if (marker < 0 || deviceId.Length < marker + 18) return false;
            return UInt64.TryParse(deviceId.Substring(marker + 5, 12), System.Globalization.NumberStyles.HexNumber, null, out address);
        }

        // Statut de connexion Bluetooth réel (Connected uniquement si l'appareil est actuellement lié).
        private static bool ReadBluetoothConnected(ulong address)
        {
            if (address == 0) return false;
            try
            {
                var le = WaitFor(BluetoothLEDevice.FromBluetoothAddressAsync(address));
                if (le != null)
                {
                    using (le)
                    {
                        if (le.ConnectionStatus == BluetoothConnectionStatus.Connected) return true;
                    }
                }
            }
            catch { }

            // Certains appareils classiques n'exposent pas de BluetoothLEDevice : on tente le mode classique.
            try
            {
                var selector = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true);
                var infos = WaitFor(Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector));
                if (infos != null)
                {
                    foreach (var info in infos)
                    {
                        if (info.Name == null) continue;
                        var bt = WaitFor(Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(info.Id));
                        if (bt == null) continue;
                        using (bt)
                        {
                            if (bt.BluetoothAddress == address)
                                return bt.ConnectionStatus == BluetoothConnectionStatus.Connected;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static int? ReadBluetoothBattery(string deviceId)
        {
            ulong address;
            if (!TryBluetoothAddress(deviceId, out address) || address == 0) return null;

            try
            {
                var device = WaitFor(BluetoothLEDevice.FromBluetoothAddressAsync(address));
                if (device == null) return null;
                using (device)
                {
                    var services = WaitFor(device.GetGattServicesAsync(BluetoothCacheMode.Uncached));
                    if (services == null || services.Status != GattCommunicationStatus.Success) return null;
                    foreach (var service in services.Services)
                    {
                        if (service.Uuid.ToString().StartsWith("0000180f-", StringComparison.OrdinalIgnoreCase))
                        {
                            var characteristics = WaitFor(service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached));
                            if (characteristics == null || characteristics.Status != GattCommunicationStatus.Success) return null;
                            foreach (var characteristic in characteristics.Characteristics)
                            {
                                if (!characteristic.Uuid.ToString().StartsWith("00002a19-", StringComparison.OrdinalIgnoreCase)) continue;
                                var value = WaitFor(characteristic.ReadValueAsync(BluetoothCacheMode.Uncached));
                                if (value.Status != GattCommunicationStatus.Success) return null;
                                var reader = DataReader.FromBuffer(value.Value);
                                return reader.ReadByte();
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static T WaitFor<T>(Windows.Foundation.IAsyncOperation<T> operation)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (operation.Status == Windows.Foundation.AsyncStatus.Started && DateTime.UtcNow < deadline) Thread.Sleep(25);
            if (operation.Status == Windows.Foundation.AsyncStatus.Started)
            {
                try { operation.Cancel(); } catch { }
                return default(T);
            }
            return operation.Status == Windows.Foundation.AsyncStatus.Completed ? operation.GetResults() : default(T);
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var startMinimized = args != null && args.Any(a => String.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
            var form = new MainForm();
            if (startMinimized)
            {
                // Démarrage en arrière-plan (au lancement de Windows) : directement dans la zone de notification.
                form.WindowState = FormWindowState.Minimized;
                form.ShowInTaskbar = false;
                form.Load += delegate { form.WindowState = FormWindowState.Minimized; };
            }
            Application.Run(form);
        }
    }

    // Lecture des niveaux que Logitech G HUB publie dans sa base settings.db (SQLite).
    internal static class GHubBattery
    {
        public static Dictionary<string, int> ReadAll()
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var candidates = new[]
                {
                    System.IO.Path.Combine(local, @"LGHUB\settings.db"),
                    System.IO.Path.Combine(local, @"LGHUB\settings.backup.db")
                };
                foreach (var db in candidates)
                {
                    if (!System.IO.File.Exists(db)) continue;
                    string text;
                    try
                    {
                        // Copie pour éviter le verrou pendant que G HUB tourne.
                        var copy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bm_lghub.db");
                        System.IO.File.Copy(db, copy, true);
                        text = System.Text.Encoding.ASCII.GetString(System.IO.File.ReadAllBytes(copy));
                    }
                    catch { continue; }
                    Parse(text, result);
                }
            }
            catch { }
            return result;
        }

        private static void Parse(string text, Dictionary<string, int> result)
        {
            // Format : "battery/<slug>/percentage": { "percentage": 85, ... }
            var index = 0;
            while (true)
            {
                var at = text.IndexOf("battery/", index, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                index = at + 8;
                var end = index;
                while (end < text.Length && text[end] != '"') end++;
                var slug = text.Substring(index, end - index);
                // Toute sous-clé (percentage, warning, level...) porte un objet avec "percentage".
                var slash = slug.IndexOf('/');
                if (slash <= 0) continue;
                var key = slug.Substring(0, slash);

                // Cherche "percentage": <nombre> dans l'objet qui suit la clé.
                var objStart = text.IndexOf('{', end);
                if (objStart < 0) continue;
                var objEnd = text.IndexOf('}', objStart);
                var span = objEnd > objStart ? text.Substring(objStart, objEnd - objStart) : text.Substring(objStart);
                var pct = span.IndexOf("\"percentage\"", StringComparison.OrdinalIgnoreCase);
                if (pct < 0) continue;
                var colon = span.IndexOf(':', pct);
                if (colon < 0) continue;
                var p = colon + 1;
                while (p < span.Length && span[p] == ' ') p++;
                var q = p;
                while (q < span.Length && Char.IsDigit(span[q])) q++;
                int value;
                if (q > p && Int32.TryParse(span.Substring(p, q - p), out value) && value >= 0 && value <= 100)
                {
                    if (!result.ContainsKey(key)) result[key] = value;
                }
            }
        }

        public static int? Match(Dictionary<string, int> map, string deviceName)
        {
            if (map.Count == 0) return null;
            // Identifiant produit : "G915 X LIGHTSPEED" -> "g915", "G502 LIGHTSPEED" -> "g502".
            var model = ModelKey(deviceName);
            if (model.Length == 0) return null;
            foreach (var pair in map)
            {
                var hubSlug = Slug(pair.Key);
                if (hubSlug.StartsWith(model, StringComparison.OrdinalIgnoreCase) || model.StartsWith(hubSlug, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
            return null;
        }

        // Extrait le jeton modèle (lettre + chiffres) : "G915 X LIGHTSPEED" -> "g915".
        private static string ModelKey(string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(name ?? "", "[a-zA-Z]+(\\d+)");
            return match.Success ? match.Value.ToLowerInvariant() : Slug(name);
        }

        private static string Slug(string name)
        {
            var lower = (name ?? "").ToLowerInvariant();
            var chars = lower.Where(c => Char.IsLetterOrDigit(c)).ToArray();
            return new string(chars);
        }
    }

    // Lecture des niveaux que Corsair iCUE publie éventuellement (cache SQLite ou config JSON).
    internal static class IcueBattery
    {
        public static Dictionary<string, int> ReadAll()
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var roots = new[]
                {
                    System.IO.Path.Combine(local, @"Corsair"),
                    System.IO.Path.Combine(roaming, @"Corsair")
                };
                foreach (var root in roots)
                {
                    if (!System.IO.Directory.Exists(root)) continue;
                    IEnumerable<string> files;
                    try { files = System.IO.Directory.GetFiles(root, "*.*", System.IO.SearchOption.AllDirectories); }
                    catch { continue; }
                    foreach (var file in files)
                    {
                        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                        if (ext != ".sqlite" && ext != ".db" && ext != ".json" && ext != ".cuecfg") continue;
                        string text;
                        try { text = System.Text.Encoding.ASCII.GetString(System.IO.File.ReadAllBytes(file)); }
                        catch { continue; }
                        Parse(text, result);
                    }
                }
            }
            catch { }
            return result;
        }

        private static void Parse(string text, Dictionary<string, int> result)
        {
            // Clés du type "batteryLevel": 85 / "battery_percentage": 85.
            foreach (var key in new[] { "batteryLevel", "battery_level", "batteryPercentage", "battery_percentage", "battery" })
            {
                var index = 0;
                while (true)
                {
                    var at = text.IndexOf(key, index, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) break;
                    index = at + key.Length;
                    var colon = text.IndexOf(':', index);
                    if (colon < 0 || colon - index > 5) continue;
                    var p = colon + 1;
                    while (p < text.Length && text[p] == ' ') p++;
                    var q = p;
                    while (q < text.Length && Char.IsDigit(text[q])) q++;
                    int value;
                    if (q > p && Int32.TryParse(text.Substring(p, q - p), out value) && value >= 0 && value <= 100)
                    {
                        if (!result.ContainsKey("corsair") || result["corsair"] != value) result["corsair"] = value;
                        break;
                    }
                }
            }
        }

        public static int? Match(Dictionary<string, int> map, string deviceName)
        {
            if (map.Count == 0) return null;
            int value;
            if (map.TryGetValue("corsair", out value)) return value;
            return null;
        }
    }

    internal enum DeviceGlyph { Battery, Headphones, Gamepad, Speaker, Laptop, Keyboard, Mouse, Mobile }

    internal sealed class DeviceCard : Panel
    {
        public readonly DeviceBattery Device;
        public event EventHandler Selected2;
        private readonly Color accent;
        private readonly Color green;
        private readonly Color ink;
        private readonly Color muted;
        private readonly Color red;
        private readonly Color orange;
        private bool selected;
        private bool hover;

        public DeviceCard(DeviceBattery device, Color ink, Color muted, Color accent, Color green, Color orange, Color red)
        {
            Device = device;
            this.ink = ink;
            this.muted = muted;
            this.accent = accent;
            this.green = green;
            this.orange = orange;
            this.red = red;
            Height = 72;
            Width = 464;
            Margin = new Padding(0, 0, 0, 8);
            BackColor = Color.White;
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.Selectable, false);

            var glyph = new GlyphBox(GlyphFor(device), accent) { Location = new Point(14, 17), Size = new Size(38, 38) };
            Controls.Add(glyph);

            var name = new Label { Text = device.Name, Font = new Font("Segoe UI", 10.5F, FontStyle.Bold), ForeColor = ink, AutoSize = false, Location = new Point(66, 16), Size = new Size(240, 22), BackColor = Color.Transparent, AutoEllipsis = true };
            Controls.Add(name);

            var status = new StatusBadge(device.Connected ? "Connected" : "Disconnected", device.Connected ? green : muted, true) { Location = new Point(66, 39) };
            Controls.Add(status);

            var levelText = device.Level.HasValue ? device.Level.Value + "%" : "\u2014";
            var levelColor = device.Level.HasValue ? (device.Level.Value <= 20 ? red : device.Level.Value <= 40 ? orange : green) : muted;
            var battery = new Label { Text = levelText, Font = new Font("Segoe UI", 13.5F, FontStyle.Bold), ForeColor = levelColor, AutoSize = true, BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            battery.Left = Width - battery.Width - 26;
            battery.Top = 26;
            Resize += delegate { battery.Left = ClientSize.Width - battery.Width - 26; };
            Controls.Add(battery);

            AttachClick(this);
        }

        private void AttachClick(Control root)
        {
            root.Click += CardClick;
            root.MouseEnter += delegate { hover = true; Invalidate(); };
            root.MouseLeave += delegate { hover = false; Invalidate(); };
            foreach (Control child in root.Controls) AttachClick(child);
        }

        private void CardClick(object sender, EventArgs e)
        {
            var handler = Selected2;
            if (handler != null) handler(this, e);
        }

        public void SetSelected(bool value)
        {
            selected = value;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // La carte repose sur un fond blanc (deviceList) : on repaint ce fond avant la forme arrondie.
            using (var back = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(back, ClientRectangle);

            var rect = new Rectangle(1, 1, Width - 3, Height - 3);
            var radius = 12;
            using (var path = Theme.Rounded(rect, radius))
            {
                // Fond : blanc par défaut, teinté accent si sélectionné, légèrement grisé au survol.
                var fill = selected ? Theme.AccentSoft : (hover ? Theme.Hover : Color.White);
                using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                // Ombre douce uniquement pour la carte sélectionnée.
                if (selected)
                {
                    using (var pen = new Pen(Theme.Accent, 1.4f)) e.Graphics.DrawPath(pen, path);
                }
                else
                {
                    using (var pen = new Pen(Theme.Line, 1f)) e.Graphics.DrawPath(pen, path);
                }
            }
        }

        internal static DeviceGlyph GlyphFor(DeviceBattery device)
        {
            var lower = (device.Name ?? "").ToLowerInvariant();
            if (device.IsSystem) return DeviceGlyph.Laptop;
            // L'audio est testé avant le mobile : « casque pour téléphone » doit rester un casque.
            if (lower.Contains("airpods") || lower.Contains("headphone") || lower.Contains("casque") || lower.Contains("void") || lower.Contains("écouteur") || lower.Contains("ecouteur")) return DeviceGlyph.Headphones;
            if (lower.Contains("xbox") || lower.Contains("controller") || lower.Contains("manette")) return DeviceGlyph.Gamepad;
            if (lower.Contains("jbl") || lower.Contains("speaker") || lower.Contains("enceinte") || lower.Contains("bose")) return DeviceGlyph.Speaker;
            if (lower.Contains("g915") || lower.Contains("keyboard") || lower.Contains("clavier")) return DeviceGlyph.Keyboard;
            if (lower.Contains("g502") || lower.Contains("mouse") || lower.Contains("souris")) return DeviceGlyph.Mouse;
            if (lower.Contains("iphone") || lower.Contains("ipad") || lower.Contains("phone") || lower.Contains("mobile")) return DeviceGlyph.Mobile;
            return DeviceGlyph.Battery;
        }
    }

    // Génère par code les icônes de batterie (bureau + zone de notification) :
    // aucun fichier .ico externe n'est nécessaire à côté de l'exécutable.
    internal static class TrayIcons
    {
        private static readonly Color GreenColor = Color.FromArgb(76, 175, 80);
        private static readonly Color RedColor = Color.FromArgb(198, 40, 40);

        private static Icon green;
        private static Icon red;
        private static Icon app;

        public static Icon Green() { if (green == null) green = Build(GreenColor, GreenColor); return green; }
        public static Icon Red() { if (red == null) red = Build(RedColor, RedColor); return red; }
        public static Icon AppIcon() { if (app == null) app = Build(Color.White, GreenColor); return app; }

        // Remplit la batterie au vert jusque sous le bord (icône d'application, multi-tailles).
        private static Icon Build(Color fill, Color body)
        {
            var sizes = new[] { 16, 20, 24, 32, 48, 64 };
            using (var stream = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(stream))
            {
                writer.Write((ushort)0); // réservé
                writer.Write((ushort)1); // type : icône
                writer.Write((ushort)sizes.Length);
                writer.Flush();

                var offset = 6 + 16 * sizes.Length;
                var images = new byte[sizes.Length][];
                for (var i = 0; i < sizes.Length; i++)
                {
                    images[i] = RenderDib(sizes[i], fill, body);
                    writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    writer.Write((byte)0); // palette
                    writer.Write((byte)0); // réservé
                    writer.Write((ushort)1); // plans
                    writer.Write((ushort)32); // bits par pixel
                    writer.Write(images[i].Length);
                    writer.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var image in images) writer.Write(image);
                writer.Flush();
                stream.Position = 0;
                using (var temp = new Icon(stream)) return (Icon)temp.Clone();
            }
        }

        // Rendu en DIB 32 bits (et non PNG) : le shell Windows n'affiche pas toujours
        // l'icône d'une info-bulle quand la trame est compressée en PNG, d'où une pastille vide.
        private static byte[] RenderDib(int size, Color fill, Color body)
        {
            using (var bitmap = new Bitmap(size, size))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.Clear(Color.Transparent);
                PaintBattery(graphics, size, fill, body);

                using (var encoded = new System.IO.MemoryStream())
                using (var writer = new System.IO.BinaryWriter(encoded))
                {
                    var data = bitmap.LockBits(new Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        // En-tête BITMAPINFOHEADER (40 octets) écrit champ par champ.
                        writer.Write(40);                 // biSize
                        writer.Write(size);               // biWidth
                        writer.Write(size * 2);           // biHeight = XOR (couleur) + AND (transparence)
                        writer.Write((ushort)1);          // biPlanes
                        writer.Write((ushort)32);         // biBitCount
                        writer.Write(0);                  // biCompression = BI_RGB
                        writer.Write(data.Stride * size); // biSizeImage
                        writer.Write(0);                  // biXPelsPerMeter
                        writer.Write(0);                  // biYPelsPerMeter
                        writer.Write(0);                  // biClrUsed
                        writer.Write(0);                  // biClrImportant
                        // Pixels BGR (l'icône ignore le canal alpha : la transparence passe par le masque AND).
                        var row = new byte[data.Stride];
                        for (var y = 0; y < size; y++)
                        {
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                            for (var x = 0; x < size; x++)
                            {
                                var blue = row[x * 4];
                                var green = row[x * 4 + 1];
                                var red = row[x * 4 + 2];
                                var alpha = row[x * 4 + 3];
                                // Trame XOR : pré-multiplication, sinon les pixels translucides restent opaques.
                                writer.Write((byte)(blue * alpha / 255));
                                writer.Write((byte)(green * alpha / 255));
                                writer.Write((byte)(red * alpha / 255));
                                writer.Write((byte)255);
                            }
                        }

                        // Masque AND 1 bit/pixel : opacité arrondie à 50 % de l'alpha.
                        var maskStride = ((size + 31) / 32) * 4; // largeur de ligne alignée sur 4 octets
                        var mask = new byte[Math.Max(1, maskStride)];
                        for (var y = 0; y < size; y++)
                        {
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                            Array.Clear(mask, 0, mask.Length);
                            for (var x = 0; x < size; x++)
                                if (row[x * 4 + 3] < 128) mask[x / 8] |= (byte)(0x80 >> (x % 8));
                            writer.Write(mask);
                        }
                    }
                    finally { bitmap.UnlockBits(data); }

                    writer.Flush();
                    return encoded.ToArray();
                }
            }
        }

        private static void PaintBattery(Graphics graphics, int size, Color fill, Color body)
        {
            var scale = size / 16f;
            using (var brush = new SolidBrush(fill))
            using (var bodyBrush = new SolidBrush(body))
            {
                // Corps horizontal de la batterie : rectangle plein (pas de jointure contour/remplissage
                // qui laisserait un liseré clair après lissage).
                var left = 1.5f * scale;
                var top = 4f * scale;
                var width = 11f * scale;
                var height = 8f * scale;
                graphics.FillRectangle(brush, left, top, width, height);
                // Borne positive collée au corps.
                graphics.FillRectangle(bodyBrush, left + width, top + 2.5f * scale, 1.5f * scale, 3f * scale);
            }
        }
    }

    // Palette et helpers visuels partagés par toute l'application (style macOS épuré).
    internal static class Theme
    {
        public static readonly Color Canvas = Color.FromArgb(244, 245, 247); // fond général
        public static readonly Color Card = Color.White;                      // cartes
        public static readonly Color Field = Color.FromArgb(247, 248, 250);   // zones de saisie
        public static readonly Color Hover = Color.FromArgb(248, 249, 251);
        public static readonly Color Line = Color.FromArgb(229, 231, 235);
        public static readonly Color Ink = Color.FromArgb(29, 29, 31);
        public static readonly Color Muted = Color.FromArgb(134, 134, 139);
        public static readonly Color Accent = Color.FromArgb(10, 132, 255);   // bleu système
        public static readonly Color AccentSoft = Color.FromArgb(232, 242, 254);
        public static readonly Color Green = Color.FromArgb(52, 199, 89);
        public static readonly Color Orange = Color.FromArgb(255, 159, 10);
        public static readonly Color Red = Color.FromArgb(255, 69, 58);

        // Chemin arrondi réutilisable pour dessiner des coins ronds.
        // Le rayon est plafonné à la demi-plus-petite-dimension : radius == height/2 donne
        // un ovale complet (pastille), y compris quand height == radius * 2.
        public static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var max = Math.Min(bounds.Width, bounds.Height) / 2;
            var r = Math.Max(0, Math.Min(radius, max));
            if (r <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            var d = r * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Version très claire d'une couleur, pour les fonds de pastille.
        public static Color Tint(Color color, float amount)
        {
            return Color.FromArgb(
                (int)(color.R + (255 - color.R) * (1 - amount)),
                (int)(color.G + (255 - color.G) * (1 - amount)),
                (int)(color.B + (255 - color.B) * (1 - amount)));
        }
    }

    // Liste déroulante stylée : fond clair arrondi, texte et chevron entièrement maison.
    internal sealed class StyledCombo : ComboBox
    {
        private bool hover;

        public StyledCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            BackColor = Theme.Field;
            ForeColor = Theme.Ink;
            Font = new Font("Segoe UI", 9.75F);
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 22;
            IntegralHeight = false;
            DropDownHeight = 180;
            SetStyle(ControlStyles.UserPaint, false);
            MouseEnter += delegate { hover = true; Invalidate(); };
            MouseLeave += delegate { hover = false; Invalidate(); };
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var selectedItem = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (var brush = new SolidBrush(selectedItem ? Theme.AccentSoft : Theme.Card))
                e.Graphics.FillRectangle(brush, e.Bounds);
            using (var brush = new SolidBrush(selectedItem ? Theme.Accent : Theme.Ink))
            using (var format = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                e.Graphics.DrawString(Convert.ToString(Items[e.Index]), Font, brush, new RectangleF(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height), format);
        }

        // Repaint du cadre : on masque entièrement le chevron natif en recouvrant la zone puis
        // en dessinant le nôtre, pour éviter tout double chevron.
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F || m.Msg == 0x0014) // WM_PAINT / WM_ERASEBKGND
            {
                using (var g = Graphics.FromHwnd(Handle))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    // Fond de la zone droite (couvre l'ancien chevron système).
                    var box = new Rectangle(0, 0, Width, Height);
                    using (var brush = new SolidBrush(hover ? Theme.Hover : Theme.Field)) g.FillRectangle(brush, box);

                    // Texte de l'élément sélectionné.
                    if (SelectedIndex >= 0)
                    {
                        using (var brush = new SolidBrush(Theme.Ink))
                        using (var format = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                            g.DrawString(Convert.ToString(Items[SelectedIndex]), Font, brush, new RectangleF(12, 0, Width - 34, Height), format);
                    }

                    // Chevron maison.
                    var cx = Width - 15;
                    var cy = Height / 2f;
                    using (var pen = new Pen(Theme.Muted, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(pen, cx - 4, cy - 2, cx, cy + 2);
                        g.DrawLine(pen, cx, cy + 2, cx + 4, cy - 2);
                    }
                }
            }
        }
    }

    // Barre de titre transparente (le fond de fenêtre est peint par le formulaire).
    internal sealed class TitleBar : Panel
    {
        public TitleBar()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Canvas;
            DoubleBuffered = true;
        }
    }

    // Glyphe affiché dans un bouton de fenêtre.
    internal enum CaptionGlyph { Minimize, Maximize, Restore, Close }

    // Bouton de la barre de titre (réduire / agrandir / fermer), style Windows 11 :
    // fond gris clair au survol, rouge pour la fermeture, glyphe vectoriel fin.
    internal sealed class CaptionButton : Control
    {
        private readonly CaptionGlyph glyph;
        private bool hover;

        public CaptionButton(CaptionGlyph glyph)
        {
            this.glyph = glyph;
            Size = new Size(46, 40);
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Color.Transparent;
            MouseEnter += delegate { hover = true; Invalidate(); };
            MouseLeave += delegate { hover = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            if (hover)
            {
                var back = glyph == CaptionGlyph.Close ? Color.FromArgb(232, 17, 35) : Color.FromArgb(232, 232, 232);
                using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, ClientRectangle);
            }

            var glyphColor = hover && glyph == CaptionGlyph.Close ? Color.White : Theme.Ink;
            using (var pen = new Pen(glyphColor, 1.2f))
            {
                var cx = Width / 2f;
                var cy = Height / 2f;
                var r = 5f;
                switch (glyph)
                {
                    case CaptionGlyph.Minimize:
                        e.Graphics.DrawLine(pen, cx - r, cy, cx + r, cy);
                        break;
                    case CaptionGlyph.Maximize:
                        e.Graphics.DrawRectangle(pen, cx - r, cy - r, r * 2, r * 2);
                        break;
                    case CaptionGlyph.Restore:
                        // Deux cadres décalés (fenêtre restaurée).
                        e.Graphics.DrawRectangle(pen, cx - r + 2, cy - r, r * 2 - 2, r * 2 - 2);
                        e.Graphics.DrawLine(pen, cx - r, cy - r + 2, cx + r - 2, cy - r + 2);
                        e.Graphics.DrawLine(pen, cx - r, cy - r + 2, cx - r, cy + r - 2);
                        e.Graphics.DrawLine(pen, cx + r - 2, cy - r + 2, cx + r - 2, cy + r - 2);
                        e.Graphics.DrawLine(pen, cx - r, cy + r - 2, cx + r - 2, cy + r - 2);
                        break;
                    default:
                        e.Graphics.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                        e.Graphics.DrawLine(pen, cx + r, cy - r, cx - r, cy + r);
                        break;
                }
            }
        }
    }

    // Liste défilante avec barre de défilement fine et arrondie (remplace la scrollbar système).
    internal sealed class ScrollList : Panel
    {
        private readonly Panel inner;
        private int offset;
        private bool dragging;
        private int dragStartY;
        private int dragStartOffset;
        private bool barHover;

        public ScrollList()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            inner = new Panel { Left = 0, Top = 0, BackColor = Theme.Card };
            Controls.Add(inner);
            MouseWheel += delegate (object s, MouseEventArgs e) { ScrollBy(-e.Delta); };
            inner.MouseWheel += delegate (object s, MouseEventArgs e) { ScrollBy(-e.Delta); };
            Resize += delegate { Layout_(); };
            MouseDown += BarMouseDown;
            MouseMove += BarMouseMove;
            MouseUp += delegate { dragging = false; };
            MouseLeave += delegate { barHover = false; Invalidate(); };
            MouseEnter += delegate { barHover = true; Invalidate(); };
        }

        // Contrôles (cartes) actuellement hébergés.
        public ControlCollection CardControls { get { return inner.Controls; } }

        // Retire toutes les cartes existantes pour reconstruire une liste propre à chaque rafraîchissement.
        public void ClearCards()
        {
            foreach (Control card in inner.Controls.Cast<Control>().ToList()) card.Dispose();
            inner.Controls.Clear();
            offset = 0;
            inner.Top = 0;
        }

        public void BeginUpdate() { inner.SuspendLayout(); }

        public void EndUpdate()
        {
            inner.ResumeLayout();
            offset = 0;
            Layout_();
            Invalidate();
        }

        // Ajoute une carte empilée depuis le haut.
        public void AddCard(Control card)
        {
            card.Dock = DockStyle.Top;
            card.Margin = new Padding(0, 0, 0, 8);
            inner.Controls.Add(card);
            card.BringToFront();
        }

        private int ContentHeight
        {
            get { return inner.Controls.Count == 0 ? 0 : inner.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical); }
        }

        private void Layout_()
        {
            inner.Width = Math.Max(0, ClientSize.Width - Padding.Right);
            inner.Height = Math.Max(ClientSize.Height, ContentHeight);
            var max = Math.Max(0, inner.Height - ClientSize.Height);
            if (offset > max) offset = max;
            if (offset < 0) offset = 0;
            inner.Top = -offset;
        }

        private void ScrollBy(int delta)
        {
            var max = Math.Max(0, inner.Height - ClientSize.Height);
            if (max <= 0) return;
            offset = Math.Max(0, Math.Min(max, offset + delta / 3));
            inner.Top = -offset;
            Invalidate();
        }

        private int ThumbHeight
        {
            get
            {
                if (inner.Height <= 0 || inner.Height <= ClientSize.Height) return 0;
                var track = ClientSize.Height - 8;
                return Math.Max(30, (int)(track * (double)ClientSize.Height / inner.Height));
            }
        }

        private int ThumbTop
        {
            get
            {
                var max = inner.Height - ClientSize.Height;
                if (max <= 0) return 4;
                var track = ClientSize.Height - 8 - ThumbHeight;
                return 4 + (int)(track * (double)offset / max);
            }
        }

        private Rectangle ThumbRect { get { return new Rectangle(ClientSize.Width - 6, ThumbTop, 4, ThumbHeight); } }

        private void BarMouseDown(object sender, MouseEventArgs e)
        {
            if (ThumbHeight == 0) return;
            if (ThumbRect.Contains(e.Location)) { dragging = true; dragStartY = e.Y; dragStartOffset = offset; }
            else if (e.X >= ClientSize.Width - 12)
            {
                // Clic sur la piste : déplace proportionnellement.
                var ratio = (double)(e.Y - 4) / Math.Max(1, ClientSize.Height - 8);
                var max = Math.Max(0, inner.Height - ClientSize.Height);
                offset = Math.Max(0, Math.Min(max, (int)(ratio * max)));
                inner.Top = -offset;
                Invalidate();
            }
        }

        private void BarMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragging || ThumbHeight == 0) return;
            var track = ClientSize.Height - 8 - ThumbHeight;
            var max = Math.Max(0, inner.Height - ClientSize.Height);
            var delta = (int)((double)(e.Y - dragStartY) / Math.Max(1, track) * max);
            offset = Math.Max(0, Math.Min(max, dragStartOffset + delta));
            inner.Top = -offset;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Fond de la zone visible (sous les cartes, y compris la bande de marge à droite).
            using (var back = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(back, ClientRectangle);
            if (ThumbHeight == 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var thumb = ThumbRect;
            // Barre discrète : plus visible au survol (comportement macOS).
            using (var path = Theme.Rounded(thumb, 2))
            using (var brush = new SolidBrush(barHover ? Color.FromArgb(120, Theme.Muted) : Color.FromArgb(70, Theme.Muted)))
                e.Graphics.FillPath(brush, path);
        }
    }

    // Carte blanche à coins arrondis, utilisée pour les trois grandes zones.
    internal sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            BackColor = Theme.Canvas;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Canvas);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Rounded(rect, 16))
            {
                using (var brush = new SolidBrush(Theme.Card)) e.Graphics.FillPath(brush, path);
                using (var pen = new Pen(Theme.Line, 1f)) e.Graphics.DrawPath(pen, path);
            }
        }
    }

    // Bouton rond discret (bordure fine, icône centrée).
    internal sealed class RoundedButton : Control
    {
        private readonly string glyph;
        private bool hover;

        public RoundedButton(string glyph)
        {
            this.glyph = glyph;
            BackColor = Theme.Card;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            MouseEnter += delegate { hover = true; Invalidate(); };
            MouseLeave += delegate { hover = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var back = new SolidBrush(Theme.Canvas)) e.Graphics.FillRectangle(back, ClientRectangle);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Rounded(rect, 12))
            {
                using (var brush = new SolidBrush(hover ? Theme.Hover : Theme.Card)) e.Graphics.FillPath(brush, path);
                using (var pen = new Pen(Theme.Line, 1f)) e.Graphics.DrawPath(pen, path);
            }

            // Flèche de rafraîchissement : anneau presque complet ouvert en haut à droite,
            // avec une pointe triangulaire tangente à l'extrémité de fin de l'arc.
            var color = hover ? Theme.Accent : Theme.Ink;
            var d = 17f;
            var cx = (Width - d) / 2f;
            var cy = (Height - d) / 2f;
            var centerX = cx + d / 2f;
            var centerY = cy + d / 2f;
            var radius = d / 2f;
            const float startAngle = 65f;   // ouverture orientée vers le haut-droite
            const float sweep = 280f;
            using (var pen = new Pen(color, 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                e.Graphics.DrawArc(pen, cx, cy, d, d, startAngle, sweep);

            // Pointe de la flèche à l'extrémité de fin de l'arc.
            var endAngle = (startAngle + sweep) * Math.PI / 180.0;
            var tipX = centerX + (float)(radius * Math.Cos(endAngle));
            var tipY = centerY + (float)(radius * Math.Sin(endAngle));
            using (var brush = new SolidBrush(color))
                e.Graphics.FillPolygon(brush, new[]
                {
                    new PointF(tipX - 1f, tipY - 4.5f),
                    new PointF(tipX + 5f, tipY + 0.5f),
                    new PointF(tipX - 4f, tipY + 2.5f)
                });
        }
    }

    // Interrupteur type iOS : piste arrondie + pastille glissante.
    // Dérivé directement de Control pour un rendu entièrement personnalisé (aucun artefact système).
    internal sealed class SwitchBox : Control
    {
        private bool isChecked;
        public event EventHandler CheckedChanged;

        public SwitchBox()
        {
            BackColor = Theme.Card;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Size = new Size(52, 28);
            Anchor = AnchorStyles.Left;
            MouseEnter += delegate { Invalidate(); };
            MouseLeave += delegate { Invalidate(); };
        }

        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                Invalidate();
                var handler = CheckedChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Fond opaque : évite le noir quand le parent est transparent.
            using (var back = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(back, ClientRectangle);

            var trackHeight = Math.Min(26, Height);
            var track = new Rectangle(0, (Height - trackHeight) / 2, 48, trackHeight);
            using (var path = Theme.Rounded(track, trackHeight / 2))
            using (var brush = new SolidBrush(isChecked ? Theme.Accent : Color.FromArgb(209, 209, 214)))
                e.Graphics.FillPath(brush, path);

            var knobSize = trackHeight - 4;
            var knobX = isChecked ? track.Right - knobSize - 2 : track.X + 2;
            var knob = new Rectangle(knobX, track.Y + 2, knobSize, knobSize);
            using (var brush = new SolidBrush(Color.White)) e.Graphics.FillEllipse(brush, knob);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Checked = !Checked;
        }
    }

    // Badge de statut arrondi (pastille fine avec point coloré et libellé).
    // Le mode compact donne un simple point + libellé, pour les listes denses.
    internal sealed class StatusBadge : Control
    {
        private readonly string text;
        private readonly Color color;
        private readonly bool compact;

        public StatusBadge(string text, Color color) : this(text, color, false) { }

        public StatusBadge(string text, Color color, bool compact)
        {
            this.text = text;
            this.color = color;
            this.compact = compact;
            DoubleBuffered = true;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Color.Transparent;
            Font = new Font("Segoe UI", compact ? 8.5F : 9F, compact ? FontStyle.Regular : FontStyle.Bold);
            Size = new Size(140, compact ? 16 : 24);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var font = new Font("Segoe UI", compact ? 8.5F : 9F, compact ? FontStyle.Regular : FontStyle.Bold))
            {
                var size = e.Graphics.MeasureString(text, font);
                var dot = compact ? 6 : 8;
                var left = compact ? 12 : 24;

                if (compact)
                {
                    // Pas de fond : simple pastille discrète dans la carte.
                    using (var brush = new SolidBrush(color)) e.Graphics.FillEllipse(brush, 1, (Height - dot) / 2f, dot, dot);
                }
                else
                {
                    Width = (int)size.Width + 40;
                    var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                    using (var path = Theme.Rounded(rect, Height / 2))
                    {
                        using (var brush = new SolidBrush(Theme.Tint(color, 0.16f))) e.Graphics.FillPath(brush, path);
                        using (var pen = new Pen(Color.FromArgb(60, color), 1f)) e.Graphics.DrawPath(pen, path);
                    }
                    using (var brush = new SolidBrush(color)) e.Graphics.FillEllipse(brush, 10, (Height - dot) / 2f, dot, dot);
                }

                Width = compact ? (int)size.Width + 14 : Width;
                using (var brush = new SolidBrush(color))
                    e.Graphics.DrawString(text, font, brush, left, (Height - size.Height) / 2f);
            }
        }
    }

    // Barre de charge fine et arrondie, remplie à la valeur indiquée.
    internal sealed class ProgressBar2 : Control
    {
        private readonly Color fillColor;
        private int value;

        public ProgressBar2(Color fillColor)
        {
            this.fillColor = fillColor;
            DoubleBuffered = true;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public void SetValue(int percent)
        {
            value = Math.Max(0, Math.Min(100, percent));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Rounded(track, Height / 2))
            using (var brush = new SolidBrush(Color.FromArgb(234, 235, 238)))
                e.Graphics.FillPath(brush, path);

            var fillWidth = (int)(track.Width * (value / 100.0));
            if (fillWidth < Height) fillWidth = value > 0 ? Height : 0; // garde un bout arrondi lisible
            if (fillWidth <= 0) return;
            var filled = new Rectangle(0, 0, fillWidth, Height - 1);
            using (var path = Theme.Rounded(filled, Height / 2))
            using (var brush = new SolidBrush(fillColor))
                e.Graphics.FillPath(brush, path);
        }
    }

    // Pastille arrondie contenant le symbole de l'appareil (style icône macOS).
    internal sealed class GlyphBox : Control
    {
        private readonly DeviceGlyph glyph;
        private readonly Color accent;

        public GlyphBox(DeviceGlyph glyph, Color accent)
        {
            this.glyph = glyph;
            this.accent = accent;
            DoubleBuffered = true;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new Rectangle(0, 0, Width - 1, Height - 1);

            // Fond de pastille : teinte douce de l'accent, coins arrondis.
            using (var path = Theme.Rounded(box, 11))
            using (var back = new SolidBrush(Theme.Tint(accent, 0.22f)))
                e.Graphics.FillPath(back, path);

            // Symbole dessiné en silhouette pleine dans une zone de 22 px, centré dans la pastille.
            var s = Math.Min(Width, Height) / 34f;
            e.Graphics.TranslateTransform(Width / 2f, Height / 2f);
            e.Graphics.ScaleTransform(s, s);
            e.Graphics.TranslateTransform(-11f, -11f);
            using (var brush = new SolidBrush(accent))
            using (var pen = new Pen(accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                switch (glyph)
                {
                    case DeviceGlyph.Laptop:
                        // Écran plein + base évasée.
                        e.Graphics.FillRectangle(brush, 3, 4, 16, 11);
                        e.Graphics.FillRectangle(brush, 1, 16, 20, 2);
                        break;
                    case DeviceGlyph.Headphones:
                        // Arceau épais + deux oreillettes pleines.
                        using (var penArc = new Pen(accent, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            e.Graphics.DrawArc(penArc, 3, 3, 16, 17, 180, 180);
                        e.Graphics.FillRectangle(brush, 2, 11, 5, 9);
                        e.Graphics.FillRectangle(brush, 15, 11, 5, 9);
                        break;
                    case DeviceGlyph.Gamepad:
                        // Manette : corps allongé + poignées évasées + croix directionnelle et boutons.
                        using (var body = new GraphicsPath())
                        {
                            body.AddEllipse(2, 5, 18, 12);
                            body.AddEllipse(-1, 10, 9, 12);
                            body.AddEllipse(14, 10, 9, 12);
                            e.Graphics.FillPath(brush, body);
                        }
                        using (var cut = new SolidBrush(Theme.Tint(accent, 0.22f)))
                        {
                            // Croix directionnelle (gauche) et bouton (droite).
                            e.Graphics.FillRectangle(cut, 5.5f, 9.5f, 3.5f, 1.2f);
                            e.Graphics.FillRectangle(cut, 6.6f, 8.4f, 1.2f, 3.5f);
                            e.Graphics.FillEllipse(cut, 13.5f, 8.8f, 2.8f, 2.8f);
                        }
                        break;
                    case DeviceGlyph.Speaker:
                        // Enceinte : boîtier plein + onde.
                        e.Graphics.FillRectangle(brush, 5, 3, 8, 16);
                        using (var penWave = new Pen(accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        {
                            e.Graphics.DrawArc(penWave, 11, 6, 8, 10, -55, 110);
                            e.Graphics.DrawArc(penWave, 13, 3.5f, 10, 15, -55, 110);
                        }
                        break;
                    case DeviceGlyph.Keyboard:
                        // Clavier plein avec touches évidées.
                        e.Graphics.FillRectangle(brush, 1, 6, 20, 11);
                        using (var cut = new SolidBrush(Theme.Tint(accent, 0.22f)))
                        {
                            e.Graphics.FillRectangle(cut, 3, 8, 16, 1.5f);
                            e.Graphics.FillRectangle(cut, 3, 11, 16, 1.5f);
                            e.Graphics.FillRectangle(cut, 3, 14, 16, 1.5f);
                        }
                        break;
                    case DeviceGlyph.Mouse:
                        // Souris pleine avec séparation des clics.
                        e.Graphics.FillEllipse(brush, 7, 3, 8, 17);
                        using (var cut = new SolidBrush(Theme.Tint(accent, 0.22f)))
                            e.Graphics.FillRectangle(cut, 10.7f, 3, 1f, 7);
                        break;
                    case DeviceGlyph.Mobile:
                        // Smartphone : cadre plein + écran en creux + barre d'écoute.
                        using (var path = Theme.Rounded(new Rectangle(6, 2, 11, 19), 3))
                            e.Graphics.FillPath(brush, path);
                        using (var cut = new SolidBrush(Theme.Tint(accent, 0.22f)))
                            e.Graphics.FillRectangle(cut, 7.5f, 6, 8, 12);
                        break;
                    default:
                        // Batterie : corps plein + borne pleine.
                        using (var path = Theme.Rounded(new Rectangle(3, 6, 16, 11), 2))
                            e.Graphics.FillPath(brush, path);
                        e.Graphics.FillRectangle(brush, 19.5f, 9, 2f, 5);
                        break;
                }
            }
            e.Graphics.ResetTransform();
        }
    }
}
