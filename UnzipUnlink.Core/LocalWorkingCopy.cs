using System.Collections.Concurrent;

namespace UnzipUnlink.Core;

public sealed record CopyProgress(int FilesDone, int FilesTotal);

/// <summary>What <see cref="LocalWorkingCopy.Upload"/> managed to put back.</summary>
public sealed class UploadResult
{
    public int FilesCopied { get; init; }
    /// <summary>How many of the groups handed to <see cref="LocalWorkingCopy.Upload"/> were sent before it stopped.</summary>
    public int GroupsCopied { get; init; }
    /// <summary>Files that could not be written to the destination (path there, and why). They keep their old content.</summary>
    public IReadOnlyList<FileFailure> Failures { get; init; } = [];
    public bool Cancelled { get; init; }
    /// <summary>True when every requested file is now at the destination.</summary>
    public bool Complete => !Cancelled && Failures.Count == 0;
}

/// <summary>
/// A private copy, on a local disk, of a folder that lives on a network share. Scanning and rewriting thousands
/// of small files over a network is slow because every open, read, write and rename is a round trip to the
/// server; here the study is pulled down once, everything is done locally, and only the files that changed are
/// sent back. The copy holds patient data, so it is deleted when disposed, when the process exits, and (should
/// the process have crashed) the next time a working copy is created.
/// </summary>
public sealed class LocalWorkingCopy : IDisposable
{
    private const string LockFileName = ".in-use";
    private const string UploadSuffix = ".unlink.tmp";
    private static readonly ConcurrentDictionary<LocalWorkingCopy, byte> Live = new();
    private static int _exitHooked;

    private readonly string _workspace;
    private FileStream? _lock;

    /// <summary>The folder on the network that this is a copy of.</summary>
    public string SourceRoot { get; }
    /// <summary>The local folder holding the copy: scan and rewrite this one.</summary>
    public string LocalRoot { get; }
    public int FileCount { get; private set; }
    public long TotalBytes { get; private set; }

    private LocalWorkingCopy(string sourceRoot, string workspace)
    {
        SourceRoot = sourceRoot;
        _workspace = workspace;
        // Keep the folder's own name, so anything that shows the scanned folder still reads naturally.
        string name = Path.GetFileName(sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        LocalRoot = Path.Combine(workspace, string.IsNullOrEmpty(name) ? "study" : name);
    }

    /// <summary>Where working copies are kept unless the caller says otherwise.</summary>
    public static string DefaultParent => Path.Combine(Path.GetTempPath(), "UnzipUnlink", "work");

    /// <summary>
    /// Copies <paramref name="sourceRoot"/> and everything under it to a new local folder.
    /// Returns null, having copied nothing, when the local disk does not have room for it (with a margin),
    /// so the caller can fall back to working on the network folder directly.
    /// Throws <see cref="OperationCanceledException"/> when cancelled; whatever was copied is removed.
    /// </summary>
    public static LocalWorkingCopy? Create(string sourceRoot, IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default, string? parent = null)
    {
        parent = Path.GetFullPath(parent ?? DefaultParent);
        Directory.CreateDirectory(parent);
        RemoveAbandoned(parent);

        string source = Path.GetFullPath(sourceRoot);
        string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        long bytes = files.Sum(f => new FileInfo(f).Length);
        if (!HasRoomFor(parent, bytes))
        {
            return null;
        }

        var copy = new LocalWorkingCopy(source, Path.Combine(parent, "copy-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(copy.LocalRoot);
        copy._lock = new FileStream(Path.Combine(copy._workspace, LockFileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Track(copy);
        try
        {
            // Empty folders too, so the local tree is the same shape as the original.
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(copy.ToLocal(directory));
            }
            int done = 0;
            var gate = new ProgressGate();
            ParallelSettings.ForEach(files, source, cancellationToken, file =>
            {
                string target = copy.ToLocal(file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                int now = Interlocked.Increment(ref done);
                if (gate.ShouldReport(now, files.Length))
                {
                    progress?.Report(new CopyProgress(now, files.Length));
                }
            });
            progress?.Report(new CopyProgress(files.Length, files.Length));
            copy.FileCount = files.Length;
            copy.TotalBytes = bytes;
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    /// <summary>The local path of a file or folder given its path under <see cref="SourceRoot"/>.</summary>
    public string ToLocal(string sourcePath) => Path.Combine(LocalRoot, Path.GetRelativePath(SourceRoot, sourcePath));

    /// <summary>The path under <see cref="SourceRoot"/> that a local file was copied from.</summary>
    public string ToSource(string localPath) => Path.Combine(SourceRoot, Path.GetRelativePath(LocalRoot, localPath));

    /// <summary>Every file in the working copy, as one group (for sending the whole study somewhere new).</summary>
    public IReadOnlyList<IReadOnlyList<string>> AllFiles() =>
        [Directory.GetFiles(LocalRoot, "*", SearchOption.AllDirectories)];

    /// <summary>
    /// The files a run really rewrote, grouped by unit in plan order: what has to go back to the network.
    /// Series the run never reached (it was cancelled) and files it failed on are left out, because their local
    /// copies are no different from what is on the network.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> RewrittenFiles(UnlinkPlan plan, UnlinkResult result)
    {
        HashSet<string> failed = result.Failures.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return plan.Series
            .Take(result.SeriesChanged)
            .GroupBy(s => s.Unit)
            .Select(unit => (IReadOnlyList<string>)unit.SelectMany(s => s.Series.Files).Where(f => !failed.Contains(f)).ToList())
            .ToList();
    }

    /// <summary>
    /// The outcome of "rewrite locally, then upload" as one result about the network folder: files count as
    /// changed only once they are back on the network, failures from both steps are listed under the path the
    /// user knows (the network one), and a cancelled upload reports the series that did make it back.
    /// </summary>
    public UnlinkResult Combine(UnlinkPlan plan, UnlinkResult run, UploadResult upload, TimeSpan duration) => new()
    {
        SeriesChanged = plan.Series.Take(run.SeriesChanged).GroupBy(s => s.Unit).Take(upload.GroupsCopied).Sum(unit => unit.Count()),
        FilesChanged = upload.FilesCopied,
        Failures = run.Failures.Select(f => new FileFailure(ToSource(f.Path), f.Error))
            .Concat(upload.Failures.Select(f => new FileFailure(f.Path, "Could not be copied back to the network folder: " + f.Error)))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        Cancelled = run.Cancelled || upload.Cancelled,
        Duration = duration,
    };

    /// <summary>
    /// Copies local files to the same relative place under <paramref name="destinationRoot"/> (by default back
    /// to <see cref="SourceRoot"/>). Each file is written beside its target and swapped in only when complete,
    /// so an interrupted upload never leaves a half-written file. Cancellation is honoured between groups only:
    /// a group is one unit of the plan (a series, or a structure set with its images), which must not end up
    /// half old and half new on the network.
    /// </summary>
    public UploadResult Upload(IReadOnlyList<IReadOnlyList<string>> groups, string? destinationRoot = null,
        IProgress<CopyProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string destination = Path.GetFullPath(destinationRoot ?? SourceRoot);
        int total = groups.Sum(g => g.Count), done = 0, copied = 0, groupsDone = 0;
        var failures = new ConcurrentBag<FileFailure>();
        var gate = new ProgressGate();
        bool cancelled = false;
        foreach (IReadOnlyList<string> group in groups)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            ParallelSettings.ForEach(group, destination, CancellationToken.None, local =>
            {
                string target = Path.Combine(destination, Path.GetRelativePath(LocalRoot, local));
                string temp = target + UploadSuffix;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(local, temp, overwrite: true);
                    File.Move(temp, target, overwrite: true);
                    Interlocked.Increment(ref copied);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TryDeleteFile(temp);
                    failures.Add(new FileFailure(target, ex.Message));
                }
                int now = Interlocked.Increment(ref done);
                if (gate.ShouldReport(now, total))
                {
                    progress?.Report(new CopyProgress(now, total));
                }
            });
            groupsDone++;
        }
        progress?.Report(new CopyProgress(done, total));
        return new UploadResult
        {
            FilesCopied = copied,
            GroupsCopied = groupsDone,
            Failures = failures.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            Cancelled = cancelled,
        };
    }

    /// <summary>Deletes the local copy. Safe to call more than once.</summary>
    public void Dispose()
    {
        Live.TryRemove(this, out _);
        _lock?.Dispose();
        _lock = null;
        TryDeleteDirectory(_workspace);
    }

    private static bool HasRoomFor(string parent, long bytes)
    {
        try
        {
            string? root = Path.GetPathRoot(parent);
            // Rewritten files are written beside the originals before they replace them, hence the margin.
            return string.IsNullOrEmpty(root) || new DriveInfo(root).AvailableFreeSpace > bytes + Math.Max(bytes / 10, 512L << 20);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;   // cannot tell (e.g. a UNC temp folder): try, and let a real shortage surface as an error
        }
    }

    private static void Track(LocalWorkingCopy copy)
    {
        Live[copy] = 0;
        if (Interlocked.Exchange(ref _exitHooked, 1) == 0)
        {
            // Patient data must not outlive the program, however it is closed.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                foreach (LocalWorkingCopy live in Live.Keys)
                {
                    live.Dispose();
                }
            };
        }
    }

    /// <summary>
    /// Removes working copies left behind by a run that crashed. Each live copy keeps a lock file open, which
    /// tells "abandoned" (delete it) from "another running copy of the program is using it" (leave it).
    /// </summary>
    internal static void RemoveAbandoned(string parent)
    {
        foreach (string folder in Directory.GetDirectories(parent, "copy-*"))
        {
            try
            {
                string lockFile = Path.Combine(folder, LockFileName);
                if (File.Exists(lockFile))
                {
                    // Throws while the owning process is alive.
                    using var probe = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use, or not ours to delete: leave it alone.
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort here; RemoveAbandoned picks it up next time.
        }
    }
}
