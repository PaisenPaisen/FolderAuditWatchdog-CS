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

    private string? _selectedNewRepoDir;
    private List<ScanResultItem> _scanResultsCache = new();
    private List<ScanResultItem> _filteredScanCache = new();
    private List<ScanSnapshot> _dashSnapshotsCache = new();
    private int _currentScanPage = 0;
    private bool _isScanning = false;
    private bool _isSyncingCombos = false;

    // Kontrolki nawigacji
    private TabControl _tabs = null!;

    // Zakładka 0: Repozytoria
    private Label _lblPickedRepo = null!;
    private TextBox _txtRepoCode = null!;
    private TextBox _txtRepoName = null!;
    private TextBox _txtRepoOwner = null!;
    private CheckBox _chkRepoArch = null!;
    private DataGridView _gridRepos = null!;

    // Zakładka 1: Monitor
    private ComboBox _cmbMonRepo = null!;
    private Button _btnScanFast = null!;
    private Button _btnScanDeep = null!;
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

        Text = "Folder Audit Watchdog – Nadzór Integralności Repozytoriów";
        Size = new Size(1420, 850);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        SetupUi();
        RefreshRepositoriesList();
        _tabs.SelectedIndex = 1; // Start w Monitorze Zmian
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
        // Górny pasek
        var topBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.FromArgb(241, 245, 249), Padding = new Padding(12, 8, 12, 8) };
        var lblDb = new Label { Text = $"Baza Audytu: {_vaultDir}", AutoSize = true, ForeColor = Color.FromArgb(31, 83, 141), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Location = new Point(12, 12) };
        var lblUser = new Label { Text = $"👤 Audytor: {_currentUser}", AutoSize = true, ForeColor = Color.FromArgb(16, 124, 65), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Location = new Point(560, 12) };
        var btnOpenDb = new Button { Text = "Otwórz folder bazy", Width = 145, Height = 28, Dock = DockStyle.Right };
        btnOpenDb.Click += (s, e) => Process.Start("explorer.exe", _vaultDir);
        topBar.Controls.AddRange(new Control[] { lblDb, lblUser, btnOpenDb });

        // Zakładki główne
        _tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10f) };
        var tRepos = new TabPage("📁 Repozytoria (Baseline)");
        var tMonitor = new TabPage("🔍 Monitor Zmian");
        var tExplorer = new TabPage("📂 Eksplorator");
        var tDashboard = new TabPage("📈 Dashboard");
        var tAudit = new TabPage("📜 Historia i Audyt");

        _tabs.TabPages.AddRange(new[] { tRepos, tMonitor, tExplorer, tDashboard, tAudit });
        _tabs.SelectedIndexChanged += (s, e) =>
        {
            switch (_tabs.SelectedIndex)
            {
                case 0: RefreshRepositoriesList(); break;
                case 1: SyncRepoDropdowns(); break;
                case 2: RefreshExplorerRepos(); break;
                case 3: RefreshDashboardTab(); break;
                case 4: RefreshAuditTab(); break;
            }
        };

        BuildReposTab(tRepos);
        BuildMonitorTab(tMonitor);
        BuildExplorerTab(tExplorer);
        BuildDashboardTab(tDashboard);
        BuildAuditTab(tAudit);

        Controls.Add(_tabs);
        Controls.Add(topBar);
    }

    // =========================================================================
    // ZAKŁADKA 0: REPOZYTORIA
    // =========================================================================
    private void BuildReposTab(TabPage tab)
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 390, FixedPanel = FixedPanel.Panel1, Padding = new Padding(10) };

        // Lewy panel formularza
        var left = split.Panel1;
        int y = 15;
        left.Controls.Add(new Label { Text = "Dodaj lokalizację do nadzoru", Font = new Font("Segoe UI", 11, FontStyle.Bold), Location = new Point(10, y), AutoSize = true });
        y += 35;

        left.Controls.Add(new Label { Text = "1. Wskaż katalog na dysku lub sieci:", Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(10, y), AutoSize = true });
        y += 22;
        var btnPick = new Button { Text = "📁 Wybierz folder...", Location = new Point(10, y), Size = new Size(350, 32) };
        btnPick.Click += (s, e) => PickRepoFolder();
        left.Controls.Add(btnPick);
        y += 38;

        _lblPickedRepo = new Label { Text = "Nie wybrano folderu", ForeColor = Color.Gray, Location = new Point(10, y), Size = new Size(350, 36) };
        left.Controls.Add(_lblPickedRepo);
        y += 42;

        left.Controls.Add(new Label { Text = "2. Kod identyfikacyjny (np. INV-01):", Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(10, y), AutoSize = true });
        y += 22;
        _txtRepoCode = new TextBox { PlaceholderText = "np. LINIA-ASEPT", Location = new Point(10, y), Size = new Size(350, 26) };
        left.Controls.Add(_txtRepoCode);
        y += 36;

        left.Controls.Add(new Label { Text = "3. Nazwa opisowa repozytorium:", Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(10, y), AutoSize = true });
        y += 22;
        _txtRepoName = new TextBox { PlaceholderText = "np. Dokumentacja Budowy Linii", Location = new Point(10, y), Size = new Size(350, 26) };
        left.Controls.Add(_txtRepoName);
        y += 36;

        left.Controls.Add(new Label { Text = "4. Osoba odpowiedzialna / Audytor:", Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(10, y), AutoSize = true });
        y += 22;
        _txtRepoOwner = new TextBox { Text = _currentUser, Location = new Point(10, y), Size = new Size(350, 26) };
        left.Controls.Add(_txtRepoOwner);
        y += 45;

        var btnCreate = new Button
        {
            Text = "🛡️ Utwórz Linię Bazową (Baseline)",
            BackColor = Color.FromArgb(16, 124, 65),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Location = new Point(10, y),
            Size = new Size(350, 40)
        };
        btnCreate.Click += async (s, e) => await RegisterNewRepositoryAsync();
        left.Controls.Add(btnCreate);

        // Prawy panel listy
        var right = split.Panel2;
        var topR = new Panel { Dock = DockStyle.Top, Height = 36 };
        topR.Controls.Add(new Label { Text = "Nadzorowane Repozytoria", Font = new Font("Segoe UI", 11, FontStyle.Bold), Location = new Point(5, 7), AutoSize = true });
        _chkRepoArch = new CheckBox { Text = "Pokaż zarchiwizowane", AutoSize = true, Dock = DockStyle.Right };
        _chkRepoArch.CheckedChanged += (s, e) => RefreshRepositoriesList();
        topR.Controls.Add(_chkRepoArch);

        var botR = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 6, 0, 0) };
        var btnOpenDir = new Button { Text = "Otwórz folder w Eksploratorze", Width = 200, Height = 30, Location = new Point(5, 6) };
        btnOpenDir.Click += (s, e) =>
        {
            if (_gridRepos.CurrentRow?.DataBoundItem is RepositoryInfo r && Directory.Exists(r.RootPath))
                Process.Start("explorer.exe", r.RootPath);
        };
        var btnArch = new Button { Text = "Zarchiwizuj / Przywróć", Width = 160, Height = 30, Location = new Point(215, 6) };
        btnArch.Click += (s, e) =>
        {
            if (_gridRepos.CurrentRow?.DataBoundItem is RepositoryInfo r)
            {
                string next = r.Status == "Active" ? "Archived" : "Active";
                _db.SetRepositoryStatus(r.Id, next);
                LogAuditTrail(r.RootPath, "REPO_STATUS_CHANGED", r.Code, $"Zmieniono status repozytorium na: {next}");
                RefreshRepositoriesList();
            }
        };
        botR.Controls.AddRange(new Control[] { btnOpenDir, btnArch });

        _gridRepos = CreateStyledGrid();
        _gridRepos.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { DataPropertyName = "Id", HeaderText = "ID", Width = 45 },
            new DataGridViewTextBoxColumn { DataPropertyName = "Code", HeaderText = "Kod", Width = 110 },
            new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Nazwa", Width = 180 },
            new DataGridViewTextBoxColumn { DataPropertyName = "RootPath", HeaderText = "Ścieżka na dysku", Width = 280 },
            new DataGridViewTextBoxColumn { DataPropertyName = "Owner", HeaderText = "Właściciel", Width = 120 },
            new DataGridViewTextBoxColumn { DataPropertyName = "Status", HeaderText = "Status", Width = 80 },
            new DataGridViewTextBoxColumn { DataPropertyName = "LastScanAt", HeaderText = "Ostatni skan", Width = 135 }
        });

        right.Controls.Add(_gridRepos);
        right.Controls.Add(topR);
        right.Controls.Add(botR);
        tab.Controls.Add(split);
    }

    private void PickRepoFolder()
    {
        using var fbd = new FolderBrowserDialog { Description = "Wybierz katalog do nadzoru" };
        if (fbd.ShowDialog() == DialogResult.OK)
        {
            _selectedNewRepoDir = fbd.SelectedPath;
            _lblPickedRepo.Text = _selectedNewRepoDir;
            _lblPickedRepo.ForeColor = Color.FromArgb(16, 124, 65);
            string dirName = Path.GetFileName(_selectedNewRepoDir.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(_txtRepoCode.Text))
                _txtRepoCode.Text = SanitizeCode(dirName);
            if (string.IsNullOrWhiteSpace(_txtRepoName.Text))
                _txtRepoName.Text = dirName;
        }
    }

    private async Task RegisterNewRepositoryAsync()
    {
        if (_isScanning) return;
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

        _isScanning = true;
        Cursor = Cursors.WaitCursor;

        try
        {
            int count = await Task.Run(() =>
            {
                string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var files = EnumerateRepoFiles(rootDir).ToList();
                var bag = new ConcurrentBag<(int, string, string, string, long, double, string, string)>();
                long totalBytes = 0;

                Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = GetWorkerCount() }, fPath =>
                {
                    try
                    {
                        var fi = new FileInfo(fPath);
                        string rel = Path.GetRelativePath(rootDir, fPath).Replace('\\', '/');
                        string hash = CalculateSha256Short(fPath);
                        double mtime = ToUnixTimestamp(fi.LastWriteTimeUtc);
                        Interlocked.Add(ref totalBytes, fi.Length);
                        bag.Add((repoId, rel, fi.Name, hash, fi.Length, mtime, nowStr, _currentUser));
                    }
                    catch { }
                });

                var records = bag.ToList();
                _db.BulkInsertBaseline(records);
                _db.AddSnapshot(_currentUser, code, records.Count, records.Count, 0, 0, 0, totalBytes / (1024.0 * 1024.0));
                LogAuditTrail(rootDir, "BASELINE_CREATED", code, $"Utworzono linię bazową dla {records.Count} plików (Właściciel: {owner}).");
                return records.Count;
            });

            _txtRepoCode.Clear();
            _txtRepoName.Clear();
            _selectedNewRepoDir = null;
            _lblPickedRepo.Text = "Nie wybrano folderu";
            _lblPickedRepo.ForeColor = Color.Gray;
            RefreshRepositoriesList();
            SyncRepoDropdowns();
            MessageBox.Show($"Repozytorium '{code}' zostało objęte nadzorem.\nZarejestrowano {count} plików wzorcowych.", "Linia Bazowa Utworzona", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            _isScanning = false;
            Cursor = Cursors.Default;
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
    private void BuildMonitorTab(TabPage tab)
    {
        var container = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };

        // Pasek 1: Sterowanie skanem
        var pTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false };
        pTop.Controls.Add(new Label { Text = "Repozytorium:", AutoSize = true, Margin = new Padding(0, 7, 5, 0), Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        _cmbMonRepo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        pTop.Controls.Add(_cmbMonRepo);

        _btnScanFast = new Button { Text = "🔍 Skanuj zmiany (Fast-Path)", BackColor = Color.FromArgb(16, 124, 65), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9, FontStyle.Bold), Width = 195, Height = 29, Margin = new Padding(8, 2, 4, 0) };
        _btnScanFast.Click += async (s, e) => await RunRepositoryAuditScanAsync(false);
        pTop.Controls.Add(_btnScanFast);

        _btnScanDeep = new Button { Text = "🔐 Pełny audyt SHA-256", Width = 165, Height = 29, Margin = new Padding(4, 2, 8, 0) };
        _btnScanDeep.Click += async (s, e) => await RunRepositoryAuditScanAsync(true);
        pTop.Controls.Add(_btnScanDeep);

        _chkMtimeOnly = new CheckBox { Text = "Traktuj zmianę mtime jako zmianę", AutoSize = true, Margin = new Padding(6, 6, 8, 0) };
        _chkMtimeOnly.CheckedChanged += async (s, e) => { if (!_isScanning && _cmbMonRepo.Text != "Brak") await RunRepositoryAuditScanAsync(false); };
        var tt = new ToolTip();
        tt.SetToolTip(_chkMtimeOnly, "Jeżeli włączone, plik jest oznaczany jako zmieniony nawet przy identycznym rozmiarze i zawartości, jeśli zmieniła się data modyfikacji.");
        pTop.Controls.Add(_chkMtimeOnly);

        _lblMonSummary = new Label { Text = "Wybierz repozytorium i uruchom skanowanie", AutoSize = true, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(12, 6, 0, 0) };
        pTop.Controls.Add(_lblMonSummary);

        // Pasek 2: Filtry
        var pFlt = new Panel { Dock = DockStyle.Top, Height = 36 };
        pFlt.Controls.Add(new Label { Text = "Filtr stanu:", AutoSize = true, Location = new Point(0, 8), Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        _cmbMonFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(75, 4), Width = 310 };
        _cmbMonFilter.Items.AddRange(new object[] { "Tylko rozbieżności (Nowe / Zmienione / Usunięte)", "Wszystkie pliki", "ZMODYFIKOWANE", "NOWE", "USUNIĘTE", "ZGODNE" });
        _cmbMonFilter.SelectedIndex = 0;
        _cmbMonFilter.SelectedIndexChanged += (s, e) => ApplyMonitorFilters(true);
        pFlt.Controls.Add(_cmbMonFilter);

        pFlt.Controls.Add(new Label { Text = "Szukaj:", AutoSize = true, Location = new Point(400, 8), Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        _txtMonSearch = new TextBox { PlaceholderText = "Fragment nazwy lub ścieżki...", Location = new Point(455, 4), Width = 240 };
        _txtMonSearch.TextChanged += (s, e) => ApplyMonitorFilters(true);
        pFlt.Controls.Add(_txtMonSearch);

        var btnCsv = new Button { Text = "Eksport raportu CSV", Width = 150, Height = 28, Dock = DockStyle.Right };
        btnCsv.Click += (s, e) => ExportMonitorCsv();
        pFlt.Controls.Add(btnCsv);

        // Pasek 3 (dół): Akcje Baseline
        var pAct = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 6, 0, 0) };
        var btnOpen = new Button { Text = "Otwórz plik", Width = 110, Height = 30, Location = new Point(0, 6) };
        btnOpen.Click += (s, e) => MonOpenFile();
        var btnLoc = new Button { Text = "📂 Pokaż w Eksploratorze", BackColor = Color.FromArgb(0, 120, 212), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Width = 185, Height = 30, Location = new Point(118, 6) };
        btnLoc.Click += (s, e) => MonLocateInExplorer();

        var btnAccAll = new Button { Text = "✅ Zatwierdź wszystkie rozbieżności (Nowy Baseline)", BackColor = Color.FromArgb(16, 124, 65), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9, FontStyle.Bold), Width = 330, Height = 30, Dock = DockStyle.Right };
        btnAccAll.Click += async (s, e) => await AcceptChangesToBaselineAsync(false);

        var btnAccSel = new Button { Text = "✔ Zatwierdź zaznaczone do Baseline", BackColor = Color.FromArgb(31, 83, 141), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Width = 235, Height = 30, Dock = DockStyle.Right };
        btnAccSel.Click += async (s, e) => await AcceptChangesToBaselineAsync(true);

        pAct.Controls.AddRange(new Control[] { btnOpen, btnLoc, btnAccSel, btnAccAll });

        // Pasek 4 (nad akcjami): Paginacja
        var pPage = new Panel { Dock = DockStyle.Bottom, Height = 32 };
        _btnMonPrev = new Button { Text = "◀ Poprzednia", Width = 105, Height = 26, Location = new Point(0, 3) };
        _btnMonPrev.Click += (s, e) => { if (_currentScanPage > 0) { _currentScanPage--; RenderMonitorPage(); } };
        _lblMonPage = new Label { Text = "Strona 1 z 1 (0 pozycji)", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(118, 7) };
        _btnMonNext = new Button { Text = "Następna ▶", Width = 105, Height = 26, Location = new Point(310, 3) };
        _btnMonNext.Click += (s, e) => { int maxP = Math.Max(0, (_filteredScanCache.Count - 1) / PageSize); if (_currentScanPage < maxP) { _currentScanPage++; RenderMonitorPage(); } };
        pPage.Controls.AddRange(new Control[] { _btnMonPrev, _lblMonPage, _btnMonNext });

        // Tabela główna
        _gridMon = CreateStyledGrid();
        _gridMon.MultiSelect = true;
        _gridMon.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "State", HeaderText = "Stan Integralności", Width = 145 },
            new DataGridViewTextBoxColumn { Name = "RelPath", HeaderText = "Folder względny", Width = 260 },
            new DataGridViewTextBoxColumn { Name = "Filename", HeaderText = "Nazwa pliku", Width = 270 },
            new DataGridViewTextBoxColumn { Name = "SizeKb", HeaderText = "Rozmiar [KB]", Width = 105, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } },
            new DataGridViewTextBoxColumn { Name = "MtimeStr", HeaderText = "Data modyfikacji (Dysk)", Width = 155 },
            new DataGridViewTextBoxColumn { Name = "BaselineDate", HeaderText = "Data Linii Bazowej", Width = 155 },
            new DataGridViewTextBoxColumn { Name = "Auditor", HeaderText = "Zatwierdził", Width = 130 }
        });
        _gridMon.CellFormatting += (s, e) =>
        {
            if (e.RowIndex >= 0 && _gridMon.Rows[e.RowIndex].Tag is ScanResultItem item)
            {
                e.CellStyle!.ForeColor = item.State switch
                {
                    "ZGODNY" => Color.FromArgb(16, 124, 65),
                    "NOWY" => Color.FromArgb(0, 120, 212),
                    "ZMIENIONY" => Color.FromArgb(217, 119, 6),
                    "USUNIĘTY" => Color.FromArgb(209, 52, 56),
                    _ => Color.Black
                };
                if (item.State is "NOWY" or "ZMIENIONY")
                    e.CellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            }
        };

        container.Controls.Add(_gridMon);
        container.Controls.Add(pFlt);
        container.Controls.Add(pTop);
        container.Controls.Add(pPage);
        container.Controls.Add(pAct);
        tab.Controls.Add(container);
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
        _btnScanFast.Enabled = false;
        _btnScanDeep.Enabled = false;
        _lblMonSummary.Text = "Trwa skanowanie dysku w tle...";
        _lblMonSummary.ForeColor = Color.FromArgb(217, 119, 6);
        bool mtimeOnly = _chkMtimeOnly.Checked;

        try
        {
            var (results, okC, newC, modC, delC) = await Task.Run(() =>
            {
                var baselineMap = _db.GetRepoBaselineMap(repo.Id);
                var seenRel = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
                var resultBag = new ConcurrentBag<ScanResultItem>();
                var statusUpdates = new ConcurrentBag<(string, long, double, int)>();

                int ok = 0, nw = 0, mod = 0, del = 0;
                long totalBytes = 0;

                var diskFiles = EnumerateRepoFiles(repo.RootPath).ToList();

                Parallel.ForEach(diskFiles, new ParallelOptions { MaxDegreeOfParallelism = GetWorkerCount() }, fPath =>
                {
                    try
                    {
                        var fi = new FileInfo(fPath);
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
                            string state;

                            if (!needsHash && !mtimeOnly)
                            {
                                state = "ZGODNY";
                            }
                            else if (mtimeOnly && mtimeChanged && currSize == bRow.FileSize)
                            {
                                state = "ZMIENIONY";
                            }
                            else
                            {
                                string currHash = CalculateSha256Short(fPath);
                                state = currHash != bRow.FileHash ? "ZMIENIONY" : "ZGODNY";
                            }

                            if (state == "ZGODNY")
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
                                State = state,
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
                    }
                    catch { }
                });

                // Pliki usunięte z dysku
                foreach (var kvp in baselineMap)
                {
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
            });

            _scanResultsCache = results;
            int disc = newC + modC + delC;
            _lblMonSummary.Text = $"Zgodne: {okC}  |  Nowe: {newC}  |  Zmienione: {modC}  |  Usunięte: {delC}";
            _lblMonSummary.ForeColor = disc == 0 ? Color.FromArgb(16, 124, 65) : Color.FromArgb(217, 119, 6);
            ApplyMonitorFilters(true);
            RefreshRepositoriesList();
        }
        finally
        {
            _isScanning = false;
            _btnScanFast.Enabled = true;
            _btnScanDeep.Enabled = true;
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
        _gridMon.Rows.Clear();
        int total = _filteredScanCache.Count;
        var pageItems = _filteredScanCache.Skip(_currentScanPage * PageSize).Take(PageSize);

        foreach (var item in pageItems)
        {
            int rIdx = _gridMon.Rows.Add(
                item.State,
                item.RelPath,
                item.Filename,
                $"{item.SizeBytes / 1024.0:N1}",
                item.MtimeStr,
                item.BaselineDate,
                item.Auditor
            );
            _gridMon.Rows[rIdx].Tag = item;
        }

        int maxPage = Math.Max(1, (total + PageSize - 1) / PageSize);
        int currDisp = _currentScanPage + 1;
        _lblMonPage.Text = $"Strona {currDisp} z {maxPage} ({total} pozycji)";
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
        _tabs.SelectedIndex = 2;
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

        string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var newInserts = new List<(int, string, string, string, long, double, string, string)>();
        var modUpdates = new List<(string, long, double, string, string, int)>();
        var delIds = new List<int>();
        string repoRoot = itemsToAccept[0].RepoRoot;

        foreach (var item in itemsToAccept)
        {
            if (item.State == "NOWY" && File.Exists(item.AbsPath))
            {
                var fi = new FileInfo(item.AbsPath);
                string hash = CalculateSha256Short(item.AbsPath);
                newInserts.Add((item.RepoId, item.FullRelPath, item.Filename, hash, fi.Length, ToUnixTimestamp(fi.LastWriteTimeUtc), nowStr, _currentUser));
                LogAuditTrail(repoRoot, "BASELINE_ADD_NEW", item.FullRelPath, $"Zatwierdzono nowy plik | Powód: {reason}");
            }
            else if (item.State == "ZMIENIONY" && File.Exists(item.AbsPath) && item.DbId.HasValue)
            {
                var fi = new FileInfo(item.AbsPath);
                string hash = CalculateSha256Short(item.AbsPath);
                modUpdates.Add((hash, fi.Length, ToUnixTimestamp(fi.LastWriteTimeUtc), nowStr, _currentUser, item.DbId.Value));
                LogAuditTrail(repoRoot, "BASELINE_ACCEPT_MODIFIED", item.FullRelPath, $"Zatwierdzono modyfikację (nowy hash: {hash}) | Powód: {reason}");
            }
            else if (item.State == "USUNIĘTY" && item.DbId.HasValue)
            {
                delIds.Add(item.DbId.Value);
                LogAuditTrail(repoRoot, "BASELINE_CONFIRM_DELETED", item.FullRelPath, $"Potwierdzono usunięcie pliku z repozytorium | Powód: {reason}");
            }
        }

        _db.BulkInsertBaseline(newInserts);
        _db.AcceptModifiedFiles(modUpdates);
        _db.DeleteBaselineRecords(delIds);

        MessageBox.Show($"Zaktualizowano Linię Bazową dla {itemsToAccept.Count} plików i zapisano wpis w Audit Trail.", "Zatwierdzono", MessageBoxButtons.OK, MessageBoxIcon.Information);
        await RunRepositoryAuditScanAsync(false);
    }

    private void ExportMonitorCsv()
    {
        if (_filteredScanCache.Count == 0) return;
        using var sfd = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "Raport_Zmian_Repozytorium.csv" };
        if (sfd.ShowDialog() != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("Stan Integralności;Folder Względny;Nazwa Pliku;Rozmiar [B];Data Modyfikacji (Dysk);Data Linii Bazowej;Zatwierdził");
        foreach (var i in _filteredScanCache)
            sb.AppendLine($"{i.State};{i.RelPath};{i.Filename};{i.SizeBytes};{i.MtimeStr};{i.BaselineDate};{i.Auditor}");

        File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(true));
        MessageBox.Show($"Zapisano raport do:\n{sfd.FileName}", "Eksport", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // =========================================================================
    // ZAKŁADKA 2: EKSPLORATOR REPOZYTORIUM
    // =========================================================================
    private void BuildExplorerTab(TabPage tab)
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 4, 8, 0) };
        top.Controls.Add(new Label { Text = "Repozytorium:", AutoSize = true, Margin = new Padding(0, 6, 5, 0), Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        _cmbExpRepo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        _cmbExpRepo.SelectedIndexChanged += (s, e) => OnExplorerRepoSelected(_cmbExpRepo.Text);
        top.Controls.Add(_cmbExpRepo);

        var btnRef = new Button { Text = "🔄 Odśwież drzewo", Width = 140, Height = 27 };
        btnRef.Click += (s, e) => OnExplorerRepoSelected(_cmbExpRepo.Text);
        top.Controls.Add(btnRef);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 340, Padding = new Padding(8) };
        _treeExpFolders = new TreeView { Dock = DockStyle.Fill };
        _treeExpFolders.AfterSelect += (s, e) => OnExplorerFolderSelected(e.Node);
        split.Panel1.Controls.Add(_treeExpFolders);

        var bot = new Panel { Dock = DockStyle.Bottom, Height = 38 };
        var btnOpen = new Button { Text = "Otwórz plik", Width = 120, Height = 28, Location = new Point(0, 5) };
        btnOpen.Click += (s, e) =>
        {
            if (_gridExpFiles.CurrentRow?.Tag is string p && File.Exists(p))
                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
        };
        bot.Controls.Add(btnOpen);

        _gridExpFiles = CreateStyledGrid();
        _gridExpFiles.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Nazwa pliku", Width = 300 },
            new DataGridViewTextBoxColumn { HeaderText = "Stan", Width = 115 },
            new DataGridViewTextBoxColumn { HeaderText = "Rozmiar [KB]", Width = 95, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } },
            new DataGridViewTextBoxColumn { HeaderText = "Data na dysku", Width = 150 },
            new DataGridViewTextBoxColumn { HeaderText = "Data Baseline", Width = 150 }
        });
        _gridExpFiles.CellFormatting += (s, e) =>
        {
            if (e.RowIndex >= 0)
            {
                string st = _gridExpFiles.Rows[e.RowIndex].Cells[1].Value?.ToString() ?? "";
                e.CellStyle!.ForeColor = st switch
                {
                    "ZGODNY" => Color.FromArgb(16, 124, 65),
                    "NOWY" => Color.FromArgb(0, 120, 212),
                    "ZMIENIONY" => Color.FromArgb(217, 119, 6),
                    "USUNIĘTY" => Color.FromArgb(209, 52, 56),
                    _ => Color.Black
                };
            }
        };

        split.Panel2.Controls.Add(_gridExpFiles);
        split.Panel2.Controls.Add(bot);

        tab.Controls.Add(split);
        tab.Controls.Add(top);
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
        _treeExpFolders.Nodes.Clear();
        _gridExpFiles.Rows.Clear();

        if (!Directory.Exists(_currExpRepoRoot)) return;
        var rootNode = new TreeNode($"📁 {code}") { Tag = ("", _currExpRepoRoot) };
        _treeExpFolders.Nodes.Add(rootNode);
        PopulateExpFolders("", _currExpRepoRoot, rootNode);
        rootNode.Expand();
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
        _gridExpFiles.Rows.Clear();
        var (relFolder, physFolder) = info;
        if (!Directory.Exists(physFolder)) return;

        var baselineMap = _db.GetRepoBaselineMap(_currExpRepoId);
        var seenRel = new HashSet<string>(StringComparer.Ordinal);

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
                        state = CalculateSha256Short(fPath) != bRow.FileHash ? "ZMIENIONY" : "ZGODNY";
                    bDate = bRow.BaselineUpdatedAt;
                }
                else
                {
                    state = "NOWY";
                    bDate = "-";
                }

                int idx = _gridExpFiles.Rows.Add(fi.Name, state, sizeKb, mtimeStr, bDate);
                _gridExpFiles.Rows[idx].Tag = fPath;
            }

            foreach (var kvp in baselineMap)
            {
                if (seenRel.Contains(kvp.Key)) continue;
                string? pRel = Path.GetDirectoryName(kvp.Key)?.Replace('\\', '/');
                if (string.IsNullOrEmpty(pRel) || pRel == ".") pRel = "";
                if (pRel == relFolder)
                {
                    int idx = _gridExpFiles.Rows.Add(kvp.Value.Filename, "USUNIĘTY", $"{kvp.Value.FileSize / 1024.0:N1}", "Brak na dysku", kvp.Value.BaselineUpdatedAt);
                    _gridExpFiles.Rows[idx].Tag = Path.Combine(_currExpRepoRoot, kvp.Key.Replace('/', Path.DirectorySeparatorChar));
                }
            }
        }
        catch { }
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
    private void BuildDashboardTab(TabPage tab)
    {
        var container = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };

        var top = new Panel { Dock = DockStyle.Top, Height = 38 };
        top.Controls.Add(new Label { Text = "📈 Dashboard Integralności Repozytoriów", Font = new Font("Segoe UI", 12, FontStyle.Bold), Location = new Point(0, 6), AutoSize = true });
        top.Controls.Add(new Label { Text = "Zakres:", Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(360, 9), AutoSize = true });
        _cmbDashScope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(415, 5), Width = 180 };
        _cmbDashScope.SelectedIndexChanged += (s, e) => RefreshDashboardTab();
        top.Controls.Add(_cmbDashScope);

        var btnCsv = new Button { Text = "Eksportuj historię CSV", Width = 160, Height = 28, Dock = DockStyle.Right };
        btnCsv.Click += (s, e) => ExportDashboardCsv();
        top.Controls.Add(btnCsv);

        // Karty KPI
        var kpiTable = new TableLayoutPanel { Dock = DockStyle.Top, Height = 92, ColumnCount = 5, RowCount = 1, Padding = new Padding(0, 6, 0, 6) };
        for (int i = 0; i < 5; i++) kpiTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));

        (_kpiTotV, _kpiTotS) = AddKpiCard(kpiTable, 0, "PLIKI POD NADZOREM", Color.FromArgb(31, 83, 141));
        (_kpiIntV, _kpiIntS) = AddKpiCard(kpiTable, 1, "WSKAŹNIK SPÓJNOŚCI", Color.FromArgb(16, 124, 65));
        (_kpiModV, _kpiModS) = AddKpiCard(kpiTable, 2, "ZMODYFIKOWANE", Color.FromArgb(217, 119, 6));
        (_kpiNewV, _kpiNewS) = AddKpiCard(kpiTable, 3, "NOWE / NIEZATWIERDZONE", Color.FromArgb(0, 120, 212));
        (_kpiDelV, _kpiDelS) = AddKpiCard(kpiTable, 4, "USUNIĘTE Z DYSKU", Color.FromArgb(209, 52, 56));

        // Panel wykresu trendu
        _chartPanel = new Panel { Dock = DockStyle.Top, Height = 195, BackColor = Color.FromArgb(248, 250, 252), BorderStyle = BorderStyle.FixedSingle };
        _chartPanel.Paint += (s, e) => DrawTrendChart(e.Graphics, _chartPanel.ClientRectangle);
        _chartPanel.Resize += (s, e) => _chartPanel.Invalidate();

        // Tabela migawek
        _gridSnaps = CreateStyledGrid();
        _gridSnaps.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Data i godzina", Width = 145 },
            new DataGridViewTextBoxColumn { HeaderText = "Audytor", Width = 125 },
            new DataGridViewTextBoxColumn { HeaderText = "Repozytorium", Width = 120 },
            new DataGridViewTextBoxColumn { HeaderText = "Pliki", Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "Przyrost", Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "Zgodne", Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "Nowe", Width = 75 },
            new DataGridViewTextBoxColumn { HeaderText = "Zmienione", Width = 85 },
            new DataGridViewTextBoxColumn { HeaderText = "Usunięte", Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "Rozmiar [MB]", Width = 100 }
        });

        container.Controls.Add(_gridSnaps);
        container.Controls.Add(_chartPanel);
        container.Controls.Add(kpiTable);
        container.Controls.Add(top);
        tab.Controls.Add(container);
    }

    private static (Label Val, Label Sub) AddKpiCard(TableLayoutPanel parent, int col, string title, Color accent)
    {
        var pnl = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(241, 245, 249), Margin = new Padding(4), Padding = new Padding(10, 6, 10, 6) };
        var lblT = new Label { Text = title, Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Color.DimGray, Dock = DockStyle.Top, Height = 18 };
        var lblV = new Label { Text = "-", Font = new Font("Segoe UI", 16, FontStyle.Bold), ForeColor = accent, Dock = DockStyle.Top, Height = 30 };
        var lblS = new Label { Text = "-", Font = new Font("Segoe UI", 8.5f), ForeColor = Color.Gray, Dock = DockStyle.Top, Height = 18 };
        pnl.Controls.AddRange(new Control[] { lblS, lblV, lblT });
        parent.Controls.Add(pnl, col, 0);
        return (lblV, lblS);
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

            _kpiTotV.Text = last.TotalFiles.ToString();
            _kpiTotS.Text = $"{deltaStr} ({last.TotalSizeMb:F1} MB)";
            _kpiIntV.Text = $"{health:F1}%";
            _kpiIntS.Text = $"{last.OkCount} zgodnych z Baseline";
            _kpiModV.Text = last.ModifiedCount.ToString();
            _kpiModS.Text = "Zmienione po Baseline";
            _kpiNewV.Text = last.NewCount.ToString();
            _kpiNewS.Text = "Nowe pliki w folderach";
            _kpiDelV.Text = last.DeletedCount.ToString();
            _kpiDelS.Text = "Brakujące na dysku";
        }
        else
        {
            _kpiTotV.Text = _kpiIntV.Text = _kpiModV.Text = _kpiNewV.Text = _kpiDelV.Text = "0";
        }

        _gridSnaps.Rows.Clear();
        for (int i = snaps.Count - 1; i >= 0; i--)
        {
            var s = snaps[i];
            string diff = i > 0 ? $"{s.TotalFiles - snaps[i - 1].TotalFiles:+0;-0;0}" : "Start";
            _gridSnaps.Rows.Add(s.Timestamp, s.Operator, s.Scope, s.TotalFiles, diff, s.OkCount, s.NewCount, s.ModifiedCount, s.DeletedCount, $"{s.TotalSizeMb:F2}");
        }
        _chartPanel.Invalidate();
    }

    private void DrawTrendChart(Graphics g, Rectangle rect)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var snaps = _dashSnapshotsCache.TakeLast(20).ToList();
        using var font = new Font("Segoe UI", 8f);
        using var boldFont = new Font("Segoe UI", 8f, FontStyle.Bold);

        if (snaps.Count == 0)
        {
            TextRenderer.DrawText(g, "Brak danych pomiarowych – uruchom skanowanie w zakładce Monitor Zmian", font, rect, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int padL = 45, padR = 25, padT = 22, padB = 26;
        int pw = Math.Max(10, rect.Width - padL - padR);
        int ph = Math.Max(10, rect.Height - padT - padB);

        var totals = snaps.Select(s => s.TotalFiles).ToList();
        var diffs = snaps.Select(s => s.NewCount + s.ModifiedCount + s.DeletedCount).ToList();
        int maxV = Math.Max(Math.Max(totals.Max(), diffs.Max()), 5);

        using var gridPen = new Pen(Color.FromArgb(226, 232, 240)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        for (int i = 0; i < 4; i++)
        {
            float y = padT + (ph * i / 3f);
            g.DrawLine(gridPen, padL, y, rect.Width - padR, y);
            int labelVal = (int)Math.Round(maxV * (1.0 - i / 3.0));
            g.DrawString(labelVal.ToString(), font, Brushes.SlateGray, 8, y - 7);
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
                g.DrawString(tsShort, font, Brushes.SlateGray, x - 26, rect.Height - 18);
            }
        }

        if (n > 1)
        {
            using var penTot = new Pen(Color.FromArgb(59, 130, 246), 2.5f);
            using var penDif = new Pen(Color.FromArgb(245, 158, 11), 2f);
            g.DrawLines(penTot, ptsTot);
            g.DrawLines(penDif, ptsDif);
        }

        for (int i = 0; i < n; i++)
        {
            g.FillEllipse(Brushes.RoyalBlue, ptsTot[i].X - 3.5f, ptsTot[i].Y - 3.5f, 7, 7);
            g.DrawString(totals[i].ToString(), boldFont, Brushes.DarkBlue, ptsTot[i].X - 8, ptsTot[i].Y - 16);
            if (diffs[i] > 0)
                g.FillEllipse(Brushes.DarkOrange, ptsDif[i].X - 3f, ptsDif[i].Y - 3f, 6, 6);
        }
    }

    private void ExportDashboardCsv()
    {
        using var sfd = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "Trendy_Audytu_Repozytorium.csv" };
        if (sfd.ShowDialog() != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("Data i godzina;Audytor;Repozytorium;Pliki;Przyrost;Zgodne;Nowe;Zmienione;Usunięte;Rozmiar [MB]");
        foreach (DataGridViewRow row in _gridSnaps.Rows)
            sb.AppendLine(string.Join(";", row.Cells.Cast<DataGridViewCell>().Select(c => c.Value?.ToString() ?? "")));

        File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(true));
        MessageBox.Show($"Zapisano historię trendów do:\n{sfd.FileName}", "Eksport", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // =========================================================================
    // ZAKŁADKA 4: HISTORIA I AUDYT
    // =========================================================================
    private void BuildAuditTab(TabPage tab)
    {
        var container = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        top.Controls.Add(new Label { Text = "Dziennik Zdarzeń Audytowych:", Font = new Font("Segoe UI", 11, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 5, 10, 0) });

        _cmbAuditRepo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
        _cmbAuditRepo.SelectedIndexChanged += (s, e) => RefreshAuditTab();
        top.Controls.Add(_cmbAuditRepo);

        var btnRef = new Button { Text = "Odśwież", Width = 95, Height = 28 };
        btnRef.Click += (s, e) => RefreshAuditTab();
        top.Controls.Add(btnRef);

        var btnPdf = new Button { Text = "Generuj raport PDF", BackColor = Color.FromArgb(31, 83, 141), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Width = 155, Height = 28 };
        btnPdf.Click += (s, e) => ExportRepoPdfReport();
        top.Controls.Add(btnPdf);

        _gridAudit = CreateStyledGrid();
        _gridAudit.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Data i Czas", Width = 145 },
            new DataGridViewTextBoxColumn { HeaderText = "Audytor", Width = 120 },
            new DataGridViewTextBoxColumn { HeaderText = "Zdarzenie", Width = 185 },
            new DataGridViewTextBoxColumn { HeaderText = "Obiekt / Ścieżka", Width = 240 },
            new DataGridViewTextBoxColumn { HeaderText = "Szczegóły i Uzasadnienie", Width = 520 }
        });

        container.Controls.Add(_gridAudit);
        container.Controls.Add(top);
        tab.Controls.Add(container);
    }

    private void RefreshAuditTab()
    {
        var repos = _db.GetRepositories(true);
        var opts = new List<string> { "Centralny (Wszystkie)" };
        opts.AddRange(repos.Select(r => r.Code));
        SyncCombo(_cmbAuditRepo, opts);

        string curr = _cmbAuditRepo.Text;
        _gridAudit.Rows.Clear();

        string? csvFile = curr == "Centralny (Wszystkie)"
            ? Path.Combine(_vaultDir, "central_audit_trail.csv")
            : repos.FirstOrDefault(r => r.Code == curr) is RepositoryInfo r ? Path.Combine(r.RootPath, "._audit_meta", "audit_trail.csv") : null;

        if (csvFile != null && File.Exists(csvFile))
        {
            var rows = File.ReadAllLines(csvFile, Encoding.UTF8).Skip(1).Reverse();
            foreach (var line in rows)
            {
                var parts = line.Split(';');
                if (parts.Length >= 5)
                    _gridAudit.Rows.Add(parts[0], parts[1], parts[2], parts[3], parts[4]);
            }
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
    // NARZĘDZIA POMOCNICZE (SHA-256, AUDIT TRAIL, SKANOWANIE)
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

    private static IEnumerable<string> EnumerateRepoFiles(string rootDir)
    {
        var stack = new Stack<string>();
        stack.Push(rootDir);
        while (stack.Count > 0)
        {
            string curr = stack.Pop();
            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(curr); }
            catch { continue; }

            foreach (var d in subDirs)
            {
                string name = Path.GetFileName(d);
                if (name is not ("._audit_meta" or "._vault_meta"))
                    stack.Push(d);
            }

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(curr); }
            catch { continue; }

            foreach (var f in files)
                yield return f;
        }
    }

    private static string CalculateSha256Short(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1048576);
            byte[] hashBytes = SHA256.HashData(stream);
            return Convert.ToHexString(hashBytes).ToLowerInvariant()[..16];
        }
        catch
        {
            return "ERROR";
        }
    }

    private static int GetWorkerCount() => Math.Min(32, Math.Max(4, Environment.ProcessorCount * 4));

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

    private static DataGridView CreateStyledGrid() => new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        ReadOnly = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        AllowUserToResizeRows = false
    };

    private string? PromptAuditReason(int count)
    {
        using var dlg = new Form { Text = "Uzasadnienie zmiany (Audit Trail)", Size = new Size(460, 200), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
        var lbl = new Label { Text = $"Zamierzasz zatwierdzić {count} zmian(y) do nowej Linii Bazowej.\nPodaj powód / komentarz audytowy:", Location = new Point(15, 15), Size = new Size(415, 38) };
        var txt = new TextBox { PlaceholderText = "np. Aktualizacja po przeglądzie", Location = new Point(15, 60), Size = new Size(415, 26) };
        var btnOk = new Button { Text = "Zatwierdź", DialogResult = DialogResult.OK, Location = new Point(250, 105), Width = 85, Height = 30 };
        var btnCancel = new Button { Text = "Anuluj", DialogResult = DialogResult.Cancel, Location = new Point(345, 105), Width = 85, Height = 30 };
        dlg.AcceptButton = btnOk;
        dlg.CancelButton = btnCancel;
        dlg.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
        return dlg.ShowDialog(this) == DialogResult.OK ? (string.IsNullOrWhiteSpace(txt.Text) ? "Brak dodatkowego komentarza" : txt.Text.Trim()) : null;
    }
}
