using FellowOakDicom;

namespace UnzipUnlink.Core;

/// <summary>
/// Decides which series get which new UIDs. Pure and fast, so it doubles as the "preview" shown to the
/// user before anything is written.
/// </summary>
public static class UnlinkPlanner
{
    public static UnlinkPlan Create(ScanResult scan, UnlinkOptions options) =>
        Create(scan.Series, options);

    public static UnlinkPlan Create(IEnumerable<DicomSeriesInfo> allSeries, UnlinkOptions options)
    {
        List<DicomSeriesInfo> selected = allSeries.Where(s => IsSelected(s, options)).ToList();

        // Frames of reference that hold both a PET and a CT among the selected series: the PET/CT sets.
        HashSet<string> petCtFrames = !options.KeepPetCtLinked ? [] : selected
            .Where(s => s.FrameOfReferenceUid.Length > 0 && (IsCt(s) || IsPet(s)))
            .GroupBy(s => s.FrameOfReferenceUid, StringComparer.Ordinal)
            .Where(g => g.Any(IsCt) && g.Any(IsPet))
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        bool StaysLinked(DicomSeriesInfo s) =>
            s.FrameOfReferenceUid.Length > 0
            && ((options.KeepCtSeriesLinked && IsCt(s))
                || ((IsCt(s) || IsPet(s)) && petCtFrames.Contains(s.FrameOfReferenceUid)));

        var sharedFrames = new Dictionary<string, string>(StringComparer.Ordinal);
        var plans = new List<SeriesPlan>();
        foreach (DicomSeriesInfo series in selected)
        {
            string? newFrame = null;
            if (options.NewFrameOfReferenceUid)
            {
                if (StaysLinked(series))
                {
                    if (!sharedFrames.TryGetValue(series.FrameOfReferenceUid, out newFrame))
                    {
                        newFrame = NewUid();
                        sharedFrames.Add(series.FrameOfReferenceUid, newFrame);
                    }
                }
                else
                {
                    newFrame = NewUid();
                }
            }
            plans.Add(new SeriesPlan
            {
                Series = series,
                NewFrameOfReferenceUid = newFrame,
                NewSeriesInstanceUid = options.NewSeriesInstanceUid ? NewUid() : null,
                NewSopInstanceUids = options.NewSopInstanceUid,
            });
        }
        return new UnlinkPlan { Series = plans };
    }

    public static bool IsSelected(DicomSeriesInfo series, UnlinkOptions options) =>
        options.ChangesAnything
        && series.IsSupported
        && options.Modalities.Contains(series.Modality)
        && (options.SeriesInstanceUids is null || options.SeriesInstanceUids.Contains(series.SeriesInstanceUid));

    private static bool IsCt(DicomSeriesInfo s) => string.Equals(s.Modality, "CT", StringComparison.OrdinalIgnoreCase);
    private static bool IsPet(DicomSeriesInfo s) => string.Equals(s.Modality, "PT", StringComparison.OrdinalIgnoreCase);

    internal static string NewUid() => DicomUIDGenerator.GenerateDerivedFromUUID().UID;
}
