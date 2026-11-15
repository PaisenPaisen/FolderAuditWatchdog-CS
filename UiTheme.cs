using System.Drawing.Drawing2D;
using System.Reflection;

namespace FolderAuditWatchdog;

public static class UiTheme
{
    // Paleta kolorów (Slate & Fluent Blue/Emerald)
    public static readonly Color SidebarBg = Color.FromArgb(30, 41, 59);        // #1e293b
    public static readonly Color SidebarHover = Color.FromArgb(51, 65, 85);     // #334155
    public static readonly Color SidebarActive = Color.FromArgb(37, 99, 235);   // #2563eb
    public static readonly Color CanvasBg = Color.FromArgb(241, 245, 249);      // #f1f5f9
    public static readonly Color CardBg = Color.White;
    public static readonly Color CardBorder = Color.FromArgb(226, 232, 240);    // #e2e8f0

    public static readonly Color TextPrimary = Color.FromArgb(15, 23, 42);      // #0f172a
    public static readonly Color TextSecondary = Color.FromArgb(100, 116, 139); // #64748b

    public static readonly Color PrimaryBlue = Color.FromArgb(37, 99, 235);     // #2563eb
    public static readonly Color PrimaryBlueHover = Color.FromArgb(29, 78, 216);
    public static readonly Color SuccessGreen = Color.FromArgb(16, 124, 65);    // #107c41
    public static readonly Color SuccessGreenHover = Color.FromArgb(12, 94, 49);
    public static readonly Color WarningAmber = Color.FromArgb(217, 119, 6);    // #d97706
    public static readonly Color DangerRed = Color.FromArgb(220, 38, 38);       // #dc2626
    public static readonly Color NeutralBtn = Color.FromArgb(226, 232, 240);    // #e2e8f0
    public static readonly Color NeutralBtnHover = Color.FromArgb(203, 213, 225);

    public static Font FontBase(float size = 9.5f, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style);

    public static Button CreatePrimaryButton(string text, Color bg, Color hoverBg, int width = 0, int height = 34)
    {
        var btn = new Button
        {
            Text = text,
            BackColor = bg,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = FontBase(9.5f, FontStyle.Bold),
            Height = height,
            Cursor = Cursors.Hand,
            Padding = new Padding(10, 0, 10, 0)
        };
        if (width > 0) btn.Width = width;
        else btn.AutoSize = true;

        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = hoverBg;
        return btn;
    }

    public static Button CreateSecondaryButton(string text, int width = 0, int height = 34)
    {
        var btn = new Button
        {
            Text = text,
            BackColor = NeutralBtn,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = FontBase(9.5f, FontStyle.Regular),
            Height = height,
            Cursor = Cursors.Hand,
            Padding = new Padding(10, 0, 10, 0)
        };
        if (width > 0) btn.Width = width;
        else btn.AutoSize = true;

        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = NeutralBtnHover;
        return btn;
    }

    public static DataGridView CreateModernGrid()
    {
        var dgv = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = CardBg,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = CardBorder,
            EnableHeadersVisualStyles = false,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            ColumnHeadersHeight = 38,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None
        };

        dgv.RowTemplate.Height = 34;

        dgv.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(248, 250, 252),
            ForeColor = TextSecondary,
            Font = FontBase(9f, FontStyle.Bold),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0),
            SelectionBackColor = Color.FromArgb(248, 250, 252),
            SelectionForeColor = TextSecondary
        };

        dgv.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = CardBg,
            ForeColor = TextPrimary,
            Font = FontBase(9.5f),
            Padding = new Padding(8, 0, 8, 0),
            SelectionBackColor = Color.FromArgb(239, 246, 255), // Delikatny błękit przy zaznaczeniu
            SelectionForeColor = TextPrimary
        };

        dgv.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(250, 252, 255),
            ForeColor = TextPrimary,
            Padding = new Padding(8, 0, 8, 0),
            SelectionBackColor = Color.FromArgb(239, 246, 255),
            SelectionForeColor = TextPrimary
        };

        // Włączenie DoubleBuffered dla płynnego przewijania
        typeof(DataGridView).InvokeMember(
            "DoubleBuffered",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty,
            null,
            dgv,
            new object[] { true });

        return dgv;
    }

    public static Panel CreateCardPanel(Padding? padding = null)
    {
        var pnl = new Panel
        {
            BackColor = CardBg,
            Padding = padding ?? new Padding(14)
        };
        pnl.Paint += (s, e) =>
        {
            using var pen = new Pen(CardBorder, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, pnl.Width - 1, pnl.Height - 1);
        };
        return pnl;
    }

    public static Panel CreateKpiCard(string title, Color accentColor, out Label valueLabel, out Label subLabel)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBg,
            Margin = new Padding(5),
            Padding = new Padding(16, 10, 12, 10)
        };

        // Lewy pasek akcentu + ramka karty
        card.Paint += (s, e) =>
        {
            using var borderPen = new Pen(CardBorder, 1);
            e.Graphics.DrawRectangle(borderPen, 0, 0, card.Width - 1, card.Height - 1);
            using var accentBrush = new SolidBrush(accentColor);
            e.Graphics.FillRectangle(accentBrush, 0, 0, 5, card.Height);
        };

        var lblTitle = new Label
        {
            Text = title,
            Font = FontBase(8.2f, FontStyle.Bold),
            ForeColor = TextSecondary,
            Dock = DockStyle.Top,
            Height = 20
        };

        valueLabel = new Label
        {
            Text = "-",
            Font = FontBase(18f, FontStyle.Bold),
            ForeColor = TextPrimary,
            Dock = DockStyle.Top,
            Height = 34
        };

        subLabel = new Label
        {
            Text = "-",
            Font = FontBase(8.5f),
            ForeColor = TextSecondary,
            Dock = DockStyle.Top,
            Height = 20
        };

        card.Controls.Add(subLabel);
        card.Controls.Add(valueLabel);
        card.Controls.Add(lblTitle);
        return card;
    }
}
