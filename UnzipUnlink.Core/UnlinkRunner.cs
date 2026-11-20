using System.Collections.Concurrent;
using System.Diagnostics;
using FellowOakDicom;

namespace UnzipUnlink.Core;

/// <summary>
/// Executes an <see cref="UnlinkPlan"/>: rewrites the chosen UIDs in every file of every planned series.
/// Cancellation is honoured between series only, so a cancelled run never leaves a series half rewritten.
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

        foreach (SeriesPlan seriesPlan in plan.Series)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            string label = Describe(seriesPlan.Series);
            progress?.Report(new UnlinkProgress(seriesDone, plan.Series.Count, filesDone, filesTotal, label));
            DicomUID? newFrame = ToUid(seriesPlan.NewFrameOfReferenceUid);
            DicomUID? newSeries = ToUid(seriesPlan.NewSeriesInstanceUid);
            // Deliberately not cancellable: a series is rewritten completely or not at all. Stopping half-way
            // would leave one series carrying two sets of identifiers, which re-imports as two broken series.
            Parallel.ForEach(seriesPlan.Series.Files, ParallelSettings.For(CancellationToken.None), path =>
            {
                try
                {
                    RewriteFile(path, newFrame, newSeries, seriesPlan.NewSopInstanceUids);
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
    internal static void RewriteFile(string path, DicomUID? newFrame, DicomUID? newSeries, DicomUID? newSop)
    {
        string temp = path + TempSuffix;
        try
        {
            using (FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (FileStream output = new(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                Rewrite(input, output, newFrame, newSeries, newSop);
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
    /// Everything else is written back unchanged. This is the single implementation behind every rewrite.
    /// </summary>
    public static void Rewrite(Stream input, Stream output, DicomUID? newFrame, DicomUID? newSeries, DicomUID? newSop)
    {
        DicomFile file = DicomFile.Open(input, FileReadOption.ReadAll);
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
        file.Save(output);
    }

    /// <summary>
    /// Rewrites one file of a planned series: the series' new Frame of Reference / Series Instance UIDs and,
    /// when the plan asks for it, a freshly generated SOP Instance UID. Returns the new SOP Instance UID (or null).
    /// </summary>
    public static string? Rewrite(Stream input, Stream output, SeriesPlan plan)
    {
        DicomUID? sop = plan.NewSopInstanceUids ? DicomUIDGenerator.GenerateDerivedFromUUID() : null;
        Rewrite(input, output, ToUid(plan.NewFrameOfReferenceUid), ToUid(plan.NewSeriesInstanceUid), sop);
        return sop?.UID;
    }

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
