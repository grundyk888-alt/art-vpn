using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ArtSport.ArtVpn.Common;
using Microsoft.Win32;

namespace ArtSport.ArtVpn.Setup;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            if (args.Length == 2 && args[0] == "--setup-connection-test")
            {
                AtomicJson.Replace(args[1], new { status = "Passed", checks = SetupConnectionTests.Run(phase =>
                    AtomicJson.Replace(args[1] + ".progress.json", new { phase, atUtc = DateTimeOffset.UtcNow, systemChanged = false })),
                    systemChanged = false, actualInstallation = false, secretDisplayed = false });
                return 0;
            }
            if (args.Length == 1 && args[0] == "--diagnostics-test")
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new { diagnostics = InstallationDiagnostics.Test(),
                    startupRepair = StartupRepairPolicy.Test(), status = "Passed", systemChanged = false, secretDisplayed = false }, JsonOptions.Output));
                return 0;
            }
            if (args.Length == 2 && args[0] == "--diagnostics-report")
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var report = await InstallationDiagnostics.CollectAsync(null, timeout.Token);
                AtomicJson.Replace(args[1], report);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--diagnostics")
            {
                using var diagnostics = new InstallationDiagnosticsForm(null, beforeInstall: false);
                Application.Run(diagnostics);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--repair-owner")
            {
                var repair = await InstallTransaction.RepairStartupAsync(args[1], CancellationToken.None);
                Console.Out.WriteLine(JsonSerializer.Serialize(repair, JsonOptions.Output));
                return repair.DetailCode == "StartupRepairVerified" ? 0 : 2;
            }
            if (args.Length == 3 && args[0] == "--visual-qa")
                return SetupFormQa.Run(args[1], args[2]);
            if (args.Length == 2 && args[0] == "--embedded-qa")
            {
                using var embeddedQaPayload = SetupPayload.Open();
                var result = PackageVerifier.Verify(embeddedQaPayload.PayloadRoot);
                AtomicJson.Replace(args[1], new
                {
                    status = "Passed",
                    files = result.Manifest.Files.Length,
                    result.Manifest.Version,
                    result.ManifestSha256,
                    embedded = true,
                    systemChanged = false,
                    secretDisplayed = false
                });
                return 0;
            }
            if (args.Length == 3 && args[0] == "--qa")
            {
                var result = PackageVerifier.Verify(args[1]);
                var shortcutContract = ShortcutManager.RunContractTest();
                var ownerBindingContract = InstallTransaction.RunOwnerBindingContractTest();
                var systemProxyLease = SystemProxyLeaseTests.Run();
                AtomicJson.Replace(args[2], new
                {
                    status = "Passed", files = result.Manifest.Files.Length, result.Manifest.Version,
                    manifestSha256 = result.ManifestSha256, shortcutContract, ownerBindingContract,
                    serviceStartContract = InstallTransaction.RunServiceStartContractTest(),
                    nativeUiHostBinding = ArtSport.Vpn.Shared.NativeUiHostBinding.Test(),
                    errorMessageContract = ErrorCode.RunContractTest(),
                    repeatInstallContract = ExistingVersionPolicy.Test(),
                    damagedInstallRemovalContract = RemovalPayloadVerifier.Test(),
                    serviceStopContract = NativeServiceState.Test(),
                    upgradeFileHandoff = await UpgradeFileHandoff.TestAsync(),
                    serviceListenerOwnership = OwnedListeners.Test(),
                    installationDiagnostics = InstallationDiagnostics.Test(),
                    startupRepair = StartupRepairPolicy.Test(),
                    systemProxyLeaseContract = true, systemProxyLease,
                    systemChanged = false, secretDisplayed = false
                });
                return 0;
            }
            if (args.Length == 1 && args[0] == "--install")
            {
                using var installPayload = SetupPayload.Open();
                var result = await InstallTransaction.InstallAsync(
                    installPayload.PayloadRoot, null, CancellationToken.None).ConfigureAwait(false);
                Console.Out.WriteLine(JsonSerializer.Serialize(result, JsonOptions.Output));
                return result.Status == "Installed" ? 0 : 2;
            }
            if (args.Length == 2 && args[0] is "--install-owner" or "--integrated-owner")
            {
                using var ownerPayload = SetupPayload.Open();
                var result = await InstallTransaction.InstallAsync(
                    ownerPayload.PayloadRoot, args[1], CancellationToken.None,
                    integrated: args[0] == "--integrated-owner").ConfigureAwait(false);
                Console.Out.WriteLine(JsonSerializer.Serialize(result, JsonOptions.Output));
                return result.Status == "Installed" ? 0 : 2;
            }
            if (args.Length == 4 && args[0] == "--update-owner")
            {
                // Update is not onboarding: keep the subscription and current
                // selected channel, and use the existing rollback transaction.
                using var updatePayload = SetupPayload.Open();
                var package = PackageVerifier.Verify(updatePayload.PayloadRoot);
                if (!File.Exists(@"C:\ProgramData\ART VPN\state\install.v1.json") ||
                    package.Manifest.Version != args[2] || package.ManifestSha256 != args[3])
                    throw new InvalidDataException("UpdatePayloadBindingRejected");
                var result = await InstallTransaction.InstallAsync(updatePayload.PayloadRoot, args[1], CancellationToken.None);
                return result.Status == "Installed" ? 0 : 2;
            }
            if (args.Length == 1 && args[0] == "--uninstall")
                return await UninstallCoordinator.BeginAsync().ConfigureAwait(false);
            if (args.Length == 2 && args[0] == "--uninstall-worker" && int.TryParse(args[1], out var parentPid))
                return await UninstallCoordinator.RunWorkerAsync(parentPid).ConfigureAwait(false);
            if (args.Length != 0) return 25;
            using var interactivePayload = SetupPayload.Open();
            using var form = new SetupForm(interactivePayload.PayloadRoot);
            Application.Run(form);
            return form.Succeeded ? 0 : 1;
        }
        catch (Exception ex)
        {
            InstallFailureDiagnostics.Record(ex);
            if (Environment.UserInteractive && args.Length > 0 && args[0].StartsWith("--uninstall", StringComparison.Ordinal))
                MessageBox.Show(ErrorCode.Describe(ex), "ART VPN — удаление не завершено", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (args.Length > 0)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { status = "Failed", code = ErrorCode.From(ex),
                    phase = ex.Data["ARTVpnPhase"] as string, errorClass = ex.GetType().Name,
                    hresult = $"0x{ex.HResult:X8}", secretDisplayed = false }, JsonOptions.Output));
            else MessageBox.Show(ErrorCode.Describe(ex), "ART VPN — установка остановлена",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }
}

internal sealed class SetupPayload : IDisposable
{
    private const string ResourceName = "ARTVpn.Payload.zip";
    private readonly string? _temporaryRoot;
    public string PayloadRoot { get; }

    private SetupPayload(string payloadRoot, string? temporaryRoot)
    {
        PayloadRoot = payloadRoot;
        _temporaryRoot = temporaryRoot;
    }

    public static SetupPayload Open()
    {
        var adjacent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "payload"));
        if (File.Exists(Path.Combine(adjacent, "manifest.v1.json")))
            return new SetupPayload(adjacent, null);

        var assembly = Assembly.GetExecutingAssembly();
        using var resource = assembly.GetManifestResourceStream(ResourceName)
                             ?? throw new InvalidOperationException("EmbeddedPayloadMissing");
        var parent = Path.Combine(Path.GetTempPath(), "ART-VPN-Setup");
        var session = Path.Combine(parent, "session-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(session, "payload");
        Directory.CreateDirectory(payload);
        try
        {
            using var archive = new ZipArchive(resource, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count is < 8 or > 64) throw new InvalidDataException("EmbeddedPayloadShapeRejected");
            long total = 0;
            var prefix = Path.GetFullPath(payload).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("..", StringComparison.Ordinal) ||
                    Path.IsPathFullyQualified(normalized) || normalized.IndexOf(':') >= 0)
                    throw new InvalidDataException("EmbeddedPayloadEntryRejected");
                var destination = Path.GetFullPath(Path.Combine(payload, normalized));
                if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("EmbeddedPayloadEntryRejected");
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                total = checked(total + entry.Length);
                if (total > 500_000_000) throw new InvalidDataException("EmbeddedPayloadSizeRejected");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.WriteThrough);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            _ = PackageVerifier.Verify(payload);
            return new SetupPayload(payload, session);
        }
        catch
        {
            DeleteSession(session, parent);
            throw;
        }
    }

    public void Dispose()
    {
        if (_temporaryRoot is not null)
            DeleteSession(_temporaryRoot, Path.Combine(Path.GetTempPath(), "ART-VPN-Setup"));
    }

    private static void DeleteSession(string session, string parent)
    {
        var resolved = Path.GetFullPath(session).TrimEnd(Path.DirectorySeparatorChar);
        var prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(resolved), "^session-[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("EmbeddedPayloadCleanupRejected");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}

internal sealed class SetupForm : Form
{
    private readonly string _payload;
    private readonly string _ownerSid;
    private readonly Button _install;
    private readonly Label _status;
    private readonly TextBox _subscription;
    private readonly CheckBox _later;
    private readonly ISetupWorkflow _workflow;
    private readonly Button _diagnose;
    private readonly ProgressBar _progress;
    private readonly Label _elapsed;
    private readonly Panel _entryPage;
    private readonly TableLayoutPanel _processPage;
    private readonly Label _processTitle;
    private readonly Label _entryExplanation;
    private readonly Label[] _steps = new Label[4];
    private readonly Button _back;
    private int _activeStep;
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 250 };
    private readonly Stopwatch _operationTime = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _installed;
    private bool _connected;
    private bool _readyToOpen;
    internal string ConnectionCode { get; private set; } = "NotAttempted";
    public bool Succeeded { get; private set; }

    public SetupForm(string payload, Action<string>? openRecommendation = null, ISetupWorkflow? workflow = null)
    {
        _payload = payload;
        _workflow = workflow ?? new SetupWorkflow();
        _ownerSid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("OwnerSidUnavailable");
        Text = "Установка ART VPN для ChatGPT, YouTube и других сервисов";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(246, 248, 252);
        ForeColor = Color.FromArgb(20, 35, 58);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(650, 710);
        // Действие установки всегда доступно, даже если Windows ограничила
        // высоту окна на небольшом экране или при увеличенном масштабе.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
            Padding = Padding.Empty, Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 174));
        var content = _entryPage = new Panel { Name = "SetupEntryPage", Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var pageHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _processPage = new TableLayoutPanel { Name = "SetupProcessPage", Dock = DockStyle.Fill, Visible = false,
            AutoScroll = true, Padding = new Padding(40, 24, 40, 18), ColumnCount = 1, RowCount = 6 };
        _processPage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 84, 72, 72, 72, 72, 94 }) _processPage.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        _processTitle = new Label { Name = "SetupProcessTitle", Text = "Устанавливаем ART VPN", Font = new Font("Segoe UI Semibold", 22), Dock = DockStyle.Fill };
        _processPage.Controls.Add(_processTitle, 0, 0);
        for (var i = 0; i < _steps.Length; i++)
        {
            _steps[i] = new Label { Name = "SetupProcessStep" + i, Dock = DockStyle.Fill, AutoSize = false,
                Font = new Font("Segoe UI", 11), Padding = new Padding(12, 8, 8, 4), Margin = new Padding(0, 0, 0, 8), BackColor = Color.White };
            _processPage.Controls.Add(_steps[i], 0, i + 1);
        }
        _processPage.Controls.Add(new Label { Name = "SetupProcessNote", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(85, 98, 120), Text = "Проверка каналов может занять несколько минут. Здесь виден ход работы, а выбранная страна появится в ART VPN после подтверждения подключения." }, 0, 5);
        // TableLayoutPanel не всегда включает последнюю абсолютную строку
        // в область прокрутки. Считаем высоту из уже масштабированных строк.
        _processPage.Layout += (_, _) =>
        {
            var height = (int)Math.Ceiling(_processPage.RowStyles.Cast<RowStyle>().Sum(row => row.Height)) + _processPage.Padding.Vertical;
            if (_processPage.AutoScrollMinSize.Height != height)
                _processPage.AutoScrollMinSize = new Size(0, height);
        };
        pageHost.Controls.Add(content); pageHost.Controls.Add(_processPage);
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1,
            Margin = Padding.Empty, Padding = new Padding(40, 10, 40, 18)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(pageHost, 0, 0);
        layout.Controls.Add(footer, 0, 1);
        var title = new Label { Text = "ART VPN", Font = new Font("Segoe UI Semibold", 24), AutoSize = true, Location = new Point(42, 35) };
        var subtitle = new Label
        {
            Text = "Устойчивый доступ к ChatGPT, OpenAI и Telegram",
            Font = new Font("Segoe UI", 11), ForeColor = Color.FromArgb(85, 98, 120), AutoSize = true, Location = new Point(45, 86)
        };
        var quattro = SetupRecommendationCard.Create(openRecommendation);
        quattro.Location = new Point(40, 128);
        var card = new Panel { Name = "SetupSubscriptionCard", BackColor = Color.White, Location = new Point(40, 292), Size = new Size(570, 226), Padding = new Padding(22) };
        card.Controls.Add(new Label { Name = "SetupSubscriptionTitle", Text = "Вставьте ссылку подписки VPN", Font = new Font("Segoe UI Semibold", 12), AutoSize = true, Location = new Point(22, 18) });
        card.Controls.Add(new Label { Name = "SetupSubscriptionHint", Text = "Скопируйте ссылку из личного кабинета вашего VPN.",
            AutoSize = true, Font = new Font("Segoe UI", 9), Location = new Point(24, 47), ForeColor = Color.FromArgb(85, 98, 120) });
        _subscription = new TextBox { Name = "SetupSubscriptionBox", Location = new Point(24, 72), Size = new Size(520, 32),
            UseSystemPasswordChar = true, MaxLength = 8192, ShortcutsEnabled = true,
            AccessibleName = "Ссылка подписки VPN", AccessibleDescription = "Вставьте HTTPS-ссылку подписки. Содержимое скрыто для безопасности.", TabIndex = 0 };
        _later = new CheckBox { Name = "SetupConfigureLater", Text = "Установить без подключения — настрою позже", AutoSize = true,
            Location = new Point(24, 116), TabIndex = 1 };
        card.Controls.Add(_subscription); card.Controls.Add(_later);
        _entryExplanation = new Label
        {
            Name = "SetupConnectionExplanation",
            Text = "Проверим компьютер, установим VPN и подключим «Авто».\nСсылка шифруется Windows и не попадает в отчёты.",
            Font = new Font("Segoe UI", 9.5f), AutoSize = true, Location = new Point(24, 158), ForeColor = Color.FromArgb(56, 70, 94)
        };
        card.Controls.Add(_entryExplanation);
        _status = new Label { Name = "SetupStatus", Text = "Всё в один шаг. Диагностика пройдёт автоматически; если потребуется ваше действие, покажем, что нужно исправить.", AutoSize = false,
            Dock = DockStyle.Fill, Margin = new Padding(5, 0, 0, 0),
            ForeColor = Color.FromArgb(85, 98, 120) };
        var activity = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2, Margin = Padding.Empty };
        activity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        activity.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
        _progress = new ProgressBar { Name = "SetupProgress", Dock = DockStyle.Fill, Margin = new Padding(5, 7, 12, 9),
            Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Visible = false,
            AccessibleName = "Выполняется установка ART VPN" };
        _elapsed = new Label { Name = "SetupElapsed", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.FromArgb(85, 98, 120), Visible = false };
        activity.Controls.Add(_progress, 0, 0); activity.Controls.Add(_elapsed, 1, 0);
        _progressTimer.Tick += (_, _) => _elapsed.Text = "Прошло " + _operationTime.Elapsed.ToString(@"mm\:ss");
        _install = new Button { Name = "InstallButton", Text = "Установить", Size = new Size(226, 48), Margin = Padding.Empty,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(48, 94, 157), ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 10), Cursor = Cursors.Hand };
        _install.FlatAppearance.BorderSize = 0;
        _install.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 80, 137);
        _later.CheckedChanged += (_, _) => { _subscription.Enabled = !_later.Checked; RefreshEntryAction(); };
        _subscription.TextChanged += (_, _) => RefreshEntryAction();
        _install.Click += async (_, _) => await InstallAsync();
        _diagnose = new Button { Name = "InstallationDiagnosticsButton", Text = "Диагностика перед установкой",
            AutoSize = true, Anchor = AnchorStyles.Left, Height = 40, FlatStyle = FlatStyle.Flat,
            BackColor = Color.White, ForeColor = Color.FromArgb(70, 86, 108), Cursor = Cursors.Hand };
        _diagnose.FlatAppearance.BorderColor = Color.FromArgb(220, 227, 237);
        _diagnose.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 241, 249);
        _diagnose.Click += (_, _) =>
        {
            using var dialog = new InstallationDiagnosticsForm(_payload, beforeInstall: false);
            dialog.ShowDialog(this);
        };
        _back = new Button { Name = "SetupBackButton", Text = "Назад", AutoSize = true, Height = 40,
            FlatStyle = FlatStyle.Flat, BackColor = Color.White, Visible = false, Margin = new Padding(0, 0, 8, 0) };
        _back.FlatAppearance.BorderColor = Color.FromArgb(220, 227, 237);
        _back.Click += (_, _) =>
        {
            if (_busy) return;
            _processPage.Visible = false; _entryPage.Visible = true; _entryPage.BringToFront(); _back.Visible = false;
            _diagnose.Text = "Диагностика перед установкой";
            _elapsed.Visible = false; RefreshEntryAction();
            _status.Text = _installed ? "Программа уже установлена. Можно исправить подписку и повторить только подключение." : "Измените параметры и продолжите установку.";
            _status.ForeColor = Color.FromArgb(85, 98, 120);
            if (_subscription.Enabled) _subscription.Focus();
        };
        try
        {
            var existing = JsonSerializer.Deserialize<InstalledState>(File.ReadAllText(@"C:\ProgramData\ART VPN\state\install.v1.json"),JsonOptions.Strict);
            var offered = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(Path.Combine(payload,"manifest.v1.json")),JsonOptions.Strict);
            if(existing?.OwnerSid == _ownerSid && existing?.Version == offered?.Version)
            {
                _status.Text = "Эта версия уже установлена. Можно подключить подписку или открыть программу без изменения сети.";
                _later.Checked = true;
            }
        }
        catch (Exception e) when(e is IOException or UnauthorizedAccessException or JsonException) { }
        content.Controls.AddRange([title, subtitle, quattro, card]);
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(activity, 0, 1);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 226));
        var secondary = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 0) };
        secondary.Controls.Add(_back); secondary.Controls.Add(_diagnose);
        actions.Controls.Add(secondary, 0, 0);
        actions.Controls.Add(_install, 1, 0);
        footer.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        RefreshEntryAction();
        Shown += (_, _) => { if (_subscription.Enabled) _subscription.Focus(); };
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    private bool WillConnect => !_later.Checked && !string.IsNullOrWhiteSpace(_subscription.Text);

    private void RefreshEntryAction()
    {
        if (_busy) return;
        _install.Text = _readyToOpen ? "Открыть ART VPN" : _installed
            ? (WillConnect ? "Подключить" : "Открыть ART VPN")
            : "Установить";
        _entryExplanation.Text = WillConnect
            ? "Проверим компьютер, установим VPN и подключим «Авто».\nСсылка шифруется Windows и не попадает в отчёты."
            : "Без подписки установим программу без подключения VPN.\nНастроить подключение можно позже.";
    }

    private void SetStep(int index, string detail, bool complete = false, bool active = false)
    {
        var title = new[] { "Проверка компьютера", "Установка программы", "Подписка и каналы", "Подключение «Авто»" }[index];
        _steps[index].Text = (complete ? "✓ " : (index + 1) + ". ") + title + "\n" + detail;
        _steps[index].ForeColor = complete ? Color.FromArgb(25, 111, 77) : active ? Color.FromArgb(48, 94, 157) : Color.FromArgb(85, 98, 120);
        if (active) _activeStep = index;
    }

    private async Task InstallAsync()
    {
        if (_busy) return;
        // Ошибка запуска окна после commit не должна повторять установку/подключение.
        if (_readyToOpen) { OpenCompletedUi(); return; }
        if (WillConnect && !SetupConnectionText.Valid(_subscription.Text))
        {
            _status.Text = "Вставьте HTTPS-ссылку подписки или выберите «настрою позже».";
            _status.ForeColor = Color.DarkRed; _subscription.Focus(); return;
        }
        using var subscription = new System.Security.SecureString();
        if (WillConnect) foreach (var c in _subscription.Text.Trim()) subscription.AppendChar(c);
        subscription.MakeReadOnly();
        var connect = WillConnect;
        _busy = true;
        _entryPage.Visible = false; _processPage.Visible = true; _processPage.BringToFront(); _back.Visible = false;
        _activeStep = 0;
        _processTitle.Text = _installed && connect ? "Подключаем ART VPN" : "Устанавливаем ART VPN";
        SetStep(0, _installed ? "Проверено ранее" : "Проверяем файлы, место и настройки…", complete: _installed, active: !_installed);
        SetStep(1, _installed ? "Уже установлена — повтор не нужен" : "После проверки компьютера", complete: _installed);
        SetStep(2, connect ? "После установки" : "Пропускаем — без подписки");
        SetStep(3, connect ? "Ожидаем подтверждённого результата" : "Не подключаем — настроите позже");
        _operationTime.Restart(); _elapsed.Text = "Прошло 00:00";
        _progress.Visible = _elapsed.Visible = true;
        _progress.MarqueeAnimationSpeed = 25; _progressTimer.Start();
        _install.Enabled = _subscription.Enabled = _later.Enabled = _diagnose.Enabled = false;
        _diagnose.Visible = false;
        _install.Text = "Подождите…";
        _status.Text = "Шаг 1. Проверяем файлы, свободное место и сеть…";
        _status.ForeColor = Color.FromArgb(85, 98, 120);
        string? failureForDiagnostics = null;
        try
        {
            if (!_installed)
            {
                if (!await _workflow.DiagnoseAsync(this, _payload, _lifetime.Token))
                {
                    SetStep(0, "Проверка не разрешила продолжение"); _processTitle.Text = "Требуется ваше решение";
                    _status.Text = "Установка не началась. Проверьте замечания диагностики."; return;
                }
                SetStep(0, "Проверка завершена", complete: true);
                SetStep(1, "Устанавливаем файлы и службу…", active: true);
                _processTitle.Text = "Устанавливаем программу";
                _status.Text = "Шаг 2. Устанавливаем файлы и службу ART VPN. Если Windows запросит разрешение, подтвердите его…";
                _installed = await _workflow.InstallAsync(_payload, _ownerSid);
                if (!_installed) { SetStep(1, "Установка не завершена"); _processTitle.Text = "Установка остановлена"; _status.Text = "Установка не завершена. Сеть не переключалась."; failureForDiagnostics = _status.Text; return; }
            }
            SetStep(1, "Установка подтверждена", complete: true);
            Succeeded = true;
            if (connect)
            {
                _processTitle.Text = "Проверяем подключение";
                SetStep(2, "Проверяем подписку и доступные каналы…", active: true);
                SetStep(3, "Подключение после проверки каналов; ждём результат");
                _status.Text = "Шаг 3. Проверяем подписку и каналы, подключаем «Авто». Проверка может занять несколько минут…";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(8));
                var result = await _workflow.ConnectAsync(subscription, timeout.Token);
                ConnectionCode = result.DetailCode;
                if (!result.Success)
                {
                    SetStep(2, "Проверка и подключение не подтверждены");
                    SetStep(3, "Нужно проверить результат перед повтором"); _processTitle.Text = "Подключение не подтверждено";
                    _status.Text = SetupConnectionText.Failure(result.DetailCode);
                    failureForDiagnostics = _status.Text;
                    _status.ForeColor = Color.DarkRed; _install.Text = "Повторить"; return;
                }
                SetStep(2, "Рабочий канал проверен", complete: true);
                SetStep(3, "Режим «Авто» подтверждён", complete: true); _processTitle.Text = "ART VPN подключён";
                _connected = true;
                _readyToOpen = true;
                _subscription.Clear();
                _status.Text = "ART VPN подключён, режим «Авто» включён.";
                StopProgress();
                string advisory;
                try { advisory = await _workflow.CompleteConnectionAsync(); }
                catch { advisory = "VPN подключён. Если Codex был открыт, сохраните работу и переоткройте его."; }
                // Вспомогательный диалог не отменяет уже подтверждённое подключение.
                if (advisory.Length > 0)
                    try { _workflow.ShowAdvisory(this, advisory); }
                    catch { _status.Text = "ART VPN подключён. Если Codex был открыт, сохраните работу и переоткройте его вручную."; }
            }
            else
            {
                _readyToOpen = true;
                _processTitle.Text = "Программа установлена";
                _status.Text = "Без подключения VPN. Настроить подписку можно в программе позже.";
                StopProgress();
            }
            OpenCompletedUi();
        }
        catch (Exception ex)
        {
            SetStep(_activeStep, "Не удалось завершить этап"); _processTitle.Text = "Требуется ваше действие";
            _status.Text = ErrorCode.Describe(ex);
            failureForDiagnostics = _status.Text;
            _status.ForeColor = Color.DarkRed;
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                StopProgress();
                _diagnose.Visible = true;
                _diagnose.Text = "Диагностика";
                _back.Visible = !_readyToOpen;
                if (_install.Text == "Подождите…") _install.Text = "Повторить";
                _install.Enabled = _later.Enabled = _diagnose.Enabled = true; _subscription.Enabled = !_later.Checked;
                // Один показ на неудачную попытку. Успех и уже показанная
                // блокирующая preflight-диагностика не открывают второе окно.
                if (failureForDiagnostics is not null)
                {
                    try { _workflow.ShowFailureDiagnostics(this, _payload, failureForDiagnostics); }
                    catch { _status.Text = failureForDiagnostics + " Нажмите «Диагностика», чтобы повторить проверку."; }
                }
            }
        }
    }

    private void StopProgress()
    {
        _progressTimer.Stop(); _operationTime.Stop(); _progress.MarqueeAnimationSpeed = 0;
        _progress.Visible = false; _elapsed.Visible = true;
        _elapsed.Text = "Прошло " + _operationTime.Elapsed.ToString(@"mm\:ss");
    }

    private void OpenCompletedUi()
    {
        try { _workflow.OpenUi(); _busy = false; Close(); }
        catch
        {
            _processTitle.Text = _connected ? "ART VPN подключён" : "Программа установлена";
            _status.Text = (_connected ? "Подключение сохранено. " : "Установка завершена. ") +
                "Не удалось открыть окно ART VPN. Нажмите «Открыть ART VPN» для повторной попытки.";
            _status.ForeColor = Color.FromArgb(136, 90, 20);
            _install.Text = "Открыть ART VPN";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed) { _progressTimer.Stop(); _progressTimer.Dispose(); _lifetime.Cancel(); _lifetime.Dispose(); _subscription?.Clear(); }
        base.Dispose(disposing);
    }
}

internal sealed record PackageFile(string Path, long Bytes, string Sha256, string Role);
internal sealed record PackageManifest(int Schema, string Kind, string Product, string Version, int MinimumWindowsBuild,
    PackageFile[] Files, bool ContainsProviderSecret);
internal sealed record VerifiedPackage(string PayloadRoot, PackageManifest Manifest, string ManifestSha256);

internal static class PackageVerifier
{
    public static VerifiedPackage Verify(string payloadRoot)
    {
        var root = Path.GetFullPath(payloadRoot).TrimEnd(Path.DirectorySeparatorChar);
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists || rootInfo.LinkTarget is not null)
            throw new InvalidDataException("PackageRootRejected");
        foreach (var directory in rootInfo.EnumerateDirectories("*", SearchOption.AllDirectories))
            if (directory.LinkTarget is not null) throw new InvalidDataException("PackageDirectoryRejected");
        var manifestPath = Path.Combine(root, "manifest.v1.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("PackageManifestMissing");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        try
        {
            var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestBytes, JsonOptions.Strict)
                           ?? throw new InvalidDataException("PackageManifestRejected");
            if (manifest.Schema != 1 || manifest.Kind != "art-vpn-consumer-package" || manifest.Product != "ART VPN" ||
                !Regex.IsMatch(manifest.Version, "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-beta\\.[0-9]+)?$") ||
                manifest.MinimumWindowsBuild < 17763 || manifest.ContainsProviderSecret || manifest.Files.Length < 8 ||
                manifest.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length)
                throw new InvalidDataException("PackageManifestRejected");
            var prefix = root + Path.DirectorySeparatorChar;
            foreach (var item in manifest.Files)
            {
                if (item.Path.Contains("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(item.Path) ||
                    item.Path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || item.Bytes < 1 ||
                    !Regex.IsMatch(item.Sha256, "^[A-F0-9]{64}$") || item.Role.Length is < 1 or > 32)
                    throw new InvalidDataException("PackageEntryRejected");
                var path = Path.GetFullPath(Path.Combine(root, item.Path));
                var info = new FileInfo(path);
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !info.Exists || info.LinkTarget is not null ||
                    info.Length != item.Bytes || Hash(path) != item.Sha256)
                    throw new InvalidDataException("PackageFileRejected");
            }
            var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(path, manifestPath, StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var declared = manifest.Files.Select(file => file.Path.Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!actual.SequenceEqual(declared, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("PackageFileSetRejected");
            return new VerifiedPackage(root, manifest, Convert.ToHexString(SHA256.HashData(manifestBytes)));
        }
        finally { Array.Clear(manifestBytes); }
    }

    private static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
}

internal sealed record InstallReceipt(string Status, string DetailCode, string Version, string InstalledAtUtc,
    bool SystemProxyChanged, bool RebootRequired, bool ContainsProviderSecret);

internal static partial class InstallTransaction
{
    private const string InstallRoot = @"C:\Program Files\ART VPN";
    private const string DataRoot = @"C:\ProgramData\ART VPN";
    private const string MaintenanceRoot = @"C:\ProgramData\ART VPN Maintenance";
    private const string MaintenanceExe = @"C:\ProgramData\ART VPN Maintenance\ARTVpn.Maintenance.exe";
    private const string ServiceName = "ARTVpnService";
    private const string ServiceStartMode = "auto";
    private const string RunValue = "ART VPN";
    internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ART VPN";

    public static async Task<InstallReceipt> InstallAsync(
        string payloadRoot,
        string? requestedOwnerSid,
        CancellationToken cancellationToken,
        bool integrated = false)
    {
        EnsureAdministrator();
        var ownerSid = ResolveOwnerSid(requestedOwnerSid, requireLoadedHive: true);
        var package = PackageVerifier.Verify(payloadRoot);
        if (!Environment.Is64BitOperatingSystem || Environment.OSVersion.Version.Build < package.Manifest.MinimumWindowsBuild)
            throw new InvalidOperationException("WindowsVersionUnsupported");
        var existingParts = new[] { Directory.Exists(InstallRoot), Directory.Exists(DataRoot), ServiceExists() };
        if (existingParts.Any(value => value))
        {
            if (!existingParts.All(value => value))
                throw new InvalidOperationException("ExistingInstallIncomplete");
            if (integrated && !File.Exists(Path.Combine(DataRoot,"state",ArtSport.Vpn.Shared.NativeUiHostBinding.FileName)))
                throw new InvalidOperationException("ExplicitIntegratedAdoptionRequired");
            return await UpgradeAsync(package, ownerSid, cancellationToken).ConfigureAwait(false);
        }
        EnsurePortsFree([22080,22090,22086,22096,22087,22097,22088]);
        var drive = new DriveInfo(Path.GetPathRoot(InstallRoot)!);
        if (drive.AvailableFreeSpace < 1_000_000_000) throw new InvalidOperationException("FreeSpaceInsufficient");
        ArtSport.Vpn.Shared.MicrosoftAuthProxyCompatibility.Ensure();

        var stage = InstallRoot + ".stage-" + Guid.NewGuid().ToString("N");
        var serviceCreated = false;
        var runCreated = false;
        var shortcutsCreated = false;
        var installStep = "StagePayload";
        try
        {
            Directory.CreateDirectory(stage);
            foreach (var item in package.Manifest.Files)
            {
                var source = Path.Combine(package.PayloadRoot, item.Path.Replace('/', Path.DirectorySeparatorChar));
                var destination = Path.Combine(stage, item.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
            }
            File.Copy(Path.Combine(package.PayloadRoot, "manifest.v1.json"), Path.Combine(stage, "manifest.v1.json"),
                overwrite: false);
            Directory.Move(stage, InstallRoot);
            installStep = "ExternalMaintenance";
            InstallExternalMaintenance(Path.Combine(InstallRoot, "runtime", "ARTVpn.Maintenance.exe"));
            installStep = "State";
            Directory.CreateDirectory(Path.Combine(DataRoot, "state"));
            var install = new
            {
                schema = 1, kind = "art-vpn-consumer-install", status = "Configured",
                computerName = Environment.MachineName, machineSid = MachineSid.Read(), ownerSid,
                installId = Guid.NewGuid().ToString("N"), version = package.Manifest.Version,
                manifestSha256 = package.ManifestSha256, installedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                systemProxyManaged = false, containsProviderSecret = false
            };
            AtomicJson.WriteNew(Path.Combine(DataRoot, "state", "install.v1.json"), install);
            if (integrated)
            {
                var installJson = File.ReadAllText(Path.Combine(DataRoot,"state","install.v1.json"));
                var binding = ArtSport.Vpn.Shared.NativeUiHostBinding.Create(installJson);
                _ = ArtSport.Vpn.Shared.NativeUiHostBinding.IsIntegrated(binding, installJson, Environment.MachineName, ownerSid);
                File.WriteAllText(Path.Combine(DataRoot,"state",ArtSport.Vpn.Shared.NativeUiHostBinding.FileName),binding);
            }
            installStep = "ServiceCreate";
            var serviceExe = Path.Combine(InstallRoot, "runtime", "ARTVpn.Service.exe");
            RunSc("create", ServiceName, "binPath=", $"\"{serviceExe}\" --service", "start=", ServiceStartMode,
                "DisplayName=", "ART VPN — защищённая служба");
            serviceCreated = true;
            RunSc("description", ServiceName, "Устойчивый контур ART VPN с безопасным A/B-переключением");
            RunSc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/30000");
            RunSc("failureflag", ServiceName, "1");
            NormalizeServiceStartMode();
            installStep = "OwnerStartup";
            if (!integrated)
            {
              using (var run = Registry.Users.CreateSubKey(ownerSid + @"\Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
                             ?? throw new InvalidOperationException("StartupRegistryUnavailable"))
                run.SetValue(RunValue, $"\"{Path.Combine(InstallRoot, "runtime", "ARTVpn.UI.exe")}\" --start-in-tray",
                    RegistryValueKind.String);
              runCreated = true;
              installStep = "Shortcuts";
              ShortcutManager.CreateInstalledShortcuts(Path.Combine(InstallRoot, "runtime", "ARTVpn.UI.exe"));
              shortcutsCreated = true;
            }
            installStep = "UninstallRegistration";
            using (var uninstall = Registry.LocalMachine.CreateSubKey(UninstallKey, writable: true)
                                   ?? throw new InvalidOperationException("UninstallRegistryUnavailable"))
            {
                uninstall.SetValue("DisplayName", "ART VPN для ChatGPT, YouTube и других сервисов", RegistryValueKind.String);
                uninstall.SetValue("DisplayVersion", package.Manifest.Version, RegistryValueKind.String);
                uninstall.SetValue("Publisher", "ARTSPORT", RegistryValueKind.String);
                uninstall.SetValue("InstallLocation", InstallRoot, RegistryValueKind.String);
                uninstall.SetValue("DisplayIcon", Path.Combine(InstallRoot, "runtime", "ARTVpn.UI.exe"), RegistryValueKind.String);
                uninstall.SetValue("UninstallString", $"\"{MaintenanceExe}\" --uninstall",
                    RegistryValueKind.String);
                uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
                uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            installStep = "ServiceStart";
            RunSc("start", ServiceName);
            await WaitServiceAndPortsAsync(cancellationToken).ConfigureAwait(false);
            installStep = "Receipt";
            var receipt = new InstallReceipt("Installed", "CleanInstallAccepted", package.Manifest.Version,
                DateTimeOffset.UtcNow.ToString("o"), false, false, false);
            AtomicJson.WriteNew(Path.Combine(DataRoot, "state", "install-receipt.v1.json"), receipt);
            return receipt;
        }
        catch (Exception ex)
        {
            if (serviceCreated)
            {
                try { RunSc("stop", ServiceName); } catch { }
                try { RunSc("delete", ServiceName); } catch { }
            }
            if (runCreated)
                try { using var run=Registry.Users.OpenSubKey(ownerSid + @"\Software\Microsoft\Windows\CurrentVersion\Run",true);run?.DeleteValue(RunValue,false); } catch { }
            if (shortcutsCreated)
                try { ShortcutManager.RemoveInstalledShortcuts(); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
            DeleteExactCreatedDirectory(stage, InstallRoot + ".stage-");
            DeleteExactCreatedDirectory(InstallRoot, InstallRoot);
            DeleteExactCreatedDirectory(DataRoot, DataRoot);
            DeleteExternalMaintenance();
            throw new InvalidOperationException("CleanInstall" + installStep + "Failed", ex);
        }
    }

    private static async Task<InstallReceipt> UpgradeAsync(
        VerifiedPackage package,
        string ownerSid,
        CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(DataRoot, "state", "install.v1.json");
        var oldState = JsonSerializer.Deserialize<InstalledState>(File.ReadAllText(statePath), JsonOptions.Strict)
                       ?? throw new InvalidDataException("InstallStateRejected");
        var hostBindingPath = Path.Combine(DataRoot,"state",ArtSport.Vpn.Shared.NativeUiHostBinding.FileName);
        var integrated = ArtSport.Vpn.Shared.NativeUiHostBinding.IsIntegrated(
            File.Exists(hostBindingPath) ? File.ReadAllText(hostBindingPath) : null,
            File.ReadAllText(statePath), Environment.MachineName, ownerSid);
        if (oldState.Schema != 1 || oldState.Kind != "art-vpn-consumer-install" || oldState.Status != "Configured" ||
            oldState.ComputerName != Environment.MachineName || oldState.MachineSid != MachineSid.Read() ||
            oldState.OwnerSid != ownerSid || oldState.ContainsProviderSecret || oldState.SystemProxyManaged)
            throw new InvalidDataException("InstallStateRejected");
        var oldPackage = PackageVerifier.Verify(InstallRoot);
        if (oldPackage.ManifestSha256 != oldState.ManifestSha256 || oldPackage.Manifest.Version != oldState.Version)
            throw new InvalidDataException("InstalledPayloadChanged");
        var decision = ExistingVersionPolicy.Decide(oldState.Version, package.Manifest.Version,
            oldPackage.ManifestSha256, package.ManifestSha256);
        // Repair before stopping the current service; repeat installs are idempotent.
        ArtSport.Vpn.Shared.MicrosoftAuthProxyCompatibility.Ensure();
        if (decision != "UpgradeRequired")
            return new InstallReceipt("Installed", decision, oldState.Version, oldState.InstalledAtUtc, false, false, false);

        var proxyBefore = ProxySnapshot.Read();
        var serviceStartBefore = ReadServiceStartMode();
        var token = Guid.NewGuid().ToString("N");
        var stage = InstallRoot + ".stage-" + token;
        var backup = InstallRoot + ".backup-" + token;
        var oldMoved = false;
        var newMoved = false;
        var serviceTouched = false;
        var wasRunning = !NativeServiceState.IsStopped(NativeServiceState.Read(ServiceName));
        var phase = "StageUpgradePayload";
        try
        {
            Directory.CreateDirectory(stage);
            foreach (var item in package.Manifest.Files)
            {
                var source = Path.Combine(package.PayloadRoot, item.Path.Replace('/', Path.DirectorySeparatorChar));
                var destination = Path.Combine(stage, item.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
            }
            File.Copy(Path.Combine(package.PayloadRoot, "manifest.v1.json"), Path.Combine(stage, "manifest.v1.json"),
                overwrite: false);
            _ = PackageVerifier.Verify(stage);

            phase = "StopOldRuntime";
            StopOwnedUiForUpgrade();
            UpdateSelectionReceipt.TryCapture(DataRoot, oldState.Version, package.Manifest.Version);
            serviceTouched = true;
            try { RunSc("stop", ServiceName); } catch { }
            await WaitServiceStoppedAsync(cancellationToken).ConfigureAwait(false);
            phase = "MoveOldRuntimeToBackup";
            await UpgradeFileHandoff.MoveAsync(InstallRoot, backup, cancellationToken).ConfigureAwait(false);
            oldMoved = true;
            phase = "MoveNewRuntimeIntoPlace";
            Directory.Move(stage, InstallRoot);
            newMoved = true;
            var upgradedState = oldState with
            {
                Version = package.Manifest.Version,
                ManifestSha256 = package.ManifestSha256,
                InstalledAtUtc = DateTimeOffset.UtcNow.ToString("o")
            };
            phase = "CommitInstallState";
            AtomicJson.Replace(statePath, upgradedState);
            NormalizeServiceStartMode();
            phase = "StartNewRuntime";
            RunSc("start", ServiceName);
            await WaitServiceAndPortsAsync(cancellationToken).ConfigureAwait(false);
            phase = "UpdateWindowsIntegration";
            using (var uninstall = Registry.LocalMachine.OpenSubKey(UninstallKey, writable: true)
                                   ?? throw new InvalidOperationException("UninstallRegistryUnavailable"))
                uninstall.SetValue("DisplayVersion", package.Manifest.Version, RegistryValueKind.String);
            if (!integrated)
            {
              using (var run = Registry.Users.CreateSubKey(ownerSid + @"\Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
                             ?? throw new InvalidOperationException("StartupRegistryUnavailable"))
                run.SetValue(RunValue, $"\"{Path.Combine(InstallRoot, "runtime", "ARTVpn.UI.exe")}\" --start-in-tray",
                    RegistryValueKind.String);
              ShortcutManager.CreateInstalledShortcuts(Path.Combine(InstallRoot, "runtime", "ARTVpn.UI.exe"));
            }
            var receipt = new InstallReceipt("Installed", "InPlaceUpgradeAccepted", package.Manifest.Version,
                DateTimeOffset.UtcNow.ToString("o"), false, false, false);
            AtomicJson.Replace(Path.Combine(DataRoot, "state", "install-receipt.v1.json"), receipt);
            if (!proxyBefore.Equals(ProxySnapshot.Read())) throw new InvalidOperationException("SystemProxyChangedDuringUpgrade");
            InstallExternalMaintenance(Path.Combine(InstallRoot, "runtime", "ARTVpn.Maintenance.exe"));
            using (var uninstall = Registry.LocalMachine.OpenSubKey(UninstallKey, writable: true)
                                   ?? throw new InvalidOperationException("UninstallRegistryUnavailable"))
                uninstall.SetValue("UninstallString", $"\"{MaintenanceExe}\" --uninstall", RegistryValueKind.String);
            DeleteUpgradeDirectory(backup, "backup", token);
            return receipt;
        }
        catch (Exception failure)
        {
            failure.Data["ARTVpnPhase"] = phase;
            if (serviceTouched)
            {
                try { RunSc("stop", ServiceName); } catch { }
                try { await WaitServiceStoppedAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            if (newMoved && Directory.Exists(InstallRoot)) DeleteUpgradeDirectory(InstallRoot, "installed", token);
            if (oldMoved && Directory.Exists(backup) && !Directory.Exists(InstallRoot)) Directory.Move(backup, InstallRoot);
            if (oldMoved)
            {
                try { AtomicJson.Replace(statePath, oldState); } catch { }
                try { RestoreServiceStartMode(serviceStartBefore); } catch { }
                try
                {
                    using var uninstall = Registry.LocalMachine.OpenSubKey(UninstallKey, writable: true);
                    uninstall?.SetValue("DisplayVersion", oldState.Version, RegistryValueKind.String);
                }
                catch { }
            }
            // A failed directory rename occurs before oldMoved becomes true.
            // It still stopped the old service: rollback must restore that too.
            if (serviceTouched && wasRunning)
            {
                try
                {
                    if (NativeServiceState.IsStopped(NativeServiceState.Read(ServiceName))) RunSc("start", ServiceName);
                    await WaitServiceAndPortsAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackFailure)
                {
                    rollbackFailure.Data["ARTVpnPhase"] = "RestoreOldRuntimeAfterUpgradeFailure";
                    InstallFailureDiagnostics.Record(rollbackFailure);
                    failure.Data["ARTVpnRollbackFailed"] = true;
                }
            }
            if (Directory.Exists(stage)) DeleteUpgradeDirectory(stage, "stage", token);
            if (!proxyBefore.Equals(ProxySnapshot.Read()))
                throw new InvalidOperationException("SystemProxyChangedDuringUpgradeRollback");
            throw;
        }
    }

    private static void StopOwnedUiForUpgrade()
    {
        var expected = Path.GetFullPath(Path.Combine(InstallRoot, "runtime", "ARTVpn.UI.exe"));
        foreach (var process in Process.GetProcessesByName("ARTVpn.UI"))
        {
            using (process)
            {
                string? actual = null;
                try { actual = process.MainModule?.FileName; } catch { }
                if (!string.Equals(actual is null ? null : Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { process.CloseMainWindow(); } catch { }
                try
                {
                    if (!process.WaitForExit(3_000)) process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(5_000)) throw new InvalidOperationException("OwnedVpnProcessStillRunning");
                }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
        }
    }

    private static async Task WaitServiceStoppedAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var consecutiveStopped = 0;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // SCM may announce Stopped before the last job-owned core releases its
            // working directory. UI Kill is asynchronous too: wait for every owned
            // runtime, not just the service, before renaming the installation.
            if (NativeServiceState.IsStopped(NativeServiceState.Read(ServiceName)) && !OwnedRuntimeProcessAlive())
            {
                if (++consecutiveStopped >= 2) return;
            }
            else consecutiveStopped = 0;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("InstalledServiceDidNotStop");
    }

    private static bool OwnedRuntimeProcessAlive()
    {
        foreach (var name in new[] { "ARTVpn.Service", "ARTVpnCore", "ARTVpn.UI" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (process.HasExited) continue;
                var expected = Path.Combine(InstallRoot, "runtime", name + ".exe");
                try { if (string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) return true; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { return true; } // fail closed when identity cannot be read
            }
        }
        return false;
    }

    private static void DeleteUpgradeDirectory(string path, string kind, string token)
    {
        var resolved = Path.GetFullPath(path);
        var allowed = kind switch
        {
            "stage" => InstallRoot + ".stage-" + token,
            "backup" => InstallRoot + ".backup-" + token,
            "installed" => InstallRoot,
            _ => throw new InvalidOperationException("UpgradeCleanupKindRejected")
        };
        if (!string.Equals(resolved, Path.GetFullPath(allowed), StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(token, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("UpgradeCleanupPathRejected");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }

    private static async Task WaitServiceAndPortsAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ServiceRunning() && OwnedListeners.Match(Path.Combine(InstallRoot, "runtime", "ARTVpn.Service.exe"), 22080, 22090)) return;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("InstalledServiceNotReady");
    }

    public static bool IsAdministrator()
    {
        using var identity=WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static bool RunOwnerBindingContractTest()
    {
        var current = WindowsIdentity.GetCurrent().User?.Value ?? "";
        if (!IsAcceptedOwnerSid(current)) return false;
        foreach (var rejected in new[] { "", "S-1-5-18", "S-1-5-21-1-2-3-4\\Software", "S-1-5-21-1-2-3-4\0" })
            if (IsAcceptedOwnerSid(rejected)) return false;
        return true;
    }

    internal static bool RunServiceStartContractTest() =>
        ServiceStartMode == "auto" && NormalizedDelayedAutoStart == 0 &&
        !ServiceStartMode.Contains("delayed", StringComparison.OrdinalIgnoreCase);

    private const int NormalizedDelayedAutoStart = 0;

    private sealed record ServiceStartSnapshot(int Start, int? DelayedAutoStart);

    private static ServiceStartSnapshot ReadServiceStartMode()
    {
        using var service = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\" + ServiceName, writable: false)
            ?? throw new InvalidOperationException("ServiceRegistryUnavailable");
        var start = service.GetValue("Start") is int startValue
            ? startValue
            : throw new InvalidDataException("ServiceStartModeRejected");
        int? delayed = service.GetValue("DelayedAutoStart") is int delayedValue ? delayedValue : null;
        return new ServiceStartSnapshot(start, delayed);
    }

    private static void NormalizeServiceStartMode()
    {
        RunSc("config", ServiceName, "start=", ServiceStartMode);
        using var service = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\" + ServiceName, writable: true)
            ?? throw new InvalidOperationException("ServiceRegistryUnavailable");
        service.SetValue("DelayedAutoStart", NormalizedDelayedAutoStart, RegistryValueKind.DWord);
        var actual = ReadServiceStartMode();
        if (actual.Start != 2 || actual.DelayedAutoStart != NormalizedDelayedAutoStart)
            throw new InvalidOperationException("ServiceStartModeNormalizationRejected");
    }

    private static void RestoreServiceStartMode(ServiceStartSnapshot snapshot)
    {
        using var service = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\" + ServiceName, writable: true)
            ?? throw new InvalidOperationException("ServiceRegistryUnavailable");
        service.SetValue("Start", snapshot.Start, RegistryValueKind.DWord);
        if (snapshot.DelayedAutoStart.HasValue)
            service.SetValue("DelayedAutoStart", snapshot.DelayedAutoStart.Value, RegistryValueKind.DWord);
        else
            service.DeleteValue("DelayedAutoStart", throwOnMissingValue: false);
    }

    private static string ResolveOwnerSid(string? requested, bool requireLoadedHive)
    {
        var sid = string.IsNullOrWhiteSpace(requested)
            ? WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("OwnerSidUnavailable")
            : requested;
        if (!IsAcceptedOwnerSid(sid)) throw new InvalidDataException("OwnerSidRejected");
        if (requireLoadedHive)
        {
            using var hive = Registry.Users.OpenSubKey(sid, writable: false);
            if (hive is null) throw new InvalidOperationException("OwnerProfileNotLoaded");
        }
        return sid;
    }

    private static bool IsAcceptedOwnerSid(string sid) =>
        Regex.IsMatch(sid, "^S-1-5-21-(?:[0-9]+-){3}[0-9]+$", RegexOptions.CultureInvariant);

    private static void EnsureAdministrator()
    {
        if (!IsAdministrator())
            throw new UnauthorizedAccessException("AdministratorRequired");
    }

    private static void EnsurePortsFree(IEnumerable<int> ports)
    {
        var listeners=System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(item=>IPAddress.IsLoopback(item.Address)).Select(item=>item.Port).ToHashSet();
        if(ports.Any(listeners.Contains))throw new InvalidOperationException("RequiredLocalPortBusy");
    }

    private static bool IsListening(int port)=>System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
        .GetActiveTcpListeners().Any(item=>IPAddress.IsLoopback(item.Address)&&item.Port==port);

    private static bool ServiceExists()=>NativeServiceState.Read(ServiceName) != NativeServiceState.Missing;
    private static bool ServiceRunning()=>NativeServiceState.Read(ServiceName) == NativeServiceState.Running;

    private static void RunSc(params string[] arguments)
    {
        var result=RunScQuery(arguments);if(result.ExitCode!=0)throw new InvalidOperationException("ServiceControlRejected");
    }

    private static (int ExitCode,string Output) RunScQuery(params string[] arguments)
    {
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"sc.exe")){UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments)info.ArgumentList.Add(argument);
        using var process=Process.Start(info)??throw new InvalidOperationException("ServiceControlUnavailable");
        var output=process.StandardOutput.ReadToEnd();_ = process.StandardError.ReadToEnd();process.WaitForExit();
        return(process.ExitCode,output);
    }

    private static void DeleteExactCreatedDirectory(string path,string expectedPrefix)
    {
        var resolved=Path.GetFullPath(path);var prefix=Path.GetFullPath(expectedPrefix);
        if((resolved.Equals(prefix,StringComparison.OrdinalIgnoreCase)||resolved.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))&&
           (resolved.Equals(InstallRoot,StringComparison.OrdinalIgnoreCase)||resolved.Equals(DataRoot,StringComparison.OrdinalIgnoreCase)||
            Regex.IsMatch(Path.GetFileName(resolved),"^ART VPN\\.stage-[a-f0-9]{32}$",RegexOptions.CultureInvariant))&&Directory.Exists(resolved))
            Directory.Delete(resolved,true);
    }

    private static void InstallExternalMaintenance(string source)
    {
        var exactSource = Path.GetFullPath(source);
        if (!string.Equals(exactSource, Path.Combine(InstallRoot, "runtime", "ARTVpn.Maintenance.exe"),
                StringComparison.OrdinalIgnoreCase) || !File.Exists(exactSource))
            throw new InvalidDataException("MaintenanceSourceRejected");
        Directory.CreateDirectory(MaintenanceRoot);
        var temporary = Path.Combine(MaintenanceRoot, "maintenance-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(exactSource, temporary, overwrite: false);
            using (var sourceStream = File.OpenRead(exactSource))
            using (var copyStream = File.OpenRead(temporary))
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(sourceStream), SHA256.HashData(copyStream)))
                    throw new InvalidDataException("MaintenanceCopyRejected");
            File.Move(temporary, MaintenanceExe, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void DeleteExternalMaintenance()
    {
        var resolved = Path.GetFullPath(MaintenanceRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(resolved, @"C:\ProgramData\ART VPN Maintenance", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MaintenanceCleanupPathRejected");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}

internal sealed record InstalledState(
    int Schema,
    string Kind,
    string Status,
    string ComputerName,
    string MachineSid,
    string OwnerSid,
    string InstallId,
    string Version,
    string ManifestSha256,
    string InstalledAtUtc,
    bool SystemProxyManaged,
    bool ContainsProviderSecret);

internal static class UninstallCoordinator
{
    private const string InstallRoot = @"C:\Program Files\ART VPN";
    private const string WorkerRoot = @"C:\ProgramData\ART VPN Uninstall";
    private const string MaintenanceRoot = @"C:\ProgramData\ART VPN Maintenance";

    public static async Task<int> BeginAsync()
    {
        if (!InstallTransaction.IsAdministrator())
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("InstallerPathUnavailable");
            using var elevated = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--uninstall",
                WorkingDirectory = AppContext.BaseDirectory
            }) ?? throw new InvalidOperationException("ElevationStartRejected");
            await elevated.WaitForExitAsync().ConfigureAwait(false);
            return elevated.ExitCode;
        }

        var current = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("InstallerPathUnavailable"));
        var prefix = Path.GetFullPath(InstallRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !current.StartsWith(Path.GetFullPath(MaintenanceRoot) + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
        {
            var receipt = await UninstallTransaction.RunAsync(CancellationToken.None).ConfigureAwait(false);
            return receipt.Status == "Uninstalled" ? 0 : 2;
        }

        var workerDirectory = Path.Combine(WorkerRoot, "worker-" + Guid.NewGuid().ToString("N"));
        if (!Path.GetFullPath(workerDirectory).StartsWith(Path.GetFullPath(WorkerRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UninstallWorkerPathRejected");
        Directory.CreateDirectory(workerDirectory);
        var worker = Path.Combine(workerDirectory, "ARTVpn.Uninstall.exe");
        File.Copy(current, worker, overwrite: false);
        using var process = Process.Start(new ProcessStartInfo(worker)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workerDirectory,
            ArgumentList = { "--uninstall-worker", Environment.ProcessId.ToString() }
        }) ?? throw new InvalidOperationException("UninstallWorkerStartRejected");
        return 0;
    }

    public static async Task<int> RunWorkerAsync(int parentPid)
    {
        if (!InstallTransaction.IsAdministrator()) throw new UnauthorizedAccessException("AdministratorRequired");
        ValidateWorkerPath();
        if (parentPid <= 0 || parentPid == Environment.ProcessId) throw new InvalidDataException("UninstallWorkerParentRejected");
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (ArgumentException) { }
        var receipt = await UninstallTransaction.RunAsync(CancellationToken.None).ConfigureAwait(false);
        ScheduleWorkerCleanup();
        if(Environment.UserInteractive && receipt.Status == "Uninstalled")
            MessageBox.Show("ART VPN удалён. Запись в списке приложений и ярлыки удалены. Если список Windows уже открыт, обновите его.",
                "ART VPN — удаление завершено",MessageBoxButtons.OK,MessageBoxIcon.Information);
        return receipt.Status == "Uninstalled" ? 0 : 2;
    }

    private static void ValidateWorkerPath()
    {
        var current = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("InstallerPathUnavailable"));
        var prefix = Path.GetFullPath(WorkerRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var directory = Path.GetDirectoryName(current) ?? "";
        if (!current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(directory), "^worker-[a-f0-9]{32}$", RegexOptions.CultureInvariant) ||
            !string.Equals(Path.GetFileName(current), "ARTVpn.Uninstall.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UninstallWorkerPathRejected");
    }

    private static void ScheduleWorkerCleanup()
    {
        var current = Path.GetFullPath(Environment.ProcessPath!);
        var directory = Path.GetDirectoryName(current)!;
        _ = MoveFileEx(current, null, 4);
        _ = MoveFileEx(directory, null, 4);
        var parent = Directory.GetParent(directory)?.FullName;
        if (parent is not null && string.Equals(parent, WorkerRoot, StringComparison.OrdinalIgnoreCase))
            _ = MoveFileEx(parent, null, 4);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);
}

internal sealed record UninstallReceipt(
    string Status,
    string DetailCode,
    string UninstalledAtUtc,
    bool SystemProxyChanged,
    bool RebootRequired,
    bool ContainsProviderSecret);

internal static class UninstallTransaction
{
    private const string InstallRoot = @"C:\Program Files\ART VPN";
    private const string DataRoot = @"C:\ProgramData\ART VPN";
    private const string ServiceName = "ARTVpnService";
    private const string RunValue = "ART VPN";

    public static async Task<UninstallReceipt> RunAsync(CancellationToken cancellationToken)
    {
        if (!InstallTransaction.IsAdministrator()) throw new UnauthorizedAccessException("AdministratorRequired");
        await WaitForOwnedMaintenanceExitAsync(cancellationToken).ConfigureAwait(false);
        var statePath = Path.Combine(DataRoot, "state", "install.v1.json");
        var state = JsonSerializer.Deserialize<InstalledState>(File.ReadAllText(statePath), JsonOptions.Strict)
                    ?? throw new InvalidDataException("InstallStateRejected");
        if (state.Schema != 1 || state.Kind != "art-vpn-consumer-install" || state.Status != "Configured" ||
            state.ComputerName != Environment.MachineName || state.MachineSid != MachineSid.Read() ||
            state.ContainsProviderSecret || state.SystemProxyManaged)
            throw new InvalidDataException("InstallStateRejected");
        RemovalPayloadVerifier.Verify(InstallRoot, state.Version, state.ManifestSha256);
        RemovalPayloadVerifier.VerifyTree(DataRoot);
        RemovalPayloadVerifier.VerifyTree(@"C:\ProgramData\ART VPN Maintenance");
        ValidateServiceOwnership();
        StopOwnedUi();
        await UninstallRouteReturn.RestoreAsync(DataRoot, state.OwnerSid, cancellationToken).ConfigureAwait(false);
        var leasePath = Path.Combine(DataRoot, "state", "system-proxy-lease.v1.json");
        var proxyRestore = SystemProxyLeaseCoordinator.Restore(
            leasePath, state.OwnerSid, new RegistrySystemProxyStore(state.OwnerSid), dropExternalLease: false,
            restoreOwnedEndpointWithChangedBypass: true);
        if (proxyRestore.Status != "Completed") throw new InvalidOperationException(proxyRestore.DetailCode);
        if (proxyRestore.DetailCode == "SystemProxyExternalOwnerPreserved")
        {
            var ownerStore = new RegistrySystemProxyStore(state.OwnerSid);
            if (SystemProxyLeaseCoordinator.IsArtVpnEndpoint(ownerStore.Read()))
                throw new InvalidOperationException("SystemProxyOwnershipRequiresManualResolution");
            _ = SystemProxyLeaseCoordinator.Restore(leasePath, state.OwnerSid, ownerStore, dropExternalLease: true);
        }
        if (proxyRestore.Changed) NotifyProxyChanged();
        var proxyAfterRestore = ProxySnapshot.Read();
        InstallTransactionControl.RunScAllowStopped("stop", ServiceName);
        await WaitForServiceStoppedAsync(cancellationToken).ConfigureAwait(false);
        await WaitForOwnedPayloadExitAsync(cancellationToken).ConfigureAwait(false);
        InstallTransactionControl.RunSc("delete", ServiceName);
        await WaitForServiceDeletedAsync(cancellationToken).ConfigureAwait(false);
        RemoveOwnerStartup(state.OwnerSid);
        ShortcutManager.RemoveInstalledShortcuts();
        await DeleteOwnedDirectoryAsync(InstallRoot,cancellationToken).ConfigureAwait(false);
        await DeleteOwnedDirectoryAsync(DataRoot,cancellationToken).ConfigureAwait(false);
        await DeleteOwnedDirectoryAsync(@"C:\ProgramData\ART VPN Maintenance",cancellationToken).ConfigureAwait(false);
        Registry.LocalMachine.DeleteSubKeyTree(InstallTransaction.UninstallKey, throwOnMissingSubKey: false);
        using(var removed = Registry.LocalMachine.OpenSubKey(InstallTransaction.UninstallKey))
            if(removed is not null) throw new InvalidOperationException("UninstallRegistrationNotRemoved");
        if (!proxyAfterRestore.Equals(ProxySnapshot.Read())) throw new InvalidOperationException("SystemProxyChangedDuringUninstall");
        return new UninstallReceipt("Uninstalled", "ExactOwnedProductRemoved", DateTimeOffset.UtcNow.ToString("o"),
            proxyRestore.Changed, false, false);
    }

    private static void ValidateServiceOwnership()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName)
                        ?? throw new InvalidDataException("OwnedServiceMissing");
        var image = key.GetValue("ImagePath") as string ?? "";
        var expected = $"\"{Path.Combine(InstallRoot, "runtime", "ARTVpn.Service.exe")}\" --service";
        if (!string.Equals(image, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ServiceOwnershipRejected");
    }

    private static void StopOwnedUi()
    {
        var expected = Path.GetFullPath(Path.Combine(InstallRoot, "runtime", "ARTVpn.UI.exe"));
        foreach (var process in Process.GetProcessesByName("ARTVpn.UI"))
        {
            using (process)
            {
                string? actual = null;
                try { actual = process.MainModule?.FileName; } catch { }
                if (!string.Equals(actual is null ? null : Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { process.CloseMainWindow(); } catch { }
                try { if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    private static async Task WaitForOwnedMaintenanceExitAsync(CancellationToken cancellationToken)
    {
        var expected = Path.GetFullPath(Path.Combine(InstallRoot, "runtime", "ARTVpn.Maintenance.exe"));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var found = false;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    string? actual = null;
                    try { actual = process.MainModule?.FileName; } catch { }
                    if (actual is not null && string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
            }
            if (!found) return;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("OwnedMaintenanceStillRunning");
    }

    private static async Task WaitForServiceStoppedAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (NativeServiceState.IsStopped(NativeServiceState.Read(ServiceName))) return;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("OwnedServiceStopTimedOut");
    }

    private static async Task WaitForServiceDeletedAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (NativeServiceState.Read(ServiceName) == NativeServiceState.Missing) return;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("OwnedServiceDeleteTimedOut");
    }

    private static void RemoveOwnerStartup(string ownerSid)
    {
        if (!Regex.IsMatch(ownerSid, "^S-1-5-21-(?:[0-9]+-){3}[0-9]+$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("OwnerSidRejected");
        using (var loaded = Registry.Users.OpenSubKey(ownerSid, writable: false))
        {
            if (loaded is not null)
            {
                DeleteAndVerifyOwnerStartup(ownerSid);
                return;
            }
        }

        using var profile = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + ownerSid, writable: false)
            ?? throw new InvalidDataException("OwnerProfileMissing");
        var configured = profile.GetValue("ProfileImagePath", null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.IsNullOrWhiteSpace(configured) || configured.IndexOf('\0') >= 0)
            throw new InvalidDataException("OwnerProfilePathRejected");
        var profilePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        if (!Path.IsPathFullyQualified(profilePath) || new Uri(profilePath).IsUnc)
            throw new InvalidDataException("OwnerProfilePathRejected");
        var hivePath = Path.GetFullPath(Path.Combine(profilePath, "NTUSER.DAT"));
        if (!string.Equals(Path.GetDirectoryName(hivePath)?.TrimEnd(Path.DirectorySeparatorChar),
                profilePath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(hivePath) || (File.GetAttributes(hivePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("OwnerProfileHiveRejected");

        var mount = "ARTVPN_UNINSTALL_" + Guid.NewGuid().ToString("N");
        if (Registry.Users.OpenSubKey(mount, writable: false) is not null)
            throw new InvalidOperationException("OwnerProfileMountCollision");
        RunReg("load", @"HKU\" + mount, hivePath);
        try
        {
            DeleteAndVerifyOwnerStartup(mount);
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            RunReg("unload", @"HKU\" + mount);
        }
    }

    private static void DeleteAndVerifyOwnerStartup(string hive)
    {
        var path = hive + @"\Software\Microsoft\Windows\CurrentVersion\Run";
        using (var key = Registry.Users.OpenSubKey(path, writable: true))
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        using var verify = Registry.Users.OpenSubKey(path, writable: false);
        if (verify?.GetValue(RunValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null)
            throw new InvalidOperationException("OwnerStartupRemovalNotVerified");
    }

    private static void RunReg(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("RegistryToolUnavailable");
        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("OwnerProfileHiveOperationRejected");
    }

    private static async Task WaitForOwnedPayloadExitAsync(CancellationToken cancellationToken)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            Path.Combine(InstallRoot,"runtime","ARTVpn.Service.exe"), Path.Combine(InstallRoot,"runtime","ARTVpnCore.exe") };
        for(var attempt=0;attempt<150;attempt++)
        {
            var pending=false;
            foreach(var name in new[]{"ARTVpn.Service","ARTVpnCore"})
                foreach(var process in Process.GetProcessesByName(name))
                    using(process)
                    {
                        try { if(expected.Contains(process.MainModule?.FileName ?? "")) pending=true; }
                        catch(InvalidOperationException) { }
                        catch(System.ComponentModel.Win32Exception) { if(!process.HasExited) throw; }
                    }
            if(!pending) return;
            await Task.Delay(200,cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("OwnedVpnProcessStillRunning");
    }

    private static async Task DeleteOwnedDirectoryAsync(string path,CancellationToken cancellationToken)
    {
        var resolved = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (resolved is not (@"C:\Program Files\ART VPN" or @"C:\ProgramData\ART VPN" or @"C:\ProgramData\ART VPN Maintenance"))
            throw new InvalidOperationException("UninstallPathRejected");
        for(var attempt=0;attempt<50;attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(!Directory.Exists(resolved)) return;
            RemovalPayloadVerifier.VerifyTree(resolved);
            try { Directory.Delete(resolved, recursive: true); return; }
            catch(IOException) when(attempt<49) { }
            catch(UnauthorizedAccessException) when(attempt<49) { }
            await Task.Delay(200,cancellationToken).ConfigureAwait(false);
        }
    }

    private static void NotifyProxyChanged()
    {
        _ = InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        _ = InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);
}

internal static class ShortcutManager
{
    private const string ShortcutName = "ART VPN.lnk";

    public static void CreateInstalledShortcuts(string target)
    {
        var exactTarget = Path.GetFullPath(target);
        if (!string.Equals(exactTarget, @"C:\Program Files\ART VPN\runtime\ARTVpn.UI.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(exactTarget))
            throw new InvalidDataException("ShortcutTargetRejected");
        foreach (var path in OwnedPaths()) Create(path, exactTarget);
    }

    public static void RemoveInstalledShortcuts()
    {
        foreach (var path in OwnedPaths())
            if (File.Exists(path)) File.Delete(path);
    }

    public static bool RunContractTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-shortcut-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, ShortcutName);
        try
        {
            Directory.CreateDirectory(root);
            var target = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPathUnavailable"));
            Create(path, target);
            if (!File.Exists(path) || new FileInfo(path).Length < 100) throw new InvalidOperationException("ShortcutNotCreated");
            var link = (IShellLinkW)(object)new ShellLink();
            try
            {
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(path, 0);
                var actual = new StringBuilder(32_768);
                link.GetPath(actual, actual.Capacity, IntPtr.Zero, 0);
                if (!string.Equals(Path.GetFullPath(actual.ToString()), target, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("ShortcutTargetMismatch");
            }
            finally { Marshal.FinalReleaseComObject(link); }
            return true;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string[] OwnedPaths()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        if (string.IsNullOrWhiteSpace(desktop) || string.IsNullOrWhiteSpace(programs))
            throw new InvalidOperationException("ShortcutFolderUnavailable");
        return [Path.Combine(desktop, ShortcutName), Path.Combine(programs, ShortcutName)];
    }

    private static void Create(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var link = (IShellLinkW)(object)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription("ART VPN — устойчивый доступ к ChatGPT и OpenAI");
            link.SetIconLocation(target, 0);
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}

internal static class InstallTransactionControl
{
    public static void RunSc(params string[] arguments)
    {
        var result = Query(arguments);
        if (result.ExitCode != 0) throw new InvalidOperationException("ServiceControlRejected");
    }

    public static void RunScAllowStopped(params string[] arguments)
    {
        var result = Query(arguments);
        if (result.ExitCode is not (0 or 1062))
            throw new InvalidOperationException("ServiceControlRejected");
    }

    public static (int ExitCode, string Output) Query(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("ServiceControlUnavailable");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}

internal sealed record ProxySnapshot(int Enabled, string Server, string Override, string AutoConfigUrl)
{
    public static ProxySnapshot Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return new ProxySnapshot(
            Convert.ToInt32(key?.GetValue("ProxyEnable", 0) ?? 0),
            Convert.ToString(key?.GetValue("ProxyServer", "")) ?? "",
            Convert.ToString(key?.GetValue("ProxyOverride", "")) ?? "",
            Convert.ToString(key?.GetValue("AutoConfigURL", "")) ?? "");
    }
}

internal static class MachineSid
{
    public static string Read()
    {
        var attributes=new LsaObjectAttributes{Length=(uint)Marshal.SizeOf<LsaObjectAttributes>()};
        if(LsaOpenPolicy(IntPtr.Zero,ref attributes,1,out var handle)!=0)throw new InvalidOperationException("MachineSidUnavailable");
        try{if(LsaQueryInformationPolicy(handle,5,out var buffer)!=0||buffer==IntPtr.Zero)throw new InvalidOperationException("MachineSidUnavailable");
            try{var info=Marshal.PtrToStructure<PolicyAccountDomainInfo>(buffer);var sid=new SecurityIdentifier(info.DomainSid).Value;
                if(!Regex.IsMatch(sid,"^S-1-5-21-(?:[0-9]+-){2}[0-9]+$"))throw new InvalidOperationException("MachineSidUnavailable");return sid;}
            finally{_ = LsaFreeMemory(buffer);}}finally{_ = LsaClose(handle);}
    }
    [DllImport("advapi32.dll")]private static extern uint LsaOpenPolicy(IntPtr systemName,ref LsaObjectAttributes attrs,int access,out IntPtr handle);
    [DllImport("advapi32.dll")]private static extern uint LsaQueryInformationPolicy(IntPtr handle,int infoClass,out IntPtr buffer);
    [DllImport("advapi32.dll")]private static extern uint LsaFreeMemory(IntPtr buffer);
    [DllImport("advapi32.dll")]private static extern uint LsaClose(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)]private struct LsaObjectAttributes{public uint Length;public IntPtr RootDirectory;public IntPtr ObjectName;public uint Attributes;public IntPtr SecurityDescriptor;public IntPtr SecurityQualityOfService;}
    [StructLayout(LayoutKind.Sequential)]private struct LsaUnicodeString{public ushort Length;public ushort MaximumLength;public IntPtr Buffer;}
    [StructLayout(LayoutKind.Sequential)]private struct PolicyAccountDomainInfo{public LsaUnicodeString DomainName;public IntPtr DomainSid;}
}

internal static class AtomicJson
{
    public static void WriteNew<T>(string path,T value)=>Write(path,value,false);
    public static void Replace<T>(string path,T value)=>Write(path,value,true);
    private static void Write<T>(string path,T value,bool overwrite){Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp=Path.Combine(Path.GetDirectoryName(path)!,"."+Path.GetFileName(path)+"."+Guid.NewGuid().ToString("N")+".tmp");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value,JsonOptions.Output);try{using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){stream.Write(bytes);stream.Flush(true);}File.Move(temp,path,overwrite);}finally{Array.Clear(bytes);if(File.Exists(temp))File.Delete(temp);}}
}

internal static class InstallFailureDiagnostics
{
    internal static void Record(Exception error)
    {
        try
        {
            var frames = new StackTrace(error, false).GetFrames() ?? [];
            var methods = frames.Select(frame => frame.GetMethod()).Where(method =>
                    method?.DeclaringType?.Namespace == "ArtSport.ArtVpn.Setup")
                .Take(5).Select(method => method!.DeclaringType!.Name + "." + method.Name).ToArray();
            var record = new
            {
                atUtc = DateTimeOffset.UtcNow.ToString("o"), code = ErrorCode.From(error),
                phase = error.Data["ARTVpnPhase"] as string, errorClass = error.GetType().Name,
                hresult = $"0x{error.HResult:X8}", methods,
                rollbackFailed = error.Data["ARTVpnRollbackFailed"] is true,
                secretDisplayed = false
            };
            // No exception message, URL, argument, account name or source path.
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ART VPN", "installer-diagnostics");
            Directory.CreateDirectory(folder);
            AtomicJson.Replace(Path.Combine(folder, "last-failure.json"), record);
        }
        catch { }
    }
}

internal static class ErrorCode
{
    public static string From(Exception exception)
    {
        if (exception is System.ComponentModel.Win32Exception native)
            return native.NativeErrorCode switch
            {
                1223 => "AdministratorConsentCancelled",
                5 => "FileAccessDenied",
                740 => "AdministratorRequired",
                1260 => "WindowsPolicyBlocked",
                577 => "WindowsSignatureRejected",
                225 or 226 => "SecuritySoftwareBlocked",
                193 or 216 => "WindowsVersionUnsupported",
                112 => "FreeSpaceInsufficient",
                _ => "InstallFailed"
            };
        return Regex.IsMatch(exception.Message,"^[A-Za-z][A-Za-z0-9]{2,63}$") ? exception.Message :
            exception is UnauthorizedAccessException ? "FileAccessDenied" :
            exception is InvalidDataException ? "PackageRejected" : "InstallFailed";
    }

    public static string Describe(Exception exception)
    {
        var code = From(exception);
        var message = code switch
        {
            "AdministratorConsentCancelled" => "Разрешение администратора отменено. Установка не началась. Повторите, когда будете готовы.",
            "AdministratorRequired" => "Для установки фоновой службы нужны права администратора. Обратитесь к администратору компьютера.",
            "UninstallReturnUnconfirmed" or "UninstallReturnRejected" => "Удаление остановлено: возврат прежнего VPN не подтверждён. Включите HAPP и повторите удаление. ART VPN пока сохранён.",
            "RepairOwnershipRejected" => "Не удалось подтвердить владельца и файлы ART VPN. Автоматическое восстановление остановлено; чужую установку не изменяем.",
            "RepairStartupConflict" => "Запись автозапуска отличается от ART VPN. Не перезаписываем чужую команду; требуется проверка администратора.",
            "RepairServiceBusy" => "Служба сейчас запускается или останавливается. Дождитесь завершения и повторите проверку.",
            "RepairRouteChanged" => "Во время восстановления другой процесс изменил подключение. Новый выбор сохранён; повторите диагностику.",
            "MicrosoftAuthProxyAdministratorRequired" or "MicrosoftAuthRepairRejected" or "MicrosoftAuthProxyReadbackFailed" => "Не удалось разрешить системным окнам Microsoft обращаться к локальному VPN. Повторите установку с разрешением администратора. Учётные записи и пароли не изменялись.",
            "FileAccessDenied" => "Windows не разрешила доступ к файлу или служебной записи. Файл может быть занят другой программой или защищён политикой. Сохраните код ошибки; не отключайте защиту.",
            "UpgradeFilesBusy" => "Windows пока не освободила файлы ART VPN. Обновление остановлено с попыткой вернуть прежнюю версию. Если VPN не восстановился, отправьте диагностику разработчику; не отключайте защиту компьютера.",
            "WindowsPolicyBlocked" => "Запуск запрещён политикой Windows. Передайте этот код администратору; не отключайте защиту.",
            "WindowsSignatureRejected" => "Windows отклонила цифровую подпись файла. Не отключайте защиту; сообщите разработчику.",
            "SecuritySoftwareBlocked" => "Защита компьютера заблокировала файл. Не добавляйте исключение; сохраните сообщение защиты и сообщите разработчику.",
            "WindowsVersionUnsupported" => "Нужна 64-разрядная Windows 10 версии 1809 или новее. Проверьте версию системы.",
            "FreeSpaceInsufficient" => "Недостаточно места для установки и отката. Освободите место на системном диске и повторите.",
            "ExistingInstallIncomplete" => "После прежней установки или удаления остались файлы. Настройки сохранены. Требуется восстановление; сообщите разработчику.",
            "SameVersionPayloadMismatch" => "Под тем же номером выпуска обнаружены другие файлы. Работающая версия сохранена; используйте проверенный новый выпуск.",
            "InstalledPayloadChanged" => "Файлы установленной программы изменены или удалены. Нужна проверка и восстановление, а не повторная установка поверх них.",
            "OwnedVpnProcessStillRunning" => "VPN ещё завершает работу. Удаление не завершено. Подождите и повторите; если ошибка сохраняется, сообщите разработчику.",
            "PackageRejected" or "PackageFileRejected" or "EmbeddedPayloadMissing" => "Установочный пакет неполный или повреждён. Скачайте ZIP заново и сравните контрольную сумму.",
            _ => "Установка остановлена. Сохраните этот код и сообщите разработчику: grundyk888@proton.me."
        };
        return message + " Код: " + code + ".";
    }

    public static bool RunContractTest()
    {
        foreach (var item in new (int Number, string Code)[] {
            (1223,"AdministratorConsentCancelled"), (5,"FileAccessDenied"), (740,"AdministratorRequired"),
            (1260,"WindowsPolicyBlocked"), (577,"WindowsSignatureRejected"), (225,"SecuritySoftwareBlocked"),
            (226,"SecuritySoftwareBlocked"), (193,"WindowsVersionUnsupported"), (216,"WindowsVersionUnsupported"),
            (112,"FreeSpaceInsufficient"), (9999,"InstallFailed") })
        {
            var error = new System.ComponentModel.Win32Exception(item.Number, "private-path-or-provider-token");
            if (From(error) != item.Code || Describe(error).Contains("private-path", StringComparison.Ordinal))
                throw new InvalidOperationException("ErrorMessageContractRejected");
        }
        if (From(new UnauthorizedAccessException("private data")) != "FileAccessDenied" ||
            From(new InvalidDataException("private data")) != "PackageRejected" ||
            Describe(new Exception("private data")).Contains("private data", StringComparison.Ordinal))
            throw new InvalidOperationException("ErrorMessageContractRejected");
        return true;
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Output=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    public static readonly JsonSerializerOptions Strict=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
}
