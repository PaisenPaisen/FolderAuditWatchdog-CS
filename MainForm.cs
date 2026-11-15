using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FolderAuditWatchdog;

public class MainForm : Form
{
    private const int PageSize = 250;
    private readonly string _currentUser;
    private readonly string _appDir;
    private readonly string _configFile;
    private readonly string _vaultDir;
    private readonly AuditDatabaseManager _db;
    private readonly RepositoryEngine _engine;

    private string? _selectedNewRepoDir;
    private List<ScanResultItem> _scanResultsCache = new();
    private List<ScanResultItem> _filteredScanCache = new();
    private List<ScanSnapshot> _dashSnapshotsCache = new();
    private int _currentScanPage = 0;
    private bool _isScanning = false;
    private bool _isSyncingCombos = false;
    private CancellationTokenSource? _repoCreationCts;
    private CancellationTokenSource? _auditScanCts;

    // Nawigacja boczna (Sidebar) i panele zakładek
    private readonly List<Button> _navButtons = new();
    private readonly List<Panel> _pages = new();
    private Label _lblPageHeader = null!;

    // Zakładka 0: Repozytoria
    private Label _lblPickedRepo = null!;
    private TextBox _txtRepoCode = null!;
    private TextBox _txtRepoName = null!;
    private TextBox _txtRepoOwner = null!;
    private Button _btnCreateRepo = null!;
    private Button _btnCancelRepo = null!;
    private ProgressBar _pbRepoProgress = null!;
    private Label _lblRepoProgress = null!;
    private CheckBox _chkRepoArch = null!;
    private DataGridView _gridRepos = null!;

    // Zakładka 1: Monitor
    private ComboBox _cmbMonRepo = null!;
    private Button _btnScanFast = null!;
    private Button _btnScanDeep = null!;
    private Button _btnCancelScan = null!;
    private CheckBox _chkMtimeOnly = null!;
    private Label _lblMonSummary = null!;
    private ComboBox _cmbMonFilter = null!;
    private TextBox _txtMonSearch = null!;
    private DataGridView _gridMon = null!;
    private Button _btnMonPrev = null!;
    private Button _btnMonNext = null!;
    private Label _lblMonPage = null!;

    // Zakładka 2: Eksplorator
    private ComboBox _cmbExpRepo = null!;
    private TreeView _treeExpFolders = null!;
    private DataGridView _gridExpFiles = null!;
    private int _currExpRepoId;
    private string _currExpRepoRoot = "";

    // Zakładka 3: Dashboard
    private ComboBox _cmbDashScope = null!;
    private Label _kpiTotV = null!, _kpiTotS = null!;
    private Label _kpiIntV = null!, _kpiIntS = null!;
    private Label _kpiModV = null!, _kpiModS = null!;
    private Label _kpiNewV = null!, _kpiNewS = null!;
    private Label _kpiDelV = null!, _kpiDelS = null!;
    private Panel _chartPanel = null!;
    private DataGridView _gridSnaps = null!;

    // Zakładka 4: Historia i Audyt
    private ComboBox _cmbAuditRepo = null!;
    private DataGridView _gridAudit = null!;

    public MainForm(string operatorName)
    {
        _currentUser = operatorName;
        _appDir = AppContext.BaseDirectory;
        _configFile = Path.Combine(_appDir, "audit_watchdog_config.json");
        _vaultDir = LoadVaultDir();
        _db = new AuditDatabaseManager(_vaultDir);
        _engine = new RepositoryEngine(_db);

        Text = "Folder Audit Watchdog – Nadzór Integralności Repozytoriów";
        Size = new Size(1460, 880);
        MinimumSize = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiTheme.FontBase();
        BackColor = UiTheme.CanvasBg;

        SetupUi();
        RefreshRepositoriesList();
        SelectPage(1); // Start w Monitorze Zmian
    }

    private string LoadVaultDir()
    {
        string defaultDir = Path.Combine(_appDir, "Audit_Watchdog_Data");
        if (File.Exists(_configFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_configFile));
                string dir = doc.RootElement.GetProperty("data_dir").GetString() ?? "";
                bool isRel = doc.RootElement.TryGetProperty("is_relative", out var relProp) && relProp.GetBoolean();
                string resolved = isRel ? Path.GetFullPath(Path.Combine(_appDir, dir)) : dir;
                if (Directory.Exists(resolved)) return resolved;
            }
            catch { }
        }
        Directory.CreateDirectory(defaultDir);
        File.WriteAllText(_configFile, JsonSerializer.Serialize(new { data_dir = "Audit_Watchdog_Data", is_relative = true }, new JsonSerializerOptions { WriteIndented = true }));
        return defaultDir;
    }

    private void SetupUi()
    {
        // 1. LEWY SIDEBAR (NAWIGACJA)
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 245,
            BackColor = UiTheme.SidebarBg,
            Padding = new Padding(12, 18, 12, 14)
        };

        var lblBrand = new Label
        {
            Text = "🛡️ AUDIT WATCHDOG",
            Font = UiTheme.FontBase(12f, FontStyle.Bold),
            ForeColor = Color.White,
            Dock = DockStyle.Top,
            Height = 32
        };

        var lblSubBrand = new Label
        {
            Text = "Nadzór integralności plików",
            Font = UiTheme.FontBase(8.5f),
            ForeColor = Color.FromArgb(148, 163, 184),
            Dock = DockStyle.Top,
            Height = 26
        };

        sidebar.Controls.Add(lblSubBrand);
        sidebar.Controls.Add(lblBrand);

        var navItems = new[]
        {
            ("📁  Repozytoria (Baseline)", 0),
            ("🔍  Monitor Zmian", 1),
            ("📂  Eksplorator", 2),
            ("📈  Dashboard", 3),
            ("📜  Historia i Audyt", 4)
        };

        // Dodajemy przyciski nawigacji (od dołu do góry ze względu na DockStyle.Top)
        foreach (var (text, idx) in navItems.Reverse())
        {
            var btn = new Button
            {
                Text = text,
                Dock = DockStyle.Top,
                Height = 44,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(226, 232, 240),
                BackColor = UiTheme.SidebarBg,
                Font = UiTheme.FontBase(10f, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 4, 0, 4)
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = UiTheme.SidebarHover;
            int targetIdx = idx;
            btn.Click += (s, e) => SelectPage(targetIdx);

            _navButtons.Insert(0, btn);
            sidebar.Controls.Add(btn);
            sidebar.Controls.SetChildIndex(btn, 0);
        }

        // Dolna karta operatora w sidebarze
        var userCard = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 68,
            BackColor = Color.FromArgb(15, 23, 42),
            Padding = new Padding(10, 8, 10, 8)
        };
        userCard.Controls.Add(new Label
        {
            Text = $"👤 {_currentUser}",
            ForeColor = Color.FromArgb(74, 222, 128),
            Font = UiTheme.FontBase(9.5f, FontStyle.Bold),
            Dock = DockStyle.Bottom,
            Height = 24
        });
        userCard.Controls.Add(new Label
        {
            Text = "AKTYWNY AUDYTOR SESJI",
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = UiTheme.FontBase(7.5f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 18
        });
        sidebar.Controls.Add(userCard);

        // 2. GÓRNY PASEK NAGŁÓWKA (TOP HEADER)
        var topBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 54,
            BackColor = Color.White,
            Padding = new Padding(20, 10, 20, 10)
        };
        topBar.Paint += (s, e) =>
        {
            using var pen = new Pen(UiTheme.CardBorder, 1);
            e.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
        };

        _lblPageHeader = new Label
        {
            Text = "Monitor Zmian",
            Font = UiTheme.FontBase(13f, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary,
            AutoSize = true,
            Location = new Point(20, 14)
        };

        var rightTopFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight
        };

        var lblDbPath = new Label
        {
            Text = $"Baza: {_vaultDir}",
            ForeColor = UiTheme.TextSecondary,
            Font = UiTheme.FontBase(8.5f),
            AutoSize = true,
            Margin = new Padding(0, 9, 14, 0)
        };

        var btnOpenDb = UiTheme.CreateSecondaryButton("📂 Folder bazy", height: 32);
        btnOpenDb.Click += (s, e) => Process.Start("explorer.exe", _vaultDir);

        rightTopFlow.Controls.Add(lblDbPath);
        rightTopFlow.Controls.Add(btnOpenDb);

        topBar.Controls.Add(_lblPageHeader);
        topBar.Controls.Add(rightTopFlow);

        // 3. KONTENER GŁÓWNEJ TREŚCI (CONTENT HOST)
        var contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            BackColor = UiTheme.CanvasBg
        };

        for (int i = 0; i < 5; i++)
        {
            var page = new Panel { Dock = DockStyle.Fill, Visible = false };
            _pages.Add(page);
            contentHost.Controls.Add(page);
        }

        BuildReposTab(_pages[0]);
        BuildMonitorTab(_pages[1]);
        BuildExplorerTab(_pages[2]);
        BuildDashboardTab(_pages[3]);
        BuildAuditTab(_pages[4]);

        Controls.Add(contentHost);
        Controls.Add(topBar);
        Controls.Add(sidebar);
    }

    private void SelectPage(int index)
    {
        string[] titles =
        {
            "Zarządzanie Repozytoriami i Linią Bazową",
            "Monitor Zmian i Weryfikacja Integralności",
            "Eksplorator Struktury Repozytorium",
            "Dashboard Analityczny i Trendy Integralności",
            "Historia Audytu i Raportowanie PDF"
        };

        _lblPageHeader.Text = titles[index];

        for (int i = 0; i < _pages.Count; i++)
        {
            bool active = i == index;
            _pages[i].Visible = active;
            _navButtons[i].BackColor = active ? UiTheme.SidebarActive : UiTheme.SidebarBg;
            _navButtons[i].Font = UiTheme.FontBase(10f, active ? FontStyle.Bold : FontStyle.Regular);
            _navButtons[i].ForeColor = active ? Color.White : Color.FromArgb(203, 213, 225);
        }

        switch (index)
        {
            case 0: RefreshRepositoriesList(); break;
            case 1: SyncRepoDropdowns(); break;
            case 2: RefreshExplorerRepos(); break;
            case 3: RefreshDashboardTab(); break;
            case 4: RefreshAuditTab(); break;
        }
    }

    // =========================================================================
    // ZAKŁADKA 0: REPOZYTORIA
    // =========================================================================
    private void BuildReposTab(Panel page)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        // Lewa karta formularza
        var leftCard = UiTheme.CreateCardPanel(new Padding(18));
        leftCard.Dock = DockStyle.Fill;
        leftCard.Margin = new Padding(0, 0, 12, 0);

        var formFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        formFlow.Controls.Add(new Label
        {
            Text = "Nowa lokalizacja pod nadzorem",
            Font = UiTheme.FontBase(11.5f, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 14)
        });

        formFlow.Controls.Add(MakeFieldLabel("1. Katalog na dysku lub zasobie sieciowym:"));
        var btnPick = UiTheme.CreateSecondaryButton("📁 Wybierz folder...", width: 355, height: 36);
        btnPick.Click += (s, e) => PickRepoFolder();
        formFlow.Controls.Add(btnPick);

        _lblPickedRepo = new Label
        {
            Text = "Nie wybrano folderu",
            ForeColor = UiTheme.TextSecondary,
            Font = UiTheme.FontBase(8.5f),
            Size = new Size(355, 36),
            Margin = new Padding(0, 6, 0, 10)
        };
        formFlow.Controls.Add(_lblPickedRepo);

        formFlow.Controls.Add(MakeFieldLabel("2. Kod identyfikacyjny (np. INV-01):"));
        _txtRepoCode = new TextBox { PlaceholderText = "np. LINIA-ASEPT", Width = 355, Font = UiTheme.FontBase(10f), Margin = new Padding(0, 0, 0, 12) };
        formFlow.Controls.Add(_txtRepoCode);

        formFlow.Controls.Add(MakeFieldLabel("3. Nazwa opisowa repozytorium:"));
        _txtRepoName = new TextBox { PlaceholderText = "np. Dokumentacja Budowy Linii", Width = 355, Font = UiTheme.FontBase(10f), Margin = new Padding(0, 0, 0, 12) };
        formFlow.Controls.Add(_txtRepoName);

        formFlow.Controls.Add(MakeFieldLabel("4. Osoba odpowiedzialna / Audytor:"));
        _txtRepoOwner = new TextBox { Text = _currentUser, Width = 355, Font = UiTheme.FontBase(10f), Margin = new Padding(0, 0, 0, 18) };
        formFlow.Controls.Add(_txtRepoOwner);

        _btnCreateRepo = UiTheme.CreatePrimaryButton("🛡️ Utwórz Linię Bazową (Baseline)", UiTheme.SuccessGreen, UiTheme.SuccessGreenHover, width: 355, height: 42);
        _btnCreateRepo.Click += async (s, e) => await RegisterNewRepositoryAsync();
        formFlow.Controls.Add(_btnCreateRepo);

        var progPanel = new FlowLayoutPanel { Width = 355, Height = 36, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        _pbRepoProgress = new ProgressBar { Width = 260, Height = 24, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Visible = false, Margin = new Padding(0, 4, 8, 0) };
        _btnCancelRepo = UiTheme.CreateSecondaryButton("Anuluj", width: 85, height: 28);
        _btnCancelRepo.Visible = false;
        _btnCancelRepo.Click += (s, e) => _repoCreationCts?.Cancel();
        progPanel.Controls.AddRange(new Control[] { _pbRepoProgress, _btnCancelRepo });
        formFlow.Controls.Add(progPanel);

        _lblRepoProgress = new Label
        {
            Text = "",
            Size = new Size(355, 44),
            ForeColor = UiTheme.PrimaryBlue,
            Font = UiTheme.FontBase(8.5f),
            Visible = false,
            Margin = new Padding(0, 4, 0, 0)
        };
        formFlow.Controls.Add(_lblRepoProgress);

        leftCard.Controls.Add(formFlow);

        // Prawa karta listy repozytoriów
        var rightCard = UiTheme.CreateCardPanel(new Padding(16));
        rightCard.Dock = DockStyle.Fill;

        var topR = new Panel { Dock = DockStyle.Top, Height = 38 };
        topR.Controls.Add(new Label
        {
            Text = "Nadzorowane Repozytoria",
            Font = UiTheme.FontBase(11.5f, FontStyle.Bold),
            ForeColor = UiTheme.TextPrimary,
            AutoSize = true,
            Location = new Point(0, 6)
        });
        _chkRepoArch = new CheckBox { Text = "Pokaż zarchiwizowane", AutoSize = true, Dock = DockStyle.Right, ForeColor = UiTheme.TextSecondary };
        _chkRepoArch.CheckedChanged += (s, e) => RefreshRepositoriesList();
        topR.Controls.Add(_chkRepoArch);

        var botR = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0) };
        var btnOpenDir = UiTheme.CreateSecondaryButton("📂 Otwórz folder w Eksploratorze", height: 32);
        btnOpenDir.Click += (s, e) =>
        {
            if (_gridRepos.CurrentRow?.DataBoundItem is RepositoryInfo r && Directory.Exists(r.RootPath))
                Process.Start("explorer.exe", r.RootPath);
        };
        var btnArch = UiTheme.CreateSecondaryButton("📦 Zarchiwizuj / Przywróć", height: 32);
        btnArch.Click += (s, e) =>
        {
            if (_gridRepos.CurrentRow?.DataBoundItem is RepositoryInfo r)
            {
                if (r.Status == "Building")
                {
                    MessageBox.Show("Trwa indeksowanie tego repozytorium. Poczekaj na zakończenie lub kliknij Anuluj.", "Operacja w toku", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string next = r.Status == "Active" ? "Archived" : "Active";
                _db.SetRepositoryStatus(r.Id, next);
                LogAuditTrail(r.RootPath, "REPO_STATUS_CHANGED", r.Code, $"Zmieniono status repozytorium na: {next}");
                RefreshRepositoriesList();
            }
        };
        botR.Controls.AddRange(new Control[] { btnOpenDir, btnArch });

        _gridRepos = UiTheme.CreateModernGrid();
        _gridRepos.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridRepos.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { DataPropertyName = "Id", HeaderText = "ID", FillWeight = 7 },
            new DataGridViewTextBoxColumn { DataPropertyName = "Code", HeaderText = "Kod", FillWeight = 15 },
            new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Nazwa", FillWeight = 22 },
            new DataGridViewTextBoxColumn { DataPropertyName = "RootPath", HeaderText = "Ścieżka na dysku", FillWeight = 28 },
            new DataGridViewTextBoxColumn { DataPropertyName = "Owner", HeaderText = "Właściciel", FillWeight = 12 },
            new DataGridViewTextBoxColumn { DataPropertyName = "Status", HeaderText = "Status", FillWeight = 10 },
            new DataGridViewTextBoxColumn { DataPropertyName = "LastScanAt", HeaderText = "Ostatni skan", FillWeight = 16 }
        });

        rightCard.Controls.Add(_gridRepos);
        rightCard.Controls.Add(topR);
        rightCard.Controls.Add(botR);

        layout.Controls.Add(leftCard, 0, 0);
        layout.Controls.Add(rightCard, 1, 0);
        page.Controls.Add(layout);
    }

    private static Label MakeFieldLabel(string text) => new()
    {
        Text = text,
        Font = UiTheme.FontBase(9f, FontStyle.Bold),
        ForeColor = UiTheme.TextPrimary,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 5)
    };

    private void PickRepoFolder()
    {
        using var fbd = new FolderBrowserDialog { Description = "Wybierz katalog do nadzoru" };
        if (fbd.ShowDialog() == DialogResult.OK)
        {
            _selectedNewRepoDir = fbd.SelectedPath;
            _lblPickedRepo.Text = _selectedNewRepoDir;
            _lblPickedRepo.ForeColor = UiTheme.SuccessGreen;
            string dirName = Path.GetFileName(_selectedNewRepoDir.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(_txtRepoCode.Text))
                _txtRepoCode.Text = SanitizeCode(dirName);
            if (string.IsNullOrWhiteSpace(_txtRepoName.Text))
                _txtRepoName.Text = dirName;
        }
    }

    private async Task RegisterNewRepositoryAsync()
    {
        if (_repoCreationCts != null) return;
        if (string.IsNullOrEmpty(_selectedNewRepoDir) || !Directory.Exists(_selectedNewRepoDir))
        {
            MessageBox.Show("Wybierz istniejący katalog do objęcia nadzorem.", "Brak folderu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string code = SanitizeCode(_txtRepoCode.Text);
        string name = _txtRepoName.Text.Trim();
        string owner = string.IsNullOrWhiteSpace(_txtRepoOwner.Text) ? _currentUser : _txtRepoOwner.Text.Trim();
        string rootDir = _selectedNewRepoDir;

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Podaj kod oraz nazwę repozytorium.", "Brak danych", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int repoId;
        try
        {
            repoId = _db.AddRepository(code, name, rootDir, owner);
        }
        catch
        {
            MessageBox.Show($"Repozytorium o kodzie '{code}' już istnieje.", "Błąd", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _db.SetRepositoryStatus(repoId, "Building");
        RefreshRepositoriesList();

        _repoCreationCts = new CancellationTokenSource();
        _btnCreateRepo.Enabled = false;
        _pbRepoProgress.Visible = true;
        _btnCancelRepo.Visible = true;
        _lblRepoProgress.Visible = true;
        _lblRepoProgress.Text = "Inicjalizacja potoku skanowania...";

        var profile = GetSelectedResourceProfile();
        _pbRepoProgress.Style = ProgressBarStyle.Marquee;
        _pbRepoProgress.Value = 0;

        var progress = new Progress<ScanProgressReport>(rep =>
        {
            double doneMb = rep.ProcessedBytes / (1024.0 * 1024.0);
            double totMb = rep.TotalDiscoveredBytes / (1024.0 * 1024.0);

            if (rep.DiscoveryCompleted && rep.TotalDiscoveredBytes > 0)
            {
                int pct = (int)Math.Clamp((rep.ProcessedBytes * 100L) / rep.TotalDiscoveredBytes, 0, 100);
                _pbRepoProgress.Style = ProgressBarStyle.Continuous;
                _pbRepoProgress.Value = pct;
                _lblRepoProgress.Text = $"{rep.Phase}: {pct}% ({rep.SpeedMbPerSec:F0} MB/s)\nPliki: {rep.ProcessedFiles:N0}/{rep.TotalDiscoveredFiles:N0} ({doneMb:N0}/{totMb:N0} MB)";
            }
            else
            {
                _pbRepoProgress.Style = ProgressBarStyle.Marquee;
                _lblRepoProgress.Text = $"Katalogowanie paczek (100/paczka)... ({rep.SpeedMbPerSec:F0} MB/s)\nPrzetworzono: {rep.ProcessedFiles:N0} z {rep.TotalDiscoveredFiles:N0} ({doneMb:N0} MB)";
            }
        });

        try
        {
            int count = await _engine.CreateBaselineStreamedAsync(
                repoId,
                code,
                rootDir,
                owner,
                _currentUser,
                profile,
                progress,
                _repoCreationCts.Token);

            _db.SetRepositoryStatus(repoId, "Active");
            LogAuditTrail(rootDir, "BASELINE_CREATED", code, $"Utworzono linię bazową dla {count} plików (Właściciel: {owner}).");

            _txtRepoCode.Clear();
            _txtRepoName.Clear();
            _selectedNewRepoDir = null;
            _lblPickedRepo.Text = "Nie wybrano folderu";
            _lblPickedRepo.ForeColor = UiTheme.TextSecondary;

            RefreshRepositoriesList();
            SyncRepoDropdowns();
            MessageBox.Show($"Repozytorium '{code}' zostało objęte nadzorem.\nZarejestrowano {count:N0} plików wzorcowych.", "Linia Bazowa Utworzona", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _db.SetRepositoryStatus(repoId, "Cancelled");
            MessageBox.Show($"Tworzenie linii bazowej dla '{code}' zostało anulowane.", "Anulowano", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _repoCreationCts.Dispose();
            _repoCreationCts = null;
            _btnCreateRepo.Enabled = true;
            _pbRepoProgress.Visible = false;
            _btnCancelRepo.Visible = false;
            _lblRepoProgress.Visible = false;
            RefreshRepositoriesList();
        }
    }

    private void RefreshRepositoriesList()
    {
        _gridRepos.DataSource = null;
        _gridRepos.DataSource = _db.GetRepositories(_chkRepoArch.Checked);
        SyncRepoDropdowns();
    }

    private void SyncRepoDropdowns()
    {
        var active = _db.GetRepositories(false).Select(r => r.Code).ToList();
        SyncCombo(_cmbMonRepo, active);
        SyncCombo(_cmbExpRepo, active);
    }

    private void SyncCombo(ComboBox cmb, List<string> codes)
    {
        _isSyncingCombos = true;
        try
        {
            string? prev = cmb.SelectedItem?.ToString();
            cmb.Items.Clear();
            if (codes.Count > 0)
            {
                cmb.Items.AddRange(codes.ToArray());
                cmb.SelectedItem = prev != null && codes.Contains(prev) ? prev : codes[0];
            }
            else
            {
                cmb.Items.Add("Brak");
                cmb.SelectedIndex = 0;
            }
        }
        finally
        {
            _isSyncingCombos = false;
        }
    }

    // =========================================================================
    // ZAKŁADKA 1: MONITOR ZMIAN
    // =========================================================================
    private void BuildMonitorTab(Panel page)
    {
        var card = UiTheme.CreateCardPanel(new Padding(16));
        card.Dock = DockStyle.Fill;

        // Pasek 1: Sterowanie skanowaniem
        var pTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, WrapContents = false };
        pTop.Controls.Add(new Label { Text = "Repozytorium:", AutoSize = true, Margin = new Padding(0, 9, 6, 0), Font = UiTheme.FontBase(9.5f, FontStyle.Bold) });
        _cmbMonRepo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, Margin = new Padding(0, 5, 10, 0) };
        pTop.Controls.Add(_cmbMonRepo);

        _btnScanFast = UiTheme.CreatePrimaryButton("🔍 Skanuj zmiany (Fast-Path)", UiTheme.SuccessGreen, UiTheme.SuccessGreenHover, height: 34);
        _btnScanFast.Margin = new Padding(0, 2, 6, 0);
        _btnScanFast.Click += async (s, e) => await RunRepositoryAuditScanAsync(false);
        pTop.Controls.Add(_btnScanFast);

        _btnScanDeep = UiTheme.CreateSecondaryButton("🔐 Pełny audyt SHA-256", height: 34);
        _btnScanDeep.Margin = new Padding(0, 2, 6, 0);
        _btnScanDeep.Click += async (s, e) => await RunRepositoryAuditScanAsync(true);
        pTop.Controls.Add(_btnScanDeep);

        _btnCancelScan = UiTheme.CreateSecondaryButton("⏹ Anuluj", height: 34);
        _btnCancelScan.Margin = new Padding(0, 2, 10, 0);
        _btnCancelScan.Visible = false;
        _btnCancelScan.Click += (s, e) => _auditScanCts?.Cancel();
        pTop.Controls.Add(_btnCancelScan);

        _chkMtimeOnly = new CheckBox { Text = "Zmiana mtime = zmiana pliku", AutoSize = true, Margin = new Padding(6, 9, 12, 0), ForeColor = UiTheme.TextSecondary };
        _chkMtimeOnly.CheckedChanged += async (s, e) => { if (!_isScanning && _cmbMonRepo.Text != "Brak") await RunRepositoryAuditScanAsync(false); };
        var tt = new ToolTip();
        tt.SetToolTip(_chkMtimeOnly, "Jeżeli włączone, plik jest oznaczany jako zmieniony nawet przy identycznym rozmiarze i zawartości, jeśli zmieniła się data modyfikacji.");
        pTop.Controls.Add(_chkMtimeOnly);

        _lblMonSummary = new Label { Text = "Wybierz repozytorium i uruchom skanowanie", AutoSize = true, ForeColor = UiTheme.TextSecondary, Font = UiTheme.FontBase(9.5f, FontStyle.Bold), Margin = new Padding(8, 9, 0, 0) };
        pTop.Controls.Add(_lblMonSummary);

        // Pasek 2: Filtry i eksport
        var pFlt = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 4, 0, 6) };
        var fltLeft = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false };
        fltLeft.Controls.Add(new Label { Text = "Filtr:", AutoSize = true, Margin = new Padding(0, 7, 6, 0), Font = UiTheme.FontBase(9f, FontStyle.Bold) });

        _cmbMonFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 310, Margin = new Padding(0, 3, 14, 0) };
        _cmbMonFilter.Items.AddRange(new object[] { "Tylko rozbieżności (Nowe / Zmienione / Usunięte)", "Wszystkie pliki", "ZMODYFIKOWANE", "NOWE", "USUNIĘTE", "ZGODNE" });
        _cmbMonFilter.SelectedIndex = 0;
        _cmbMonFilter.SelectedIndexChanged += (s, e) => ApplyMonitorFilters(true);
        fltLeft.Controls.Add(_cmbMonFilter);

        fltLeft.Controls.Add(new Label { Text = "Szukaj:", AutoSize = true, Margin = new Padding(0, 7, 6, 0), Font = UiTheme.FontBase(9f, FontStyle.Bold) });
        _txtMonSearch = new TextBox { PlaceholderText = "Fragment nazwy lub ścieżki...", Width = 250, Margin = new Padding(0, 3, 0, 0) };
        _txtMonSearch.TextChanged += (s, e) => ApplyMonitorFilters(true);
        fltLeft.Controls.Add(_txtMonSearch);

        var btnCsv = UiTheme.CreateSecondaryButton("📄 Eksport CSV", height: 32);
        btnCsv.Dock = DockStyle.Right;
        btnCsv.Click += (s, e) => ExportMonitorCsv();

        pFlt.Controls.Add(fltLeft);
        pFlt.Controls.Add(btnCsv);

        // Pasek 3 (dół): Akcje Baseline
        var pAct = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(0, 8, 0, 0) };
        var actLeft = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false };
        var btnOpen = UiTheme.CreateSecondaryButton("Otwórz plik", height: 34);
        btnOpen.Click += (s, e) => MonOpenFile();
        var btnLoc = UiTheme.CreatePrimaryButton("📂 Pokaż w Eksploratorze", UiTheme.PrimaryBlue, UiTheme.PrimaryBlueHover, height: 34);
        btnLoc.Click += (s, e) => MonLocateInExplorer();
        actLeft.Controls.AddRange(new Control[] { btnOpen, btnLoc });

        var actRight = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
        var btnAccSel = UiTheme.CreatePrimaryButton("✔ Zatwierdź zaznaczone", UiTheme.PrimaryBlue, UiTheme.PrimaryBlueHover, height: 34);
        btnAccSel.Click += async (s, e) => await AcceptChangesToBaselineAsync(true);
        var btnAccAll = UiTheme.CreatePrimaryButton("✅ Zatwierdź wszystkie rozbieżności", UiTheme.SuccessGreen, UiTheme.SuccessGreenHover, height: 34);
        btnAccAll.Click += async (s, e) => await AcceptChangesToBaselineAsync(false);
        actRight.Controls.AddRange(new Control[] { btnAccSel, btnAccAll });

        pAct.Controls.Add(actLeft);
        pAct.Controls.Add(actRight);

        // Pasek 4 (nad akcjami): Paginacja
        var pPage = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(0, 4, 0, 0) };
        _btnMonPrev = UiTheme.CreateSecondaryButton("◀ Poprzednia", height: 28);
        _btnMonPrev.Click += (s, e) => { if (_currentScanPage > 0) { _currentScanPage--; RenderMonitorPage(); } };
        _lblMonPage = new Label { Text = "Strona 1 z 1 (0 pozycji)", AutoSize = true, Font = UiTheme.FontBase(9f, FontStyle.Bold), ForeColor = UiTheme.TextSecondary, Margin = new Padding(12, 6, 12, 0) };
        _btnMonNext = UiTheme.CreateSecondaryButton("Następna ▶", height: 28);
        _btnMonNext.Click += (s, e) => { int maxP = Math.Max(0, (_filteredScanCache.Count - 1) / PageSize); if (_currentScanPage < maxP) { _currentScanPage++; RenderMonitorPage(); } };
        pPage.Controls.AddRange(new Control[] { _btnMonPrev, _lblMonPage, _btnMonNext });

        // Tabela główna
        _gridMon = UiTheme.CreateModernGrid();
        _gridMon.MultiSelect = true;
        _gridMon.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridMon.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "State", HeaderText = "Stan Integralności", FillWeight = 13 },
            new DataGridViewTextBoxColumn { Name = "RelPath", HeaderText = "Folder względny", FillWeight = 24 },
            new DataGridViewTextBoxColumn { Name = "Filename", HeaderText = "Nazwa pliku", FillWeight = 25 },
            new DataGridViewTextBoxColumn { Name = "SizeKb", HeaderText = "Rozmiar [KB]", FillWeight = 10, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } },
            new DataGridViewTextBoxColumn { Name = "MtimeStr", HeaderText = "Data modyfikacji (Dysk)", FillWeight = 14 },
            new DataGridViewTextBoxColumn { Name = "BaselineDate", HeaderText = "Data Linii Bazowej", FillWeight = 14 },
            new DataGridViewTextBoxColumn { Name = "Auditor", HeaderText = "Zatwierdził", FillWeight = 12 }
        });
        _gridMon.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) MonOpenFile(); };
        _gridMon.CellFormatting += (s, e) =>
        {
            if (e.RowIndex >= 0 && _gridMon.Rows[e.RowIndex].Tag is ScanResultItem item)
            {
                if (e.ColumnIndex == 0)
                {
                    e.CellStyle!.ForeColor = item.State switch
                    {
                        "ZGODNY" => UiTheme.SuccessGreen,
                        "NOWY" => UiTheme.PrimaryBlue,
                        "ZMIENIONY" => UiTheme.WarningAmber,
                        "USUNIĘTY" => UiTheme.DangerRed,
                        _ => UiTheme.TextPrimary
                    };
                    e.CellStyle.Font = UiTheme.FontBase(9f, FontStyle.Bold);
                }
            }
        };

        card.Controls.Add(_gridMon);
        card.Controls.Add(pFlt);
        card.Controls.Add(pTop);
        card.Controls.Add(pPage);
        card.Controls.Add(pAct);
        page.Controls.Add(card);
    }

    private async Task RunRepositoryAuditScanAsync(bool forceSha256)
    {
        if (_isScanning) return;
        string repoCode = _cmbMonRepo.Text;
        if (string.IsNullOrEmpty(repoCode) || repoCode == "Brak")
        {
            MessageBox.Show("Dodaj najpierw repozytorium w zakładce 'Repozytoria'.", "Brak wyboru", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var repo = _db.GetRepositories(true).FirstOrDefault(r => r.Code == repoCode);
        if (repo == null) return;
        if (!Directory.Exists(repo.RootPath))
        {
            MessageBox.Show($"Ścieżka repozytorium nie jest dostępna:\n{repo.RootPath}", "Błąd dostępu", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _isScanning = true;
        _auditScanCts = new CancellationTokenSource();
        var ct = _auditScanCts.Token;

        _btnScanFast.Enabled = false;
        _btnScanDeep.Enabled = false;
        _btnCancelScan.Visible = true;
        _lblMonSummary.Text = "Wczytywanie linii bazowej...";
        _lblMonSummary.ForeColor = UiTheme.WarningAmber;
        bool mtimeOnly = _chkMtimeOnly.Checked;

        var progress = new Progress<int>(count =>
        {
            _lblMonSummary.Text = $"Skanowanie... zweryfikowano {count:N0} plików";
        });

        try
        {
            var (results, okC, newC, modC, delC) = await Task.Run(() =>
            {
                var baselineMap = _db.GetRepoBaselineMap(repo.Id);
                var seenRel = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
                var resultBag = new ConcurrentBag<ScanResultItem>();
                var statusUpdates = new ConcurrentBag<(string, long, double, int)>();

                int ok = 0, nw = 0, mod = 0, del = 0, scanned = 0;
                long totalBytes = 0;
                long lastReportTicks = 0;

                int workerCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
                var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = workerCount, CancellationToken = ct };

                Parallel.ForEach(
                    EnumerateRepoFilesFast(repo.RootPath),
                    parallelOpts,
                    () => ArrayPool<byte>.Shared.Rent(262144),
                    (fPath, state, buffer) =>
                    {
                        try
                        {
                            var fi = new FileInfo(fPath);
                            if (!fi.Exists) return buffer;

                            string relP = Path.GetRelativePath(repo.RootPath, fPath).Replace('\\', '/');
                            string? parentRel = Path.GetDirectoryName(relP)?.Replace('\\', '/');
                            string folderRel = string.IsNullOrEmpty(parentRel) || parentRel == "." ? "(Katalog główny)" : parentRel;

                            long currSize = fi.Length;
                            double currMtime = ToUnixTimestamp(fi.LastWriteTimeUtc);
                            string mtimeStr = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                            Interlocked.Add(ref totalBytes, currSize);

                            if (baselineMap.TryGetValue(relP, out var bRow))
                            {
                                seenRel.TryAdd(relP, 0);
                                bool mtimeChanged = Math.Abs(currMtime - bRow.LastMtime) >= 0.01;
                                bool needsHash = forceSha256 || currSize != bRow.FileSize || mtimeChanged;
                                string fileState;

                                if (!needsHash && !mtimeOnly)
                                    fileState = "ZGODNY";
                                else if (mtimeOnly && mtimeChanged && currSize == bRow.FileSize)
                                    fileState = "ZMIENIONY";
                                else
                                    fileState = CalculateSha256Pooled(fPath, buffer) != bRow.FileHash ? "ZMIENIONY" : "ZGODNY";

                                if (fileState == "ZGODNY")
                                {
                                    Interlocked.Increment(ref ok);
                                    if (bRow.IntegrityStatus != "ZGODNY" || currSize != bRow.FileSize || mtimeChanged)
                                        statusUpdates.Add(("ZGODNY", currSize, currMtime, bRow.Id));
                                }
                                else
                                {
                                    Interlocked.Increment(ref mod);
                                    if (bRow.IntegrityStatus != "ZMIENIONY")
                                        statusUpdates.Add(("ZMIENIONY", bRow.FileSize, bRow.LastMtime, bRow.Id));
                                }

                                resultBag.Add(new ScanResultItem
                                {
                                    State = fileState,
                                    RelPath = folderRel,
                                    FullRelPath = relP,
                                    Filename = fi.Name,
                                    SizeBytes = currSize,
                                    Mtime = currMtime,
                                    MtimeStr = mtimeStr,
                                    BaselineDate = bRow.BaselineUpdatedAt,
                                    Auditor = bRow.UpdatedBy ?? "-",
                                    DbId = bRow.Id,
                                    AbsPath = fPath,
                                    RepoId = repo.Id,
                                    RepoCode = repo.Code,
                                    RepoRoot = repo.RootPath
                                });
                            }
                            else
                            {
                                Interlocked.Increment(ref nw);
                                resultBag.Add(new ScanResultItem
                                {
                                    State = "NOWY",
                                    RelPath = folderRel,
                                    FullRelPath = relP,
                                    Filename = fi.Name,
                                    SizeBytes = currSize,
                                    Mtime = currMtime,
                                    MtimeStr = mtimeStr,
                                    BaselineDate = "-",
                                    Auditor = "-",
                                    DbId = null,
                                    AbsPath = fPath,
                                    RepoId = repo.Id,
                                    RepoCode = repo.Code,
                                    RepoRoot = repo.RootPath
                                });
                            }

                            int currScanned = Interlocked.Increment(ref scanned);
                            long nowTicks = Environment.TickCount64;
                            long prevTicks = Interlocked.Read(ref lastReportTicks);
                            if (nowTicks - prevTicks >= 250 && Interlocked.CompareExchange(ref lastReportTicks, nowTicks, prevTicks) == prevTicks)
                            {
                                ((IProgress<int>)progress).Report(currScanned);
                            }
                        }
                        catch { }
                        return buffer;
                    },
                    buffer => ArrayPool<byte>.Shared.Return(buffer)
                );

                foreach (var kvp in baselineMap)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seenRel.ContainsKey(kvp.Key))
                    {
                        del++;
                        var bRow = kvp.Value;
                        if (bRow.IntegrityStatus != "USUNIĘTY")
                            statusUpdates.Add(("USUNIĘTY", bRow.FileSize, bRow.LastMtime, bRow.Id));

                        string? pRel = Path.GetDirectoryName(kvp.Key)?.Replace('\\', '/');
                        string parentRel = string.IsNullOrEmpty(pRel) || pRel == "." ? "(Katalog główny)" : pRel;

                        resultBag.Add(new ScanResultItem
                        {
                            State = "USUNIĘTY",
                            RelPath = parentRel,
                            FullRelPath = kvp.Key,
                            Filename = bRow.Filename,
                            SizeBytes = bRow.FileSize,
                            Mtime = bRow.LastMtime,
                            MtimeStr = "Brak na dysku",
                            BaselineDate = bRow.BaselineUpdatedAt,
                            Auditor = bRow.UpdatedBy ?? "-",
                            DbId = bRow.Id,
                            AbsPath = Path.Combine(repo.RootPath, kvp.Key.Replace('/', Path.DirectorySeparatorChar)),
                            RepoId = repo.Id,
                            RepoCode = repo.Code,
                            RepoRoot = repo.RootPath
                        });
                    }
                }

                if (!statusUpdates.IsEmpty)
                    _db.BulkUpdateStatuses(statusUpdates);

                _db.UpdateRepositoryScanTime(repo.Id);
                int totalOnDisk = ok + nw + mod;
                _db.AddSnapshot(_currentUser, repo.Code, totalOnDisk, ok, nw, mod, del, totalBytes / (1024.0 * 1024.0));

                string scanType = forceSha256 ? "FULL_SHA256_SCAN" : "FAST_PATH_SCAN";
                LogAuditTrail(repo.RootPath, scanType, repo.Code, $"Wynik kontroli: Zgodne={ok}, Nowe={nw}, Zmienione={mod}, Usunięte={del}");

                var sorted = resultBag.OrderBy(x => x.State switch { "ZMIENIONY" => 0, "NOWY" => 1, "USUNIĘTY" => 2, _ => 3 })
                                      .ThenBy(x => x.FullRelPath, StringComparer.OrdinalIgnoreCase)
                                      .ToList();

                return (sorted, ok, nw, mod, del);
            }, ct);

            _scanResultsCache = results;
            int disc = newC + modC + delC;
            _lblMonSummary.Text = $"✔ Zgodne: {okC:N0}   |   Nowe: {newC:N0}   |   Zmienione: {modC:N0}   |   Usunięte: {delC:N0}";
            _lblMonSummary.ForeColor = disc == 0 ? UiTheme.SuccessGreen : UiTheme.WarningAmber;
            ApplyMonitorFilters(true);
            RefreshRepositoriesList();
        }
        catch (OperationCanceledException)
        {
            _lblMonSummary.Text = "Skanowanie przerwane.";
            _lblMonSummary.ForeColor = UiTheme.TextSecondary;
        }
        finally
        {
            _auditScanCts?.Dispose();
            _auditScanCts = null;
            _isScanning = false;
            _btnScanFast.Enabled = true;
            _btnScanDeep.Enabled = true;
            _btnCancelScan.Visible = false;
        }
    }

    private void ApplyMonitorFilters(bool resetPage)
    {
        if (resetPage) _currentScanPage = 0;
        string flt = _cmbMonFilter.Text;
        string q = _txtMonSearch.Text.Trim().ToLowerInvariant();

        _filteredScanCache = _scanResultsCache.Where(item =>
        {
            if (flt.StartsWith("Tylko rozbieżności") && item.State == "ZGODNY") return false;
            if (flt == "ZMODYFIKOWANE" && item.State != "ZMIENIONY") return false;
            if (flt == "NOWE" && item.State != "NOWY") return false;
            if (flt == "USUNIĘTE" && item.State != "USUNIĘTY") return false;
            if (flt == "ZGODNE" && item.State != "ZGODNY") return false;

            if (!string.IsNullOrEmpty(q) &&
                !item.Filename.ToLowerInvariant().Contains(q) &&
                !item.RelPath.ToLowerInvariant().Contains(q))
                return false;

            return true;
        }).ToList();

        RenderMonitorPage();
    }

    private void RenderMonitorPage()
    {
        int total = _filteredScanCache.Count;
        var pageItems = _filteredScanCache.Skip(_currentScanPage * PageSize).Take(PageSize).ToList();

        _gridMon.SuspendLayout();
        try
        {
            _gridMon.Rows.Clear();
            if (pageItems.Count > 0)
            {
                var rows = new DataGridViewRow[pageItems.Count];
                for (int i = 0; i < pageItems.Count; i++)
                {
                    var item = pageItems[i];
                    var row = new DataGridViewRow();
                    row.CreateCells(_gridMon,
                        item.State,
                        item.RelPath,
                        item.Filename,
                        $"{item.SizeBytes / 1024.0:N1}",
                        item.MtimeStr,
                        item.BaselineDate,
                        item.Auditor);
                    row.Tag = item;
                    rows[i] = row;
                }
                _gridMon.Rows.AddRange(rows);
            }
        }
        finally
        {
            _gridMon.ResumeLayout();
        }

        int maxPage = Math.Max(1, (total + PageSize - 1) / PageSize);
        int currDisp = _currentScanPage + 1;
        _lblMonPage.Text = $"Strona {currDisp} z {maxPage} ({total:N0} pozycji)";
        _btnMonPrev.Enabled = _currentScanPage > 0;
        _btnMonNext.Enabled = currDisp < maxPage;
    }

    private void MonOpenFile()
    {
        if (_gridMon.CurrentRow?.Tag is not ScanResultItem item) return;
        if (File.Exists(item.AbsPath))
            Process.Start(new ProcessStartInfo(item.AbsPath) { UseShellExecute = true });
        else
            MessageBox.Show("Plik nie istnieje na dysku (został usunięty).", "Brak pliku", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void MonLocateInExplorer()
    {
        if (_gridMon.CurrentRow?.Tag is not ScanResultItem item) return;
        SelectPage(2);
        _cmbExpRepo.SelectedItem = item.RepoCode;
        OnExplorerRepoSelected(item.RepoCode);
        string? targetDir = Path.GetDirectoryName(item.AbsPath);
        if (targetDir != null)
        {
            var node = FindTreeNodeByPath(_treeExpFolders.Nodes, targetDir);
            if (node != null)
            {
                _treeExpFolders.SelectedNode = node;
                node.EnsureVisible();
            }
        }
    }

    private async Task AcceptChangesToBaselineAsync(bool onlySelected)
    {
        if (_isScanning) return;
        List<ScanResultItem> candidates;

        if (onlySelected)
        {
            candidates = _gridMon.SelectedRows.Cast<DataGridViewRow>()
                .Select(r => r.Tag as ScanResultItem)
                .Where(x => x != null && x.State != "ZGODNY")
                .Cast<ScanResultItem>()
                .ToList();
            if (candidates.Count == 0)
            {
                MessageBox.Show("Zaznacz rozbieżności na liście.", "Wybór", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        else
        {
            candidates = (_filteredScanCache.Count > 0 ? _filteredScanCache : _scanResultsCache)
                .Where(x => x.State != "ZGODNY")
                .ToList();
        }

        var itemsToAccept = candidates.GroupBy(x => (x.RepoId, x.FullRelPath)).Select(g => g.First()).ToList();
        if (itemsToAccept.Count == 0)
        {
            MessageBox.Show("Wybrane elementy są już zgodne z Linią Bazową.", "Brak zmian", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string? reason = PromptAuditReason(itemsToAccept.Count);
        if (reason == null) return;

        await Task.Run(() =>
        {
            string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var newInserts = new List<(int, string, string, string, long, double, string, string)>();
            var modUpdates = new List<(string, long, double, string, string, int)>();
            var delIds = new List<int>();
            string repoRoot = itemsToAccept[0].RepoRoot;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(262144);

            try
            {
                foreach (var item in itemsToAccept)
                {
                    if (item.State == "NOWY" && File.Exists(item.AbsPath))
                    {
                        var fi = new FileInfo(item.AbsPath);
                        string hash = CalculateSha256Pooled(item.AbsPath, buffer);
                        newInserts.Add((item.RepoId, item.FullRelPath, item.Filename, hash, fi.Length, ToUnixTimestamp(fi.LastWriteTimeUtc), nowStr, _currentUser));
                        LogAuditTrail(repoRoot, "BASELINE_ADD_NEW", item.FullRelPath, $"Zatwierdzono nowy plik | Powód: {reason}");
                    }
                    else if (item.State == "ZMIENIONY" && File.Exists(item.AbsPath) && item.DbId.HasValue)
                    {
                        var fi = new FileInfo(item.AbsPath);
                        string hash = CalculateSha256Pooled(item.AbsPath, buffer);
                        modUpdates.Add((hash, fi.Length, ToUnixTimestamp(fi.LastWriteTimeUtc), nowStr, _currentUser, item.DbId.Value));
                        LogAuditTrail(repoRoot, "BASELINE_ACCEPT_MODIFIED", item.FullRelPath, $"Zatwierdzono modyfikację (nowy hash: {hash}) | Powód: {reason}");
                    }
                    else if (item.State == "USUNIĘTY" && item.DbId.HasValue)
                    {
                        delIds.Add(item.DbId.Value);
                        LogAuditTrail(repoRoot, "BASELINE_CONFIRM_DELETED", item.FullRelPath, $"Potwierdzono usunięcie pliku z repozytorium | Powód: {reason}");
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            _db.BulkInsertBaseline(newInserts);
            _db.AcceptModifiedFiles(modUpdates);
            _db.DeleteBaselineRecords(delIds);
        });

        MessageBox.Show($"Zaktualizowano Linię Bazową dla {itemsToAccept.Count:N0} plików i zapisano wpis w Audit Trail.", "Zatwierdzono", MessageBoxButtons.OK, MessageBoxIcon.Information);
        await RunRepositoryAuditScanAsync(false);
    }

    private void ExportMonitorCsv()
    {
        if (_filteredScanCache.Count == 0) return;
        using var sfd = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "Raport_Zmian_Repozytorium.csv" };
        if (sfd.ShowDialog() != DialogResult.OK) return;

        using var sw = new StreamWriter(sfd.FileName, false, new UTF8Encoding(true));
        sw.WriteLine("Stan Integralności;Folder Względny;Nazwa Pliku;Rozmiar [B];Data Modyfikacji (Dysk);Data Linii Bazowej;Zatwierdził");
        foreach (var i in _filteredScanCache)
            sw.WriteLine($"{i.State};{i.RelPath};{i.Filename};{i.SizeBytes};{i.MtimeStr};{i.BaselineDate};{i.Auditor}");

        MessageBox.Show($"Zapisano raport do:\n{sfd.FileName}", "Eksport", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // =========================================================================
    // ZAKŁADKA 2: EKSPLORATOR REPOZYTORIUM
    // =========================================================================
    private void BuildExplorerTab(Panel page)
    {
        var card = UiTheme.CreateCardPanel(new Padding(16));
        card.Dock = DockStyle.Fill;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42 };
        top.Controls.Add(new Label { Text = "Repozytorium:", AutoSize = true, Margin = new Padding(0, 8, 6, 0), Font = UiTheme.FontBase(9.5f, FontStyle.Bold) });
        _cmbExpRepo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 4, 10, 0) };
        _cmbExpRepo.SelectedIndexChanged += (s, e) => { if (!_isSyncingCombos) OnExplorerRepoSelected(_cmbExpRepo.Text); };
        top.Controls.Add(_cmbExpRepo);

        var btnRef = UiTheme.CreateSecondaryButton("🔄 Odśwież strukturę", height: 32);
        btnRef.Margin = new Padding(0, 2, 0, 0);
        btnRef.Click += (s, e) => OnExplorerRepoSelected(_cmbExpRepo.Text);
        top.Controls.Add(btnRef);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 350, Padding = new Padding(0, 8, 0, 0) };
        _treeExpFolders = new TreeView
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Font = UiTheme.FontBase(9.5f),
            ItemHeight = 26
        };
        _treeExpFolders.AfterSelect += (s, e) => OnExplorerFolderSelected(e.Node);
        split.Panel1.Controls.Add(_treeExpFolders);

        var bot = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0) };
        var btnOpen = UiTheme.CreatePrimaryButton("Otwórz zaznaczony plik", UiTheme.PrimaryBlue, UiTheme.PrimaryBlueHover, height: 32);
        btnOpen.Click += (s, e) =>
        {
            if (_gridExpFiles.CurrentRow?.Tag is string p && File.Exists(p))
                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
        };
        bot.Controls.Add(btnOpen);

        _gridExpFiles = UiTheme.CreateModernGrid();
        _gridExpFiles.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridExpFiles.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Nazwa pliku", FillWeight = 36 },
            new DataGridViewTextBoxColumn { HeaderText = "Stan", FillWeight = 14 },
            new DataGridViewTextBoxColumn { HeaderText = "Rozmiar [KB]", FillWeight = 12, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } },
            new DataGridViewTextBoxColumn { HeaderText = "Data na dysku", FillWeight = 19 },
            new DataGridViewTextBoxColumn { HeaderText = "Data Baseline", FillWeight = 19 }
        });
        _gridExpFiles.CellDoubleClick += (s, e) =>
        {
            if (e.RowIndex >= 0 && _gridExpFiles.Rows[e.RowIndex].Tag is string p && File.Exists(p))
                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
        };
        _gridExpFiles.CellFormatting += (s, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == 1)
            {
                string st = _gridExpFiles.Rows[e.RowIndex].Cells[1].Value?.ToString() ?? "";
                e.CellStyle!.ForeColor = st switch
                {
                    "ZGODNY" => UiTheme.SuccessGreen,
                    "NOWY" => UiTheme.PrimaryBlue,
                    "ZMIENIONY" => UiTheme.WarningAmber,
                    "USUNIĘTY" => UiTheme.DangerRed,
                    _ => UiTheme.TextPrimary
                };
                e.CellStyle.Font = UiTheme.FontBase(9f, FontStyle.Bold);
            }
        };

        split.Panel2.Controls.Add(_gridExpFiles);
        split.Panel2.Controls.Add(bot);

        card.Controls.Add(split);
        card.Controls.Add(top);
        page.Controls.Add(card);
    }

    private void RefreshExplorerRepos()
    {
        SyncRepoDropdowns();
        if (_cmbExpRepo.Text != "Brak") OnExplorerRepoSelected(_cmbExpRepo.Text);
    }

    private void OnExplorerRepoSelected(string code)
    {
        if (string.IsNullOrEmpty(code) || code == "Brak") return;
        var repo = _db.GetRepositories(true).FirstOrDefault(r => r.Code == code);
        if (repo == null) return;

        _currExpRepoId = repo.Id;
        _currExpRepoRoot = repo.RootPath;
        _treeExpFolders.BeginUpdate();
        try
        {
            _treeExpFolders.Nodes.Clear();
            _gridExpFiles.Rows.Clear();

            if (!Directory.Exists(_currExpRepoRoot)) return;
            var rootNode = new TreeNode($"📁 {code}") { Tag = ("", _currExpRepoRoot) };
            _treeExpFolders.Nodes.Add(rootNode);
            PopulateExpFolders("", _currExpRepoRoot, rootNode);
            rootNode.Expand();
        }
        finally
        {
            _treeExpFolders.EndUpdate();
        }
    }

    private static void PopulateExpFolders(string relStr, string physDir, TreeNode parent)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(physDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(dir);
                if (name is "._audit_meta" or "._vault_meta") continue;
                string currRel = string.IsNullOrEmpty(relStr) ? name : $"{relStr}/{name}";
                var node = new TreeNode($"📁 {name}") { Tag = (currRel, dir) };
                parent.Nodes.Add(node);
                PopulateExpFolders(currRel, dir, node);
            }
        }
        catch { }
    }

    private void OnExplorerFolderSelected(TreeNode? node)
    {
        if (node?.Tag is not ValueTuple<string, string> info) return;
        var (relFolder, physFolder) = info;
        if (!Directory.Exists(physFolder)) return;

        var baselineMap = _db.GetRepoBaselineMap(_currExpRepoId);
        var seenRel = new HashSet<string>(StringComparer.Ordinal);
        var newRows = new List<DataGridViewRow>();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(262144);

        try
        {
            foreach (var fPath in Directory.EnumerateFiles(physFolder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var fi = new FileInfo(fPath);
                string relFile = string.IsNullOrEmpty(relFolder) ? fi.Name : $"{relFolder}/{fi.Name}";
                string mtimeStr = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                string sizeKb = $"{fi.Length / 1024.0:N1}";
                string state, bDate;

                if (baselineMap.TryGetValue(relFile, out var bRow))
                {
                    seenRel.Add(relFile);
                    double currMt = ToUnixTimestamp(fi.LastWriteTimeUtc);
                    bool mtChg = Math.Abs(currMt - bRow.LastMtime) >= 0.01;
                    if (_chkMtimeOnly.Checked && mtChg && fi.Length == bRow.FileSize)
                        state = "ZMIENIONY";
                    else if (fi.Length == bRow.FileSize && !mtChg)
                        state = "ZGODNY";
                    else
                        state = CalculateSha256Pooled(fPath, buffer) != bRow.FileHash ? "ZMIENIONY" : "ZGODNY";
                    bDate = bRow.BaselineUpdatedAt;
                }
                else
                {
                    state = "NOWY";
                    bDate = "-";
                }

                var row = new DataGridViewRow();
                row.CreateCells(_gridExpFiles, fi.Name, state, sizeKb, mtimeStr, bDate);
                row.Tag = fPath;
                newRows.Add(row);
            }

            foreach (var kvp in baselineMap)
            {
                if (seenRel.Contains(kvp.Key)) continue;
                string? pRel = Path.GetDirectoryName(kvp.Key)?.Replace('\\', '/');
                if (string.IsNullOrEmpty(pRel) || pRel == ".") pRel = "";
                if (pRel == relFolder)
                {
                    var row = new DataGridViewRow();
                    row.CreateCells(_gridExpFiles, kvp.Value.Filename, "USUNIĘTY", $"{kvp.Value.FileSize / 1024.0:N1}", "Brak na dysku", kvp.Value.BaselineUpdatedAt);
                    row.Tag = Path.Combine(_currExpRepoRoot, kvp.Key.Replace('/', Path.DirectorySeparatorChar));
                    newRows.Add(row);
                }
            }
        }
        catch { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _gridExpFiles.SuspendLayout();
        try
        {
            _gridExpFiles.Rows.Clear();
            if (newRows.Count > 0)
                _gridExpFiles.Rows.AddRange(newRows.ToArray());
        }
        finally
        {
            _gridExpFiles.ResumeLayout();
        }
    }

    private static TreeNode? FindTreeNodeByPath(TreeNodeCollection nodes, string targetDir)
    {
        foreach (TreeNode n in nodes)
        {
            if (n.Tag is ValueTuple<string, string> info && string.Equals(Path.GetFullPath(info.Item2), Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                return n;
            var found = FindTreeNodeByPath(n.Nodes, targetDir);
            if (found != null) return found;
        }
        return null;
    }

    // =========================================================================
    // ZAKŁADKA 3: DASHBOARD (KPI + WYKRES + MIGAWKI)
    // =========================================================================
    private void BuildDashboardTab(Panel page)
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 44 };
        var topFlow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false };
        topFlow.Controls.Add(new Label { Text = "Zakres analizy:", Font = UiTheme.FontBase(9.5f, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        _cmbDashScope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 4, 0, 0) };
        _cmbDashScope.SelectedIndexChanged += (s, e) => { if (!_isSyncingCombos) RefreshDashboardTab(); };
        topFlow.Controls.Add(_cmbDashScope);

        var btnCsv = UiTheme.CreateSecondaryButton("📄 Eksportuj historię CSV", height: 32);
        btnCsv.Dock = DockStyle.Right;
        btnCsv.Click += (s, e) => ExportDashboardCsv();

        top.Controls.Add(topFlow);
        top.Controls.Add(btnCsv);

        // Karty KPI z lewym paskiem akcentu
        var kpiTable = new TableLayoutPanel { Dock = DockStyle.Top, Height = 104, ColumnCount = 5, RowCount = 1, Padding = new Padding(0, 4, 0, 8) };
        for (int i = 0; i < 5; i++) kpiTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));

        kpiTable.Controls.Add(UiTheme.CreateKpiCard("PLIKI POD NADZOREM", UiTheme.PrimaryBlue, out _kpiTotV, out _kpiTotS), 0, 0);
        kpiTable.Controls.Add(UiTheme.CreateKpiCard("WSKAŹNIK SPÓJNOŚCI", UiTheme.SuccessGreen, out _kpiIntV, out _kpiIntS), 1, 0);
        kpiTable.Controls.Add(UiTheme.CreateKpiCard("ZMODYFIKOWANE", UiTheme.WarningAmber, out _kpiModV, out _kpiModS), 2, 0);
        kpiTable.Controls.Add(UiTheme.CreateKpiCard("NOWE / NIEZATWIERDZONE", Color.FromArgb(2, 132, 199), out _kpiNewV, out _kpiNewS), 3, 0);
        kpiTable.Controls.Add(UiTheme.CreateKpiCard("USUNIĘTE Z DYSKU", UiTheme.DangerRed, out _kpiDelV, out _kpiDelS), 4, 0);

        // Karta wykresu trendu
        var chartCard = UiTheme.CreateCardPanel(new Padding(12));
        chartCard.Dock = DockStyle.Top;
        chartCard.Height = 215;
        chartCard.Margin = new Padding(0, 0, 0, 12);

        var chartHdr = new Panel { Dock = DockStyle.Top, Height = 24 };
        chartHdr.Controls.Add(new Label { Text = "Trend liczby plików oraz rozbieżności (ostatnie 20 kontroli)", Font = UiTheme.FontBase(9.5f, FontStyle.Bold), AutoSize = true, Location = new Point(0, 2) });
        chartHdr.Controls.Add(new Label { Text = "● Pliki ogółem    ● Rozbieżności", Font = UiTheme.FontBase(8.5f), ForeColor = UiTheme.TextSecondary, AutoSize = true, Dock = DockStyle.Right });

        _chartPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        _chartPanel.Paint += (s, e) => DrawTrendChart(e.Graphics, _chartPanel.ClientRectangle);
        _chartPanel.Resize += (s, e) => _chartPanel.Invalidate();

        chartCard.Controls.Add(_chartPanel);
        chartCard.Controls.Add(chartHdr);

        // Karta dziennika migawek
        var gridCard = UiTheme.CreateCardPanel(new Padding(14));
        gridCard.Dock = DockStyle.Fill;

        _gridSnaps = UiTheme.CreateModernGrid();
        _gridSnaps.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridSnaps.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Data i godzina", FillWeight = 16 },
            new DataGridViewTextBoxColumn { HeaderText = "Audytor", FillWeight = 14 },
            new DataGridViewTextBoxColumn { HeaderText = "Repozytorium", FillWeight = 13 },
            new DataGridViewTextBoxColumn { HeaderText = "Pliki", FillWeight = 8 },
            new DataGridViewTextBoxColumn { HeaderText = "Przyrost", FillWeight = 8 },
            new DataGridViewTextBoxColumn { HeaderText = "Zgodne", FillWeight = 8 },
            new DataGridViewTextBoxColumn { HeaderText = "Nowe", FillWeight = 8 },
            new DataGridViewTextBoxColumn { HeaderText = "Zmienione", FillWeight = 9 },
            new DataGridViewTextBoxColumn { HeaderText = "Usunięte", FillWeight = 8 },
            new DataGridViewTextBoxColumn { HeaderText = "Rozmiar [MB]", FillWeight = 10 }
        });
        gridCard.Controls.Add(_gridSnaps);

        var spacer = new Panel { Dock = DockStyle.Top, Height = 12 };

        page.Controls.Add(gridCard);
        page.Controls.Add(spacer);
        page.Controls.Add(chartCard);
        page.Controls.Add(kpiTable);
        page.Controls.Add(top);
    }

    private void RefreshDashboardTab()
    {
        var scopes = new List<string> { "Wszystkie" };
        scopes.AddRange(_db.GetRepositories(true).Select(r => r.Code));
        SyncCombo(_cmbDashScope, scopes);

        string curr = _cmbDashScope.Text;
        var snaps = _db.GetSnapshots(curr);
        _dashSnapshotsCache = snaps;

        if (snaps.Count > 0)
        {
            var last = snaps[^1];
            string deltaStr = snaps.Count >= 2 ? $"{last.TotalFiles - snaps[^2].TotalFiles:+0;-0;0} od poprzedniego skanu" : "Pierwszy pomiar";
            double health = (last.TotalFiles + last.DeletedCount) > 0 ? (last.OkCount * 100.0 / (last.TotalFiles + last.DeletedCount)) : 100.0;

            _kpiTotV.Text = last.TotalFiles.ToString("N0");
            _kpiTotS.Text = $"{deltaStr} ({last.TotalSizeMb:F1} MB)";
            _kpiIntV.Text = $"{health:F1}%";
            _kpiIntS.Text = $"{last.OkCount:N0} zgodnych z Baseline";
            _kpiModV.Text = last.ModifiedCount.ToString("N0");
            _kpiModS.Text = "Zmienione po Baseline";
            _kpiNewV.Text = last.NewCount.ToString("N0");
            _kpiNewS.Text = "Nowe pliki w folderach";
            _kpiDelV.Text = last.DeletedCount.ToString("N0");
            _kpiDelS.Text = "Brakujące na dysku";
        }
        else
        {
            _kpiTotV.Text = _kpiIntV.Text = _kpiModV.Text = _kpiNewV.Text = _kpiDelV.Text = "0";
        }

        _gridSnaps.SuspendLayout();
        try
        {
            _gridSnaps.Rows.Clear();
            if (snaps.Count > 0)
            {
                var rows = new DataGridViewRow[snaps.Count];
                int rIdx = 0;
                for (int i = snaps.Count - 1; i >= 0; i--)
                {
                    var s = snaps[i];
                    string diff = i > 0 ? $"{s.TotalFiles - snaps[i - 1].TotalFiles:+0;-0;0}" : "Start";
                    var row = new DataGridViewRow();
                    row.CreateCells(_gridSnaps, s.Timestamp, s.Operator, s.Scope, s.TotalFiles, diff, s.OkCount, s.NewCount, s.ModifiedCount, s.DeletedCount, $"{s.TotalSizeMb:F2}");
                    rows[rIdx++] = row;
                }
                _gridSnaps.Rows.AddRange(rows);
            }
        }
        finally
        {
            _gridSnaps.ResumeLayout();
        }
        _chartPanel.Invalidate();
    }

    private void DrawTrendChart(Graphics g, Rectangle rect)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var snaps = _dashSnapshotsCache.TakeLast(20).ToList();
        using var font = UiTheme.FontBase(8f);
        using var boldFont = UiTheme.FontBase(8f, FontStyle.Bold);

        if (snaps.Count == 0)
        {
            TextRenderer.DrawText(g, "Brak danych pomiarowych – uruchom skanowanie w zakładce Monitor Zmian", font, rect, UiTheme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int padL = 48, padR = 25, padT = 18, padB = 24;
        int pw = Math.Max(10, rect.Width - padL - padR);
        int ph = Math.Max(10, rect.Height - padT - padB);

        var totals = snaps.Select(s => s.TotalFiles).ToList();
        var diffs = snaps.Select(s => s.NewCount + s.ModifiedCount + s.DeletedCount).ToList();
        int maxV = Math.Max(Math.Max(totals.Max(), diffs.Max()), 5);

        using var gridPen = new Pen(UiTheme.CardBorder) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        for (int i = 0; i < 4; i++)
        {
            float y = padT + (ph * i / 3f);
            g.DrawLine(gridPen, padL, y, rect.Width - padR, y);
            int labelVal = (int)Math.Round(maxV * (1.0 - i / 3.0));
            g.DrawString(labelVal.ToString(), font, Brushes.SlateGray, 6, y - 7);
        }

        int n = snaps.Count;
        var ptsTot = new PointF[n];
        var ptsDif = new PointF[n];

        for (int i = 0; i < n; i++)
        {
            float x = padL + (n == 1 ? pw / 2f : (pw * i / (float)(n - 1)));
            ptsTot[i] = new PointF(x, padT + ph - (totals[i] / (float)maxV * ph));
            ptsDif[i] = new PointF(x, padT + ph - (diffs[i] / (float)maxV * ph));
            if (n <= 8 || i == 0 || i == n - 1 || i % 2 == 0)
            {
                string tsShort = snaps[i].Timestamp.Length >= 16 ? snaps[i].Timestamp[5..16] : snaps[i].Timestamp;
                g.DrawString(tsShort, font, Brushes.SlateGray, x - 26, rect.Height - 17);
            }
        }

        if (n > 1)
        {
            using var penTot = new Pen(UiTheme.PrimaryBlue, 2.5f);
            using var penDif = new Pen(UiTheme.WarningAmber, 2f);
            g.DrawLines(penTot, ptsTot);
            g.DrawLines(penDif, ptsDif);
        }

        for (int i = 0; i < n; i++)
        {
            g.FillEllipse(Brushes.RoyalBlue, ptsTot[i].X - 4f, ptsTot[i].Y - 4f, 8, 8);
            g.DrawString(totals[i].ToString(), boldFont, Brushes.DarkBlue, ptsTot[i].X - 8, ptsTot[i].Y - 17);
            if (diffs[i] > 0)
                g.FillEllipse(Brushes.DarkOrange, ptsDif[i].X - 3.5f, ptsDif[i].Y - 3.5f, 7, 7);
        }
    }

    private void ExportDashboardCsv()
    {
        using var sfd = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "Trendy_Audytu_Repozytorium.csv" };
        if (sfd.ShowDialog() != DialogResult.OK) return;

        using var sw = new StreamWriter(sfd.FileName, false, new UTF8Encoding(true));
        sw.WriteLine("Data i godzina;Audytor;Repozytorium;Pliki;Przyrost;Zgodne;Nowe;Zmienione;Usunięte;Rozmiar [MB]");
        foreach (DataGridViewRow row in _gridSnaps.Rows)
            sw.WriteLine(string.Join(";", row.Cells.Cast<DataGridViewCell>().Select(c => c.Value?.ToString() ?? "")));

        MessageBox.Show($"Zapisano historię trendów do:\n{sfd.FileName}", "Eksport", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // =========================================================================
    // ZAKŁADKA 4: HISTORIA I AUDYT
    // =========================================================================
    private void BuildAuditTab(Panel page)
    {
        var card = UiTheme.CreateCardPanel(new Padding(16));
        card.Dock = DockStyle.Fill;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44 };
        top.Controls.Add(new Label { Text = "Dziennik Zdarzeń Audytowych:", Font = UiTheme.FontBase(10f, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 8, 10, 0) });

        _cmbAuditRepo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Margin = new Padding(0, 4, 10, 0) };
        _cmbAuditRepo.SelectedIndexChanged += (s, e) => { if (!_isSyncingCombos) RefreshAuditTab(); };
        top.Controls.Add(_cmbAuditRepo);

        var btnRef = UiTheme.CreateSecondaryButton("🔄 Odśwież", height: 32);
        btnRef.Margin = new Padding(0, 2, 8, 0);
        btnRef.Click += (s, e) => RefreshAuditTab();
        top.Controls.Add(btnRef);

        var btnPdf = UiTheme.CreatePrimaryButton("📑 Generuj raport PDF", UiTheme.PrimaryBlue, UiTheme.PrimaryBlueHover, height: 32);
        btnPdf.Margin = new Padding(0, 2, 0, 0);
        btnPdf.Click += (s, e) => ExportRepoPdfReport();
        top.Controls.Add(btnPdf);

        _gridAudit = UiTheme.CreateModernGrid();
        _gridAudit.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridAudit.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Data i Czas", FillWeight = 14 },
            new DataGridViewTextBoxColumn { HeaderText = "Audytor", FillWeight = 12 },
            new DataGridViewTextBoxColumn { HeaderText = "Zdarzenie", FillWeight = 18 },
            new DataGridViewTextBoxColumn { HeaderText = "Obiekt / Ścieżka", FillWeight = 22 },
            new DataGridViewTextBoxColumn { HeaderText = "Szczegóły i Uzasadnienie", FillWeight = 34 }
        });

        card.Controls.Add(_gridAudit);
        card.Controls.Add(top);
        page.Controls.Add(card);
    }

    private void RefreshAuditTab()
    {
        var repos = _db.GetRepositories(true);
        var opts = new List<string> { "Centralny (Wszystkie)" };
        opts.AddRange(repos.Select(r => r.Code));
        SyncCombo(_cmbAuditRepo, opts);

        string curr = _cmbAuditRepo.Text;
        string? csvFile = curr == "Centralny (Wszystkie)"
            ? Path.Combine(_vaultDir, "central_audit_trail.csv")
            : repos.FirstOrDefault(r => r.Code == curr) is RepositoryInfo r ? Path.Combine(r.RootPath, "._audit_meta", "audit_trail.csv") : null;

        var rows = new List<DataGridViewRow>();
        if (csvFile != null && File.Exists(csvFile))
        {
            foreach (var line in File.ReadAllLines(csvFile, Encoding.UTF8).Skip(1).Reverse())
            {
                var parts = line.Split(';');
                if (parts.Length >= 5)
                {
                    var row = new DataGridViewRow();
                    row.CreateCells(_gridAudit, parts[0], parts[1], parts[2], parts[3], parts[4]);
                    rows.Add(row);
                }
            }
        }

        _gridAudit.SuspendLayout();
        try
        {
            _gridAudit.Rows.Clear();
            if (rows.Count > 0)
                _gridAudit.Rows.AddRange(rows.ToArray());
        }
        finally
        {
            _gridAudit.ResumeLayout();
        }
    }

    private void ExportRepoPdfReport()
    {
        string code = _cmbAuditRepo.Text;
        if (string.IsNullOrEmpty(code) || code == "Centralny (Wszystkie)")
        {
            MessageBox.Show("Wybierz repozytorium, dla którego chcesz wygenerować raport PDF.", "Brak wyboru", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var repo = _db.GetRepositories(true).FirstOrDefault(r => r.Code == code);
        if (repo == null) return;

        using var sfd = new SaveFileDialog { Filter = "Pliki PDF (*.pdf)|*.pdf", FileName = $"Raport_Audytowy_{code}.pdf" };
        if (sfd.ShowDialog() != DialogResult.OK) return;

        try
        {
            PdfReportGenerator.Generate(repo, _db, sfd.FileName);
            MessageBox.Show($"Utworzono raport audytowy:\n{sfd.FileName}", "Raport PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Nie udało się wygenerować raportu PDF:\n{ex.Message}", "Błąd", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // =========================================================================
    // NARZĘDZIA POMOCNICZE
    // =========================================================================
    private void LogAuditTrail(string repoRoot, string action, string target, string details)
    {
        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string line = $"{ts};{_currentUser};{action};{target};{details}";

        if (!string.IsNullOrEmpty(repoRoot) && Directory.Exists(repoRoot))
        {
            string metaDir = Path.Combine(repoRoot, "._audit_meta");
            var di = Directory.CreateDirectory(metaDir);
            try { di.Attributes |= FileAttributes.Hidden; } catch { }
            AppendCsvLine(Path.Combine(metaDir, "audit_trail.csv"), line);
        }
        AppendCsvLine(Path.Combine(_vaultDir, "central_audit_trail.csv"), line);
    }

    private static void AppendCsvLine(string path, string line)
    {
        try
        {
            bool exists = File.Exists(path);
            using var sw = new StreamWriter(path, append: true, encoding: new UTF8Encoding(true));
            if (!exists) sw.WriteLine("Timestamp;Operator;Action;Target;Details");
            sw.WriteLine(line);
        }
        catch { }
    }

    private static IEnumerable<string> EnumerateRepoFilesFast(string rootDir)
    {
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            RecurseSubdirectories = false
        };

        var stack = new Stack<string>();
        stack.Push(rootDir);
        while (stack.Count > 0)
        {
            string curr = stack.Pop();
            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(curr, "*", opts); }
            catch { continue; }

            foreach (var d in subDirs)
            {
                string name = Path.GetFileName(d);
                if (name is not ("._audit_meta" or "._vault_meta"))
                    stack.Push(d);
            }

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(curr, "*", opts); }
            catch { continue; }

            foreach (var f in files)
                yield return f;
        }
    }

    private static string CalculateSha256Pooled(string filePath, byte[] buffer)
    {
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: buffer.Length,
                options: FileOptions.SequentialScan);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.AppendData(buffer, 0, bytesRead);
            }
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant()[..16];
        }
        catch
        {
            return "ERROR";
        }
    }

    private static double ToUnixTimestamp(DateTime utcTime) =>
        (utcTime - DateTime.UnixEpoch).TotalSeconds;

    private static string SanitizeCode(string text)
    {
        string[,] map = { { "ą", "a" }, { "ć", "c" }, { "ę", "e" }, { "ł", "l" }, { "ń", "n" }, { "ó", "o" }, { "ś", "s" }, { "ź", "z" }, { "ż", "z" },
                          { "Ą", "A" }, { "Ć", "C" }, { "Ę", "E" }, { "Ł", "L" }, { "Ń", "N" }, { "Ó", "O" }, { "Ś", "S" }, { "Ź", "Z" }, { "Ż", "Z" } };
        for (int i = 0; i < map.GetLength(0); i++)
            text = text.Replace(map[i, 0], map[i, 1]);
        text = Regex.Replace(text, @"[^\w\s-]", "").Trim();
        return Regex.Replace(text, @"[\s_]+", "-").ToUpperInvariant();
    }

    private string? PromptAuditReason(int count)
    {
        using var dlg = new Form
        {
            Text = "Uzasadnienie zmiany (Audit Trail)",
            Size = new Size(480, 215),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.White,
            Font = UiTheme.FontBase()
        };
        var lbl = new Label
        {
            Text = $"Zamierzasz zatwierdzić {count:N0} zmian(y) do nowej Linii Bazowej.\nPodaj powód / komentarz audytowy:",
            Location = new Point(18, 16),
            Size = new Size(430, 40)
        };
        var txt = new TextBox { PlaceholderText = "np. Aktualizacja po przeglądzie", Location = new Point(18, 64), Size = new Size(430, 28) };
        var btnOk = UiTheme.CreatePrimaryButton("Zatwierdź", UiTheme.SuccessGreen, UiTheme.SuccessGreenHover, width: 105, height: 32);
        btnOk.Location = new Point(242, 112);
        btnOk.DialogResult = DialogResult.OK;

        var btnCancel = UiTheme.CreateSecondaryButton("Anuluj", width: 95, height: 32);
        btnCancel.Location = new Point(353, 112);
        btnCancel.DialogResult = DialogResult.Cancel;

        dlg.AcceptButton = btnOk;
        dlg.CancelButton = btnCancel;
        dlg.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
        return dlg.ShowDialog(this) == DialogResult.OK ? (string.IsNullOrWhiteSpace(txt.Text) ? "Brak dodatkowego komentarza" : txt.Text.Trim()) : null;
    }
}
