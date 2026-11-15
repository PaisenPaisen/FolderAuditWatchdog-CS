using System.Buffers;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace FolderAuditWatchdog;

public record ScanProgressReport(int ProcessedFiles, long ProcessedBytes, string CurrentFile, string Phase);

public class RepositoryEngine
{
    private readonly AuditDatabaseManager _db;
    private const int BatchSize = 2000;

    public RepositoryEngine(AuditDatabaseManager db)
    {
        _db = db;
    }

    /// <summary>
    /// Tworzy nową linię bazową strumieniowo (Producer -> Hash Workers -> Batch SQLite Writer),
    /// bez ładowania listy wszystkich plików do pamięci RAM i bez blokowania UI.
    /// </summary>
    public async Task<int> CreateBaselineStreamedAsync(
        int repoId,
        string code,
        string rootDir,
        string owner,
        string currentUser,
        IProgress<ScanProgressReport>? progress,
        CancellationToken ct)
    {
        string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        // Kanał 1: ścieżki plików z dysku -> workery SHA-256 (max 4096 w buforze)
        var fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        // Kanał 2: gotowe rekordy -> pojedynczy zapisujący do SQLite
        var recordChannel = Channel.CreateBounded<(int RepoId, string RelPath, string Filename, string Hash, long Size, double Mtime, string UpdatedAt, string UpdatedBy)>(
            new BoundedChannelOptions(BatchSize * 2)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        int processedCount = 0;
        long totalBytes = 0;
        long lastUiReportTicks = 0;

        // 1. PRODUCER: Szybkie skanowanie struktury katalogów
        var producerTask = Task.Run(async () =>
        {
            try
            {
                foreach (var file in EnumerateRepoFilesFast(rootDir))
                {
                    ct.ThrowIfCancellationRequested();
                    await fileChannel.Writer.WriteAsync(file, ct);
                }
            }
            finally
            {
                fileChannel.Writer.Complete();
            }
        }, ct);

        // 2. WORKERS: Optymalna liczba wątków dla I/O dyskowego (4-6 zamiast 32)
        int workerCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        var workerTasks = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(262144); // 256 KB bufor wielokrotnego użytku
            try
            {
                await foreach (var fPath in fileChannel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        var fi = new FileInfo(fPath);
                        if (!fi.Exists) continue;

                        string rel = Path.GetRelativePath(rootDir, fPath).Replace('\\', '/');
                        string hash = CalculateSha256Pooled(fPath, buffer);
                        double mtime = (fi.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds;
                        long len = fi.Length;

                        await recordChannel.Writer.WriteAsync((repoId, rel, fi.Name, hash, len, mtime, nowStr, currentUser), ct);

                        int current = Interlocked.Increment(ref processedCount);
                        long bytes = Interlocked.Add(ref totalBytes, len);

                        // Throttling raportowania do UI: odświeżaj maksymalnie co 250 ms
                        long nowTicks = Environment.TickCount64;
                        long prevTicks = Interlocked.Read(ref lastUiReportTicks);
                        if (nowTicks - prevTicks >= 250 && Interlocked.CompareExchange(ref lastUiReportTicks, nowTicks, prevTicks) == prevTicks)
                        {
                            progress?.Report(new ScanProgressReport(current, bytes, fi.Name, "Indeksowanie i obliczanie SHA-256..."));
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* Pominięcie zablokowanego pliku systemowego */ }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }, ct)).ToArray();

        // Zamknięcie kanału zapisu po zakończeniu wszystkich workerów
        var closeRecordsTask = Task.WhenAll(workerTasks).ContinueWith(
            t => recordChannel.Writer.Complete(t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // 3. CONSUMER: Zapis paczkowy (Batch) do SQLite w trakcie skanowania
        var consumerTask = Task.Run(async () =>
        {
            var batch = new List<(int, string, string, string, long, double, string, string)>(BatchSize);
            await foreach (var rec in recordChannel.Reader.ReadAllAsync(ct))
            {
                batch.Add(rec);
                if (batch.Count >= BatchSize)
                {
                    _db.BulkInsertBaseline(batch);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                _db.BulkInsertBaseline(batch);
                batch.Clear();
            }
        }, ct);

        await Task.WhenAll(producerTask, closeRecordsTask, consumerTask);

        progress?.Report(new ScanProgressReport(processedCount, totalBytes, "", "Zapisywanie migawki audytowej..."));
        _db.AddSnapshot(currentUser, code, processedCount, processedCount, 0, 0, 0, totalBytes / (1024.0 * 1024.0));

        return processedCount;
    }

    private static IEnumerable<string> EnumerateRepoFilesFast(string rootDir)
    {
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // Unika zapętlenia na dowiązaniach symbolicznych
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
                options: FileOptions.SequentialScan); // Podpowiedź dla cache systemu Windows

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
}
