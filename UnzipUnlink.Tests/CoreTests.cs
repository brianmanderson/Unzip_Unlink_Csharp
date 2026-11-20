using System.IO.Compression;
using FellowOakDicom;
using UnzipUnlink.Core;

namespace UnzipUnlink.Tests;

public class ScannerTests
{
    [Fact]
    public void Scan_groups_files_by_series_and_reads_headers()
    {
        using var study = new SyntheticStudy();
        string frame = SyntheticStudy.NewUid();
        string ct = study.AddSeries("CT", frame, slices: 4, subfolder: "ct", description: "Planning CT");
        string mr = study.AddSeries("MR", frame, slices: 2, subfolder: "nested/mr");
        study.AddTextFile("notes.txt");

        ScanResult scan = DicomScanner.Scan(study.Root);

        Assert.Equal(2, scan.Series.Count);
        Assert.Equal(6, scan.DicomFileCount);
        Assert.Equal(1, scan.OtherFileCount);
        DicomSeriesInfo ctInfo = scan.Series.Single(s => s.SeriesInstanceUid == ct);
        Assert.Equal("CT", ctInfo.Modality);
        Assert.Equal(frame, ctInfo.FrameOfReferenceUid);
        Assert.Equal(4, ctInfo.Files.Count);
        Assert.Equal("Planning CT", ctInfo.SeriesDescription);
        Assert.Equal("TEST^SYNTHETIC", ctInfo.PatientName);
        Assert.Equal(1, ctInfo.DistinctFrameOfReferenceCount);
        Assert.Equal(2, scan.Series.Single(s => s.SeriesInstanceUid == mr).Files.Count);
    }

    [Fact]
    public void Scan_reports_progress_up_to_total()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 5);
        var seen = new List<ScanProgress>();

        DicomScanner.Scan(study.Root, new SynchronousProgress<ScanProgress>(seen.Add));

        Assert.Equal(new ScanProgress(5, 5), seen[^1]);
    }

    [Fact]
    public void Scan_merges_one_series_spread_over_two_folders()
    {
        // The original tool threw on this layout (duplicate dictionary key).
        using var study = new SyntheticStudy();
        string frame = SyntheticStudy.NewUid(), series = SyntheticStudy.NewUid();
        study.AddSeries("CT", frame, slices: 2, subfolder: "a", seriesUid: series);
        study.AddSeries("CT", frame, slices: 3, subfolder: "b", seriesUid: series);

        ScanResult scan = DicomScanner.Scan(study.Root);

        Assert.Equal(5, Assert.Single(scan.Series).Files.Count);
    }
}

public class PlannerTests
{
    private static UnlinkOptions Options(params string[] modalities) => new()
    {
        Modalities = new HashSet<string>(modalities, StringComparer.OrdinalIgnoreCase),
    };

    private static DicomSeriesInfo Series(string modality, string frame) => new()
    {
        SeriesInstanceUid = SyntheticStudy.NewUid(),
        Modality = modality,
        FrameOfReferenceUid = frame,
        Files = ["x.dcm"],
    };

    [Fact]
    public void Every_series_gets_its_own_frame_of_reference_by_default()
    {
        string frame = SyntheticStudy.NewUid();
        DicomSeriesInfo[] all = [Series("CT", frame), Series("CT", frame), Series("MR", frame)];

        UnlinkPlan plan = UnlinkPlanner.Create(all, Options("CT", "MR"));

        Assert.Equal(3, plan.Series.Count);
        Assert.Equal(3, plan.Series.Select(p => p.NewFrameOfReferenceUid).Distinct().Count());
        Assert.All(plan.Series, p => Assert.NotEqual(frame, p.NewFrameOfReferenceUid));
        Assert.All(plan.Series, p => Assert.Null(p.NewSeriesInstanceUid));
        Assert.All(plan.Series, p => Assert.True(p.NewSopInstanceUids));
    }

    [Fact]
    public void Only_checked_modalities_are_planned()
    {
        string frame = SyntheticStudy.NewUid();
        DicomSeriesInfo[] all = [Series("CT", frame), Series("MR", frame), Series("PT", frame)];

        UnlinkPlan plan = UnlinkPlanner.Create(all, Options("mr"));

        Assert.Equal("MR", Assert.Single(plan.Series).Series.Modality);
    }

    [Fact]
    public void Unsupported_modalities_are_never_planned()
    {
        DicomSeriesInfo[] all = [Series("RTSTRUCT", SyntheticStudy.NewUid()), Series("REG", "")];

        UnlinkPlan plan = UnlinkPlanner.Create(all, Options("RTSTRUCT", "REG", "CT"));

        Assert.Empty(plan.Series);
    }

    [Fact]
    public void FourDct_keeps_ct_series_with_a_shared_frame_together()
    {
        string frameA = SyntheticStudy.NewUid(), frameB = SyntheticStudy.NewUid();
        DicomSeriesInfo phase1 = Series("CT", frameA), phase2 = Series("CT", frameA), other = Series("CT", frameB), mr = Series("MR", frameA);
        UnlinkOptions options = Options("CT", "MR");
        options.KeepCtSeriesLinked = true;

        UnlinkPlan plan = UnlinkPlanner.Create([phase1, phase2, other, mr], options);
        string? NewFrame(DicomSeriesInfo s) => plan.Series.Single(p => p.Series == s).NewFrameOfReferenceUid;

        Assert.Equal(NewFrame(phase1), NewFrame(phase2));
        Assert.NotEqual(NewFrame(phase1), NewFrame(other));
        Assert.NotEqual(NewFrame(phase1), NewFrame(mr));
        Assert.NotEqual(frameA, NewFrame(phase1));
    }

    [Fact]
    public void PetCt_keeps_pet_and_ct_with_a_shared_frame_together()
    {
        string frame = SyntheticStudy.NewUid();
        DicomSeriesInfo ct = Series("CT", frame), pt = Series("PT", frame), mr = Series("MR", frame);
        UnlinkOptions options = Options("CT", "PT", "MR");
        options.KeepPetCtLinked = true;

        UnlinkPlan plan = UnlinkPlanner.Create([ct, pt, mr], options);
        string? NewFrame(DicomSeriesInfo s) => plan.Series.Single(p => p.Series == s).NewFrameOfReferenceUid;

        Assert.Equal(NewFrame(ct), NewFrame(pt));
        Assert.NotEqual(NewFrame(ct), NewFrame(mr));
    }

    [Fact]
    public void PetCt_alone_does_not_keep_ct_only_frames_together()
    {
        string petCtFrame = SyntheticStudy.NewUid(), fourDFrame = SyntheticStudy.NewUid();
        DicomSeriesInfo ct = Series("CT", petCtFrame), pt = Series("PT", petCtFrame);
        DicomSeriesInfo phase1 = Series("CT", fourDFrame), phase2 = Series("CT", fourDFrame);
        UnlinkOptions options = Options("CT", "PT");
        options.KeepPetCtLinked = true;

        UnlinkPlan plan = UnlinkPlanner.Create([ct, pt, phase1, phase2], options);
        string? NewFrame(DicomSeriesInfo s) => plan.Series.Single(p => p.Series == s).NewFrameOfReferenceUid;

        Assert.Equal(NewFrame(ct), NewFrame(pt));
        Assert.NotEqual(NewFrame(phase1), NewFrame(phase2));

        options.KeepCtSeriesLinked = true;
        plan = UnlinkPlanner.Create([ct, pt, phase1, phase2], options);
        Assert.Equal(NewFrame(ct), NewFrame(pt));
        Assert.Equal(NewFrame(phase1), NewFrame(phase2));
        Assert.NotEqual(NewFrame(ct), NewFrame(phase1));
    }

    [Fact]
    public void PetCt_needs_the_pet_to_be_selected()
    {
        string frame = SyntheticStudy.NewUid();
        DicomSeriesInfo ct1 = Series("CT", frame), ct2 = Series("CT", frame), pt = Series("PT", frame);
        UnlinkOptions options = Options("CT");   // PET not selected, so it keeps its old frame
        options.KeepPetCtLinked = true;

        UnlinkPlan plan = UnlinkPlanner.Create([ct1, ct2, pt], options);

        Assert.Equal(2, plan.Series.Count);
        Assert.NotEqual(plan.Series[0].NewFrameOfReferenceUid, plan.Series[1].NewFrameOfReferenceUid);
    }

    [Fact]
    public void Series_without_a_frame_of_reference_are_never_grouped_together()
    {
        UnlinkOptions options = Options("CT");
        options.KeepCtSeriesLinked = true;

        UnlinkPlan plan = UnlinkPlanner.Create([Series("CT", ""), Series("CT", "")], options);

        Assert.Equal(2, plan.Series.Select(p => p.NewFrameOfReferenceUid).Distinct().Count());
    }

    [Fact]
    public void Explicit_series_selection_narrows_the_plan()
    {
        DicomSeriesInfo keep = Series("CT", SyntheticStudy.NewUid()), skip = Series("CT", SyntheticStudy.NewUid());
        UnlinkOptions options = Options("CT");
        options.SeriesInstanceUids = new HashSet<string> { keep.SeriesInstanceUid };

        UnlinkPlan plan = UnlinkPlanner.Create([keep, skip], options);

        Assert.Same(keep, Assert.Single(plan.Series).Series);
    }

    [Fact]
    public void Nothing_is_planned_when_no_change_is_requested()
    {
        UnlinkOptions options = Options("CT");
        options.NewFrameOfReferenceUid = options.NewSeriesInstanceUid = options.NewSopInstanceUid = false;

        Assert.Empty(UnlinkPlanner.Create([Series("CT", SyntheticStudy.NewUid())], options).Series);
    }
}

public class RunnerTests
{
    [Fact]
    public void Run_rewrites_selected_uids_and_leaves_everything_else_intact()
    {
        using var study = new SyntheticStudy();
        string frame = SyntheticStudy.NewUid();
        string ctUid = study.AddSeries("CT", frame, slices: 4, subfolder: "ct");
        string mrUid = study.AddSeries("MR", frame, slices: 3, subfolder: "mr");
        ScanResult before = DicomScanner.Scan(study.Root);
        var original = before.Series.SelectMany(s => s.Files).ToDictionary(f => f, f => DicomFile.Open(f, FileReadOption.ReadAll));
        var options = new UnlinkOptions
        {
            NewFrameOfReferenceUid = true,
            NewSeriesInstanceUid = true,
            NewSopInstanceUid = true,
            Modalities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CT" },
        };

        UnlinkPlan plan = UnlinkPlanner.Create(before, options);
        UnlinkResult result = UnlinkRunner.Run(plan);

        Assert.Empty(result.Failures);
        Assert.Equal(4, result.FilesChanged);
        Assert.Equal(1, result.SeriesChanged);
        Assert.False(result.Cancelled);

        ScanResult after = DicomScanner.Scan(study.Root);
        DicomSeriesInfo ct = after.Series.Single(s => s.Modality == "CT");
        DicomSeriesInfo mr = after.Series.Single(s => s.Modality == "MR");
        SeriesPlan ctPlan = Assert.Single(plan.Series);

        // CT moved to the planned UIDs, all slices together.
        Assert.Equal(ctPlan.NewSeriesInstanceUid, ct.SeriesInstanceUid);
        Assert.NotEqual(ctUid, ct.SeriesInstanceUid);
        Assert.Equal(ctPlan.NewFrameOfReferenceUid, ct.FrameOfReferenceUid);
        Assert.Equal(4, ct.Files.Count);
        Assert.Equal(1, ct.DistinctFrameOfReferenceCount);
        // MR untouched.
        Assert.Equal(mrUid, mr.SeriesInstanceUid);
        Assert.Equal(frame, mr.FrameOfReferenceUid);

        var newSops = new HashSet<string>();
        foreach (string path in ct.Files)
        {
            DicomFile now = DicomFile.Open(path, FileReadOption.ReadAll);
            DicomFile was = original[path];
            string sop = now.Dataset.GetString(DicomTag.SOPInstanceUID);
            Assert.NotEqual(was.Dataset.GetString(DicomTag.SOPInstanceUID), sop);
            Assert.True(newSops.Add(sop), "SOP Instance UIDs must be unique per file");
            Assert.Equal(sop, now.FileMetaInfo.MediaStorageSOPInstanceUID.UID);
            // Untouched content survives byte for byte.
            Assert.Equal(was.Dataset.GetString(DicomTag.StudyInstanceUID), now.Dataset.GetString(DicomTag.StudyInstanceUID));
            Assert.Equal(was.Dataset.GetString(DicomTag.PatientName), now.Dataset.GetString(DicomTag.PatientName));
            Assert.Equal(was.Dataset.GetString(DicomTag.InstanceNumber), now.Dataset.GetString(DicomTag.InstanceNumber));
            Assert.Equal(was.Dataset.InternalTransferSyntax, now.Dataset.InternalTransferSyntax);
            Assert.Equal(
                was.Dataset.GetDicomItem<DicomOtherWord>(DicomTag.PixelData).Buffer.Data,
                now.Dataset.GetDicomItem<DicomOtherWord>(DicomTag.PixelData).Buffer.Data);
        }
        Assert.Empty(Directory.GetFiles(study.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Run_with_only_frame_of_reference_keeps_series_and_sop_uids()
    {
        using var study = new SyntheticStudy();
        string frame = SyntheticStudy.NewUid();
        string mrUid = study.AddSeries("MR", frame, slices: 2);
        ScanResult before = DicomScanner.Scan(study.Root);
        var sopsBefore = before.Series[0].Files.Select(f => DicomFile.Open(f).Dataset.GetString(DicomTag.SOPInstanceUID)).ToHashSet();
        var options = new UnlinkOptions
        {
            NewSopInstanceUid = false,
            Modalities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MR" },
        };

        UnlinkRunner.Run(UnlinkPlanner.Create(before, options));

        DicomSeriesInfo after = Assert.Single(DicomScanner.Scan(study.Root).Series);
        Assert.Equal(mrUid, after.SeriesInstanceUid);
        Assert.NotEqual(frame, after.FrameOfReferenceUid);
        Assert.Equal(sopsBefore, after.Files.Select(f => DicomFile.Open(f).Dataset.GetString(DicomTag.SOPInstanceUID)).ToHashSet());
    }

    [Fact]
    public void Run_collects_failures_instead_of_throwing()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2);
        ScanResult scan = DicomScanner.Scan(study.Root);
        UnlinkPlan plan = UnlinkPlanner.Create(scan, new UnlinkOptions { Modalities = new HashSet<string> { "CT" } });
        File.Delete(scan.Series[0].Files[0]);

        UnlinkResult result = UnlinkRunner.Run(plan);

        Assert.Single(result.Failures);
        Assert.Equal(1, result.FilesChanged);
    }

    [Fact]
    public void Run_stops_when_cancelled()
    {
        using var study = new SyntheticStudy();
        string frame = SyntheticStudy.NewUid();
        study.AddSeries("CT", frame, slices: 2);
        ScanResult scan = DicomScanner.Scan(study.Root);
        UnlinkPlan plan = UnlinkPlanner.Create(scan, new UnlinkOptions { Modalities = new HashSet<string> { "CT" } });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        UnlinkResult result = UnlinkRunner.Run(plan, cancellationToken: cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.FilesChanged);
        Assert.Equal(frame, DicomScanner.Scan(study.Root).Series[0].FrameOfReferenceUid);
    }
}

public class RunnerCancellationTests
{
    [Fact]
    public void Cancelling_mid_run_finishes_the_current_series_and_leaves_the_rest_untouched()
    {
        using var study = new SyntheticStudy();
        string frameA = SyntheticStudy.NewUid(), frameB = SyntheticStudy.NewUid();
        study.AddSeries("CT", frameA, slices: 12, subfolder: "a");
        study.AddSeries("MR", frameB, slices: 12, subfolder: "b");
        ScanResult scan = DicomScanner.Scan(study.Root);
        UnlinkPlan plan = UnlinkPlanner.Create(scan, new UnlinkOptions
        {
            NewSeriesInstanceUid = true,
            Modalities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CT", "MR" },
        });
        using var cts = new CancellationTokenSource();
        // Cancel as soon as the first file of the first series has been written.
        var progress = new SynchronousProgress<UnlinkProgress>(p => { if (p.FilesDone >= 1) cts.Cancel(); });

        UnlinkResult result = UnlinkRunner.Run(plan, progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.SeriesChanged);
        Assert.Equal(12, result.FilesChanged);
        ScanResult after = DicomScanner.Scan(study.Root);
        Assert.Equal(2, after.Series.Count);                                   // no series was split in two
        Assert.All(after.Series, s => Assert.Equal(12, s.Files.Count));
        Assert.All(after.Series, s => Assert.Equal(1, s.DistinctFrameOfReferenceCount));
        DicomSeriesInfo first = plan.Series[0].Series, second = plan.Series[1].Series;
        Assert.DoesNotContain(after.Series, s => s.SeriesInstanceUid == first.SeriesInstanceUid);   // fully rewritten
        Assert.Contains(after.Series, s => s.SeriesInstanceUid == second.SeriesInstanceUid && s.FrameOfReferenceUid == second.FrameOfReferenceUid);
    }
}

public class ZipToolsTests
{
    [Fact]
    public void ExtractAllInTree_unpacks_beside_the_archive_and_can_delete_it()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2, subfolder: "payload");
        string zip = Path.Combine(study.Root, "export.zip");
        ZipFile.CreateFromDirectory(Path.Combine(study.Root, "payload"), zip);
        Directory.Delete(Path.Combine(study.Root, "payload"), recursive: true);

        IReadOnlyList<string> created = ZipTools.ExtractAllInTree(study.Root, deleteArchives: true);

        Assert.Equal(Path.Combine(study.Root, "export"), Assert.Single(created));
        Assert.False(File.Exists(zip));
        Assert.Equal(2, DicomScanner.Scan(study.Root).DicomFileCount);
    }

    [Fact]
    public void ExtractAllInTree_skips_archives_that_were_already_extracted()
    {
        using var study = new SyntheticStudy();
        string zip = Path.Combine(study.Root, "export.zip");
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("a.txt");
        }
        Directory.CreateDirectory(Path.Combine(study.Root, "export"));

        Assert.Empty(ZipTools.ExtractAllInTree(study.Root, deleteArchives: true));
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public void Extract_rejects_entries_that_escape_the_destination()
    {
        using var study = new SyntheticStudy();
        string zip = Path.Combine(study.Root, "evil.zip");
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using StreamWriter writer = new(archive.CreateEntry("../../escaped.txt").Open());
            writer.Write("nope");
        }

        Assert.Throws<InvalidDataException>(() => ZipTools.Extract(zip, Path.Combine(study.Root, "out", "deep")));
        Assert.False(File.Exists(Path.Combine(study.Root, "escaped.txt")));
    }

    [Fact]
    public async Task WriteZip_round_trips_a_folder_tree()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("MR", SyntheticStudy.NewUid(), slices: 2, subfolder: "in/a/b");
        string zip = Path.Combine(study.Root, "out.zip");
        await using (FileStream stream = File.Create(zip))
        {
            await ZipTools.WriteZipAsync(Path.Combine(study.Root, "in"), stream);
        }

        ZipTools.Extract(zip, Path.Combine(study.Root, "roundtrip"));

        Assert.Equal(2, Directory.GetFiles(Path.Combine(study.Root, "roundtrip", "a", "b")).Length);
    }
}

public class TransferGuardTests
{
    [Fact]
    public void A_quiet_folder_is_stable()
    {
        using var study = new SyntheticStudy();
        study.AddTextFile("a.txt");

        Assert.True(TransferGuard.WaitUntilStable(study.Root, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2)));
    }

    /// <summary>Feeds a scripted series of folder states to the stability check; no real files or timing.</summary>
    private static bool StableFor(int quietChecks, params int[] states)
    {
        int next = 0, waits = 0;
        return TransferGuard.WaitUntilStable(
            () => states[Math.Min(next++, states.Length - 1)],
            () => ++waits < states.Length - 1,          // the deadline passes when the script runs out
            quietChecks);
    }

    [Fact]
    public void A_slow_trickle_that_changes_on_every_other_check_is_not_stable()
    {
        // A file arriving slightly slower than the check interval: two looks the same, then it changes again.
        int[] trickle = [1, 1, 2, 2, 3, 3, 4, 4, 5];

        Assert.False(StableFor(2, trickle));
        // ...which is exactly what a single quiet check (the 1.x-style test) would have let through.
        Assert.True(StableFor(1, trickle));
    }

    [Fact]
    public void Stability_needs_the_required_number_of_consecutive_quiet_checks()
    {
        Assert.True(StableFor(2, 7, 7, 7));                 // quiet from the start
        Assert.True(StableFor(2, 1, 2, 3, 3, 3));           // settles after the copy finishes
        Assert.False(StableFor(2, 1, 2, 3, 4, 5, 6));       // never settles before the deadline
        Assert.False(StableFor(3, 1, 1, 1, 2, 2, 2, 3));    // two quiet checks are not three
    }

    [Fact]
    public void An_open_file_is_reported_as_locked()
    {
        using var study = new SyntheticStudy();
        string path = study.AddTextFile("a.txt");
        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            Assert.True(TransferGuard.IsFileLocked(path));
        }
        Assert.False(TransferGuard.IsFileLocked(path));
    }
}

/// <summary>IProgress that invokes inline (Progress&lt;T&gt; posts to the thread pool, which races with assertions).</summary>
internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Lock _gate = new();

    public void Report(T value)
    {
        lock (_gate)
        {
            handler(value);
        }
    }
}
