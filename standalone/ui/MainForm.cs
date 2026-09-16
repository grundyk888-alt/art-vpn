using System.Reflection;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Ui;

internal sealed class MainForm : Form
{
    private readonly IArtVpnUiController _controller;
    private readonly ISetupLauncher _setupLauncher;
    private readonly Action<string>? _openRecommendation;
    private readonly Action<IWin32Window, string> _showCodexRestartAdvice;

    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _ageTimer;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly List<Button> _actionButtons = [];
    private readonly Dictionary<string, Button> _modeButtons = [];
    private readonly Dictionary<string, ToolStripMenuItem> _trayModeButtons = [];
    private readonly ToolStripMenuItem _trayMode;
    private readonly ToolStripMenuItem _trayActiveChannel;
    private Label _activeChannel = null!;
    private Label _headline = null!;
    private Label _explanation = null!;
    private Label _country = null!;
    private Label _connectedAge = null!;
    private Label _quality = null!;
    private Label _mode = null!;
    private Label _latency = null!;
    private Label _stability = null!;
    private Label _lastSwitch = null!;
    private Label _switchReason = null!;
    private Label _subscription = null!;
    private Label _subscriptionAt = null!;
    private Label _bypass = null!;
    private Label _update = null!;
    private Button _updateButton = null!;
    private string _notifiedUpdate = "";

    private Label _windowsRoute = null!;
    private readonly Label _footer;
    private readonly ToolTip _details = new() { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };
    private readonly Label[] _nodeRole = new Label[3];
    private readonly Label[] _nodeCountry = new Label[3];
    private readonly Label[] _nodeDetail = new Label[3];
    private readonly RoundedPanel _hero;
    private DateTimeOffset? _selectedAt;
    private string _connectedAgeWithoutTimestamp = "Время подключения не подтверждено";
    private VpnHealth? _lastHealth;
    private bool _busy;
    private string? _operationError;
    private bool _allowExit;
    private DiagnosticsDialog? _diagnosticsDialog;
    private HelpDialog? _helpDialog;

    public MainForm(IArtVpnUiController controller, ISetupLauncher setupLauncher, Action<string>? openRecommendation = null,
        Action<IWin32Window, string>? showCodexRestartAdvice = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _setupLauncher = setupLauncher ?? throw new ArgumentNullException(nameof(setupLauncher));
        _openRecommendation = openRecommendation;
        _showCodexRestartAdvice = showCodexRestartAdvice ?? ((owner, message) =>
            MessageBox.Show(owner, message, "Перезапустите Codex после смены VPN",
                MessageBoxButtons.OK, MessageBoxIcon.Information));

        Text = "ART VPN для ChatGPT, YouTube и других сервисов";
        Name = "ARTVpnMainForm";
        Font = new Font("Segoe UI", 10f);
        BackColor = Palette.Canvas;
        ForeColor = Palette.Ink;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(860, 680);
        Size = new Size(1040, 780);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        Icon = LoadIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Palette.Canvas
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        var scroll = new FlowLayoutPanel
        {
            Name = "MainScroll",
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(28, 22, 28, 24),
            BackColor = Palette.Canvas
        };
        root.Controls.Add(scroll, 0, 1);

        _hero = BuildHero();
        scroll.Controls.Add(_hero);
        scroll.Controls.Add(BuildWindowsConnectionCard());
        scroll.Controls.Add(BuildConnectionCard());
        scroll.Controls.Add(QuattroRecommendation.CreateCard("MainQuattroRecommendation", 920, _openRecommendation, prominent: true));
        scroll.Controls.Add(BuildAutomationCard());
        ResizeCards(scroll);
        scroll.ClientSizeChanged += (_, _) => ResizeCards(scroll);

        _footer = UiFactory.Label("Подключение к службе…", 9, false, Palette.Muted);
        _footer.Dock = DockStyle.Fill;
        _footer.AutoSize = false;
        _footer.TextAlign = ContentAlignment.MiddleLeft;
        _footer.Padding = new Padding(30, 0, 20, 0);
        _footer.BackColor = Color.White;
        root.Controls.Add(_footer, 0, 2);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Открыть ART VPN", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("Проверить сейчас", null, async (_, _) => await ExecuteAsync(
            token => _controller.CheckNowAsync(token), refreshAfter: true));
        _trayMode = new ToolStripMenuItem("Режим ещё не подтверждён") { Enabled = false, Name = "TrayModeStatus" };
        trayMenu.Items.Add(_trayMode);
        _trayActiveChannel = new ToolStripMenuItem("Сейчас: канал не подтверждён")
            { Enabled = false, Name = "TrayActiveChannelStatus" };
        trayMenu.Items.Add(_trayActiveChannel);
        foreach (var (mode, caption) in new[] { ("Auto", "Авто"), ("ArtVpn", "ART VPN"), ("Throne", "Throne — вручную"), ("Happ", "HAPP — вручную") })
        {
            var item = new ToolStripMenuItem(caption) { Name = "TrayMode" + mode, CheckOnClick = false };
            item.Click += async (_, _) => await ExecuteAsync(token => _controller.SetModeAsync(mode, token),
                refreshAfter: true, TimeSpan.FromMinutes(3));
            _trayModeButtons.Add(mode, item);
            trayMenu.Items.Add(item);
        }
        trayMenu.Items.Add("Сообщить о проблеме", null, (_, _) => OpenDiagnostics());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Закрыть интерфейс", null, (_, _) =>
        {
            _allowExit = true;
            Close();
        });
        _tray = new NotifyIcon
        {
            Icon = (Icon)Icon.Clone(),
            Text = "ART VPN — загрузка состояния",
            Visible = true,
            ContextMenuStrip = trayMenu
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.BalloonTipClicked += (_, _) => RestoreFromTray();

        _ageTimer = new System.Windows.Forms.Timer { Interval = 1_000 };
        _ageTimer.Tick += (_, _) => UpdateAge();
        _ageTimer.Start();
        _statusTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _statusTimer.Tick += async (_, _) =>
        {
            if (!_busy) await RefreshStatusAsync();
        };
        _statusTimer.Start();
        Shown += async (_, _) =>
        {
            await RefreshStatusAsync();
        };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) HideToTray();
        };
        FormClosing += OnFormClosing;
    }

    internal bool IsBusy => _busy;
    internal NotifyIcon Tray => _tray;

    internal async void ShowSetupCompletion(string message, bool codexRestartSuggested = false)
    {
        if (IsDisposed) return;
        await RefreshStatusAsync();
        if (!IsDisposed)
        {
            _footer.Text = message;
            if (codexRestartSuggested) ShowCodexRestartAdvice();
        }
    }

    private Control BuildHeader()
    {
        var header = new Panel { Name = "HeaderPanel", Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(28, 13, 28, 10) };
        var brand = UiFactory.Label("ART VPN", 19, true);
        brand.Location = new Point(28, 13);
        var subtitle = UiFactory.Label("Стабильный доступ к ChatGPT и OpenAI", 9.5f, false, Palette.Muted);
        subtitle.Location = new Point(30, 47);
        var quattro = ProductLinks.QuattroLink("QuattroReferralLink",
            "Рекомендуем Quattro VPN", new Point(315, 37), _openRecommendation);
        quattro.BackColor = Color.FromArgb(233, 240, 255);
        quattro.Padding = new Padding(10, 7, 10, 7);
        quattro.LinkBehavior = LinkBehavior.HoverUnderline;
        quattro.LinkColor = Color.FromArgb(37, 76, 133);
        header.Controls.Add(brand);
        header.Controls.Add(subtitle);
        header.Controls.Add(quattro);

        var setup = UiFactory.Button("SetupButton", "Настроить", false, 124);
        setup.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        setup.Location = new Point(Width - 180, 16);
        setup.Click += (_, _) => _setupLauncher.Open(this, _controller);
        var help = UiFactory.Button("HelpButton", "Как пользоваться", false, 174);
        help.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        help.Location = new Point(setup.Left - help.Width - 12, 16);
        help.Click += (_, _) => OpenHelp();
        header.Controls.Add(setup);
        header.Controls.Add(help);
        _actionButtons.Add(setup);
        _actionButtons.Add(help);
        header.Resize += (_, _) =>
        {
            setup.Left = header.ClientSize.Width - setup.Width - 28;
            help.Left = setup.Left - help.Width - 12;
            quattro.Text = "Рекомендуем Quattro VPN";
            // On narrow windows use the free centre of the first row; do not
            // let the larger recommendation pill collide with Help/subtitle.
            var narrow = 315 * DeviceDpi / 96 + quattro.Width + 12 > help.Left;
            quattro.Location = narrow ? new Point(Math.Max(brand.Right + 16, help.Left - quattro.Width - 12), 4)
                : new Point(315 * DeviceDpi / 96, 37 * DeviceDpi / 96);
        };
        return header;
    }

    private RoundedPanel BuildHero()
    {
        var card = new RoundedPanel
        {
            Name = "StatusHero",
            Width = 920,
            Height = 170,
            BackColor = Palette.GreenSoft,
            BorderColor = Color.FromArgb(193, 232, 214),
            Margin = new Padding(0, 0, 0, 16)
        };
        var marker = new Label
        {
            Name = "StatusMarker",
            Text = "✓",
            Font = new Font("Segoe UI Semibold", 24),
            ForeColor = Palette.Green,
            BackColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(58, 58),
            Location = new Point(24, 27)
        };
        _headline = UiFactory.Label("Проверяем состояние…", 18, true);
        _headline.Name = "ConnectionHeadline";
        _headline.AutoSize = false;
        _headline.AutoEllipsis = true;
        _headline.Location = new Point(102, 24);
        _explanation = UiFactory.Label("Текущий канал во время проверки не изменяется.", 10, false, Palette.Muted);
        _explanation.Name = "ConnectionExplanation";
        _explanation.Location = new Point(104, 61);
        _explanation.AutoSize = false;
        _explanation.AutoEllipsis = true;
        _explanation.Size = new Size(590, 34);
        _mode = CreatePill("Авто", new Point(104, 98), Palette.BlueSoft, Palette.Blue);
        _quality = CreatePill("Не проверено", new Point(210, 98), Color.White, Palette.Muted);
        _quality.Visible = false; // Repeated quality text belongs in the status tooltip.
        _mode.Width = 140;
        _quality.Left = 254;
        _activeChannel = UiFactory.Label("Сейчас: канал не подтверждён", 10, true, Palette.Muted);
        _activeChannel.Name = "ActiveChannelStatus";
        _activeChannel.Location = new Point(104, 136);
        _activeChannel.AutoSize = false;
        _activeChannel.AutoEllipsis = true;
        _activeChannel.Size = new Size(680, 24);

        var check = UiFactory.Button("CheckNowButton", "Проверить сейчас", true, 174);
        check.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        check.Location = new Point(card.Width - check.Width - 26, 50);
        check.Click += async (_, _) => await ExecuteAsync(token => _controller.CheckNowAsync(token), refreshAfter: true);
        var layingOut = false;
        void LayoutStatus()
        {
            if (layingOut) return;
            layingOut = true;
            try
            {
                int Px(int value) => (int)Math.Round(value * card.DeviceDpi / 96.0);
                check.Left = card.ClientSize.Width - check.Width - Px(26);
                _headline.Width = Math.Max(Px(100), check.Left - _headline.Left - Px(14));
                _explanation.Width = Math.Max(Px(100), check.Left - _explanation.Left - Px(14));
                int TextHeight(Label label, int minimum, int maximum) => Math.Clamp(
                    TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + Px(2), Px(minimum), Px(maximum));
                _headline.Height = TextHeight(_headline, 30, 90);
                _explanation.Top = _headline.Bottom + Px(6);
                _explanation.Height = TextHeight(_explanation, 20, 180);
                _mode.Top = _quality.Top = _explanation.Bottom + Px(5);
                _activeChannel.Top = Math.Max(_mode.Bottom, _quality.Bottom) + Px(7);
                _activeChannel.Width = Math.Max(Px(100), card.ClientSize.Width - _activeChannel.Left - Px(24));
                card.Height = Math.Max(Px(148), _activeChannel.Bottom + Px(14));
            }
            finally { layingOut = false; }
        }
        card.Resize += (_, _) => LayoutStatus();
        _headline.TextChanged += (_, _) => LayoutStatus();
        _explanation.TextChanged += (_, _) => LayoutStatus();
        LayoutStatus();
        _actionButtons.Add(check);
        card.Controls.Add(marker);
        card.Controls.Add(_headline);
        card.Controls.Add(_explanation);
        card.Controls.Add(_mode);
        card.Controls.Add(_quality);
        card.Controls.Add(_activeChannel);
        card.Controls.Add(check);
        return card;
    }

    private RoundedPanel BuildConnectionCard()
    {
        var card = new RoundedPanel
        {
            Name = "ConnectionCard",
            Width = 920,
            Height = 282,
            Margin = new Padding(0, 0, 0, 16)
        };
        var title = UiFactory.Label("Текущее подключение", 13, true);
        title.Location = new Point(24, 20);
        _country = UiFactory.Label("—", 20, true);
        _country.Name = "CurrentCountry";
        _country.Location = new Point(24, 57);
        _country.AutoSize = false;
        _country.AutoEllipsis = true;
        _country.Size = new Size(292, 38);
        _connectedAge = UiFactory.Label("ещё не подключено", 9.5f, false, Palette.Muted);
        _connectedAge.Name = "ConnectionAge";
        _connectedAge.Location = new Point(26, 94);
        _connectedAge.AutoSize = false;
        _connectedAge.AutoEllipsis = true;
        _connectedAge.Size = new Size(290, 32);

        _latency = ValueBlock(card, "Задержка", "—", 330);
        _stability = ValueBlock(card, "Стабильность", "—", 500);
        _lastSwitch = ValueBlock(card, "Последняя смена", "—", 680);

        _switchReason = UiFactory.Label("Причина смены: —", 9, false, Palette.Muted);
        _switchReason.Name = "ConnectionSwitchReason";
        _switchReason.Location = new Point(26, 130);
        _switchReason.AutoSize = false;
        _switchReason.AutoEllipsis = true;
        _switchReason.Size = new Size(card.Width - 52, 34);
        card.Resize += (_, _) => _switchReason.Width = Math.Max(100, card.ClientSize.Width - 52);
        card.Controls.Add(title);
        card.Controls.Add(_country);
        card.Controls.Add(_connectedAge);
        card.Controls.Add(_switchReason);

        for (var index = 0; index < 3; index++)
        {
            var node = new RoundedPanel
            {
                Name = "NodeCard" + index,
                Width = 276,
                Height = 91,
                Radius = 12,
                Padding = new Padding(14),
                BackColor = index == 0 ? Palette.GreenSoft : Color.FromArgb(248, 250, 253),
                BorderColor = index == 0 ? Color.FromArgb(188, 229, 211) : Palette.Border,
                Location = new Point(24 + index * 294, 171)
            };
            _nodeRole[index] = UiFactory.Label(index == 0 ? "● ОСНОВНОЙ" : "○ РЕЗЕРВ " + index, 8.5f, true,
                index == 0 ? Palette.Green : Palette.Blue);
            _nodeRole[index].Location = new Point(14, 10);
            _nodeCountry[index] = UiFactory.Label("—", 11, true);
            _nodeCountry[index].Location = new Point(14, 34);
            _nodeDetail[index] = UiFactory.Label("не проверен", 8.5f, false, Palette.Muted);
            _nodeDetail[index].Location = new Point(14, 61);
            foreach (var label in new[] { _nodeRole[index], _nodeCountry[index], _nodeDetail[index] })
            {
                label.AutoSize = false;
                label.AutoEllipsis = true;
                label.Size = new Size(node.Width - 28, 22);
            }
            node.Controls.Add(_nodeRole[index]);
            node.Controls.Add(_nodeCountry[index]);
            node.Controls.Add(_nodeDetail[index]);
            card.Controls.Add(node);
        }
        void LayoutConnection()
        {
            int Px(int value) => (int)Math.Round(value * card.DeviceDpi / 96.0);
            var inner = card.ClientSize.Width - Px(48);
            var countryWidth = Math.Clamp(inner / 3, Px(220), Px(300));
            _country.Width = _connectedAge.Width = countryWidth - Px(16);
            var metrics = new[] { _latency, _stability, _lastSwitch };
            var metricWidth = Math.Max(Px(100), (inner - countryWidth) / metrics.Length);
            for (var i = 0; i < metrics.Length; i++)
            {
                var value = metrics[i];
                value.Left = Px(24) + countryWidth + i * metricWidth;
                value.Width = metricWidth - Px(10);
                if (value.Tag is Label caption)
                {
                    caption.Left = value.Left; caption.AutoSize = false; caption.AutoEllipsis = true;
                    caption.Size = new Size(value.Width, Px(22));
                }
            }
            var nodes = card.Controls.OfType<RoundedPanel>().OrderBy(node => node.Name).ToArray();
            var nodeWidth = (inner - Px(24)) / 3;
            for (var i = 0; i < nodes.Length; i++)
            {
                nodes[i].Left = Px(24) + i * (nodeWidth + Px(12));
                nodes[i].Top = Px(_switchReason.Visible ? 171 : 137);
                nodes[i].Width = nodeWidth;
                foreach (Control label in nodes[i].Controls) label.Width = nodeWidth - Px(28);
            }
            if (nodes.Length > 0) card.Height = nodes.Max(node => node.Bottom) + Px(24);
        }
        card.Resize += (_, _) => LayoutConnection();
        _switchReason.VisibleChanged += (_, _) => LayoutConnection();
        LayoutConnection();
        return card;
    }

    private RoundedPanel BuildWindowsConnectionCard()
    {
        var card = new RoundedPanel
        {
            Name = "WindowsConnectionCard",
            Width = 920,
            Height = 164,
            Margin = new Padding(0, 0, 0, 16),
            BackColor = Color.White
        };
        var title = UiFactory.Label("Режим VPN", 13, true);
        title.Location = new Point(24, 18);
        var description = UiFactory.Label(
            "Подключение Windows ещё не проверено.",
            9.3f, false, Palette.Muted);
        _windowsRoute = description;
        description.Name = "WindowsRouteStatus";
        description.Location = new Point(26, 50);
        description.MaximumSize = new Size(card.Width - 52, 42);
        var modes = new FlowLayoutPanel { Location = new Point(20, 80), Size = new Size(610, 46), WrapContents = false };
        foreach (var (mode, name, caption) in new[]
        {
            ("Auto", "AutoModeButton", "Авто"),
            ("ArtVpn", "UseArtVpnButton", "ART VPN"),
            ("Throne", "ThroneModeButton", "Throne"),
            ("Happ", "HappModeButton", "HAPP")
        })
        {
            var button = UiFactory.Button(name, caption, false, mode == "Auto" ? 90 : 112);
            button.AccessibleDescription = mode == "Auto" ? "Основной канал ART VPN; проверенный внешний резерв используется при сбое"
                : mode == "ArtVpn" ? "Встроенный канал ART VPN; внешний клиент не выбирается автоматически"
                : $"Проверить {ExternalProxyEndpoint.ForMode(mode)!.DisplayName} (HTTP {ExternalProxyEndpoint.ForMode(mode)!.Port}) и выбрать вручную; не возвращаться на ART VPN без вашего действия";
            button.Click += async (_, _) => await ExecuteAsync(token => _controller.SetModeAsync(mode, token),
                refreshAfter: true, TimeSpan.FromMinutes(3));
            _modeButtons.Add(mode, button);
            _actionButtons.Add(button);
            modes.Controls.Add(button);
        }
        var restore = UiFactory.Button("RestoreProxyButton", "Вернуть прежнее", false, 178);
        restore.AccessibleDescription = "Вернуть прежнее подключение. Если до ART работал поддерживаемый HAPP, включить его и проверить связь.";
        restore.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        restore.Location = new Point(card.Width - 202, 80);
        restore.Click += async (_, _) => await ExecuteAsync(
            token => _controller.RestoreSystemProxyAsync(token), refreshAfter: true, TimeSpan.FromMinutes(3));
        card.Resize += (_, _) =>
        {
            restore.Left = card.ClientSize.Width - restore.Width - 24;
            description.MaximumSize = new Size(card.ClientSize.Width - 52, 42);
            modes.Width = restore.Left - modes.Left - 12;
        };
        var hint = UiFactory.Label("Авто — основной ART VPN, внешний VPN — резерв.", 9, false, Palette.Muted);
        hint.Name = "WindowsModeHint";
        hint.Location = new Point(26, 138);
        hint.MaximumSize = new Size(690, 36);
        _actionButtons.Add(restore);
        card.Controls.Add(title);
        card.Controls.Add(description);
        card.Controls.Add(modes);
        card.Controls.Add(hint);
        card.Controls.Add(restore);
        return card;
    }

    private RoundedPanel BuildAutomationCard()
    {
        var card = new RoundedPanel
        {
            Name = "AutomationCard",
            Width = 920,
            Height = 232,
            Margin = new Padding(0, 0, 0, 16)
        };
        var title = UiFactory.Label("Автоматика и обновления", 13, true);
        title.Location = new Point(24, 20);
        card.Controls.Add(title);

        _subscription = TimelineRow(card, 58, "Подписка", "—");
        _subscriptionAt = TimelineRow(card, 91, "Последняя проверка", "—");
        _bypass = TimelineRow(card, 124, "Российские сайты напрямую", "—");
        _update = TimelineRow(card, 157, "Обновления ART VPN", "—");

        var refresh = UiFactory.Button("RefreshSubscriptionButton", "Проверить подписку", false, 178);
        refresh.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        refresh.Location = new Point(card.Width - 390, 176);
        refresh.Click += async (_, _) => await ExecuteAsync(token => _controller.RefreshSubscriptionAsync(token), refreshAfter: true);
        var updates = UiFactory.Button("CheckUpdatesButton", "Проверить обновления", false, 184);
        _updateButton = updates;
        updates.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        updates.Location = new Point(card.Width - 586, 176);
        updates.Click += async (_, _) =>
        {
            UiOperationResult? result = null;
            await ExecuteAsync(async token => result = await _controller.CheckUpdatesAsync(token), refreshAfter: true,
                operationTimeout: TimeSpan.FromSeconds(40));
            if (result?.Success == true && result.IncidentCode == "SignedUpdateAvailable")
                UpdateDownloadLink.Offer(this);
        };
        var diagnostics = UiFactory.Button("DiagnosticsButton", "Сообщить о проблеме", false, 190);
        diagnostics.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        diagnostics.Location = new Point(card.Width - 196, 176);
        diagnostics.Click += (_, _) => OpenDiagnostics();
        card.Resize += (_, _) =>
        {
            diagnostics.Left = card.ClientSize.Width - diagnostics.Width - 24;
            refresh.Left = diagnostics.Left - refresh.Width - 12;
            updates.Left = refresh.Left - updates.Width - 12;
        };
        _actionButtons.Add(updates);
        _actionButtons.Add(refresh);
        _actionButtons.Add(diagnostics);
        card.Controls.Add(updates);
        card.Controls.Add(refresh);
        card.Controls.Add(diagnostics);
        return card;
    }

    private void OpenDiagnostics()
    {
        if (_diagnosticsDialog is { IsDisposed: false })
        {
            _diagnosticsDialog.Show();
            _diagnosticsDialog.Activate();
            return;
        }
        _diagnosticsDialog = new DiagnosticsDialog(_controller);
        _diagnosticsDialog.FormClosed += (_, _) => _diagnosticsDialog = null;
        _diagnosticsDialog.Show(this);
    }

    private void OpenHelp()
    {
        if (_helpDialog is { IsDisposed: false })
        {
            _helpDialog.Show();
            _helpDialog.Activate();
            return;
        }
        _helpDialog = new HelpDialog(_openRecommendation);
        _helpDialog.FormClosed += (_, _) => _helpDialog = null;
        _helpDialog.Show(this);
    }

    private static Label ValueBlock(Control parent, string caption, string value, int left)
    {
        var key = UiFactory.Label(caption, 8.5f, false, Palette.Muted);
        key.Location = new Point(left, 54);
        var label = UiFactory.Label(value, 11, true);
        label.Tag = key;
        label.Location = new Point(left, 78);
        // Long telemetry/date text must not overlap the neighbouring metric.
        label.AutoSize = false;
        label.AutoEllipsis = true;
        label.Size = new Size(160, 46);
        parent.Controls.Add(key);
        parent.Controls.Add(label);
        return label;
    }

    private static Label TimelineRow(Control parent, int top, string caption, string value)
    {
        var dot = UiFactory.Label("●", 8, true, Palette.Green);
        dot.Location = new Point(26, top + 2);
        var key = UiFactory.Label(caption, 9.5f, true);
        key.Location = new Point(48, top);
        var label = UiFactory.Label(value, 9.3f, false, Palette.Muted);
        label.Location = new Point(250, top);
        label.AutoSize = false;
        label.AutoEllipsis = true;
        label.Size = new Size(Math.Max(100, parent.ClientSize.Width - label.Left - 24), 22);
        parent.Resize += (_, _) => label.Width = Math.Max(100, parent.ClientSize.Width - label.Left - 24);
        parent.Controls.Add(dot);
        parent.Controls.Add(key);
        parent.Controls.Add(label);
        return label;
    }

    private static Label CreatePill(string text, Point location, Color background, Color foreground) => new()
    {
        Text = text,
        AutoSize = false,
        Size = new Size(92, 30),
        Location = location,
        TextAlign = ContentAlignment.MiddleCenter,
        BackColor = background,
        ForeColor = foreground,
        Font = new Font("Segoe UI Semibold", 9)
    };

    private static Icon LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ARTVpn.UI.Assets.art-monitor.ico")
            ?? throw new InvalidOperationException("ART VPN icon is missing.");
        return new Icon(stream);
    }

    private static void ResizeCards(FlowLayoutPanel scroll)
    {
        if (scroll.Tag is true) return;
        scroll.Tag = true;
        try
        {
            // ClientSize already excludes the scrollbar. Batch widths so the
            // top-down flow does not retain a column from the previous size.
            var width = Math.Max(720, scroll.ClientSize.Width - scroll.Padding.Horizontal - 4);
            scroll.SuspendLayout();
            try { foreach (Control card in scroll.Controls) card.Width = width; }
            finally { scroll.ResumeLayout(performLayout: true); }
            scroll.PerformLayout();
        }
        finally { scroll.Tag = null; }
    }

    private async Task ExecuteAsync(
        Func<CancellationToken, Task<UiOperationResult>> action,
        bool refreshAfter,
        TimeSpan? operationTimeout = null)
    {
        if (_busy) return;
        _operationError = null;
        SetBusy(true, "Выполняется безопасная проверка…");
        try
        {
            using var timeout = new CancellationTokenSource(operationTimeout ?? TimeSpan.FromSeconds(20));
            var result = await action(timeout.Token);
            // Read actual state first; its generic footer must not erase the
            // command receipt, especially a lost acknowledgement after commit.
            if (refreshAfter) await RefreshStatusAsync(setBusy: false);
            _footer.Text = result.Success ? "Готово: " + result.Message : "Требуется внимание: " + result.Message;
            _footer.ForeColor = result.Success ? Palette.Green : Palette.Amber;
            if (!result.Success) _operationError = _footer.Text;
            if (result.Success && result.CodexRestartSuggested) ShowCodexRestartAdvice();
        }
        catch (Exception)
        {
            // An async click handler must not crash the console. No blind retry
            // or claimed rollback: the service may already have committed.
            if (refreshAfter) await RefreshStatusAsync(setBusy: false);
            _operationError = "Действие не подтверждено. Проверьте текущий канал перед повтором.";
            _footer.Text = _operationError;
            _footer.ForeColor = Palette.Amber;
        }
        finally { SetBusy(false, _footer.Text); }
    }

    private void ShowCodexRestartAdvice()
    {
        if (IsDisposed) return;
        // A deliberate user command may originate in the tray. Bring this
        // one-off notice into view; status polls must never reopen it.
        if (!Visible || WindowState == FormWindowState.Minimized) RestoreFromTray();
        try { _showCodexRestartAdvice(this, RouteChangeAdvisory.RestartMessage); }
        catch { /* An advisory failure must not relabel a committed VPN switch. */ }
    }

    internal async Task RefreshStatusAsync(bool setBusy = true)
    {
        if (_busy && setBusy) return;
        if (setBusy) SetBusy(true, "Читаем состояние без изменения сети…");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var state = await _controller.GetStatusAsync(timeout.Token);
            Render(state);
            _footer.Text = state.ServiceAvailable
                ? "VPN продолжает работать при закрытии окна."
                : "Служба пока недоступна. Нажмите «Настроить» — текущая сеть не изменится.";
            _footer.ForeColor = state.ServiceAvailable ? Palette.Green : Palette.Amber;
        }
        catch
        {
            _footer.Text = "Не удалось прочитать состояние. Проверьте текущий канал.";
            _footer.ForeColor = Palette.Amber;
            _activeChannel.Text = _trayActiveChannel.Text = "Сейчас: канал не подтверждён";
            _activeChannel.ForeColor = Palette.Amber;
            _selectedAt = null;
            _connectedAgeWithoutTimestamp = "Время подключения не подтверждено";
            UpdateAge();
        }
        finally
        {
            // Leave an unacknowledged error visible until the next user action.
            if (_operationError is { } error)
            {
                _footer.Text = error;
                _footer.ForeColor = Palette.Amber;
            }
            if (setBusy) SetBusy(false, _footer.Text);
        }
    }

    private void Render(VpnViewState state)
    {
        var modeCaption = state.ConfirmedRouteMode switch
        {
            "Auto" => "Авто", "ArtVpn" => "ART VPN", "Throne" => "Throne — вручную", "Happ" => "HAPP — вручную", _ => "не подтверждён"
        };
        _trayMode.Text = "Режим: " + modeCaption;
        _activeChannel.Text = _trayActiveChannel.Text = state.ActiveChannelVerified
            ? "Сейчас: " + (state.ActiveChannel is "Throne" or "HAPP" ? state.ActiveChannel + " — внешний VPN" : state.ActiveChannel)
            : "Сейчас: канал не подтверждён";
        _activeChannel.ForeColor = state.ActiveChannelVerified ? Palette.Ink : Palette.Amber;
        foreach (var (mode, button) in _modeButtons)
        {
            var selected = mode == state.ConfirmedRouteMode;
            button.BackColor = selected ? Palette.Blue : Color.White;
            button.ForeColor = selected ? Color.White : Palette.Ink;
            button.FlatAppearance.BorderColor = selected ? Palette.Blue : Palette.Border;
        }
        foreach (var (mode, item) in _trayModeButtons) item.Checked = mode == state.ConfirmedRouteMode;
        _details.SetToolTip(_windowsRoute, state.WindowsRouteStatus);
        _windowsRoute.Text = state.WindowsUsesArtVpn == true
            ? "Windows: ART VPN · 127.0.0.1:22080" : state.WindowsRouteStatus;
        var previousHealth = _lastHealth;
        _lastHealth = state.Health;
        _headline.Text = state.Headline;
        _details.SetToolTip(_headline, state.Explanation + "\n" + state.Quality);
        _explanation.Text = state.Health == VpnHealth.Healthy && state.ActiveChannelVerified && state.WindowsUsesArtVpn == true
            ? "Соединение проверено." : state.Explanation;
        _country.Text = state.Country;
        _selectedAt = state.SelectedAt;
        // An external client does not publish its node-selection timestamp.
        // Missing ART timing is not proof that a verified external VPN is off.
        _connectedAgeWithoutTimestamp = state.ActiveChannelVerified && state.ActiveChannel is "Throne" or "HAPP"
            ? "Внешний канал подключён" : "Время подключения не подтверждено";
        _mode.Text = modeCaption;
        _quality.Text = state.Quality;
        _latency.Text = state.Latency;
        _stability.Text = state.Stability;
        _lastSwitch.Text = state.LastSwitch;
        _switchReason.Text = "Причина смены: " + state.LastSwitchReason;
        _switchReason.Visible = state.Health != VpnHealth.Healthy;
        _details.SetToolTip(_country, "Причина смены: " + state.LastSwitchReason);
        _subscription.Text = state.Subscription;
        _subscriptionAt.Text = state.SubscriptionAt;
        _bypass.Text = state.Bypass;
        _update.Text = state.Update + "  •  версия " + state.Version;
        var updateAvailable = state.Update.StartsWith("доступна версия ", StringComparison.Ordinal);
        _updateButton.Text = updateAvailable ? "Скачать обновление" : "Проверить обновления";
        if (updateAvailable && _notifiedUpdate != state.Update)
        {
            _notifiedUpdate = state.Update;
            _tray.ShowBalloonTip(6000, "Доступно обновление ART VPN", "Откройте ART VPN и нажмите «Скачать обновление». Текущий VPN продолжает работать.", ToolTipIcon.Info);
        }
        for (var index = 0; index < _nodeRole.Length; index++)
        {
            var node = index < state.Nodes.Length ? state.Nodes[index] : null;
            _nodeRole[index].Text = node?.Role ?? (index == 0 ? "● ОСНОВНОЙ" : "○ РЕЗЕРВ " + index);
            _nodeCountry[index].Text = node?.Country ?? "—";
            _nodeDetail[index].Text = node is null ? (index > 0 ? "рабочий резерв пока не найден" : "не проверен") : node.Label + " • " + node.Quality;
        }

        var colors = state.Health switch
        {
            VpnHealth.Healthy => (Palette.GreenSoft, Palette.Green, Color.FromArgb(193, 232, 214), "✓"),
            VpnHealth.Checking => (Palette.BlueSoft, Palette.Blue, Color.FromArgb(197, 216, 250), "↻"),
            VpnHealth.Fallback => (Palette.AmberSoft, Palette.Amber, Color.FromArgb(240, 218, 170), "!"),
            _ => (Palette.RedSoft, Palette.Red, Color.FromArgb(239, 199, 203), "×")
        };
        if (state.Health == VpnHealth.Healthy && state.WindowsUsesArtVpn == false)
            colors = (Palette.BlueSoft, Palette.Blue, Color.FromArgb(197, 216, 250), "i");
        _hero.BackColor = colors.Item1;
        _hero.BorderColor = colors.Item3;
        var marker = _hero.Controls.Find("StatusMarker", false).OfType<Label>().Single();
        marker.ForeColor = colors.Item2;
        marker.Text = colors.Item4;
        _tray.Text = ("ART VPN — " + state.Headline + " — " + state.Country)[..Math.Min(63,
            ("ART VPN — " + state.Headline + " — " + state.Country).Length)];
        if (state.Health == VpnHealth.Unavailable && previousHealth.HasValue && previousHealth.Value != VpnHealth.Unavailable)
            _tray.ShowBalloonTip(7_000, "ART VPN: требуется внимание",
                state.Subscription.Length > 180 ? state.Subscription[..180] : state.Subscription,
                ToolTipIcon.Warning);
        UpdateAge();
        _hero.Invalidate();
    }

    private void UpdateAge()
    {
        if (_selectedAt is null)
        {
            _connectedAge.Text = _connectedAgeWithoutTimestamp;
            return;
        }
        var age = DateTimeOffset.Now - _selectedAt.Value.ToLocalTime();
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        _connectedAge.Text = $"Без смены {FormatAge(age)}";
        _details.SetToolTip(_connectedAge, $"Канал подключён {_selectedAt.Value.ToLocalTime():dd.MM.yyyy HH:mm}");
    }

    private static string FormatAge(TimeSpan age) => age.TotalDays >= 1
        ? $"{(int)age.TotalDays} д {age.Hours} ч"
        : age.TotalHours >= 1 ? $"{(int)age.TotalHours} ч {age.Minutes} мин" : $"{Math.Max(0, age.Minutes)} мин";

    private void SetBusy(bool value, string message)
    {
        _busy = value;
        foreach (var item in _trayModeButtons.Values) item.Enabled = !value;
        foreach (var button in _actionButtons) button.Enabled = !value;
        _footer.Text = message;
        UseWaitCursor = value;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowExit || e.CloseReason == CloseReason.WindowsShutDown) return;
        e.Cancel = true;
        HideToTray();
    }

    internal void HideToTray()
    {
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = false;
        Hide();
        _tray.ShowBalloonTip(4_000, "ART VPN продолжает работать",
            "Программа свернута к часам. Дважды нажмите значок ART VPN, чтобы открыть окно.", ToolTipIcon.Info);
    }

    internal void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ageTimer.Dispose();
            _statusTimer.Dispose();
            _details.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            Icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
