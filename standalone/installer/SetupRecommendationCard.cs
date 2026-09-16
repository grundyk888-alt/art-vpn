using System.Drawing.Drawing2D;
using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Setup;

// Акцент только в первом окне установки. Общие карточки приложения и
// сетевой сценарий не меняются; переход — только по явному нажатию.
internal static class SetupRecommendationCard
{
    internal static Panel Create(Action<string>? openWebsite)
    {
        const string name = "SetupQuattroRecommendation";
        var blue = Color.FromArgb(48, 94, 157);
        var card = new HeroPanel
        {
            Name = name, Size = new Size(570, 148), Padding = new Padding(20, 12, 20, 12),
            AccessibleName = QuattroRecommendation.Title
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.Transparent,
            ColumnCount = 2, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        foreach (var height in new[] { 18, 34, 48, 24 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Label TextRow(string suffix, string text, float size, Color color, bool strong = false) => new()
        {
            Name = name + suffix, Text = text, Dock = DockStyle.Fill,
            Margin = Padding.Empty, BackColor = Color.Transparent, ForeColor = color,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(strong ? "Segoe UI Semibold" : "Segoe UI", size)
        };
        var eyebrow = TextRow("Eyebrow", "ПОДПИСКА ДЛЯ ART VPN", 8, blue, true);
        var title = TextRow("Title", QuattroRecommendation.Title, 16, Color.FromArgb(23, 53, 107), true);
        var description = TextRow("Description", QuattroRecommendation.BenefitText, 9.5f, Color.FromArgb(52, 73, 108));
        var choice = TextRow("Choice", "Есть подписка? Вставьте её ниже — Quattro или другую.", 9, Color.FromArgb(64, 85, 118));
        var button = new Button
        {
            Name = name + "Button", Text = QuattroRecommendation.ButtonText,
            Size = new Size(188, 40), Margin = Padding.Empty, Anchor = AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat, BackColor = blue, ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5f), Cursor = Cursors.Hand,
            AccessibleDescription = "Открыть регистрацию Quattro VPN в браузере. Подключение и введённая подписка не изменятся."
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 80, 137);
        button.Click += (_, _) => (openWebsite ?? QuattroRecommendation.OpenWebsite)(QuattroRecommendation.Url);
        layout.Controls.Add(eyebrow, 0, 0); layout.SetColumnSpan(eyebrow, 2);
        layout.Controls.Add(title, 0, 1); layout.SetColumnSpan(title, 2);
        layout.Controls.Add(description, 0, 2); layout.Controls.Add(button, 1, 2);
        layout.Controls.Add(choice, 0, 3); layout.SetColumnSpan(choice, 2);
        card.Controls.Add(layout);
        return card;
    }

    private sealed class HeroPanel : Panel
    {
        internal HeroPanel() => DoubleBuffered = true;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            using var background = new LinearGradientBrush(ClientRectangle,
                Color.FromArgb(239, 243, 249), Color.FromArgb(229, 237, 247), 12f);
            e.Graphics.FillRectangle(background, ClientRectangle);
            using var glow = new SolidBrush(Color.FromArgb(72, Color.White));
            var diameter = Height * 2;
            e.Graphics.FillEllipse(glow, Width - diameter / 2, -diameter / 2, diameter, diameter);
            using var accent = new SolidBrush(Color.FromArgb(48, 94, 157));
            e.Graphics.FillRectangle(accent, 0, 0, Math.Max(3, 4 * DeviceDpi / 96), Height);
        }
    }
}
