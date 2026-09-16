namespace ArtSport.ArtVpn.Ui;

internal static class Program
{
    private const string MutexName = @"Local\ARTSPORT.ARTVpn.StandaloneUI.v1";
    private const string ActivationEventName = @"Local\ARTSPORT.ARTVpn.StandaloneUI.Activate.v1";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length == 1 && args[0] == "--apply-update")
        {
            try { return UpdateDownloadLink.RunWorker(); }
            catch
            {
                MessageBox.Show("Обновление не запущено: не удалось подтвердить установленную версию. VPN не изменён.",
                    "ART VPN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
        if (args.Length == 2 && args[0] == "--update-transfer-test")
        {
            var result = UpdatePackageTransferTests.RunAsync().GetAwaiter().GetResult();
            File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(result));
            return 0;
        }

        if (args.Length == 3 && args[0] == "--qa")
            return UiQaHarness.Run(args[1], args[2]);

        if (args.Length == 2 && args[0] == "--discovery-report")
        {
            ClientDiscovery.WriteSanitizedReport(args[1]);
            return 0;
        }

        if (args.Length == 2 && args[0] == "--request")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            var controller = new ServiceClient();
            var result = UiRequestRouter.ExecuteAsync(controller, args[1], timeout.Token).GetAwaiter().GetResult();
            return result.Success ? 0 : 24;
        }

        if (args.Length > 1 || (args.Length == 1 && args[0] != "--start-in-tray")) return 25;
        UpdateDownloadLink.CleanupCompleted();
        using var mutex = new Mutex(false, MutexName);
        var owns = false;
        try
        {
            try { owns = mutex.WaitOne(0, false); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns)
            {
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(ActivationEventName);
                    existing.Set();
                    return 0;
                }
                catch (WaitHandleCannotBeOpenedException) { return 23; }
            }
            using var form = new MainForm(new ServiceClient(), new ProductionSetupLauncher());
            using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            var registration = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) =>
            {
                if (form.IsDisposed || !form.IsHandleCreated) return;
                try { form.BeginInvoke(form.RestoreFromTray); } catch (InvalidOperationException) { }
            }, null, Timeout.Infinite, executeOnlyOnce: false);
            if (args.Length == 1)
            {
                form.Shown += (_, _) => form.HideToTray();
            }
            try { Application.Run(form); }
            finally { registration.Unregister(null); }
            return 0;
        }
        finally
        {
            if (owns) mutex.ReleaseMutex();
        }
    }
}

internal static class UiRequestRouter
{
    public static Task<UiOperationResult> ExecuteAsync(
        IArtVpnUiController controller,
        string action,
        CancellationToken cancellationToken) => action switch
        {
            "RefreshSubscription" => controller.RefreshSubscriptionAsync(cancellationToken),
            "CheckNow" => controller.CheckNowAsync(cancellationToken),
            "EnableSystemProxy" => controller.EnableSystemProxyAsync(cancellationToken),
            "RestoreSystemProxy" => controller.RestoreSystemProxyAsync(cancellationToken),
            "AcquireThrone" => controller.AcquireClientAsync("Throne", cancellationToken),
            "AcquireHapp" => controller.AcquireClientAsync("Happ", cancellationToken),
            _ => Task.FromResult(new UiOperationResult(
                false, "Неподдерживаемая служебная команда.", "UiRequestRejected"))
        };
}
