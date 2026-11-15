namespace FolderAuditWatchdog;

public record RepositoryInfo(
    int Id,
    string Code,
    string Name,
    string RootPath,
    string Owner,
    string Status,
    string CreatedAt,
    string? LastScanAt
);

public record BaselineRecord(
    int Id,
    int RepoId,
    string RelativePath,
    string Filename,
    string FileHash,
    long FileSize,
    double LastMtime,
    string IntegrityStatus,
    string BaselineUpdatedAt,
    string? UpdatedBy
);

public record ScanSnapshot(
    int Id,
    string Timestamp,
    string Operator,
    string Scope,
    int TotalFiles,
    int OkCount,
    int NewCount,
    int ModifiedCount,
    int DeletedCount,
    double TotalSizeMb
);

public class ScanResultItem
{
    public string State { get; set; } = "ZGODNY"; // ZGODNY, NOWY, ZMIENIONY, USUNIĘTY
    public string RelPath { get; set; } = "";
    public string FullRelPath { get; set; } = "";
    public string Filename { get; set; } = "";
    public long SizeBytes { get; set; }
    public double Mtime { get; set; }
    public string MtimeStr { get; set; } = "";
    public string BaselineDate { get; set; } = "-";
    public string Auditor { get; set; } = "-";
    public int? DbId { get; set; }
    public string AbsPath { get; set; } = "";
    public int RepoId { get; set; }
    public string RepoCode { get; set; } = "";
    public string RepoRoot { get; set; } = "";
}
