using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ArtSport.ArtVpn.Ui;

internal static class SupportEmailComposer
{
    internal const string DeveloperEmail = "grundyk888@proton.me";
    private const int MapiTo = 1;
    private const int MapiLogonUi = 0x00000001;
    private const int MapiDialog = 0x00000008;
    private const int MapiForceUnicode = 0x00040000;
    private const int Success = 0;
    private const int UserAbort = 1;

    public static async Task<UiOperationResult> ComposeAsync(string reportPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(reportPath) || new FileInfo(reportPath).Length is 0 or > 524_288)
            return new UiOperationResult(false, "Очищенный отчёт отсутствует или повреждён.", "SupportReportRejected");

        try
        {
            var result = await Task.Run(() => ShowMailComposer(reportPath), cancellationToken).ConfigureAwait(false);
            return result switch
            {
                Success => new UiOperationResult(true,
                    $"Письмо с очищенной диагностикой передано почтовой программе для отправки на {DeveloperEmail}.",
                    "SupportEmailAccepted"),
                UserAbort => new UiOperationResult(false,
                    "Письмо закрыто без отправки. Диагностика осталась на компьютере.", "SupportEmailCancelled"),
                _ => OpenManualFallback(reportPath)
            };
        }
        catch (OperationCanceledException)
        {
            return new UiOperationResult(false, "Подготовка письма отменена. Диагностика сохранена локально.",
                "SupportEmailCancelled");
        }
        catch
        {
            return OpenManualFallback(reportPath);
        }
    }

    private static UiOperationResult OpenManualFallback(string reportPath)
    {
        try
        {
            var subject = Uri.EscapeDataString("ART VPN для ChatGPT — диагностика");
            var body = Uri.EscapeDataString(
                "Здравствуйте! Прикладываю очищенный диагностический отчёт ART VPN. " +
                "Ссылка подписки, ключи, IP/MAC и содержимое трафика в него не входят.\r\n\r\n" +
                "Если вложение не добавилось автоматически, приложите выделенный файл вручную.");
            Process.Start(new ProcessStartInfo($"mailto:{DeveloperEmail}?subject={subject}&body={body}")
                { UseShellExecute = true });
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{reportPath}\"")
                { UseShellExecute = true });
            return new UiOperationResult(true,
                $"Открыто письмо на {DeveloperEmail}; диагностический файл выделен рядом. Добавьте его, если почтовая программа не прикрепила файл сама.",
                "SupportEmailManualAttachment");
        }
        catch
        {
            return new UiOperationResult(false,
                $"Почтовая программа не настроена. Диагностика сохранена: {reportPath}. Отправьте файл на {DeveloperEmail}.",
                "SupportEmailClientUnavailable");
        }
    }

    private static int ShowMailComposer(string reportPath)
    {
        var recipient = new MapiRecipDesc
        {
            RecipClass = MapiTo,
            Name = "Разработчики ART VPN",
            Address = "SMTP:" + DeveloperEmail
        };
        var attachment = new MapiFileDesc
        {
            Position = -1,
            PathName = reportPath,
            FileName = "ART-VPN-diagnostics.json"
        };
        var recipientPtr = IntPtr.Zero;
        var attachmentPtr = IntPtr.Zero;
        try
        {
            recipientPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MapiRecipDesc>());
            Marshal.StructureToPtr(recipient, recipientPtr, false);
            attachmentPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MapiFileDesc>());
            Marshal.StructureToPtr(attachment, attachmentPtr, false);
            var message = new MapiMessage
            {
                Subject = "ART VPN для ChatGPT — диагностика",
                NoteText = "Очищенный отчёт приложен автоматически. Он не содержит ссылку подписки, ключи, IP/MAC или содержимое трафика.",
                RecipCount = 1,
                Recips = recipientPtr,
                FileCount = 1,
                Files = attachmentPtr
            };
            return MapiSendMailW(UIntPtr.Zero, IntPtr.Zero, ref message,
                MapiLogonUi | MapiDialog | MapiForceUnicode, 0);
        }
        finally
        {
            if (recipientPtr != IntPtr.Zero)
            {
                Marshal.DestroyStructure<MapiRecipDesc>(recipientPtr);
                Marshal.FreeHGlobal(recipientPtr);
            }
            if (attachmentPtr != IntPtr.Zero)
            {
                Marshal.DestroyStructure<MapiFileDesc>(attachmentPtr);
                Marshal.FreeHGlobal(attachmentPtr);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiRecipDesc
    {
        public int Reserved;
        public int RecipClass;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Address;
        public int EntryIdSize;
        public IntPtr EntryId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiFileDesc
    {
        public int Reserved;
        public int Flags;
        public int Position;
        [MarshalAs(UnmanagedType.LPWStr)] public string? PathName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? FileName;
        public IntPtr FileType;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiMessage
    {
        public int Reserved;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Subject;
        [MarshalAs(UnmanagedType.LPWStr)] public string? NoteText;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MessageType;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DateReceived;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ConversationId;
        public int Flags;
        public IntPtr Originator;
        public int RecipCount;
        public IntPtr Recips;
        public int FileCount;
        public IntPtr Files;
    }

    [DllImport("MAPI32.DLL", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MapiSendMailW(
        UIntPtr session,
        IntPtr uiParam,
        ref MapiMessage message,
        int flags,
        int reserved);
}
