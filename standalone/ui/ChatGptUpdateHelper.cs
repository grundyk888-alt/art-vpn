using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ArtSport.ArtVpn.Ui;

internal sealed record ChatGptUpdateViewState(
    string State,
    string Message,
    string CurrentVersion,
    string StagedVersion,
    int ProcessCount,
    bool CanComplete,
    string CheckedAt);

internal interface IChatGptUpdateHelper
{
    Task<ChatGptUpdateViewState> InspectAsync(CancellationToken cancellationToken);
    Task<UiOperationResult> CompleteAsync(IWin32Window owner, CancellationToken cancellationToken);
}

internal sealed class ProductionChatGptUpdateHelper : IChatGptUpdateHelper
{
    private readonly string _scriptPath = Path.Combine(AppContext.BaseDirectory, "Complete-ChatGptUpdate.ps1");

    public async Task<ChatGptUpdateViewState> InspectAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_scriptPath))
            return Unavailable("Помощник обновления ChatGPT не установлен.");

        using var process = StartPowerShell("-InspectOnly", redirect: true);
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            return Unavailable(string.IsNullOrWhiteSpace(stderr)
                ? "Не удалось проверить обновление ChatGPT."
                : stderr.Trim());

        try
        {
            return JsonSerializer.Deserialize<ChatGptUpdateViewState>(stdout.Trim(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? Unavailable("Помощник обновления вернул пустой ответ.");
        }
        catch (JsonException)
        {
            return Unavailable("Состояние обновления ChatGPT не удалось прочитать.");
        }
    }

    public async Task<UiOperationResult> CompleteAsync(IWin32Window owner, CancellationToken cancellationToken)
    {
        var state = await InspectAsync(cancellationToken).ConfigureAwait(true);
        if (!state.CanComplete)
            return new UiOperationResult(true, state.Message);

        var confirmation = MessageBox.Show(owner,
            state.Message +
            "\n\nВсе окна ChatGPT/Codex будут закрыты. ART VPN продолжит работать независимо, " +
            "а ChatGPT откроется снова после установки. Продолжить?",
            "ART VPN — завершить обновление ChatGPT",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
            return new UiOperationResult(true, "Обновление ChatGPT отложено; VPN не изменён.");

        _ = StartPowerShell("-Complete", redirect: false);
        return new UiOperationResult(true,
            "Запущено безопасное завершение обновления. ChatGPT закроется и откроется снова; VPN останется включён.");
    }

    private Process StartPowerShell(string action, bool redirect)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            StandardOutputEncoding = redirect ? Encoding.UTF8 : null,
            StandardErrorEncoding = redirect ? Encoding.UTF8 : null,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-WindowStyle");
        start.ArgumentList.Add("Hidden");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(_scriptPath);
        start.ArgumentList.Add(action);
        ArtSport.Vpn.Shared.CodexProxyCompatibility.PrepareHelperEnvironment(start);
        return Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить помощник обновления ChatGPT.");
    }

    private static ChatGptUpdateViewState Unavailable(string message) =>
        new("Unavailable", message, "", "", 0, false, DateTimeOffset.Now.ToString("o"));
}
