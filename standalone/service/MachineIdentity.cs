using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace ArtSport.ArtVpn.Service;

internal static partial class MachineIdentity
{
    private const int PolicyViewLocalInformation = 0x00000001;
    private const int PolicyAccountDomainInformation = 5;

    [GeneratedRegex("^S-1-5-21-(?:[0-9]+-){2}[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MachineSidPattern();

    public static string ReadLocalSamSid()
    {
        var attributes = new LsaObjectAttributes { Length = (uint)Marshal.SizeOf<LsaObjectAttributes>() };
        var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyViewLocalInformation, out var handle);
        if (status != 0) throw new InvalidOperationException("MachineSidUnavailable");
        try
        {
            status = LsaQueryInformationPolicy(handle, PolicyAccountDomainInformation, out var buffer);
            if (status != 0 || buffer == IntPtr.Zero) throw new InvalidOperationException("MachineSidUnavailable");
            try
            {
                var info = Marshal.PtrToStructure<PolicyAccountDomainInfo>(buffer);
                if (info.DomainSid == IntPtr.Zero) throw new InvalidOperationException("MachineSidUnavailable");
                var sid = new SecurityIdentifier(info.DomainSid).Value;
                if (!MachineSidPattern().IsMatch(sid)) throw new InvalidOperationException("MachineSidUnavailable");
                return sid;
            }
            finally { _ = LsaFreeMemory(buffer); }
        }
        finally { _ = LsaClose(handle); }
    }

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(
        IntPtr systemName,
        ref LsaObjectAttributes objectAttributes,
        int desiredAccess,
        out IntPtr policyHandle);

    [DllImport("advapi32.dll")]
    private static extern uint LsaQueryInformationPolicy(
        IntPtr policyHandle,
        int informationClass,
        out IntPtr buffer);

    [DllImport("advapi32.dll")]
    private static extern uint LsaFreeMemory(IntPtr buffer);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PolicyAccountDomainInfo
    {
        public LsaUnicodeString DomainName;
        public IntPtr DomainSid;
    }
}

internal static class MachineIdentityTests
{
    public static MachineIdentityTestReceipt Run()
    {
        var first = MachineIdentity.ReadLocalSamSid();
        var second = MachineIdentity.ReadLocalSamSid();
        if (first != second || !Regex.IsMatch(first, "^S-1-5-21-(?:[0-9]+-){2}[0-9]+$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("MachineIdentityInvariantFailed");
        return new MachineIdentityTestReceipt("Passed", true, true, false);
    }
}

internal sealed record MachineIdentityTestReceipt(
    string Status,
    bool LocalSamSidRead,
    bool Stable,
    bool SecretDisplayed);
