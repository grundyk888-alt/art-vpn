using System.Security.AccessControl;
using System.Security.Principal;

namespace ArtSport.ArtVpn.Service;

internal static class ClientDirectorySecurity
{
    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("ClientPathLinkRejected");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void EnsurePublicRoot(string dataRoot, string product, string version)
    {
        // Public means readable/executable, not writable by every local user.
        // Inherited ProgramData permissions can otherwise let a user pre-create
        // a managed-client directory before the elevated service downloads it.
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var paths = new List<string> { Path.Combine(dataRoot, "clients"), Path.Combine(dataRoot, "clients", product) };
        var installed = Path.Combine(dataRoot, "clients", product, version);
        if (Directory.Exists(installed)) paths.Add(installed);
        foreach (var path in paths)
        {
            EnsureNoLinks(path);
            var directory = new DirectoryInfo(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(system);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var owner in new[] { system, admins })
                security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                    inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
            if (!directory.Exists) directory.Create(security);
            // Check the creator even after Create: an unprivileged pre-creation
            // race must fail closed, not be converted into a trusted directory.
            var ownerSid = directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            if (!system.Equals(ownerSid) && !admins.Equals(ownerSid))
                throw new InvalidDataException("ClientDirectoryOwnerRejected");
            directory.SetAccessControl(security);
            EnsureNoLinks(path);
        }
    }
}
