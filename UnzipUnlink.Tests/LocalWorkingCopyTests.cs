using UnzipUnlink.Core;

namespace UnzipUnlink.Tests;

/// <summary>
/// "Copy to this computer, change it there, copy it back". Nothing here needs a real network share: the
/// working copy is made of an ordinary temp folder, which exercises exactly the same code.
/// </summary>
public sealed class LocalWorkingCopyTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "UnzipUnlinkTests", "work-" + Guid.NewGuid().ToString("N"));

    private static UnlinkOptions Everything() => new()
    {
        NewFrameOfReferenceUid = true,
        NewSeriesInstanceUid = true,
        NewSopInstanceUid = true,
        Modalities = new HashSet<string>(["CT", "MR", "PT"], StringComparer.OrdinalIgnoreCase),
        KeepStructureSetsLinked = true,
    };

    private static Dictionary<string, byte[]> Snapshot(string root) => Directory
        .GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllBytes);

    [Fact]
    public void Create_copies_the_whole_tree_and_leaves_the_source_alone()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 3, subfolder: "a/ct");
        study.AddSeries("MR", SyntheticStudy.NewUid(), slices: 2, subfolder: "b/deep/mr");
        study.AddTextFile("notes.txt");
        Directory.CreateDirectory(Path.Combine(study.Root, "empty"));
        Dictionary<string, byte[]> before = Snapshot(study.Root);
        var seen = new List<CopyProgress>();

        using LocalWorkingCopy copy = LocalWorkingCopy.Create(study.Root, new SynchronousProgress<CopyProgress>(seen.Add), parent: _parent)!;

        Assert.Equal(6, copy.FileCount);
        Assert.StartsWith(_parent, copy.LocalRoot);
        Assert.Equal(Path.GetFileName(study.Root), Path.GetFileName(copy.LocalRoot));
        Assert.Equal(before.Keys.Order(), Snapshot(copy.LocalRoot).Keys.Order());
        Assert.All(Snapshot(copy.LocalRoot), kv => Assert.Equal(before[kv.Key], kv.Value));
        Assert.True(Directory.Exists(Path.Combine(copy.LocalRoot, "empty")));
        Assert.Equal(new CopyProgress(6, 6), seen[^1]);
        string someFile = Directory.GetFiles(study.Root, "*.dcm", SearchOption.AllDirectories)[0];
        Assert.Equal(someFile, copy.ToSource(copy.ToLocal(someFile)));
        Assert.Equal(before.Keys.Order(), Snapshot(study.Root).Keys.Order());
    }

    [Fact]
    public void Rewriting_the_copy_and_uploading_gives_the_same_study_as_rewriting_in_place()
    {
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 4, subfolder: "ct");
        string rs = study.AddStructureSet(ct);
        string sr = study.AddSeries("SR", null, slices: 1, subfolder: "sr");
        study.AddTextFile("notes.txt");
        Dictionary<string, byte[]> original = Snapshot(study.Root);

        using LocalWorkingCopy copy = LocalWorkingCopy.Create(study.Root, parent: _parent)!;
        UnlinkOptions options = Everything();
        options.TagEdits.Add(new TagEdit("PatientID", "ANON001"));
        UnlinkPlan plan = UnlinkPlanner.Create(DicomScanner.Scan(copy.LocalRoot), options);
        UnlinkResult run = UnlinkRunner.Run(plan);
        // Nothing has reached the "network" folder yet.
        Assert.All(Snapshot(study.Root), kv => Assert.Equal(original[kv.Key], kv.Value));

        var seen = new List<CopyProgress>();
        IReadOnlyList<IReadOnlyList<string>> changed = LocalWorkingCopy.RewrittenFiles(plan, run);
        UploadResult upload = copy.Upload(changed, progress: new SynchronousProgress<CopyProgress>(seen.Add));
        UnlinkResult result = copy.Combine(plan, run, upload, TimeSpan.FromSeconds(1));

        Assert.True(upload.Complete);
        Assert.Equal(5, upload.FilesCopied);                       // 4 CT images + the structure set that follows them
        Assert.Equal(new CopyProgress(5, 5), seen[^1]);
        Assert.Single(changed);                                    // one unit: they travel together
        Assert.Equal(2, result.SeriesChanged);
        Assert.Equal(5, result.FilesChanged);
        Assert.False(result.Cancelled);
        // The source now holds exactly what the working copy holds, and nothing else was written to it.
        Dictionary<string, byte[]> after = Snapshot(study.Root), local = Snapshot(copy.LocalRoot);
        Assert.Equal(original.Keys.Order(), after.Keys.Order());
        Assert.All(after, kv => Assert.Equal(local[kv.Key], kv.Value));
        // ...which is a correctly unlinked study.
        ScanResult scan = DicomScanner.Scan(study.Root);
        DicomSeriesInfo images = scan.Series.Single(s => s.Modality == "CT");
        Assert.NotEqual(ct, images.SeriesInstanceUid);
        Assert.Equal("ANON001", images.PatientId);
        Assert.Equal([images.SeriesInstanceUid], scan.Series.Single(s => s.SeriesInstanceUid == rs).ReferencedSeriesInstanceUids);
        // Files that were not part of the plan were not uploaded: byte-identical, same timestamp.
        string untouched = scan.Series.Single(s => s.SeriesInstanceUid == sr).Files.Single();
        Assert.Equal(original[Path.GetRelativePath(study.Root, untouched)], File.ReadAllBytes(untouched));
    }

    [Fact]
    public void A_cancelled_upload_sends_whole_units_only_and_reports_what_got_back()
    {
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 3, subfolder: "ct");
        string rs = study.AddStructureSet(ct);
        string mr = study.AddSeries("MR", SyntheticStudy.NewUid(), slices: 3, subfolder: "mr");
        using LocalWorkingCopy copy = LocalWorkingCopy.Create(study.Root, parent: _parent)!;
        // CT first, so the unit "CT + structure set" is the one being sent when the cancel arrives.
        UnlinkPlan plan = UnlinkPlanner.Create(DicomScanner.Scan(copy.LocalRoot).Series.OrderBy(s => s.Modality, StringComparer.Ordinal), Everything());
        UnlinkResult run = UnlinkRunner.Run(plan);

        using var cts = new CancellationTokenSource();
        UploadResult upload = copy.Upload(LocalWorkingCopy.RewrittenFiles(plan, run),
            progress: new SynchronousProgress<CopyProgress>(_ => cts.Cancel()), cancellationToken: cts.Token);
        UnlinkResult result = copy.Combine(plan, run, upload, TimeSpan.Zero);

        Assert.True(upload.Cancelled);
        Assert.False(upload.Complete);
        Assert.Equal(1, upload.GroupsCopied);
        Assert.Equal(4, upload.FilesCopied);
        Assert.True(result.Cancelled);
        Assert.Equal(2, result.SeriesChanged);
        ScanResult source = DicomScanner.Scan(study.Root);
        DicomSeriesInfo images = source.Series.Single(s => s.Modality == "CT");
        Assert.NotEqual(ct, images.SeriesInstanceUid);
        Assert.Equal([images.SeriesInstanceUid], source.Series.Single(s => s.SeriesInstanceUid == rs).ReferencedSeriesInstanceUids);
        Assert.Equal(mr, source.Series.Single(s => s.Modality == "MR").SeriesInstanceUid);   // never sent: still the original
    }

    [Fact]
    public void A_file_that_cannot_be_written_back_is_reported_and_the_rest_still_arrive()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 3, subfolder: "ct");
        using LocalWorkingCopy copy = LocalWorkingCopy.Create(study.Root, parent: _parent)!;
        UnlinkPlan plan = UnlinkPlanner.Create(DicomScanner.Scan(copy.LocalRoot), Everything());
        UnlinkResult run = UnlinkRunner.Run(plan);
        string locked = Directory.GetFiles(study.Root, "*.dcm", SearchOption.AllDirectories)[0];
        byte[] lockedContent = File.ReadAllBytes(locked);

        UploadResult upload;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            upload = copy.Upload(LocalWorkingCopy.RewrittenFiles(plan, run));
        }
        UnlinkResult result = copy.Combine(plan, run, upload, TimeSpan.Zero);

        Assert.False(upload.Complete);
        Assert.Equal(2, upload.FilesCopied);
        FileFailure failure = Assert.Single(result.Failures);
        Assert.Equal(locked, failure.Path);
        Assert.Contains("copied back", failure.Error);
        Assert.Equal(lockedContent, File.ReadAllBytes(locked));
        Assert.Empty(Directory.GetFiles(study.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Series_a_cancelled_run_never_reached_and_files_it_failed_on_are_not_uploaded()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2, subfolder: "ct");
        study.AddSeries("MR", SyntheticStudy.NewUid(), slices: 2, subfolder: "mr");
        using LocalWorkingCopy copy = LocalWorkingCopy.Create(study.Root, parent: _parent)!;
        UnlinkPlan plan = UnlinkPlanner.Create(DicomScanner.Scan(copy.LocalRoot), Everything());
        string failedFile = plan.Series[0].Series.Files[0];
        var run = new UnlinkResult { SeriesChanged = 1, FilesChanged = 1, Cancelled = true, Failures = [new FileFailure(failedFile, "locked")] };

        IReadOnlyList<IReadOnlyList<string>> changed = LocalWorkingCopy.RewrittenFiles(plan, run);

        Assert.Equal([plan.Series[0].Series.Files[1]], Assert.Single(changed));
        UnlinkResult result = copy.Combine(plan, run, copy.Upload(changed), TimeSpan.Zero);
        Assert.Equal(copy.ToSource(failedFile), Assert.Single(result.Failures).Path);   // reported under the path the user knows
        Assert.Equal(1, result.FilesChanged);
    }

    [Fact]
    public void Upload_to_another_folder_writes_the_whole_study_there()
    {
        using var study = new SyntheticStudy();
        string source = Path.Combine(study.Root, "Export");
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2, subfolder: "Export/ct");
        File.WriteAllText(Path.Combine(source, "readme.txt"), "hello");
        using LocalWorkingCopy copy = LocalWorkingCopy.Create(source, parent: _parent)!;
        string destination = Path.Combine(study.Root, "Export_unlinked");

        UploadResult upload = copy.Upload(copy.AllFiles(), destination);

        Assert.True(upload.Complete);
        Assert.Equal(3, upload.FilesCopied);
        Assert.Equal(Snapshot(source).Keys.Order(), Snapshot(destination).Keys.Order());
    }

    [Fact]
    public void Disposing_deletes_the_local_copy_and_abandoned_copies_are_removed_next_time()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 1);
        LocalWorkingCopy first = LocalWorkingCopy.Create(study.Root, parent: _parent)!;
        string firstFolder = Path.GetDirectoryName(first.LocalRoot)!;
        // Left behind by a crash: a copy folder whose lock file nobody holds.
        string abandoned = Path.Combine(_parent, "copy-abandoned");
        Directory.CreateDirectory(Path.Combine(abandoned, "study"));
        File.WriteAllText(Path.Combine(abandoned, ".in-use"), "");
        File.WriteAllText(Path.Combine(abandoned, "study", "patient.dcm"), "left behind");

        using (LocalWorkingCopy second = LocalWorkingCopy.Create(study.Root, parent: _parent)!)
        {
            Assert.False(Directory.Exists(abandoned));      // cleaned up...
            Assert.True(Directory.Exists(firstFolder));     // ...but a copy that is in use is left alone
            Assert.True(Directory.Exists(second.LocalRoot));
        }

        first.Dispose();
        first.Dispose();   // harmless twice
        Assert.False(Directory.Exists(firstFolder));
        Assert.Empty(Directory.GetDirectories(_parent));
    }

    [Fact]
    public void A_cancelled_copy_down_leaves_nothing_behind()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 30);
        using var cts = new CancellationTokenSource();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            LocalWorkingCopy.Create(study.Root, new SynchronousProgress<CopyProgress>(_ => cts.Cancel()), cts.Token, _parent));

        Assert.Empty(Directory.GetDirectories(_parent));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_parent, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}
