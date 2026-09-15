using Microsoft.Win32;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArtSport.ArtVpn.Common;

internal sealed record SystemProxySnapshot(
    int? ProxyEnable,
    string? ProxyServer,
    string? ProxyOverride,
    string? AutoConfigUrl);

internal sealed record SystemProxyLease(
    int Schema,
    string Kind,
    string OwnerSid,
    string Phase,
    SystemProxySnapshot Previous,
    SystemProxySnapshot Applied,
    string PreparedAtUtc,
    string AppliedAtUtc,
    bool ContainsProviderSecret);

internal sealed record SystemProxyOperationReceipt(
    string Status,
    string DetailCode,
    bool Changed,
    bool OwnershipRetained,
    bool ContainsProviderSecret);

internal interface ISystemProxyStore
{
    SystemProxySnapshot Read();
    void Write(SystemProxySnapshot snapshot);
}

// HKU is not loaded before the owner's interactive logon. This is an
// expected lifecycle boundary, not a corrupt lease or permission to use HKLM.
internal sealed class SystemProxyOwnerUnavailableException : InvalidOperationException
{
    public SystemProxyOwnerUnavailableException() : base("SystemProxyOwnerHiveUnavailable") { }
}

internal sealed class RegistrySystemProxyStore : ISystemProxyStore
{
    private const string SettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private readonly string _ownerSid;

    public RegistrySystemProxyStore(string ownerSid)
    {
        _ownerSid = SystemProxyLeaseCoordinator.ValidateOwnerSid(ownerSid);
    }

    public SystemProxySnapshot Read()
    {
        using var key = Registry.Users.OpenSubKey(_ownerSid + "\\" + SettingsPath, writable: false)
                        ?? throw new SystemProxyOwnerUnavailableException();
        return new SystemProxySnapshot(
            ReadInteger(key, "ProxyEnable"),
            ReadString(key, "ProxyServer"),
            ReadString(key, "ProxyOverride"),
            ReadString(key, "AutoConfigURL"));
    }

    public void Write(SystemProxySnapshot snapshot)
    {
        SystemProxyLeaseCoordinator.ValidateSnapshot(snapshot);
        using var key = Registry.Users.OpenSubKey(_ownerSid + "\\" + SettingsPath, writable: true)
                        ?? throw new SystemProxyOwnerUnavailableException();
        WriteOrDelete(key, "ProxyEnable", snapshot.ProxyEnable, RegistryValueKind.DWord);
        WriteOrDelete(key, "ProxyServer", snapshot.ProxyServer, RegistryValueKind.String);
        WriteOrDelete(key, "ProxyOverride", snapshot.ProxyOverride, RegistryValueKind.String);
        WriteOrDelete(key, "AutoConfigURL", snapshot.AutoConfigUrl, RegistryValueKind.String);
        key.Flush();
    }

    private static int? ReadInteger(RegistryKey key, string name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? ReadString(RegistryKey key, string name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteOrDelete(RegistryKey key, string name, object? value, RegistryValueKind kind)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, kind);
    }
}

internal static class SystemProxyLeaseCoordinator
{
    private const int Schema = 1;
    private const string Kind = "art-vpn-system-proxy-lease";
    private const string Prepared = "Prepared";
    private const string Applied = "Applied";
    private const string ArtVpnProxy = "127.0.0.1:22080";
    private static readonly string[] RequiredBypass =
    [
        "<local>", "localhost", "127.*", "*.ru", "*.su", "*.xn--p1ai",
        "*.avito.ru", "*.avito.st", "*.ozon.ru", "*.ozonusercontent.com"
    ];
    private static readonly JsonSerializerOptions Output = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static SystemProxyOperationReceipt Enable(
        string leasePath,
        string ownerSid,
        bool stableFrontReady,
        ISystemProxyStore? store = null,
        DateTimeOffset? now = null)
    {
        ownerSid = ValidateOwnerSid(ownerSid);
        leasePath = ValidateLeasePath(leasePath);
        if (!stableFrontReady)
            return Receipt("Deferred", "StableFrontNotReady", changed: false, ownership: false);
        store ??= new RegistrySystemProxyStore(ownerSid);

        var current = store.Read();
        ValidateSnapshot(current);
        var existing = TryReadLease(leasePath);
        if (existing is not null)
        {
            ValidateLease(existing, ownerSid);
            if (current == existing.Applied)
            {
                if (!IsArtVpnEndpoint(existing.Applied))
                    return Receipt("Rejected", "ExplicitModeSelectionRequired", changed: false, ownership: false);
                if (existing.Phase == Prepared)
                    WriteLease(leasePath, existing with
                    {
                        Phase = Applied,
                        AppliedAtUtc = (now ?? DateTimeOffset.UtcNow).ToString("o")
                    });
                return Receipt("Completed", "SystemProxyAlreadyEnabled", changed: false, ownership: true);
            }
            if (existing.Phase == Prepared && current == existing.Previous)
                return ApplyPreparedLease(leasePath, existing, store, now ?? DateTimeOffset.UtcNow);
            return Receipt("Rejected", "SystemProxyOwnershipLost", changed: false, ownership: false);
        }

        if (!string.IsNullOrWhiteSpace(current.AutoConfigUrl))
            return Receipt("Rejected", "SystemProxyPacConflict", changed: false, ownership: false);

        var applied = new SystemProxySnapshot(
            1,
            ArtVpnProxy,
            MergeBypass(current.ProxyOverride),
            current.AutoConfigUrl);
        var timestamp = (now ?? DateTimeOffset.UtcNow).ToString("o");
        var lease = new SystemProxyLease(
            Schema, Kind, ownerSid, Prepared, current, applied, timestamp, "", false);
        WriteLease(leasePath, lease);
        return ApplyPreparedLease(leasePath, lease, store, now ?? DateTimeOffset.UtcNow);
    }

    public static SystemProxyOperationReceipt Restore(
        string leasePath,
        string ownerSid,
        ISystemProxyStore? store = null,
        bool dropExternalLease = true,
        bool restoreOwnedEndpointWithChangedBypass = false)
    {
        ownerSid = ValidateOwnerSid(ownerSid);
        leasePath = ValidateLeasePath(leasePath);
        var lease = TryReadLease(leasePath);
        if (lease is null)
            return Receipt("Completed", "SystemProxyNotManaged", changed: false, ownership: false);
        ValidateLease(lease, ownerSid);
        store ??= new RegistrySystemProxyStore(ownerSid);
        var current = store.Read();
        ValidateSnapshot(current);

        // Some older releases could remember 22080 as their own fallback after
        // reclaiming a bypass-only edit. Explicit removal must not leave Windows
        // pointing at the listener it is about to delete.
        var restoreTarget = restoreOwnedEndpointWithChangedBypass
            ? WithoutRemovedEndpoint(lease.Previous) : lease.Previous;
        if (current == restoreTarget)
        {
            DeleteLease(leasePath);
            return Receipt("Completed", "SystemProxyAlreadyRestored", changed: false, ownership: false);
        }
        if (current != lease.Applied)
        {
            // Explicit uninstall may release our still-owned endpoint when a
            // different client changed only bypasses. Preserve those new rules;
            // a changed server, enable flag or PAC is never this exception.
            if (restoreOwnedEndpointWithChangedBypass && RestoreAfterBypassOnlyChange(lease, current) is { } restore)
            {
                restore = WithoutRemovedEndpoint(restore);
                if (store.Read() != current)
                    return Receipt("Rejected", "SystemProxyOwnershipLost", changed: false, ownership: false);
                store.Write(restore);
                if (store.Read() != restore) throw new InvalidOperationException("SystemProxyRestoreNotVerified");
                DeleteLease(leasePath);
                return Receipt("Completed", "SystemProxyRestoredPreservingNewBypass", changed: true, ownership: false);
            }
            if (dropExternalLease) DeleteLease(leasePath);
            return Receipt("Completed", "SystemProxyExternalOwnerPreserved", changed: false, ownership: false);
        }

        store.Write(restoreTarget);
        if (store.Read() != restoreTarget)
            throw new InvalidOperationException("SystemProxyRestoreNotVerified");
        DeleteLease(leasePath);
        return Receipt("Completed", "SystemProxyRestored", changed: true, ownership: false);
    }

    public static bool IsArtVpnEndpoint(SystemProxySnapshot snapshot) =>
        snapshot.ProxyEnable == 1 && string.Equals(snapshot.ProxyServer, ArtVpnProxy, StringComparison.OrdinalIgnoreCase);

    internal static SystemProxySnapshot? RestoreAfterBypassOnlyChange(SystemProxyLease lease, SystemProxySnapshot current) =>
        lease.Phase == Applied && IsArtVpnEndpoint(current) && string.IsNullOrWhiteSpace(current.AutoConfigUrl) &&
        current with { ProxyOverride = lease.Applied.ProxyOverride } == lease.Applied
            ? lease.Previous with { ProxyOverride = current.ProxyOverride } : null;

    private static SystemProxySnapshot WithoutRemovedEndpoint(SystemProxySnapshot snapshot) =>
        IsArtVpnEndpoint(snapshot) && string.IsNullOrWhiteSpace(snapshot.AutoConfigUrl)
            ? snapshot with { ProxyEnable = 0, ProxyServer = null } : snapshot;

    internal static string MergeBypass(string? existing)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in (existing ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Concat(RequiredBypass))
        {
            if (item.Length > 0 && item.Length <= 512 && seen.Add(item)) ordered.Add(item);
        }
        return string.Join(';', ordered);
    }

    internal static string ValidateOwnerSid(string ownerSid)
    {
        SecurityIdentifier sid;
        try { sid = new SecurityIdentifier(ownerSid); }
        catch (Exception ex) when (ex is ArgumentException or SystemException)
        {
            throw new InvalidDataException("SystemProxyOwnerSidRejected", ex);
        }
        if (!sid.IsAccountSid() || !(ownerSid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || ownerSid == "S-1-5-18"))
            throw new InvalidDataException("SystemProxyOwnerSidRejected");
        return sid.Value;
    }

    internal static void ValidateSnapshot(SystemProxySnapshot snapshot)
    {
        if (snapshot.ProxyEnable is not (null or 0 or 1))
            throw new InvalidDataException("SystemProxySnapshotRejected");
        foreach (var value in new[] { snapshot.ProxyServer, snapshot.ProxyOverride, snapshot.AutoConfigUrl })
            if (value is { Length: > 16_384 } || value?.Any(character => character is '\0' or '\r' or '\n') == true)
                throw new InvalidDataException("SystemProxySnapshotRejected");
    }

    private static SystemProxyOperationReceipt ApplyPreparedLease(
        string leasePath,
        SystemProxyLease lease,
        ISystemProxyStore store,
        DateTimeOffset now)
    {
        try
        {
            if (store.Read() != lease.Previous)
                throw new InvalidOperationException("SystemProxyOwnershipLost");
            store.Write(lease.Applied);
            if (store.Read() != lease.Applied)
                throw new InvalidOperationException("SystemProxyApplyNotVerified");
            WriteLease(leasePath, lease with { Phase = Applied, AppliedAtUtc = now.ToString("o") });
            return Receipt("Completed", "SystemProxyEnabled", changed: true, ownership: true);
        }
        catch
        {
            try
            {
                // A failed operation must never overwrite a newer choice by
                // the user or a different client. Registry is not a CAS API.
                if (store.Read() == lease.Applied) store.Write(lease.Previous);
                if (store.Read() == lease.Previous) DeleteLease(leasePath);
            }
            catch { }
            throw;
        }
    }

    internal static SystemProxyLease? TryReadLease(string leasePath)
    {
        if (!File.Exists(leasePath)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(leasePath));
        var expected = new[]
        {
            "schema", "kind", "ownerSid", "phase", "previous", "applied", "preparedAtUtc", "appliedAtUtc",
            "containsProviderSecret"
        };
        if (!document.RootElement.EnumerateObject().Select(property => property.Name)
                .SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("SystemProxyLeaseRejected");
        return JsonSerializer.Deserialize<SystemProxyLease>(document.RootElement.GetRawText(), Strict)
               ?? throw new InvalidDataException("SystemProxyLeaseRejected");
    }

    internal static void ValidateLease(SystemProxyLease lease, string ownerSid)
    {
        if (lease.Schema != Schema || lease.Kind != Kind || lease.OwnerSid != ownerSid ||
            lease.Phase is not (Prepared or Applied) || lease.ContainsProviderSecret ||
            !DateTimeOffset.TryParseExact(lease.PreparedAtUtc, "o", null,
                System.Globalization.DateTimeStyles.None, out _) ||
            (lease.Phase == Applied && !DateTimeOffset.TryParseExact(lease.AppliedAtUtc, "o", null,
                System.Globalization.DateTimeStyles.None, out _)))
            throw new InvalidDataException("SystemProxyLeaseRejected");
        ValidateSnapshot(lease.Previous);
        ValidateSnapshot(lease.Applied);
        if (!(IsArtVpnEndpoint(lease.Applied) ||
              (lease.Applied.ProxyEnable == 1 && lease.Applied.ProxyServer is "127.0.0.1:2080" or "127.0.0.1:10809")) ||
            !string.IsNullOrWhiteSpace(lease.Applied.AutoConfigUrl))
            throw new InvalidDataException("SystemProxyLeaseRejected");
    }

    internal static void WriteLease(string leasePath, SystemProxyLease lease)
    {
        ValidateLease(lease, lease.OwnerSid);
        var directory = Path.GetDirectoryName(leasePath) ?? throw new InvalidDataException("SystemProxyLeasePathRejected");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(leasePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(lease, Output);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, leasePath, overwrite: true);
        }
        finally
        {
            Array.Clear(bytes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ValidateLeasePath(string leasePath)
    {
        var full = Path.GetFullPath(leasePath);
        if (!string.Equals(Path.GetFileName(full), "system-proxy-lease.v1.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SystemProxyLeasePathRejected");
        return full;
    }

    private static void DeleteLease(string leasePath)
    {
        if (File.Exists(leasePath)) File.Delete(leasePath);
    }

    private static SystemProxyOperationReceipt Receipt(string status, string code, bool changed, bool ownership) =>
        new(status, code, changed, ownership, false);
}

internal sealed class MemorySystemProxyStore : ISystemProxyStore
{
    public SystemProxySnapshot Current { get; set; }
    public bool FailNextWrite { get; set; }
    public int Writes { get; private set; }

    public MemorySystemProxyStore(SystemProxySnapshot current) => Current = current;
    public SystemProxySnapshot Read() => Current;
    public void Write(SystemProxySnapshot snapshot)
    {
        Writes++;
        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new IOException("InjectedSystemProxyWriteFailure");
        }
        Current = snapshot;
    }
}

internal static class SystemProxyLeaseTests
{
    public static object Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-vpn-system-proxy-test-" + Guid.NewGuid().ToString("N"));
        var leasePath = Path.Combine(root, "system-proxy-lease.v1.json");
        const string ownerSid = "S-1-5-21-111-222-333-1001";
        var original = new SystemProxySnapshot(null, "127.0.0.1:2080", "custom.local;<local>", null);
        try
        {
            var notReady = new MemorySystemProxyStore(original);
            var deferred = SystemProxyLeaseCoordinator.Enable(leasePath, ownerSid, false, notReady);
            Assert(deferred.DetailCode == "StableFrontNotReady" && notReady.Writes == 0 && !File.Exists(leasePath));

            var pac = new MemorySystemProxyStore(original with { AutoConfigUrl = "https://pac.example.invalid/config.pac" });
            var pacReceipt = SystemProxyLeaseCoordinator.Enable(leasePath, ownerSid, true, pac);
            Assert(pacReceipt.DetailCode == "SystemProxyPacConflict" && pac.Writes == 0 && !File.Exists(leasePath));

            var store = new MemorySystemProxyStore(original);
            var enabled = SystemProxyLeaseCoordinator.Enable(leasePath, ownerSid, true, store,
                DateTimeOffset.Parse("2026-09-01T00:00:00.0000000+00:00"));
            Assert(enabled.DetailCode == "SystemProxyEnabled" && enabled.Changed && enabled.OwnershipRetained);
            Assert(store.Current.ProxyEnable == 1 && store.Current.ProxyServer == "127.0.0.1:22080");
            Assert(store.Current.ProxyOverride!.Contains("custom.local", StringComparison.Ordinal) &&
                   store.Current.ProxyOverride.Contains("*.ru", StringComparison.Ordinal) &&
                   store.Current.ProxyOverride.Contains("*.avito.ru", StringComparison.Ordinal) &&
                   store.Current.ProxyOverride.Contains("*.ozon.ru", StringComparison.Ordinal));
            var writes = store.Writes;
            var idempotent = SystemProxyLeaseCoordinator.Enable(leasePath, ownerSid, true, store);
            Assert(idempotent.DetailCode == "SystemProxyAlreadyEnabled" && store.Writes == writes);
            var restored = SystemProxyLeaseCoordinator.Restore(leasePath, ownerSid, store);
            Assert(restored.DetailCode == "SystemProxyRestored" && restored.Changed && store.Current == original && !File.Exists(leasePath));

            SystemProxyLeaseCoordinator.Enable(leasePath, ownerSid, true, store);
            var external = new SystemProxySnapshot(1, "127.0.0.1:9999", "external", null);
            store.Current = external;
            var preserved = SystemProxyLeaseCoordinator.Restore(leasePath, ownerSid, store);
            Assert(preserved.DetailCode == "SystemProxyExternalOwnerPreserved" && store.Current == external && !File.Exists(leasePath));

            var bypassStore = new MemorySystemProxyStore(original);
            SystemProxyLeaseCoordinator.Enable(leasePath, ownerSid, true, bypassStore);
            bypassStore.Current = bypassStore.Current with { ProxyOverride = "new.local;*.example" };
            var bypassBefore = bypassStore.Current;
            var strictRestore = SystemProxyLeaseCoordinator.Restore(leasePath, ownerSid, bypassStore, false);
            Assert(!strictRestore.Changed && bypassStore.Current == bypassBefore && File.Exists(leasePath));
            var scopedRestore = SystemProxyLeaseCoordinator.Restore(leasePath, ownerSid, bypassStore, false, true);
            Assert(scopedRestore.DetailCode == "SystemProxyRestoredPreservingNewBypass" &&
                bypassStore.Current == original with { ProxyOverride = "new.local;*.example" } && !File.Exists(leasePath));
            foreach (var foreign in new[] {
                new SystemProxySnapshot(1, "127.0.0.1:10809", "new", null),
                new SystemProxySnapshot(0, "127.0.0.1:22080", "new", null),
                new SystemProxySnapshot(1, "127.0.0.1:22080", "new", "https://pac.example.invalid/new.pac") })
            {
                var protectedStore = new MemorySystemProxyStore(original);
                SystemProxyLeaseCoordinator.Enable(leasePath, ownerSid, true, protectedStore);
                protectedStore.Current = foreign; var protectedWrites = protectedStore.Writes;
                var untouched = SystemProxyLeaseCoordinator.Restore(leasePath, ownerSid, protectedStore, true, true);
                Assert(!untouched.Changed && protectedStore.Current == foreign && protectedStore.Writes == protectedWrites);
            }

            var selfFallback = new MemorySystemProxyStore(original with { ProxyEnable = 1, ProxyServer = "127.0.0.1:22080" });
            SystemProxyLeaseCoordinator.Enable(leasePath, ownerSid, true, selfFallback);
            var selfReleased = SystemProxyLeaseCoordinator.Restore(leasePath, ownerSid, selfFallback, false, true);
            Assert(selfReleased.Changed && selfFallback.Current.ProxyEnable == 0 && selfFallback.Current.ProxyServer is null && !File.Exists(leasePath));

            var failing = new MemorySystemProxyStore(original) { FailNextWrite = true };
            var failureRaised = false;
            try { SystemProxyLeaseCoordinator.Enable(leasePath, ownerSid, true, failing); }
            catch (IOException) { failureRaised = true; }
            Assert(failureRaised && failing.Current == original && !File.Exists(leasePath));

            Assert(SystemProxyLeaseCoordinator.MergeBypass("<local>;*.ru;<LOCAL>") ==
                   "<local>;*.ru;localhost;127.*;*.su;*.xn--p1ai;*.avito.ru;*.avito.st;*.ozon.ru;*.ozonusercontent.com");
            return new
            {
                status = "Passed",
                scenarios = 16,
                stableFrontRequired = true,
                pacConflictRejected = true,
                compareAndSwap = true,
                exactRestore = true,
                externalOwnerPreserved = true,
                bypassMerged = true,
                secretDisplayed = false
            };
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("SystemProxyLeaseInvariantFailed");
    }
}
