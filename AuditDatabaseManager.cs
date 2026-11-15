using Microsoft.Data.Sqlite;

namespace FolderAuditWatchdog;

public class AuditDatabaseManager
{
    public string VaultDir { get; }
    public string DbPath { get; }

    public AuditDatabaseManager(string vaultDir)
    {
        VaultDir = vaultDir;
        Directory.CreateDirectory(VaultDir);
        DbPath = Path.Combine(VaultDir, "audit_watchdog.db");
        InitDb();
    }

    private SqliteConnection GetConnection()
    {
        var conn = new SqliteConnection($"Data Source={DbPath};Default Timeout=30");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-32000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private void InitDb()
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS repositories (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                code TEXT UNIQUE NOT NULL,
                name TEXT NOT NULL,
                root_path TEXT NOT NULL,
                owner TEXT,
                status TEXT DEFAULT 'Active',
                created_at TEXT NOT NULL,
                last_scan_at TEXT
            );

            CREATE TABLE IF NOT EXISTS baseline_files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                repo_id INTEGER NOT NULL,
                relative_path TEXT NOT NULL,
                filename TEXT NOT NULL,
                file_hash TEXT NOT NULL,
                file_size INTEGER NOT NULL,
                last_mtime REAL NOT NULL,
                integrity_status TEXT DEFAULT 'ZGODNY',
                baseline_updated_at TEXT NOT NULL,
                updated_by TEXT,
                FOREIGN KEY (repo_id) REFERENCES repositories(id),
                UNIQUE(repo_id, relative_path)
            );

            CREATE INDEX IF NOT EXISTS idx_bf_repo_id ON baseline_files(repo_id);
            CREATE INDEX IF NOT EXISTS idx_bf_status ON baseline_files(integrity_status);
            CREATE INDEX IF NOT EXISTS idx_bf_rel_path ON baseline_files(relative_path);

            CREATE TABLE IF NOT EXISTS scan_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                operator TEXT NOT NULL,
                scope TEXT NOT NULL,
                total_files INTEGER NOT NULL,
                ok_count INTEGER NOT NULL,
                new_count INTEGER NOT NULL,
                modified_count INTEGER NOT NULL,
                deleted_count INTEGER NOT NULL,
                total_size_mb REAL NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    public int AddRepository(string code, string name, string rootPath, string owner)
    {
        string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO repositories (code, name, root_path, owner, status, created_at, last_scan_at)
            VALUES ($code, $name, $path, $owner, 'Active', $created, $scan);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$path", rootPath);
        cmd.Parameters.AddWithValue("$owner", owner);
        cmd.Parameters.AddWithValue("$created", nowStr);
        cmd.Parameters.AddWithValue("$scan", nowStr);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<RepositoryInfo> GetRepositories(bool includeArchived = false)
    {
        var list = new List<RepositoryInfo>();
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = includeArchived
            ? "SELECT id, code, name, root_path, owner, status, created_at, last_scan_at FROM repositories ORDER BY id DESC"
            : "SELECT id, code, name, root_path, owner, status, created_at, last_scan_at FROM repositories WHERE status = 'Active' ORDER BY id DESC";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RepositoryInfo(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)
            ));
        }
        return list;
    }

    public void SetRepositoryStatus(int repoId, string status)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE repositories SET status = $st WHERE id = $id";
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$id", repoId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateRepositoryScanTime(int repoId)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE repositories SET last_scan_at = $ts WHERE id = $id";
        cmd.Parameters.AddWithValue("$ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$id", repoId);
        cmd.ExecuteNonQuery();
    }

    public void BulkInsertBaseline(IEnumerable<(int RepoId, string RelPath, string Filename, string Hash, long Size, double Mtime, string UpdatedAt, string UpdatedBy)> records)
    {
        using var conn = GetConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO baseline_files
            (repo_id, relative_path, filename, file_hash, file_size, last_mtime, integrity_status, baseline_updated_at, updated_by)
            VALUES ($repo, $rel, $fname, $hash, $size, $mtime, 'ZGODNY', $updAt, $updBy)";

        var pRepo = cmd.Parameters.Add("$repo", SqliteType.Integer);
        var pRel = cmd.Parameters.Add("$rel", SqliteType.Text);
        var pFname = cmd.Parameters.Add("$fname", SqliteType.Text);
        var pHash = cmd.Parameters.Add("$hash", SqliteType.Text);
        var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
        var pMtime = cmd.Parameters.Add("$mtime", SqliteType.Real);
        var pUpdAt = cmd.Parameters.Add("$updAt", SqliteType.Text);
        var pUpdBy = cmd.Parameters.Add("$updBy", SqliteType.Text);

        foreach (var r in records)
        {
            pRepo.Value = r.RepoId;
            pRel.Value = r.RelPath;
            pFname.Value = r.Filename;
            pHash.Value = r.Hash;
            pSize.Value = r.Size;
            pMtime.Value = r.Mtime;
            pUpdAt.Value = r.UpdatedAt;
            pUpdBy.Value = r.UpdatedBy;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void BulkUpdateStatuses(IEnumerable<(string Status, long Size, double Mtime, int FileId)> updates)
    {
        using var conn = GetConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE baseline_files SET integrity_status = $st, file_size = $sz, last_mtime = $mt WHERE id = $id";

        var pSt = cmd.Parameters.Add("$st", SqliteType.Text);
        var pSz = cmd.Parameters.Add("$sz", SqliteType.Integer);
        var pMt = cmd.Parameters.Add("$mt", SqliteType.Real);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);

        foreach (var u in updates)
        {
            pSt.Value = u.Status;
            pSz.Value = u.Size;
            pMt.Value = u.Mtime;
            pId.Value = u.FileId;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void AcceptModifiedFiles(IEnumerable<(string NewHash, long NewSize, double NewMtime, string UpdatedAt, string UpdatedBy, int FileId)> updates)
    {
        using var conn = GetConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE baseline_files
            SET file_hash = $h, file_size = $sz, last_mtime = $mt, integrity_status = 'ZGODNY', baseline_updated_at = $ua, updated_by = $ub
            WHERE id = $id";

        var pH = cmd.Parameters.Add("$h", SqliteType.Text);
        var pSz = cmd.Parameters.Add("$sz", SqliteType.Integer);
        var pMt = cmd.Parameters.Add("$mt", SqliteType.Real);
        var pUa = cmd.Parameters.Add("$ua", SqliteType.Text);
        var pUb = cmd.Parameters.Add("$ub", SqliteType.Text);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);

        foreach (var u in updates)
        {
            pH.Value = u.NewHash;
            pSz.Value = u.NewSize;
            pMt.Value = u.NewMtime;
            pUa.Value = u.UpdatedAt;
            pUb.Value = u.UpdatedBy;
            pId.Value = u.FileId;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void DeleteBaselineRecords(IEnumerable<int> fileIds)
    {
        using var conn = GetConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM baseline_files WHERE id = $id";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);

        foreach (var id in fileIds)
        {
            pId.Value = id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public Dictionary<string, BaselineRecord> GetRepoBaselineMap(int repoId)
    {
        var map = new Dictionary<string, BaselineRecord>(StringComparer.Ordinal);
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, repo_id, relative_path, filename, file_hash, file_size, last_mtime, integrity_status, baseline_updated_at, updated_by
            FROM baseline_files WHERE repo_id = $repo";
        cmd.Parameters.AddWithValue("$repo", repoId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rec = new BaselineRecord(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetDouble(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)
            );
            map[rec.RelativePath] = rec;
        }
        return map;
    }

    public void AddSnapshot(string op, string scope, int total, int ok, int nw, int mod, int del, double sizeMb)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO scan_snapshots (timestamp, operator, scope, total_files, ok_count, new_count, modified_count, deleted_count, total_size_mb)
            VALUES ($ts, $op, $sc, $tot, $ok, $nw, $mod, $del, $sz)";
        cmd.Parameters.AddWithValue("$ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$op", op);
        cmd.Parameters.AddWithValue("$sc", scope);
        cmd.Parameters.AddWithValue("$tot", total);
        cmd.Parameters.AddWithValue("$ok", ok);
        cmd.Parameters.AddWithValue("$nw", nw);
        cmd.Parameters.AddWithValue("$mod", mod);
        cmd.Parameters.AddWithValue("$del", del);
        cmd.Parameters.AddWithValue("$sz", Math.Round(sizeMb, 2));
        cmd.ExecuteNonQuery();
    }

    public List<ScanSnapshot> GetSnapshots(string scopeFilter = "Wszystkie")
    {
        var list = new List<ScanSnapshot>();
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        if (scopeFilter == "Wszystkie")
        {
            cmd.CommandText = "SELECT id, timestamp, operator, scope, total_files, ok_count, new_count, modified_count, deleted_count, total_size_mb FROM scan_snapshots ORDER BY id ASC";
        }
        else
        {
            cmd.CommandText = "SELECT id, timestamp, operator, scope, total_files, ok_count, new_count, modified_count, deleted_count, total_size_mb FROM scan_snapshots WHERE scope = $sc ORDER BY id ASC";
            cmd.Parameters.AddWithValue("$sc", scopeFilter);
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ScanSnapshot(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetDouble(9)
            ));
        }
        return list;
    }
}
