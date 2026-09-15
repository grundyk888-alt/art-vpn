using System.Diagnostics;

namespace ArtSport.ArtVpn.Common;

// Один адрес для установщика и приложения. Рекомендация не управляет сетью,
// подпиской или установкой: браузер открывается только по действию пользователя.
internal static class QuattroRecommendation
{
    internal const string Url = "https://quattro.app/register?ref=_oxkaWjifVhzEeUH";
    internal const string Title = "Рекомендуем Quattro VPN";
    internal const string ButtonText = "Выбрать Quattro VPN";

    internal static Panel CreateCard(string name, int width, Action<string>? openWebsite = null)
    {
        var blue = Color.FromArgb(47, 111, 235);
        var card = new Panel
        {
            Name = name, Width = width, Height = 128,
            BackColor = Color.FromArgb(234, 241, 255),
            Padding = new Padding(18, 10, 18, 10),
            Margin = new Padding(0, 0, 0, 16),
            AccessibleName = Title
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        var title = new Label
        {
            Text = Title, Dock = DockStyle.Fill, Margin = Padding.Empty,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = blue, Font = new Font("Segoe UI Semibold", 12)
        };
        var description = new Label
        {
            Text = "Большой выбор стран для подбора устойчивого канала.",
            Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(56, 70, 94), Font = new Font("Segoe UI", 9.5f)
        };
        var button = new Button
        {
            Name = name + "Button", Text = ButtonText, Width = 190, Height = 44,
            Anchor = AnchorStyles.Right, Margin = Padding.Empty,
            FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = blue,
            Font = new Font("Segoe UI Semibold", 9.5f), Cursor = Cursors.Hand,
            AccessibleDescription = "Открыть сайт Quattro VPN в браузере. Текущая подписка и подключение не изменятся."
        };
        button.FlatAppearance.BorderColor = blue;
        button.Click += (_, _) => (openWebsite ?? OpenWebsite)(Url);
        var choice = new Label
        {
            Text = "Уже есть подписка? Можно использовать свою совместимую подписку.",
            Dock = DockStyle.Fill, Margin = Padding.Empty,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(85, 98, 120), Font = new Font("Segoe UI", 9)
        };
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);
        layout.Controls.Add(description, 0, 1);
        layout.Controls.Add(button, 1, 1);
        layout.Controls.Add(choice, 0, 2);
        layout.SetColumnSpan(choice, 2);
        card.Controls.Add(layout);
        return card;
    }

    internal static void OpenWebsite(string url)
    {
        // Не принимаем адрес подписки, содержимое буфера или произвольный URL.
        if (!string.Equals(url, Url, StringComparison.Ordinal))
            throw new ArgumentException("RecommendationUrlRejected", nameof(url));
        try { Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true }); }
        catch
        {
            MessageBox.Show("Не удалось открыть браузер. Ссылка: " + Url,
                "Quattro VPN", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
