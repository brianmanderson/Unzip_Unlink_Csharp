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

    /// <exception cref="ArgumentException">A tag edit in <paramref name="options"/> is not valid (see <see cref="DicomTagCatalog.Validate(IEnumerable{TagEdit})"/>).</exception>
    public static UnlinkPlan Create(IEnumerable<DicomSeriesInfo> allSeries, UnlinkOptions options)
    {
        List<DicomSeriesInfo> all = allSeries.ToList();
        List<DicomSeriesInfo> selected = all.Where(s => IsSelected(s, options)).ToList();
        IReadOnlyList<PlannedTagEdit> tagEdits = DicomTagCatalog.Plan(options.TagEdits);

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

        // Structure sets: which of the selected image series does each one point at?
        var selectedUids = selected.Select(s => s.SeriesInstanceUid).ToHashSet(StringComparer.Ordinal);
        var statuses = new Dictionary<string, StructureSetStatus>(StringComparer.Ordinal);
        var followers = new List<(DicomSeriesInfo StructureSet, List<DicomSeriesInfo> Images)>();
        foreach (DicomSeriesInfo structureSet in all.Where(s => s.IsStructureSet))
        {
            List<DicomSeriesInfo> images = ImagesOf(structureSet, all);
            List<DicomSeriesInfo> changing = images.Where(i => selectedUids.Contains(i.SeriesInstanceUid)).ToList();
            StructureSetStatus status =
                images.Count == 0 ? StructureSetStatus.ImagesNotFound
                : changing.Count == 0 ? StructureSetStatus.ImagesNotSelected
                : options.KeepStructureSetsLinked ? StructureSetStatus.Follows
                : StructureSetStatus.LeftBehind;
            statuses[structureSet.SeriesInstanceUid] = status;
            if (status == StructureSetStatus.Follows)
            {
                followers.Add((structureSet, changing));
            }
        }
        HashSet<string> followed = followers.SelectMany(f => f.Images).Select(i => i.SeriesInstanceUid).ToHashSet(StringComparer.Ordinal);

        // A structure set and its images form one unit: rewritten together or not at all.
        var unitOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < selected.Count; i++)
        {
            unitOf[selected[i].SeriesInstanceUid] = i;
        }
        foreach ((_, List<DicomSeriesInfo> images) in followers)
        {
            // Several series under one structure set (rare) merge into the unit of the first.
            int target = unitOf[images[0].SeriesInstanceUid];
            foreach (int merged in images.Select(i => unitOf[i.SeriesInstanceUid]).Where(u => u != target).Distinct().ToList())
            {
                foreach (string key in unitOf.Where(kv => kv.Value == merged).Select(kv => kv.Key).ToList())
                {
                    unitOf[key] = target;
                }
            }
        }

        var sharedFrames = new Dictionary<string, string>(StringComparer.Ordinal);
        var imagePlans = new Dictionary<string, SeriesPlan>(StringComparer.Ordinal);
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
            imagePlans.Add(series.SeriesInstanceUid, new SeriesPlan
            {
                Series = series,
                NewFrameOfReferenceUid = newFrame,
                NewSeriesInstanceUid = options.NewSeriesInstanceUid ? NewUid() : null,
                NewSopInstanceUids = options.NewSopInstanceUid,
                // Decided now, not while writing, so the structure set that follows this series can be told.
                SopInstanceUidMap = options.NewSopInstanceUid && followed.Contains(series.SeriesInstanceUid) ? NewSopUids(series) : null,
                TagEdits = tagEdits,
                Unit = unitOf[series.SeriesInstanceUid],
            });
        }

        var structurePlans = followers.Select(f => new SeriesPlan
        {
            Series = f.StructureSet,
            References = ReferencesTo(f.Images.Select(i => imagePlans[i.SeriesInstanceUid]).ToList()),
            NewFrameOfReferenceUid = f.Images.Select(i => imagePlans[i.SeriesInstanceUid].NewFrameOfReferenceUid).FirstOrDefault(frame => frame is not null),
            TagEdits = tagEdits,
            Unit = unitOf[f.Images[0].SeriesInstanceUid],
        }).ToList();

        // Scan order, except that the members of a unit sit together with their structure sets last.
        var plans = new List<SeriesPlan>(imagePlans.Count + structurePlans.Count);
        foreach (int unit in selected.Select(s => unitOf[s.SeriesInstanceUid]).Distinct())
        {
            plans.AddRange(selected.Where(s => unitOf[s.SeriesInstanceUid] == unit).Select(s => imagePlans[s.SeriesInstanceUid]));
            plans.AddRange(structurePlans.Where(p => p.Unit == unit));
        }
        return new UnlinkPlan { Series = plans, StructureSets = statuses };
    }

    public static bool IsSelected(DicomSeriesInfo series, UnlinkOptions options) =>
        options.ChangesAnything
        && series.IsSupported
        && options.Modalities.Contains(series.Modality)
        && (options.SeriesInstanceUids is null || options.SeriesInstanceUids.Contains(series.SeriesInstanceUid));

    /// <summary>
    /// The image series (CT, MR, PET) in <paramref name="all"/> that <paramref name="structureSet"/> was drawn
    /// on: the series it names, or, when it names none that are in the scan, the series in a frame of reference
    /// it refers to. Empty when its images are not here.
    /// </summary>
    public static List<DicomSeriesInfo> ImagesOf(DicomSeriesInfo structureSet, IEnumerable<DicomSeriesInfo> all)
    {
        List<DicomSeriesInfo> images = all.Where(s => s.IsSupported).ToList();
        List<DicomSeriesInfo> named = images
            .Where(i => structureSet.ReferencedSeriesInstanceUids.Contains(i.SeriesInstanceUid, StringComparer.Ordinal))
            .ToList();
        if (named.Count > 0)
        {
            return named;
        }
        IEnumerable<string> frames = structureSet.ReferencedFrameOfReferenceUids.Count > 0
            ? structureSet.ReferencedFrameOfReferenceUids
            : [structureSet.FrameOfReferenceUid];
        HashSet<string> wanted = frames.Where(f => f.Length > 0).ToHashSet(StringComparer.Ordinal);
        return images.Where(i => wanted.Contains(i.FrameOfReferenceUid)).ToList();
    }

    private static StructureSetReferences ReferencesTo(List<SeriesPlan> images)
    {
        var series = new Dictionary<string, string>(StringComparer.Ordinal);
        var frameBySeries = new Dictionary<string, string>(StringComparer.Ordinal);
        var frames = new Dictionary<string, string>(StringComparer.Ordinal);
        var sops = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SeriesPlan image in images)
        {
            string uid = image.Series.SeriesInstanceUid;
            if (image.NewSeriesInstanceUid is { } newSeries)
            {
                series[uid] = newSeries;
            }
            if (image.NewFrameOfReferenceUid is { } newFrame)
            {
                frameBySeries[uid] = newFrame;
                if (image.Series.FrameOfReferenceUid.Length > 0)
                {
                    // Two followed series that shared a frame but are being unlinked: the first one wins here;
                    // references that name their series are resolved through FrameBySeries instead.
                    frames.TryAdd(image.Series.FrameOfReferenceUid, newFrame);
                }
            }
            foreach ((string oldSop, string newSop) in image.SopInstanceUidMap ?? new Dictionary<string, string>())
            {
                sops.TryAdd(oldSop, newSop);
            }
        }
        return new StructureSetReferences
        {
            FollowedSeries = images.Select(i => i.Series.SeriesInstanceUid).ToHashSet(StringComparer.Ordinal),
            Series = series,
            FrameBySeries = frameBySeries,
            Frames = frames,
            SopInstances = sops,
        };
    }

    private static Dictionary<string, string> NewSopUids(DicomSeriesInfo series)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string sop in series.SopInstanceUids)
        {
            if (sop.Length > 0)
            {
                map.TryAdd(sop, NewUid());
            }
        }
        return map;
    }

    private static bool IsCt(DicomSeriesInfo s) => string.Equals(s.Modality, "CT", StringComparison.OrdinalIgnoreCase);
    private static bool IsPet(DicomSeriesInfo s) => string.Equals(s.Modality, "PT", StringComparison.OrdinalIgnoreCase);

    internal static string NewUid() => DicomUIDGenerator.GenerateDerivedFromUUID().UID;
}
