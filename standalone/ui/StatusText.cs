using System.Globalization;

namespace ArtSport.ArtVpn.Ui;

internal static class StatusText
{
    internal static string LastSwitch(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at.ToLocalTime().ToString("dd.MM.yyyy\nHH:mm:ss", CultureInfo.InvariantCulture)
            : string.IsNullOrWhiteSpace(value) ? "—" : value;

    internal static string Stability(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        if (value == "канал стабилен • watchdog Healthy • подтверждений сбоя 0 из 3")
            return "Стабилен\nсбоев не обнаружено";
        const string prefix = "канал стабилен • watchdog ";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.TryParse(value[prefix.Length..], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return "Стабилен\nпроверен " + at.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
        return value;
    }

    internal static void VerifyContract()
    {
        const string timestamp = "2026-09-08T11:01:23.4980736+00:00";
        if (LastSwitch(timestamp).Contains('T') || !LastSwitch(timestamp).Contains('\n') || LastSwitch("") != "—" ||
            Stability("канал стабилен • watchdog " + timestamp).Contains("watchdog") ||
            Stability("проверка не завершена") != "проверка не завершена" || Stability(null) != "—" ||
            Stability("канал стабилен • watchdog Healthy • подтверждений сбоя 0 из 3").Contains("watchdog"))
            throw new InvalidOperationException("StatusTextContractFailed");
    }
}
