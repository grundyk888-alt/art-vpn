namespace ArtSport.ArtVpn.Common;

// One immutable acceptance baseline is shared by downloader and user-session launcher.
// The exact official HAPP asset was checked on ART-TEST-WIN10, including its valid
// Flyfrog LLC Authenticode signature. A changed upstream file is never accepted.
internal static class AcceptedClientRelease
{
    internal const string HappVersion = "4.2.1";
    internal const string HappAssetName = "setup-Happ.x64.exe";
    internal const long HappBytes = 118_930_936;
    internal const string HappSha256 = "08B7146E0B7C4F0326B2DA894D653A42C98E26235302869595970CE20FA65B27";
    internal const string HappUrl = "https://github.com/Happ-proxy/happ-desktop/releases/download/4.2.1/setup-Happ.x64.exe";

    internal static string HappInstallerPath(string dataRoot) =>
        Path.Combine(dataRoot, "clients", "Happ", HappVersion, HappAssetName);
}
