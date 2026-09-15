using ArtSport.ArtVpn.Common;
using Microsoft.Win32;
using System.Text.Json;

namespace ArtSport.ArtVpn.Setup;

internal sealed record StartupRepairPlan(bool NormalizeService, bool StartService, bool WriteUserStartup);

internal static class StartupRepairPolicy
{
    internal static StartupRepairPlan Decide(bool verified, bool integrated, int serviceStart,
        uint serviceState, string? userStartup, string expectedStartup)
    {
        if (!verified) throw new InvalidOperationException("RepairOwnershipRejected");
        if (serviceState is not (NativeServiceState.Running or NativeServiceState.Stopped))
            throw new InvalidOperationException("RepairServiceBusy");
        // Не перезаписываем чужую команду под совпавшим именем автозапуска.
        if (!integrated && userStartup is not null && !string.Equals(userStartup, expectedStartup, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("RepairStartupConflict");
        return new(serviceStart != 2, serviceState == NativeServiceState.Stopped, !integrated && userStartup is null);
    }

    internal static object Test()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("StartupRepairRegression"); count++; }
        const string command = "owned-ui --start-in-tray";
        Check(Decide(true, false, 2, NativeServiceState.Running, command, command) == new StartupRepairPlan(false, false, false));
        Check(Decide(true, false, 4, NativeServiceState.Stopped, null, command) == new StartupRepairPlan(true, true, true));
        Check(!Decide(true, true, 2, NativeServiceState.Running, null, command).WriteUserStartup);
        Check(!Decide(true, true, 2, NativeServiceState.Running, "foreign", command).WriteUserStartup);
        foreach (var scenario in new[] { (false, 4u, (string?)null), (true, 2u, (string?)null), (true, 3u, (string?)null),
            (true, 0u, (string?)null), (true, 4u, (string?)"foreign") })
        {
            try { Decide(scenario.Item1, false, 2, scenario.Item2, scenario.Item3, command); throw new Exception("UnsafeRepairAccepted"); }
            catch (InvalidOperationException) { count++; }
        }
        return new { status = "Passed", scenarios = count, systemChanged = false, secretDisplayed = false };
    }
}

internal static partial class InstallTransaction
{
    internal static async Task<InstallReceipt> RepairStartupAsync(string? requestedOwnerSid, CancellationToken token)
    {
        EnsureAdministrator();
        var owner = ResolveOwnerSid(requestedOwnerSid, requireLoadedHive: true);
        var stateText = File.ReadAllText(Path.Combine(DataRoot, "state", "install.v1.json"));
        var state = JsonSerializer.Deserialize<InstalledState>(stateText, JsonOptions.Strict)
            ?? throw new InvalidDataException("InstallStateRejected");
        var verified = PackageVerifier.Verify(InstallRoot);
        if (state.Schema != 1 || state.Kind != "art-vpn-consumer-install" || state.Status != "Configured" ||
            state.ComputerName != Environment.MachineName || state.MachineSid != MachineSid.Read() ||
            state.OwnerSid != owner || state.ContainsProviderSecret || state.SystemProxyManaged ||
            state.Version != verified.Manifest.Version || state.ManifestSha256 != verified.ManifestSha256)
            throw new InvalidOperationException("RepairOwnershipRejected");
        var bindingPath = Path.Combine(DataRoot, "state", ArtSport.Vpn.Shared.NativeUiHostBinding.FileName);
        var integrated = ArtSport.Vpn.Shared.NativeUiHostBinding.IsIntegrated(
            File.Exists(bindingPath) ? File.ReadAllText(bindingPath) : null, stateText, Environment.MachineName, owner);
        var expectedImage = $"\"{InstallRoot}\\runtime\\ARTVpn.Service.exe\" --service";
        using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName))
            if (!string.Equals(service?.GetValue("ImagePath") as string, expectedImage, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("RepairOwnershipRejected");
        var expectedRun = $"\"{InstallRoot}\\runtime\\ARTVpn.UI.exe\" --start-in-tray";
        var runPath = owner + @"\Software\Microsoft\Windows\CurrentVersion\Run";
        using var runBeforeKey = Registry.Users.OpenSubKey(runPath);
        var beforeObject = runBeforeKey?.GetValue(RunValue);
        if (beforeObject is not null && beforeObject is not string) throw new InvalidOperationException("RepairStartupConflict");
        var beforeRun = beforeObject as string;
        var start = ReadServiceStartMode();
        var stateBefore = NativeServiceState.Read(ServiceName);
        var plan = StartupRepairPolicy.Decide(true, integrated, start.Start, stateBefore, beforeRun, expectedRun);
        if (plan.StartService) EnsurePortsFree(InstallationDiagnostics.ManagedPorts);
        var store = new RegistrySystemProxyStore(owner);
        var proxyBefore = store.Read();
        // Только служебные несекретные поля; protected state наследует ACL установки.
        AtomicJson.Replace(Path.Combine(DataRoot, "state", "startup-repair-before.v1.json"), new
        { schema = 1, atUtc = DateTimeOffset.UtcNow, serviceStart = start.Start, delayedAutoStart = start.DelayedAutoStart,
            serviceWasRunning = stateBefore == NativeServiceState.Running, ownedStartupWasPresent = beforeRun is not null,
            integrated, plan, containsProviderSecret = false });
        var runWritten = false;
        var serviceStarted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (plan.NormalizeService || start.DelayedAutoStart is not (null or 0)) NormalizeServiceStartMode();
            if (plan.WriteUserStartup)
            {
                using var run = Registry.Users.CreateSubKey(runPath, writable: true)
                    ?? throw new InvalidOperationException("StartupRegistryUnavailable");
                if (run.GetValue(RunValue) is not null) throw new InvalidOperationException("RepairStartupConflict");
                run.SetValue(RunValue, expectedRun, RegistryValueKind.String);
                runWritten = true;
                if (!string.Equals(run.GetValue(RunValue) as string, expectedRun, StringComparison.Ordinal))
                    throw new InvalidOperationException("RepairStartupReadbackFailed");
            }
            if (store.Read() != proxyBefore) throw new InvalidOperationException("RepairRouteChanged");
            if (plan.StartService) { RunSc("start", ServiceName); serviceStarted = true; }
            await WaitServiceAndPortsAsync(token).ConfigureAwait(false);
            if (store.Read() != proxyBefore) throw new InvalidOperationException("RepairRouteChanged");
            var receipt = new InstallReceipt("Installed", "StartupRepairVerified", state.Version,
                DateTimeOffset.UtcNow.ToString("o"), false, false, false);
            AtomicJson.Replace(Path.Combine(DataRoot, "state", "startup-repair-receipt.v1.json"), receipt);
            return receipt;
        }
        catch (Exception failure)
        {
            // Чужие сетевые изменения не откатываем. Запущенную рабочую службу
            // не убиваем из-за позднего отказа проверки/записи отчёта.
            if (!serviceStarted)
            {
                try { RestoreServiceStartMode(start); } catch { }
                if (runWritten)
                    try
                    {
                        using var run = Registry.Users.OpenSubKey(runPath, writable: true);
                        if (string.Equals(run?.GetValue(RunValue) as string, expectedRun, StringComparison.Ordinal))
                            run!.DeleteValue(RunValue, false);
                    }
                    catch { }
            }
            failure.Data["ARTVpnPhase"] = "RepairOwnedStartup";
            throw;
        }
    }
}
