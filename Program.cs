namespace FolderAuditWatchdog;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var login = new SessionLoginDialog();
        if (login.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(login.OperatorName))
            return;

        Application.Run(new MainForm(login.OperatorName));
    }
}

public class SessionLoginDialog : Form
{
    public string OperatorName { get; private set; } = "";
    private readonly TextBox _txtUser;

    public SessionLoginDialog()
    {
        Text = "Identyfikacja Operatora Sesji";
        Size = new Size(420, 220);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;

        var lblTitle = new Label
        {
            Text = "Identyfikacja Audytora",
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            Location = new Point(20, 15),
            AutoSize = true
        };

        var lblDesc = new Label
        {
            Text = "Wpisz swoje imię i nazwisko lub inicjały.\nDane te będą zapisywane przy każdej kontroli i akceptacji zmian.",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.DimGray,
            Location = new Point(20, 45),
            Size = new Size(360, 36)
        };

        _txtUser = new TextBox
        {
            PlaceholderText = "np. Jan Kowalski (JK)",
            Font = new Font("Segoe UI", 10),
            Location = new Point(20, 88),
            Size = new Size(360, 28)
        };
        _txtUser.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) Confirm(); };

        var btnStart = new Button
        {
            Text = "Rozpocznij nadzór",
            BackColor = Color.FromArgb(16, 124, 65),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Location = new Point(20, 128),
            Size = new Size(360, 36)
        };
        btnStart.Click += (s, e) => Confirm();

        Controls.AddRange(new Control[] { lblTitle, lblDesc, _txtUser, btnStart });
    }

    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(_txtUser.Text))
        {
            MessageBox.Show(this, "Musisz podać dane operatora!", "Wymagane", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        OperatorName = _txtUser.Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }
}
