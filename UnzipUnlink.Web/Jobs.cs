using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using UnzipUnlink.Core;

namespace UnzipUnlink.Web;

public sealed class WebOptions
{
    /// <summary>LoopbackOnly | Always | Never.</summary>
    public string LocalFolders { get; set; } = "LoopbackOnly";
    public string WorkspaceRoot { get; set; } = "";
    public int RetentionMinutes { get; set; } = 60;
    public long MaxUploadMegabytes { get; set; } = 20480;
    public bool OpenBrowser { get; set; } = true;

    public long MaxUploadBytes => MaxUploadMegabytes * 1024 * 1024;
}

public enum JobState { Created, Preparing, Ready, Running, Done, Failed }

/// <summary>What the browser asked us to change. Mirrors <see cref="UnlinkOptions"/>.</summary>
public sealed record ChangeRequest(
    bool FrameOfReference,
    bool SeriesUid,
    bool SopUid,
    string[]? Modalities,
    bool KeepCtLinked,
    bool KeepPetCtLinked,
    string[]? SeriesUids,
    bool WorkOnCopy = false,
    bool KeepStructureSetsLinked = false,
    TagEditRequest[]? TagEdits = null)
{
    /// <summary>One entry per tag edit, in order: null when it is fine, otherwise what is wrong with it.</summary>
    public IReadOnlyList<string?> TagErrors() =>
        DicomTagCatalog.Validate((TagEdits ?? []).Select(e => new TagEdit(e.Tag ?? "", e.Value ?? "")));

    /// <param name="withTagEdits">False to plan the UID changes alone, for a preview while a tag edit is still invalid.</param>
    public UnlinkOptions ToOptions(bool withTagEdits = true) => new()
    {
        KeepStructureSetsLinked = KeepStructureSetsLinked,
        TagEdits = withTagEdits ? (TagEdits ?? []).Select(e => new TagEdit(e.Tag ?? "", e.Value ?? "")).ToList() : [],
        NewFrameOfReferenceUid = FrameOfReference,
        NewSeriesInstanceUid = SeriesUid,
        NewSopInstanceUid = SopUid,
        Modalities = new HashSet<string>(Modalities ?? [], StringComparer.OrdinalIgnoreCase),
        KeepCtSeriesLinked = KeepCtLinked,
        KeepPetCtLinked = KeepPetCtLinked,
        SeriesInstanceUids = SeriesUids is null ? null : new HashSet<string>(SeriesUids, StringComparer.Ordinal),
    };
}

/// <summary>"Set this tag (keyword or number) to this value" as the browser sends it.</summary>
public sealed record TagEditRequest(string? Tag, string? Value);

/// <summary>One unit of work: a study that was uploaded, or a folder on the machine the server runs on.</summary>
public sealed class Job
{
    private readonly Lock _gate = new();

    public required string Id { get; init; }
    public required bool IsUpload { get; init; }
    public required string Name { get; set; }
    /// <summary>Upload jobs only: the private temp folder that is deleted with the job.</summary>
    public string? WorkspaceDir { get; init; }
    /// <summary>Local jobs only: the folder or .zip the user pointed at.</summary>
    public string? SourcePath { get; init; }
    /// <summary>The folder that is scanned and rewritten.</summary>
    public string ContentRoot { get; set; } = "";

    public JobState State { get; private set; } = JobState.Created;
    public string Phase { get; private set; } = "";
    public int ProgressDone { get; private set; }
    public int ProgressTotal { get; private set; }
    public string? Error { get; private set; }
    private readonly List<string> _notes = [];

    /// <summary>Things worth telling the user about how the data was opened (snapshot; safe across threads).</summary>
    public string[] Notes
    {
        get { lock (_gate) { return [.. _notes]; } }
    }

    public void AddNote(string note)
    {
        lock (_gate) { _notes.Add(note); }
    }

    public void ClearNotes()
    {
        lock (_gate) { _notes.Clear(); }
    }

    public ScanResult? Scan { get; set; }
    public UnlinkPlan? ExecutedPlan { get; set; }
    public UnlinkResult? Result { get; set; }
    /// <summary>Where the rewritten files ended up (local jobs).</summary>
    public string? OutputFolder { get; set; }
    public long UploadedBytes;
    public int UploadedFiles;
    public DateTime LastTouchedUtc { get; private set; } = DateTime.UtcNow;
    public CancellationTokenSource? Cancellation { get; set; }

    public void Touch() => LastTouchedUtc = DateTime.UtcNow;

    /// <summary>Atomically moves between states; false when the job is not in one of the allowed source states.</summary>
    public bool TryTransition(JobState to, string phase, params JobState[] from)
    {
        lock (_gate)
        {
            if (!from.Contains(State))
            {
                return false;
            }
            State = to;
            Phase = phase;
            ProgressDone = ProgressTotal = 0;
            Error = null;
            Touch();
            return true;
        }
    }

    public void SetPhase(string phase)
    {
        lock (_gate)
        {
            Phase = phase;
            ProgressDone = ProgressTotal = 0;
            Touch();
        }
    }

    public void SetProgress(int done, int total)
    {
        lock (_gate)
        {
            ProgressDone = done;
            ProgressTotal = total;
            LastTouchedUtc = DateTime.UtcNow;
        }
    }

    public void Finish(JobState state, string phase = "")
    {
        lock (_gate)
        {
            State = state;
            Phase = phase;
            Touch();
        }
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            State = JobState.Failed;
            Error = error;
            Phase = "";
            Touch();
        }
    }
}

/// <summary>Owns every job, runs the long operations in the background and cleans up uploaded data.</summary>
public sealed class JobManager : BackgroundService
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly WebOptions _options;
    private readonly ILogger<JobManager> _log;

    public string WorkspaceRoot { get; }

    public JobManager(IOptions<WebOptions> options, ILogger<JobManager> log)
    {
        _options = options.Value;
        _log = log;
        string parent = Path.GetFullPath(string.IsNullOrWhiteSpace(_options.WorkspaceRoot)
            ? Path.Combine(Path.GetTempPath(), "UnzipUnlinkWeb")
            : _options.WorkspaceRoot);
        Directory.CreateDirectory(parent);
        RemoveAbandonedWorkspaces(parent);

        // Each running copy of the app gets its own folder, marked by a lock file it keeps open. That lets a
        // later start-up tell "left behind by a crash" (delete it) from "another copy is using it" (leave it).
        WorkspaceRoot = Path.Combine(parent, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkspaceRoot);
        _workspaceLock = new FileStream(Path.Combine(WorkspaceRoot, LockFileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }

    private const string LockFileName = ".in-use";
    private readonly FileStream _workspaceLock;

    private void RemoveAbandonedWorkspaces(string parent)
    {
        foreach (string folder in Directory.GetDirectories(parent, "run-*"))
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
                _log.LogInformation("Removed uploads left behind by an earlier run: {Folder}", folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use by another running copy (or not ours to delete): leave it alone.
            }
        }
    }

    public Job? Find(string id)
    {
        if (_jobs.TryGetValue(id, out Job? job))
        {
            job.Touch();
            return job;
        }
        return null;
    }

    public Job CreateUpload(string? name)
    {
        string id = Guid.NewGuid().ToString("N");
        string workspace = Path.Combine(WorkspaceRoot, id);
        string content = Path.Combine(workspace, "content");
        Directory.CreateDirectory(content);
        var job = new Job
        {
            Id = id,
            IsUpload = true,
            Name = SafeName(name, "study"),
            WorkspaceDir = workspace,
            ContentRoot = content,
        };
        _jobs[id] = job;
        return job;
    }

    public Job CreateLocal(string path)
    {
        string full = Path.GetFullPath(path);
        var job = new Job
        {
            Id = Guid.NewGuid().ToString("N"),
            IsUpload = false,
            Name = SafeName(Path.GetFileNameWithoutExtension(full.TrimEnd(Path.DirectorySeparatorChar)), "folder"),
            SourcePath = full,
        };
        _jobs[job.Id] = job;
        return job;
    }

    public void Remove(Job job)
    {
        job.Cancellation?.Cancel();
        _jobs.TryRemove(job.Id, out _);
        if (job.WorkspaceDir is not null)
        {
            // A running operation may still hold files open; retry in the background.
            _ = Task.Run(async () =>
            {
                for (int attempt = 0; attempt < 10 && !TryDeleteDirectory(job.WorkspaceDir); attempt++)
                {
                    await Task.Delay(1000);
                }
            });
        }
    }

    /// <summary>Extract archives (where appropriate) and scan. Returns false if the job is busy.</summary>
    public bool StartPrepare(Job job)
    {
        if (!job.TryTransition(JobState.Preparing, "Getting ready", JobState.Created, JobState.Ready, JobState.Failed, JobState.Done))
        {
            return false;
        }
        job.Scan = null;
        job.Result = null;
        job.ExecutedPlan = null;
        job.ClearNotes();
        CancellationToken token = NewCancellation(job);
        _ = Task.Run(() => Guarded(job, () => Prepare(job, token)), CancellationToken.None);
        return true;
    }

    public bool StartRun(Job job, ChangeRequest request)
    {
        if (job.Scan is null || !job.TryTransition(JobState.Running, "Starting", JobState.Ready))
        {
            return false;
        }
        CancellationToken token = NewCancellation(job);
        _ = Task.Run(() => Guarded(job, () => Run(job, request, token)), CancellationToken.None);
        return true;
    }

    private void Prepare(Job job, CancellationToken token)
    {
        if (job.IsUpload)
        {
            // Uploaded archives are unpacked in place and discarded; nested archives get a few passes.
            for (int pass = 0; pass < 3; pass++)
            {
                job.SetPhase("Extracting archives");
                IReadOnlyList<string> created = ZipTools.ExtractAllInTree(
                    job.ContentRoot, deleteArchives: true, ProgressTo(job, (ZipProgress p) => (p.EntriesDone, p.EntriesTotal)), token);
                if (created.Count == 0)
                {
                    break;
                }
            }
        }
        else
        {
            PrepareLocal(job, token);
        }

        job.SetPhase("Reading DICOM headers");
        job.Scan = DicomScanner.Scan(job.ContentRoot, ProgressTo(job, (ScanProgress p) => (p.FilesDone, p.FilesTotal)), token);
        job.Finish(JobState.Ready);
    }

    private static void PrepareLocal(Job job, CancellationToken token)
    {
        string source = job.SourcePath!;
        if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            job.SetPhase("Waiting for the archive to finish copying");
            if (!TransferGuard.WaitUntilUnlocked(source, TimeSpan.FromSeconds(20), token))
            {
                throw new UserFacingException("This .zip is still being written by another program. Try again once the transfer has finished.");
            }
            string destination = ZipTools.DefaultExtractFolder(source);
            if (Directory.Exists(destination))
            {
                job.AddNote($"A folder named \"{Path.GetFileName(destination)}\" already exists next to the archive, so it was used as is (nothing was extracted).");
            }
            else
            {
                job.SetPhase($"Extracting {Path.GetFileName(source)}");
                try
                {
                    ZipTools.Extract(source, destination, ProgressTo(job, (ZipProgress p) => (p.EntriesDone, p.EntriesTotal)), token);
                }
                catch
                {
                    TryDeleteDirectory(destination);
                    throw;
                }
                job.AddNote($"Extracted to \"{destination}\". The original .zip was kept.");
            }
            job.ContentRoot = destination;
        }
        else if (Directory.Exists(source))
        {
            job.ContentRoot = source;
            int zips = Directory.EnumerateFiles(source, "*.zip", SearchOption.AllDirectories).Count();
            if (zips > 0)
            {
                job.AddNote($"{zips} .zip file{(zips == 1 ? "" : "s")} inside this folder {(zips == 1 ? "was" : "were")} not extracted. Choose a .zip directly to extract it.");
            }
        }
        else
        {
            throw new UserFacingException("That folder or .zip does not exist on this machine.");
        }

        job.SetPhase("Checking that the files have finished copying");
        if (!TransferGuard.WaitUntilStable(job.ContentRoot, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), token))
        {
            throw new UserFacingException("Files in this folder are still changing - a transfer may be in progress. Try again when it has finished.");
        }
    }

    private void Run(Job job, ChangeRequest request, CancellationToken token)
    {
        UnlinkOptions options = request.ToOptions();
        ScanResult scan = job.Scan!;

        if (!job.IsUpload && request.WorkOnCopy)
        {
            string copy = UniqueSibling(job.ContentRoot, "_unlinked");
            job.SetPhase($"Copying to {Path.GetFileName(copy)}");
            try
            {
                CopyTree(job.ContentRoot, copy, job, token);
            }
            catch
            {
                TryDeleteDirectory(copy);
                throw;
            }
            job.SetPhase("Reading DICOM headers");
            scan = DicomScanner.Scan(copy, ProgressTo(job, (ScanProgress p) => (p.FilesDone, p.FilesTotal)), token);
            job.OutputFolder = copy;
        }
        else if (!job.IsUpload)
        {
            job.OutputFolder = job.ContentRoot;
        }

        UnlinkPlan plan = UnlinkPlanner.Create(scan, options);
        if (plan.Series.Count == 0)
        {
            throw new UserFacingException("Nothing is selected to change.");
        }
        job.SetPhase("Writing new identifiers");
        var progress = new InlineProgress<UnlinkProgress>(p =>
        {
            job.SetProgress(p.FilesDone, p.FilesTotal);
        });
        UnlinkResult result = UnlinkRunner.Run(plan, progress, token);
        job.ExecutedPlan = plan;
        job.Result = result;
        _log.LogInformation("Job {Job}: {Series} series / {Files} files rewritten, {Failures} failures, cancelled={Cancelled}",
            job.Id, result.SeriesChanged, result.FilesChanged, result.Failures.Count, result.Cancelled);
        job.Finish(JobState.Done);
    }

    private void Guarded(Job job, Action work)
    {
        try
        {
            work();
        }
        catch (OperationCanceledException)
        {
            // Cancelled before any file was rewritten (e.g. while copying): go back to the review step if
            // there is still a scan to review, otherwise report it.
            if (job.Scan is not null)
            {
                job.Finish(JobState.Ready);
            }
            else
            {
                job.Fail("Cancelled.");
            }
        }
        catch (UserFacingException ex)
        {
            job.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job {Job} failed", job.Id);
            job.Fail(ex.Message);
        }
    }

    private static CancellationToken NewCancellation(Job job)
    {
        job.Cancellation?.Dispose();
        job.Cancellation = new CancellationTokenSource();
        return job.Cancellation.Token;
    }

    private static InlineProgress<T> ProgressTo<T>(Job job, Func<T, (int Done, int Total)> map) =>
        new(value =>
        {
            (int done, int total) = map(value);
            job.SetProgress(done, total);
        });

    private static void CopyTree(string source, string destination, Job job, CancellationToken token)
    {
        string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        Directory.CreateDirectory(destination);
        int done = 0;
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            job.SetProgress(++done, files.Length);
        }
    }

    private static string UniqueSibling(string folder, string suffix)
    {
        string trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = trimmed + suffix;
        for (int n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++)
        {
            candidate = $"{trimmed}{suffix}_{n}";
        }
        return candidate;
    }

    internal static string SafeName(string? name, string fallback)
    {
        string cleaned = new string((name ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '"').ToArray()).Trim().Trim('.');
        if (cleaned.Length > 80)
        {
            cleaned = cleaned[..80];
        }
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                DateTime cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, _options.RetentionMinutes));
                foreach (Job job in _jobs.Values)
                {
                    bool busy = job.State is JobState.Preparing or JobState.Running;
                    if (!busy && job.LastTouchedUtc < cutoff)
                    {
                        _log.LogInformation("Job {Job} expired after {Minutes} idle minutes; removing", job.Id, _options.RetentionMinutes);
                        Remove(job);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        foreach (Job job in _jobs.Values)
        {
            job.Cancellation?.Cancel();
        }
        // Uploaded patient data never outlives the process.
        _workspaceLock.Dispose();
        for (int attempt = 0; attempt < 5 && !TryDeleteDirectory(WorkspaceRoot); attempt++)
        {
            await Task.Delay(300, CancellationToken.None);
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>An error whose message is written for the person using the app.</summary>
public sealed class UserFacingException(string message) : Exception(message);

/// <summary>Progress that reports inline on the worker thread (no synchronization context hop).</summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
