using System.Collections.Concurrent;
using System.Diagnostics;
using FellowOakDicom;

namespace UnzipUnlink.Core;

/// <summary>
/// Executes an <see cref="UnlinkPlan"/>: rewrites the chosen UIDs in every file of every planned series.
/// Cancellation is honoured between units only (a unit is one series, or a structure set together with the
/// images it follows), so a cancelled run never leaves a series half rewritten, nor a structure set pointing
/// at identifiers its images no longer have.
/// </summary>
public static class UnlinkRunner
{
    private const string TempSuffix = ".unlink.tmp";

    public static UnlinkResult Run(UnlinkPlan plan, IProgress<UnlinkProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var failures = new ConcurrentBag<FileFailure>();
        int filesTotal = plan.FileCount;
        int filesDone = 0, filesChanged = 0, seriesDone = 0;
        var gate = new ProgressGate();
        bool cancelled = false;
        int? currentUnit = null;

        foreach (SeriesPlan seriesPlan in plan.Series)
        {
            // Stop only where a new unit begins: the planner keeps a unit's series next to each other.
            if (seriesPlan.Unit != currentUnit && cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            currentUnit = seriesPlan.Unit;
            string label = Describe(seriesPlan.Series);
            progress?.Report(new UnlinkProgress(seriesDone, plan.Series.Count, filesDone, filesTotal, label));
            // Deliberately not cancellable: a series is rewritten completely or not at all. Stopping half-way
            // would leave one series carrying two sets of identifiers, which re-imports as two broken series.
            Parallel.ForEach(seriesPlan.Series.Files, ParallelSettings.For(CancellationToken.None), path =>
            {
                try
                {
                    RewriteFile(path, seriesPlan);
                    Interlocked.Increment(ref filesChanged);
                }
                catch (Exception ex)
                {
                    failures.Add(new FileFailure(path, ex.Message));
                }
                int now = Interlocked.Increment(ref filesDone);
                if (gate.ShouldReport(now, filesTotal))
                {
                    progress?.Report(new UnlinkProgress(seriesDone, plan.Series.Count, now, filesTotal, label));
                }
            });
            seriesDone++;
        }
        progress?.Report(new UnlinkProgress(seriesDone, plan.Series.Count, filesDone, filesTotal, ""));

        return new UnlinkResult
        {
            SeriesChanged = seriesDone,
            FilesChanged = filesChanged,
            Failures = failures.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            Cancelled = cancelled,
            Duration = stopwatch.Elapsed,
        };
    }

    /// <summary>
    /// Rewrites one file. The new content is written beside the original and swapped in only once it is
    /// complete, so an interrupted run never leaves a half-written DICOM file behind.
    /// </summary>
    internal static void RewriteFile(string path, DicomUID? newFrame, DicomUID? newSeries, bool newSop) =>
        RewriteFile(path, newFrame, newSeries, newSop ? DicomUIDGenerator.GenerateDerivedFromUUID() : null);

    /// <summary>Path-based rewrite with an explicit SOP Instance UID; a thin wrapper around <see cref="Rewrite(Stream, Stream, DicomUID?, DicomUID?, DicomUID?)"/>.</summary>
    internal static void RewriteFile(string path, DicomUID? newFrame, DicomUID? newSeries, DicomUID? newSop) =>
        RewriteFile(path, (input, output) => Rewrite(input, output, newFrame, newSeries, newSop));

    /// <summary>Path-based rewrite of one file of a planned series; a thin wrapper around <see cref="Rewrite(Stream, Stream, SeriesPlan)"/>.</summary>
    internal static void RewriteFile(string path, SeriesPlan plan) =>
        RewriteFile(path, (input, output) => Rewrite(input, output, plan));

    private static void RewriteFile(string path, Action<Stream, Stream> rewrite)
    {
        string temp = path + TempSuffix;
        try
        {
            using (FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (FileStream output = new(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                rewrite(input, output);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Rewrites one file held in memory or any other stream: reads the whole DICOM file from
    /// <paramref name="input"/> and writes it, with the given UIDs replaced, to <paramref name="output"/>.
    /// A non-null <paramref name="newSop"/> also updates the file meta Media Storage SOP Instance UID.
    /// Everything else is written back unchanged.
    /// </summary>
    public static void Rewrite(Stream input, Stream output, DicomUID? newFrame, DicomUID? newSeries, DicomUID? newSop)
    {
        DicomFile file = DicomFile.Open(input, FileReadOption.ReadAll);
        SetUids(file, newFrame, newSeries, newSop);
        file.Save(output);
    }

    /// <summary>
    /// Rewrites one file of a planned series. An image gets the series' new Frame of Reference / Series Instance
    /// UIDs and, when the plan asks for it, a new SOP Instance UID (the one the plan fixed in advance if a
    /// structure set follows this series, otherwise a freshly generated one). A structure set keeps its own
    /// identifiers and has its references repointed instead. Planned tag edits are applied to either.
    /// Returns the file's new SOP Instance UID (or null when it kept the old one).
    /// This is the single implementation behind every planned rewrite, on disk or in memory.
    /// </summary>
    public static string? Rewrite(Stream input, Stream output, SeriesPlan plan)
    {
        DicomFile file = DicomFile.Open(input, FileReadOption.ReadAll);
        DicomUID? sop = null;
        if (plan.References is { } references)
        {
            RepointStructureSet(file.Dataset, references);
        }
        else
        {
            if (plan.NewSopInstanceUids)
            {
                string old = file.Dataset.TryGetString(DicomTag.SOPInstanceUID, out string current) ? (current ?? "").Trim().TrimEnd('\0') : "";
                sop = plan.SopInstanceUidMap is { } map && map.TryGetValue(old, out string? decided)
                    ? ToUid(decided)
                    : DicomUIDGenerator.GenerateDerivedFromUUID();
            }
            SetUids(file, ToUid(plan.NewFrameOfReferenceUid), ToUid(plan.NewSeriesInstanceUid), sop);
        }
        foreach (PlannedTagEdit edit in plan.TagEdits)
        {
            DicomTagCatalog.Apply(file.Dataset, edit);
        }
        file.Save(output);
        return sop?.UID;
    }

    private static void SetUids(DicomFile file, DicomUID? newFrame, DicomUID? newSeries, DicomUID? newSop)
    {
        if (newFrame is not null)
        {
            file.Dataset.AddOrUpdate(DicomTag.FrameOfReferenceUID, newFrame);
        }
        if (newSeries is not null)
        {
            file.Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, newSeries);
        }
        if (newSop is not null)
        {
            file.Dataset.AddOrUpdate(DicomTag.SOPInstanceUID, newSop);
            // Keep the file meta header consistent with the dataset.
            file.FileMetaInfo?.AddOrUpdate(DicomTag.MediaStorageSOPInstanceUID, newSop);
        }
    }

    /// <summary>
    /// Points every reference in an RT Structure Set at the new UIDs of the series it follows:
    /// the frame of reference and series under Referenced Frame of Reference Sequence, the frame each ROI is
    /// defined in, and the image behind every contour. References to anything else are left alone.
    /// </summary>
    private static void RepointStructureSet(DicomDataset ds, StructureSetReferences references)
    {
        var movedFrames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DicomDataset frameItem in DicomScanner.Items(ds, DicomTag.ReferencedFrameOfReferenceSequence))
        {
            string oldFrame = Uid(frameItem, DicomTag.FrameOfReferenceUID);
            string? newFrame = null;
            foreach (DicomDataset study in DicomScanner.Items(frameItem, DicomTag.RTReferencedStudySequence))
            {
                foreach (DicomDataset series in DicomScanner.Items(study, DicomTag.RTReferencedSeriesSequence))
                {
                    string oldSeries = Uid(series, DicomTag.SeriesInstanceUID);
                    if (!references.FollowedSeries.Contains(oldSeries))
                    {
                        continue;
                    }
                    // The frame goes where the series it names goes, even if another series shared the old frame.
                    newFrame ??= references.FrameBySeries.GetValueOrDefault(oldSeries);
                    if (references.Series.TryGetValue(oldSeries, out string? newSeries))
                    {
                        series.AddOrUpdate(DicomTag.SeriesInstanceUID, ToUid(newSeries));
                    }
                    RepointImages(series, references);
                }
            }
            newFrame ??= references.Frames.GetValueOrDefault(oldFrame);
            if (newFrame is not null && oldFrame.Length > 0)
            {
                frameItem.AddOrUpdate(DicomTag.FrameOfReferenceUID, ToUid(newFrame));
                movedFrames.TryAdd(oldFrame, newFrame);
            }
        }

        string? Moved(string oldFrame) => movedFrames.GetValueOrDefault(oldFrame) ?? references.Frames.GetValueOrDefault(oldFrame);

        foreach (DicomDataset roi in DicomScanner.Items(ds, DicomTag.StructureSetROISequence))
        {
            if (Moved(Uid(roi, DicomTag.ReferencedFrameOfReferenceUID)) is { } newFrame)
            {
                roi.AddOrUpdate(DicomTag.ReferencedFrameOfReferenceUID, ToUid(newFrame));
            }
        }
        foreach (DicomDataset roiContour in DicomScanner.Items(ds, DicomTag.ROIContourSequence))
        {
            foreach (DicomDataset contour in DicomScanner.Items(roiContour, DicomTag.ContourSequence))
            {
                RepointImages(contour, references);
            }
        }
        // Not part of the RT Structure Set module, but some systems write a top-level frame as well.
        if (Moved(Uid(ds, DicomTag.FrameOfReferenceUID)) is { } topLevel)
        {
            ds.AddOrUpdate(DicomTag.FrameOfReferenceUID, ToUid(topLevel));
        }
    }

    private static void RepointImages(DicomDataset owner, StructureSetReferences references)
    {
        foreach (DicomDataset image in DicomScanner.Items(owner, DicomTag.ContourImageSequence))
        {
            if (references.SopInstances.TryGetValue(Uid(image, DicomTag.ReferencedSOPInstanceUID), out string? newSop))
            {
                image.AddOrUpdate(DicomTag.ReferencedSOPInstanceUID, ToUid(newSop));
            }
        }
    }

    private static string Uid(DicomDataset ds, DicomTag tag) =>
        ds.TryGetString(tag, out string value) ? (value ?? "").Trim().TrimEnd('\0') : "";

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(uid))]
    private static DicomUID? ToUid(string? uid) => uid is null ? null : new DicomUID(uid, "Generated", DicomUidType.Unknown);

    private static string Describe(DicomSeriesInfo series)
    {
        string description = series.SeriesDescription.Length > 0 ? series.SeriesDescription : "(no description)";
        return $"{series.Modality} · {description}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort clean-up only.
        }
    }
}
