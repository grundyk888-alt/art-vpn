using System.Diagnostics;
using System.Security;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Ui;

internal sealed class FirstRunWizard : Form
{
    private readonly IArtVpnUiController _controller;
    private readonly IClientInstallerLauncher _clientInstaller;
    private readonly IClientDiscovery _clientDiscovery;
    private readonly ToolTip _clientLocations = new() { AutoPopDelay = 12_000, InitialDelay = 500 };
    private readonly Action<string>? _openRecommendation;
    private readonly Panel _body;
    private readonly Label _step;
    private readonly Label _message;
    private readonly Button _back;
    private readonly Button _next;
    private readonly List<Control> _pages = [];
    private TextBox? _providerFirst;
    private int _page;
    private bool _busy;
    private readonly List<Func<Task>> _clientSearches = [];
    private readonly CancellationTokenSource _lifetime = new();

    public FirstRunWizard(IArtVpnUiController controller, IClientInstallerLauncher? clientInstaller = null,
        Action<string>? openRecommendation = null, IClientDiscovery? clientDiscovery = null)
    {
        _controller = controller;
        _clientInstaller = clientInstaller ?? new ClientInstallerLauncher();
        _clientDiscovery = clientDiscovery ?? new DesktopClientDiscovery();
        _openRecommendation = openRecommendation;
        Name = "FirstRunWizard";
        Text = "Настройка ART VPN для ChatGPT, YouTube и других сервисов";
        Font = new Font("Segoe UI", 10);
        BackColor = Palette.Canvas;
        ForeColor = Palette.Ink;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(760, 650);
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = Color.White, Padding = new Padding(28, 18, 24, 10) };
        var title = UiFactory.Label("Настройка ART VPN для ChatGPT, YouTube и других сервисов", 16.5f, true);
        title.Location = new Point(28, 17);
        _step = UiFactory.Label("Шаг 1 из 5", 9.5f, false, Palette.Muted);
        _step.Location = new Point(30, 54);
        header.Controls.Add(title);
        header.Controls.Add(_step);
        Controls.Add(header);

        _body = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Canvas, Padding = new Padding(34, 26, 34, 20) };
        Controls.Add(_body);
        _body.BringToFront();

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 82, BackColor = Color.White, Padding = new Padding(26, 17, 26, 16) };
        _message = UiFactory.Label("Кнопка «Сохранить и подключить» проверит канал и включит «Авто».", 9, false, Palette.Muted);
        _message.Name = "WizardOperationMessage";
        _message.Location = new Point(28, 14);
        _message.AutoSize = false;
        _message.AutoEllipsis = true;
        _message.Size = new Size(424, 55);
        footer.Controls.Add(_message);
        _back = UiFactory.Button("WizardBackButton", "Назад", false, 112);
        _next = UiFactory.Button("WizardNextButton", "Далее", true, 150);
        _back.Location = new Point(496, 18);
        _next.Location = new Point(616, 18);
        _back.Click += (_, _) => ShowPage(Math.Max(0, _page - 1));
        _next.Click += async (_, _) => await OnNextAsync();
        footer.Resize += (_, _) =>
        {
            _next.Left = footer.ClientSize.Width - _next.Width - 26;
            _back.Left = _next.Left - _back.Width - 8;
            _message.Width = Math.Max(100, _back.Left - _message.Left - 12);
        };
        footer.Controls.Add(_back);
        footer.Controls.Add(_next);
        Controls.Add(footer);

        _pages.Add(WelcomePage());
        _pages.Add(ComputerPage());
        _pages.Add(SubscriptionPage());
        _pages.Add(ClientsPage());
        _pages.Add(ReadyPage());
        foreach (var page in _pages)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _body.Controls.Add(page);
        }
        ShowPage(0);
        Shown += (_, _) =>
        {
            // Скрытая кнопка другой страницы не получает PerformClick.
            // Автопоиск использует тот же обработчик напрямую, не имитируя клик.
            foreach (var search in _clientSearches) _ = search();
        };
    }

    internal int CurrentPage => _page;
    internal string CompletedMessage { get; private set; } = "";
    internal bool CompletedCodexRestartSuggested { get; private set; }

    private Control WelcomePage()
    {
        var panel = NewPage();
        AddHeading(panel, "Добро пожаловать", "ART VPN помогает сохранять устойчивый доступ к ChatGPT, OpenAI и Telegram.");
        AddBullet(panel, 130, "✓", "Сначала проверяет каналы, затем подключает лучший из проверенных.");
        AddBullet(panel, 184, "✓", "При обновлении подписки рабочий канал остаётся активным.");
        AddBullet(panel, 238, "✓", "Российские сайты, Avito и Ozon могут идти напрямую.");
        AddBullet(panel, 292, "✓", "Throne или HAPP можно использовать как внешний резерв.");
        panel.Controls.Add(ProductLinks.QuattroLink("WelcomeQuattroReferralLink",
            "Рекомендуем Quattro VPN: большой выбор стран", new Point(50, 354), _openRecommendation));
        return panel;
    }

    private Control ComputerPage()
    {
        var panel = NewPage();
        AddHeading(panel, "Проверка компьютера", "Эта проверка только читает параметры и ничего не устанавливает.");
        var results = new[]
        {
            ("Windows", Environment.OSVersion.Version.Build >= 17763 ? "поддерживается" : "требуется обновление"),
            ("Свободное место", "будет проверено установщиком"),
            ("Конфликт локальных портов", "будет проверен до подключения"),
            ("Throne / HAPP", "можно добавить на следующем шаге")
        };
        var top = 130;
        foreach (var item in results)
        {
            var row = new RoundedPanel { Location = new Point(0, top), Size = new Size(680, 48), Radius = 10, Padding = new Padding(14), BackColor = Color.White };
            var key = UiFactory.Label(item.Item1, 9.5f, true); key.Location = new Point(14, 13);
            var value = UiFactory.Label(item.Item2, 9.2f, false, Palette.Muted); value.Location = new Point(265, 13);
            row.Controls.Add(key); row.Controls.Add(value); panel.Controls.Add(row); top += 58;
        }
        return panel;
    }

    private Control SubscriptionPage()
    {
        var panel = NewPage();
        AddHeading(panel, "Подписка VPN", "Вставьте одну HTTPS‑ссылку подписки любого совместимого провайдера. Сейчас проверен формат VLESS Reality.");
        var label1 = UiFactory.Label("Ссылка подписки", 9.5f, true); label1.Location = new Point(0, 106);
        _providerFirst = ProviderBox("ProviderFirstBox", new Point(0, 135));
        var paste = UiFactory.Button("PasteProviderButton", "Вставить из буфера", false, 182);
        paste.Location = new Point(0, 186);
        paste.Click += (_, _) => PasteProviderFromClipboard();
        var how = UiFactory.Button("ProviderHelpButton", "Как взять ссылку", false, 170);
        how.Location = new Point(194, 186);
        how.Click += (_, _) => MessageBox.Show(this,
            "Если VPN уже работает в Throne или HAPP:\n\n1. Откройте в клиенте раздел «Подписки».\n2. Выберите действующую подписку.\n3. Нажмите «Копировать ссылку» или откройте её свойства и скопируйте исходный HTTPS‑адрес.\n4. Вернитесь сюда и нажмите «Вставить из буфера».\n\nART VPN не читает секретную базу другого клиента без вашего действия.",
            "Как получить ссылку подписки", MessageBoxButtons.OK, MessageBoxIcon.Information);
        var reveal = new CheckBox
        {
            Name = "RevealProviderCheckBox",
            Text = "Показать ссылку",
            AutoSize = true,
            Location = new Point(382, 198),
            ForeColor = Palette.Muted
        };
        reveal.CheckedChanged += (_, _) =>
        {
            if (_providerFirst is not null) _providerFirst.UseSystemPasswordChar = !reveal.Checked;
        };
        var privacy = UiFactory.Label("Ссылка шифруется Windows и не попадает в логи или отчёты. Неподдерживаемый формат будет отклонён без изменения текущей сети.", 9, false, Palette.Muted);
        privacy.Location = new Point(0, 242);
        privacy.MaximumSize = new Size(670, 0);
        var quattro = QuattroRecommendation.CreateCard("ProviderQuattroRecommendation", 680, _openRecommendation);
        quattro.Location = new Point(0, 298);
        panel.Controls.Add(label1); panel.Controls.Add(_providerFirst);
        panel.Controls.Add(paste); panel.Controls.Add(how); panel.Controls.Add(reveal); panel.Controls.Add(privacy); panel.Controls.Add(quattro);
        return panel;
    }

    private Control ClientsPage()
    {
        var panel = NewPage();
        AddHeading(panel, "Резервный клиент", "ART VPN работает сам. Throne или HAPP нужны только как внешний запасной канал, если основной контур недоступен.");
        var throne = ClientCard("Throne", "Искать Throne", 122,
            [@"C:\Program Files\Throne\Throne.exe", @"C:\Program Files\Throne\throne.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Throne", "Throne.exe"),
                @"C:\ProgramData\ART VPN\clients\Throne\1.2.4\Throne.exe"],
            canAcquire: true,
            officialPage: "https://github.com/throneproj/Throne/releases");
        var happ = ClientCard("HAPP", "Искать HAPP", 214,
            [@"C:\Program Files\Happ\Happ.exe", @"C:\Program Files\Happ Desktop\Happ.exe", @"C:\Program Files\FlyFrogLLC\Happ\Happ.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Happ", "Happ.exe")],
            canAcquire: true,
            officialPage: "https://github.com/Happ-proxy/happ-desktop/releases");
        panel.Controls.Add(throne); panel.Controls.Add(happ);
        var note = UiFactory.Label("Найденная программа ещё не означает работающий резерв. В клиенте добавьте подписку (если её ещё нет) и подключитесь. Затем выберите Throne или HAPP в главном окне ART VPN: сначала будет проверена связь. Успешный выбор запомнится как резерв для «Авто».", 9, false, Palette.Muted);
        note.MaximumSize = new Size(670, 0); note.Location = new Point(0, 310); panel.Controls.Add(note);
        var endpoints = UiFactory.Label("Для этой версии: Throne — HTTP 127.0.0.1:2080; HAPP — HTTP 127.0.0.1:10809. Если в клиенте другой порт, выбор не сработает: текущая сеть останется прежней. ART VPN обращается только к вашему компьютеру.", 9, false, Palette.Muted);
        endpoints.Name = "ExternalReserveEndpointGuide";
        endpoints.MaximumSize = new Size(670, 0); endpoints.Location = new Point(0, 385); panel.Controls.Add(endpoints);
        return panel;
    }

    private Control ReadyPage()
    {
        var panel = NewPage();
        AddHeading(panel, "Подключим автоматически", "Сохраним подписку, найдём проверенный канал и включим «Авто». Если сейчас работает другой VPN, сначала проверим ART VPN и только затем переключим подключение Windows.");
        AddBullet(panel, 150, "1", "Подписка шифруется для этой установки Windows.");
        AddBullet(panel, 208, "2", "Новые узлы проверяются отдельно от рабочего канала.");
        AddBullet(panel, 266, "3", "Если подходящий канал не найден, прежнее подключение сохраняется. Codex не закрывается внезапно; при необходимости предложим открыть его заново.");
        return panel;
    }

    private static Panel NewPage() => new() { BackColor = Palette.Canvas, Padding = Padding.Empty, AutoScroll = true };

    private static void AddHeading(Control panel, string titleText, string description)
    {
        var title = UiFactory.Label(titleText, 18, true); title.Location = new Point(0, 8);
        var body = UiFactory.Label(description, 10, false, Palette.Muted); body.Location = new Point(2, 55); body.MaximumSize = new Size(670, 0);
        panel.Controls.Add(title); panel.Controls.Add(body);
    }

    private static void AddBullet(Control panel, int top, string marker, string text)
    {
        var icon = new Label { Text = marker, Size = new Size(34, 34), Location = new Point(0, top), TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 11), BackColor = Palette.GreenSoft, ForeColor = Palette.Green };
        var label = UiFactory.Label(text, 10, false); label.Location = new Point(50, top + 7); label.MaximumSize = new Size(620, 0);
        panel.Controls.Add(icon); panel.Controls.Add(label);
    }

    private static TextBox ProviderBox(string name, Point location) => new()
    {
        Name = name,
        Location = location,
        Size = new Size(670, 42),
        UseSystemPasswordChar = true,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 11),
        AccessibleDescription = "Защищённое поле ссылки подписки"
    };

    private Control ClientCard(string clientName, string buttonText, int top, string[] knownPaths, bool canAcquire,
        string? officialPage = null)
    {
        var discoverySequence = 0;
        var card = new RoundedPanel { Location = new Point(0, top), Size = new Size(680, 84), Radius = 12, Padding = new Padding(16), BackColor = Color.White };
        var name = UiFactory.Label(clientName, 12, true); name.Location = new Point(16, 14);
        var state = UiFactory.Label("не найден", 9, false, Palette.Muted);
        state.Location = new Point(18, 51); state.Name = clientName + "State";
        state.AutoSize = false; state.Size = new Size(260, 23);
        var official = new LinkLabel
        {
            Name = "Official" + clientName + "Link",
            Text = "официальный сайт",
            AutoSize = true,
            Location = new Point(145, 16),
            LinkColor = Palette.Blue,
            ActiveLinkColor = Palette.Blue,
            Visible = officialPage is not null
        };
        official.LinkClicked += (_, _) =>
        {
            if (officialPage is not null) OpenOfficialPage(officialPage);
        };
        var progress = new ProgressBar
        {
            Name = clientName + "DownloadProgress",
            Location = new Point(288, 70),
            Size = new Size(362, 6),
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };
        var acquireText = clientName == "HAPP" ? "Установить HAPP" : "Скачать " + clientName;
        var acquire = UiFactory.Button("Acquire" + clientName + "Button", acquireText, false, 158);
        acquire.Location = new Point(288, 24);
        acquire.Enabled = canAcquire || officialPage is not null;
        acquire.AccessibleDescription = clientName == "HAPP"
            ? "Скачать проверенный официальный HAPP и установить. Windows может запросить разрешение; готовность резерва проверяется отдельно"
            : canAcquire
            ? "Скачать и проверить файлы; настройка подписки и проверка резерва выполняются отдельно"
            : "Открыть проверенную официальную страницу загрузки HAPP";
        acquire.Click += async (_, _) =>
        {
            if (_busy) return;
            // Поздний ответ поиска не должен затереть текущую загрузку.
            discoverySequence++;
            if (!canAcquire)
            {
                var opened = officialPage is not null && OpenOfficialPage(officialPage);
                state.Text = opened ? "открыта официальная страница загрузки" : "браузер не открылся — ссылка ниже";
                state.ForeColor = opened ? Palette.Blue : Palette.Amber;
                return;
            }
            _busy = true;
            ShowPage(_page);
            acquire.Enabled = false;
            progress.Visible = true;
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 25;
            var checkingInstallation = clientName == "HAPP";
            state.Text = checkingInstallation ? "проверяем установку…" : "скачиваем и проверяем…";
            state.ForeColor = Palette.Blue;
            _message.Text = checkingInstallation ? "Проверяем, установлен ли HAPP. Текущий VPN не меняется."
                : "Скачиваем " + clientName + " с официального сайта. Шкала движется, пока идёт загрузка и проверка…";
            try
            {
                if (checkingInstallation)
                {
                    using var preflight = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    preflight.CancelAfter(TimeSpan.FromSeconds(15));
                    var installed = await _clientInstaller.IsHappInstalledAsync(preflight.Token).WaitAsync(preflight.Token);
                    if (IsDisposed) return;
                    if (installed)
                    {
                        state.Text = "уже установлен • настройки сохранены";
                        state.ForeColor = Palette.Blue;
                        _message.Text = "HAPP уже установлен; повторная установка не нужна. Готовность резервного подключения ещё не подтверждена.";
                        _message.ForeColor = Palette.Blue;
                        progress.Visible = false;
                        return;
                    }
                    checkingInstallation = false;
                    state.Text = "скачиваем и проверяем…";
                    _message.Text = "Скачиваем HAPP с официального сайта. Шкала движется, пока идёт загрузка и проверка…";
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                var result = await _controller.AcquireClientAsync(clientName == "HAPP" ? "Happ" : clientName, timeout.Token);
                if (IsDisposed) return;
                _message.Text = result.Message;
                _message.ForeColor = result.Success ? Palette.Green : Palette.Amber;
                if (result.Success)
                {
                    if (clientName == "HAPP")
                    {
                        state.Text = "устанавливаем HAPP…";
                        _message.Text = "Установка HAPP. Если Windows попросит разрешение, подтвердите его в системном окне.";
                        using var installationTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        installationTimeout.CancelAfter(TimeSpan.FromMinutes(10));
                        result = await _clientInstaller.InstallHappAsync(installationTimeout.Token);
                        if (IsDisposed) return;
                        _message.Text = result.Message;
                        _message.ForeColor = result.Success ? Palette.Green : Palette.Amber;
                    }
                    var found = await _clientDiscovery.FindAsync(clientName, knownPaths);
                    if (IsDisposed) return;
                    SetClientLocation(state, found);
                    state.Text = !result.Success ? "установка не подтверждена • повторите поиск" : found.Found
                        ? "файлы готовы; резерв ещё не настроен"
                        : "скачан и проверен; повторите поиск";
                    state.ForeColor = found.Found && result.Success ? Palette.Green : Palette.Amber;
                    progress.Style = ProgressBarStyle.Continuous;
                    progress.Value = result.Success ? 100 : 0;
                }
                else
                {
                    progress.Style = ProgressBarStyle.Continuous;
                    progress.Value = 0;
                    state.Text = "не скачан • используйте официальный сайт";
                    state.ForeColor = Palette.Amber;
                }
            }
            catch (Exception)
            {
                if (IsDisposed) return;
                // A download/IPC failure must not terminate the async UI handler.
                progress.Style = ProgressBarStyle.Continuous;
                progress.Value = 0;
                state.Text = checkingInstallation ? "проверка установки не завершена" : "загрузка не подтверждена";
                state.ForeColor = Palette.Amber;
                _message.Text = checkingInstallation
                    ? "Проверка HAPP не завершилась. Ничего не скачивали и не устанавливали; повторите поиск."
                    : "Не удалось подтвердить загрузку. Повторите поиск или откройте официальный сайт.";
                _message.ForeColor = Palette.Amber;
            }
            finally
            {
                _busy = false;
                if (!IsDisposed)
                {
                    ShowPage(_page);
                    acquire.Enabled = canAcquire || officialPage is not null;
                }
            }
        };
        var find = UiFactory.Button("Find" + clientName + "Button", buttonText, false, 190); find.Location = new Point(460, 24);
        async Task FindClientAsync()
        {
            if (_busy) return;
            var sequence = ++discoverySequence;
            find.Enabled = false;
            state.Text = "ищем на этом компьютере…";
            state.ForeColor = Palette.Muted;
            try
            {
                var found = await _clientDiscovery.FindAsync(clientName, knownPaths);
                if (IsDisposed || sequence != discoverySequence) return;
                SetClientLocation(state, found);
                state.Text = found.IsRunning ? "запущен • канал ещё не проверен"
                    : found.Found ? "найден • резерв ещё не настроен"
                    : clientName == "HAPP" ? "не найден — нажмите «Установить HAPP»"
                    : "не найден — нажмите «Скачать Throne»";
                state.ForeColor = found.Found ? Palette.Blue : Palette.Amber;
            }
            catch
            {
                if (IsDisposed || sequence != discoverySequence) return;
                state.Text = "поиск не завершён • повторите";
                state.ForeColor = Palette.Amber;
            }
            finally { if (!IsDisposed) find.Enabled = true; }
        }
        find.Click += async (_, _) => await FindClientAsync();
        _clientSearches.Add(FindClientAsync);
        card.Controls.Add(name); card.Controls.Add(official); card.Controls.Add(state);
        card.Controls.Add(acquire); card.Controls.Add(find); card.Controls.Add(progress);
        return card;
    }

    private void SetClientLocation(Label state, ClientDiscoveryResult result)
    {
        var text = result.Found
            ? result.Product + ": " + result.ExecutablePath + "\n" + result.Source +
              (string.IsNullOrWhiteSpace(result.Version) ? "" : " • версия " + result.Version) +
              "\nЭто обнаружение программы, не проверка её VPN-подключения."
            : "Клиент не найден в текущем сеансе и локальных папках установки.";
        state.AccessibleDescription = text;
        _clientLocations.SetToolTip(state, text);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
            _clientLocations.Dispose();
        }
        base.Dispose(disposing);
    }

    private bool OpenOfficialPage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            _message.Text = "Не удалось открыть браузер. Официальная ссылка: " + url;
            _message.ForeColor = Palette.Amber;
            return false;
        }
    }

    private void PasteProviderFromClipboard()
    {
        if (_providerFirst is null) return;
        try
        {
            var value = Clipboard.ContainsText(TextDataFormat.UnicodeText)
                ? Clipboard.GetText(TextDataFormat.UnicodeText).Trim()
                : "";
            if (value.Length is < 12 or > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            {
                _message.Text = "В буфере нет подходящей защищённой HTTPS‑ссылки подписки.";
                _message.ForeColor = Palette.Amber;
                return;
            }
            _providerFirst.Text = value;
            _message.Text = "Ссылка вставлена. Проверьте и продолжайте.";
            _message.ForeColor = Palette.Green;
        }
        catch
        {
            _message.Text = "Не удалось прочитать буфер обмена. Вставьте ссылку вручную.";
            _message.ForeColor = Palette.Amber;
        }
    }

    private void ShowPage(int index)
    {
        if (IsDisposed) return;
        _page = Math.Clamp(index, 0, _pages.Count - 1);
        for (var item = 0; item < _pages.Count; item++) _pages[item].Visible = item == _page;
        _step.Text = $"Шаг {_page + 1} из {_pages.Count}";
        _back.Enabled = _page > 0 && !_busy;
        _next.Text = _page == _pages.Count - 1 ? "Сохранить и подключить" : "Далее";
        _next.Width = _page == _pages.Count - 1 ? 218 : 150;
        _next.Enabled = !_busy;
        _next.Parent?.PerformLayout();
    }

    private async Task OnNextAsync()
    {
        if (_busy) return;
        if (_page < _pages.Count - 1)
        {
            ShowPage(_page + 1);
            return;
        }
        if (_providerFirst is null || string.IsNullOrWhiteSpace(_providerFirst.Text))
        {
            _message.Text = "Вернитесь к шагу 3 и вставьте ссылку подписки.";
            _message.ForeColor = Palette.Amber;
            ShowPage(2);
            return;
        }

        using var first = ToSecureString(_providerFirst);
        using var second = ToSecureString(_providerFirst);
        _providerFirst.Clear();
        _busy = true;
        ShowPage(_page);
        _message.Text = "Сохраняем подписку, проверяем каналы и подключаем «Авто»…";
        _message.ForeColor = Palette.Blue;
        var started = Stopwatch.StartNew();
        using var progress = new System.Windows.Forms.Timer { Interval = 1000 };
        progress.Tick += (_, _) =>
        {
            if (!IsDisposed) _message.Text = $"Проверяем каналы и подключаем «Авто»… {started.Elapsed:mm\\:ss}. Дождитесь результата; повторно нажимать не нужно.";
        };
        progress.Start();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(8));
            var result = await _controller.ConfigureProviderAsync(first, second, timeout.Token);
            progress.Stop();
            if (IsDisposed) return;
            _message.Text = result.Message;
            _message.ForeColor = result.Success ? Palette.Green : Palette.Amber;
            if (result.Success)
            {
                // This result now means Windows is connected, not just saved.
                CompletedMessage = result.Message;
                CompletedCodexRestartSuggested = result.CodexRestartSuggested;
                DialogResult = DialogResult.OK;
                Close();
            }
        }
        catch (Exception)
        {
            if (IsDisposed) return;
            _message.Text = "Сохранение не подтверждено. Проверьте состояние подписки перед повторным вводом.";
            _message.ForeColor = Palette.Amber;
        }
        finally
        {
            progress.Stop();
            _busy = false;
            ShowPage(_page);
        }
    }

    private static SecureString ToSecureString(TextBox box)
    {
        var secure = new SecureString();
        foreach (var character in box.Text) secure.AppendChar(character);
        secure.MakeReadOnly();
        return secure;
    }
}
