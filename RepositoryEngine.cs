using System.Buffers;
using System.IO.Enumeration;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace FolderAuditWatchdog;

public enum ResourceProfileMode
{
    Eco,        // Niskie obciążenie RAM/CPU (praca w tle / sieć)
    Balanced,   // Zrównoważony (domyślny)
    Maximum     // Maksymalna przepustowość NVMe + większa alokacja buforów w RAM
}

public record EngineResourceProfile(
    ResourceProfileMode Mode,
    string DisplayName,
    int WorkerCount,
    int ReadBufferBytes,
    int ChannelBatchCapacity,
    int DbBatchSize
)
{
    public static EngineResourceProfile FromMode(ResourceProfileMode mode)
    {
        int cores = Environment.ProcessorCount;
        return mode switch
        {
            ResourceProfileMode.Eco => new EngineResourceProfile(
                Mode: mode,
                DisplayName: "🟢 Ekonomiczny (niskie zużycie RAM/I/O)",
                WorkerCount: Math.Clamp(cores / 4, 2, 4),
                ReadBufferBytes: 1 * 1024 * 1024,      // 1 MB
                ChannelBatchCapacity: 32,              // 32 paczki x 100 plików = 3200 plików w buforze
                DbBatchSize: 500
            ),
            ResourceProfileMode.Maximum => new EngineResourceProfile(
                Mode: mode,
                DisplayName: "🚀 Maksymalny (pełna przepustowość RAM/NVMe)",
                WorkerCount: Math.Clamp(cores, 8, 16),
                ReadBufferBytes: 8 * 1024 * 1024,      // 8 MB
                ChannelBatchCapacity: 128,             // 128 paczek x 100 plików = 12 800 plików w buforze
                DbBatchSize: 2000
            ),
            _ => new EngineResourceProfile(
                Mode: ResourceProfileMode.Balanced,
                DisplayName: "🔵 Zrównoważony (standardowy)",
                WorkerCount: Math.Clamp(cores / 2, 4, 8),
                ReadBufferBytes: 4 * 1024 * 1024,      // 4 MB
                ChannelBatchCapacity: 64,              // 64 paczki x 100 plików = 6400 plików w buforze
                DbBatchSize: 1000
            )
        };
    }
}

public record ScanProgressReport(
    int ProcessedFiles,
    int TotalDiscoveredFiles,
    bool DiscoveryCompleted,
    long ProcessedBytes,
    long TotalDiscoveredBytes,
    double SpeedMbPerSec,
    string CurrentFile,
    string Phase
);

internal readonly record struct DiscoveredFile(
    string FullPath,
    string RelativePath,
    string FileName,
    long Length,
    double LastWriteUnix
);

public class RepositoryEngine
{
    private readonly AuditDatabaseManager _db;
    private const int DiscoveryBatchSize = 100; // Paczki po 100 plików przekazywane do warstwy 2
    private const long MmfThresholdBytes = 1 * 1024 * 1024; // Zero-Copy MMF dla plików >= 1 MB

    public RepositoryEngine(AuditDatabaseManager db)
    {
        _db = db;
    }

    public async Task<int> CreateBaselineStreamedAsync(
        int repoId,
        string code,
        string rootDir,
        string owner,
        string currentUser,
        EngineResourceProfile profile,
        IProgress<ScanProgressReport>? progress,
        CancellationToken ct)
    {
        string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        long startTicks = Environment.TickCount64;

        // Warstwa 1 -> Warstwa 2: kanał przenoszący paczki po 100 plików (100x mniej operacji synchronizacji)
        var batchChannel = Channel.CreateBounded<List<DiscoveredFile>>(new BoundedChannelOptions(profile.ChannelBatchCapacity)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        // Warstwa 2 -> Warstwa 3: kanał gotowych rekordów do zapisu w SQLite
        var recordChannel = Channel.CreateBounded<(int RepoId, string RelPath, string Filename, string Hash, long Size, double Mtime, string UpdatedAt, string UpdatedBy)>(
            new BoundedChannelOptions(profile.DbBatchSize * 2)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        int discoveredFiles = 0;
        long discoveredBytes = 0;
        int discoveryDone = 0;

        int processedCount = 0;
        long totalBytes = 0;
        long lastUiReportTicks = 0;

        // =====================================================================
        // WARSTWA 1 (PRODUCER): Katalogowanie dysku w paczkach po 100 plików
        // =====================================================================
        var producerTask = Task.Run(async () =>
        {
            var currentBatch = new List<DiscoveredFile>(DiscoveryBatchSize);
            try
            {
                foreach (var item in EnumerateDiscoveredFilesFast(rootDir))
                {
                    ct.ThrowIfCancellationRequested();
                    currentBatch.Add(item);
                    Interlocked.Increment(ref discoveredFiles);
                    Interlocked.Add(ref discoveredBytes, item.Length);

                    if (currentBatch.Count >= DiscoveryBatchSize)
                    {
                        await batchChannel.Writer.WriteAsync(currentBatch, ct);
                        currentBatch = new List<DiscoveredFile>(DiscoveryBatchSize);
                    }
                }

                if (currentBatch.Count > 0)
                {
                    await batchChannel.Writer.WriteAsync(currentBatch, ct);
                }
            }
            finally
            {
                Interlocked.Exchange(ref discoveryDone, 1);
                batchChannel.Writer.Complete();
            }
        }, ct);

        // =====================================================================
        // WARSTWA 2 (WORKERS): Przetwarzanie paczek 100-plikowych (Zero-Copy MMF + RandomAccess)
        // =====================================================================
        var workerTasks = Enumerable.Range(0, profile.WorkerCount).Select(_ => Task.Run(async () =>
        {
            byte[] smallFileBuffer = ArrayPool<byte>.Shared.Rent(profile.ReadBufferBytes);
            try
            {
                await foreach (var fileBatch in batchChannel.Reader.ReadAllAsync(ct))
                {
                    foreach (var item in fileBatch)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            string hash = item.Length >= MmfThresholdBytes
                                ? CalculateSha256MemoryMapped(item.FullPath, item.Length)
                                : CalculateSha256RandomAccess(item.FullPath, smallFileBuffer);

                            await recordChannel.Writer.WriteAsync(
                                (repoId, item.RelativePath, item.FileName, hash, item.Length, item.LastWriteUnix, nowStr, currentUser),
                                ct);

                            int current = Interlocked.Increment(ref processedCount);
                            long bytes = Interlocked.Add(ref totalBytes, item.Length);

                            long nowTicks = Environment.TickCount64;
                            long prevTicks = Interlocked.Read(ref lastUiReportTicks);
                            if (nowTicks - prevTicks >= 200 && Interlocked.CompareExchange(ref lastUiReportTicks, nowTicks, prevTicks) == prevTicks)
                            {
                                double elapsedSec = Math.Max(0.1, (nowTicks - startTicks) / 1000.0);
                                double speedMb = (bytes / (1024.0 * 1024.0)) / elapsedSec;
                                progress?.Report(new ScanProgressReport(
                                    ProcessedFiles: current,
                                    TotalDiscoveredFiles: Volatile.Read(ref discoveredFiles),
                                    DiscoveryCompleted: Volatile.Read(ref discoveryDone) == 1,
                                    ProcessedBytes: bytes,
                                    TotalDiscoveredBytes: Volatile.Read(ref discoveredBytes),
                                    SpeedMbPerSec: speedMb,
                                    CurrentFile: item.FileName,
                                    Phase: $"Przetwarzanie paczek ({profile.WorkerCount} wątków I/O)"
                                ));
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { /* Pominięcie pliku zablokowanego przez inny proces */ }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(smallFileBuffer);
            }
        }, ct)).ToArray();

        var closeRecordsTask = Task.WhenAll(workerTasks).ContinueWith(
            t => recordChannel.Writer.Complete(t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // =====================================================================
        // WARSTWA 3 (CONSUMER): Paczkowy zapis do SQLite (po uzbieraniu paczki LUB co 1s)
        // =====================================================================
        var consumerTask = Task.Run(async () =>
        {
            var dbBatch = new List<(int, string, string, string, long, double, string, string)>(profile.DbBatchSize);
            long lastFlushTicks = Environment.TickCount64;

            await foreach (var rec in recordChannel.Reader.ReadAllAsync(ct))
            {
                dbBatch.Add(rec);
                long now = Environment.TickCount64;

                if (dbBatch.Count >= profile.DbBatchSize || (now - lastFlushTicks >= 1000 && dbBatch.Count > 0))
                {
                    _db.BulkInsertBaseline(dbBatch);
                    dbBatch.Clear();
                    lastFlushTicks = now;
                }
            }

            if (dbBatch.Count > 0)
            {
                _db.BulkInsertBaseline(dbBatch);
                dbBatch.Clear();
            }
        }, ct);

        await Task.WhenAll(producerTask, closeRecordsTask, consumerTask);

        double finalSec = Math.Max(0.1, (Environment.TickCount64 - startTicks) / 1000.0);
        double finalSpeed = (totalBytes / (1024.0 * 1024.0)) / finalSec;
        progress?.Report(new ScanProgressReport(
            processedCount, processedCount, true, totalBytes, totalBytes, finalSpeed, "", "Zapisywanie migawki audytowej..."
        ));

        _db.AddSnapshot(currentUser, code, processedCount, processedCount, 0, 0, 0, totalBytes / (1024.0 * 1024.0));
        return processedCount;
    }

    /// <summary>
    /// Zero-Copy SHA-256 dla dużych plików (>= 1 MB) przy użyciu okna przesuwnego MemoryMappedFile (32 MB).
    /// Brak kopiowania danych między pamięcią jądra Windows a buforem C#.
    /// </summary>
    public static string CalculateSha256MemoryMapped(string filePath, long fileLength)
    {
        const long windowSize = 32L * 1024 * 1024; // Okno 32 MB
        try
        {
            using var mmf = MemoryMappedFile.CreateFromFile(
                filePath,
                FileMode.Open,
                mapName: null,
                capacity: 0,
                access: MemoryMappedFileAccess.Read);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;

            while (offset < fileLength)
            {
                long length = Math.Min(windowSize, fileLength - offset);
                using var accessor = mmf.CreateViewAccessor(offset, length, MemoryMappedFileAccess.Read);
                unsafe
                {
                    byte* ptr = null;
                    accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                    try
                    {
                        var span = new ReadOnlySpan<byte>(ptr + accessor.PointerOffset, (int)length);
                        sha.AppendData(span);
                    }
                    finally
                    {
                        accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    }
                }
                offset += length;
            }

            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant()[..16];
        }
        catch
        {
            // Fallback do standardowego RandomAccess (np. dla plików na niektórych udziałach sieciowych)
            byte[] fallbackBuf = ArrayPool<byte>.Shared.Rent(2 * 1024 * 1024);
            try
            {
                return CalculateSha256RandomAccess(filePath, fallbackBuf);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(fallbackBuf);
            }
        }
    }

    public static string CalculateSha256RandomAccess(string filePath, byte[] buffer)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                FileOptions.SequentialScan);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            int bytesRead;
            while ((bytesRead = RandomAccess.Read(handle, buffer, offset)) > 0)
            {
                sha.AppendData(buffer.AsSpan(0, bytesRead));
                offset += bytesRead;
            }
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant()[..16];
        }
        catch
        {
            return "ERROR";
        }
    }

    private static IEnumerable<DiscoveredFile> EnumerateDiscoveredFilesFast(string rootDir)
    {
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            RecurseSubdirectories = true
        };

        var enumerable = new FileSystemEnumerable<DiscoveredFile>(
            rootDir,
            (ref FileSystemEntry entry) =>
            {
                string fullPath = entry.ToFullPath();
                string relPath = Path.GetRelativePath(rootDir, fullPath).Replace('\\', '/');
                double unixMtime = (entry.LastWriteTimeUtc.UtcDateTime - DateTime.UnixEpoch).TotalSeconds;
                return new DiscoveredFile(fullPath, relPath, entry.FileName.ToString(), entry.Length, unixMtime);
            },
            opts)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
            {
                ReadOnlySpan<char> name = entry.FileName;
                return !name.Equals("._audit_meta", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("._vault_meta", StringComparison.OrdinalIgnoreCase);
            }
        };

        foreach (var item in enumerable)
            yield return item;
    }
}
