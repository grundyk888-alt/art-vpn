using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Ui;

internal sealed class HelpDialog : Form
{
    public HelpDialog(Action<string>? openRecommendation = null)
    {
        Name = "HelpDialog";
        Text = "Как пользоваться ART VPN";
        Font = new Font("Segoe UI", 10);
        BackColor = Palette.Canvas;
        ForeColor = Palette.Ink;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 650);
        Size = new Size(820, 720);
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(30, 24, 30, 22),
            BackColor = Palette.Canvas
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        var title = UiFactory.Label("🧭 Как пользоваться ART VPN", 20, true);
        var intro = UiFactory.Label("Пять простых шагов — технические знания не нужны.", 9.5f, false, Palette.Muted);
        intro.Location = new Point(2, 42);
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Canvas };
        header.Controls.Add(title);
        header.Controls.Add(intro);
        root.Controls.Add(header, 0, 0);

        var steps = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, BackColor = Palette.Canvas, Padding = new Padding(0, 4, 8, 12)
        };
        steps.Controls.Add(QuattroRecommendation.CreateCard("HelpQuattroRecommendation", 730, openRecommendation));
        steps.Controls.Add(Step("1", "🔗 Добавьте подписку", "Вставьте HTTPS-ссылку на подписку в первом окне установки. Если пропустили этот шаг, нажмите «Настроить»."));
        steps.Controls.Add(Step("2", "⚡ Дождитесь проверки", "Проверка найдёт рабочий канал и до двух резервов. После успешной проверки ART VPN подключится автоматически; при ошибке покажет причину."));
        steps.Controls.Add(Step("3", "▶ Основной канал — ART VPN", "«Авто» использует ART VPN, внешний резерв — при сбое. «ART VPN» — только встроенный канал. «Throne» и «HAPP» — ручной выбор настроенного клиента. Для возврата нажмите «Авто» или «ART VPN»."));
        steps.Controls.Add(Step("4", "🇷🇺 Российские сайты идут напрямую", "При подключении через ART VPN российские сайты, Avito и Ozon обходят VPN, а ChatGPT и OpenAI направляются через выбранный узел."));
        steps.Controls.Add(Step("5", "🩺 Если что-то не так", "Нажмите «Сообщить о проблеме», посмотрите состав отчёта и выберите, разрешаете ли Вы его отправку разработчикам."));
        steps.Controls.Add(Step("+", "🛟 Внешний резерв", "В Throne или HAPP нужна своя настроенная подписка. ART VPN проверяет HTTP-вход: Throne 2080, HAPP 10809. После успешного ручного выбора нажмите «Авто» — этот клиент останется предпочтительным резервом."));
        steps.Controls.Add(Step("↩", "Прежние настройки Windows", "«Вернуть прежнее» восстанавливает сохранённые настройки сети, а не выбирает HAPP. Для HAPP используйте его отдельную кнопку. Работающему ART VPN не требуется автозапуск HAPP или Throne."));
        steps.Controls.Add(Step("!", "🔄 Кто управляет сетью", "Если другой клиент изменил прокси Windows, ART VPN сохранит этот выбор и приостановит свою автоматику. Вернуться можно кнопкой «Авто». Обновления списков ART не переписывают базу внешнего клиента."));
        void ResizeSteps()
        {
            var width = Math.Max(610, steps.ClientSize.Width - steps.Padding.Horizontal -
                SystemInformation.VerticalScrollBarWidth - 4);
            foreach (Control card in steps.Controls) card.Width = width;
        }
        steps.ClientSizeChanged += (_, _) => ResizeSteps();
        ResizeSteps();
        root.Controls.Add(steps, 0, 1);

        var close = UiFactory.Button("HelpCloseButton", "Понятно 👍", true, 146);
        close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        close.Click += (_, _) => Close();
        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Canvas };
        close.Location = new Point(600, 8);
        footer.Controls.Add(close);
        footer.Resize += (_, _) => close.Left = footer.ClientSize.Width - close.Width;
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
    }

    private static Control Step(string number, string title, string description)
    {
        var card = new RoundedPanel { Width = 730, Height = 102, Radius = 15, Margin = new Padding(0, 0, 0, 12) };
        var badge = new Label
        {
            Text = number, AutoSize = false, Size = new Size(42, 42), Location = new Point(20, 24),
            TextAlign = ContentAlignment.MiddleCenter, BackColor = Palette.BlueSoft, ForeColor = Palette.Blue,
            Font = new Font("Segoe UI Semibold", 13)
        };
        var heading = UiFactory.Label(title, 11, true); heading.Location = new Point(80, 17);
        var body = UiFactory.Label(description, 9.2f, false, Palette.Muted); body.Location = new Point(82, 49); body.MaximumSize = new Size(610, 0);
        card.ClientSizeChanged += (_, _) => body.MaximumSize = new Size(Math.Max(200, card.ClientSize.Width - 102), 0);
        card.Controls.AddRange([badge, heading, body]);
        return card;
    }

}
