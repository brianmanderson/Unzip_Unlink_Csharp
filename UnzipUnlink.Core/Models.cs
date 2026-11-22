using FellowOakDicom;

namespace UnzipUnlink.Core;

/// <summary>One DICOM series found while scanning a folder tree.</summary>
public sealed class DicomSeriesInfo
{
    public required string SeriesInstanceUid { get; init; }
    public string Modality { get; init; } = "";
    /// <summary>
    /// Frame of Reference UID of the first file in the series ("" when absent). A structure set has no frame of
    /// its own, so for one this is the first frame it references: it then shows in the same link group as its images.
    /// </summary>
    public string FrameOfReferenceUid { get; init; } = "";
    /// <summary>Number of distinct Frame of Reference UIDs seen inside this one series (normally 1).</summary>
    public int DistinctFrameOfReferenceCount { get; init; }
    public string StudyInstanceUid { get; init; } = "";
    public string PatientName { get; init; } = "";
    public string PatientId { get; init; } = "";
    public string StudyDescription { get; init; } = "";
    public string StudyDate { get; init; } = "";
    public string SeriesDescription { get; init; } = "";
    public string SeriesNumber { get; init; } = "";
    /// <summary>Absolute paths of every file belonging to the series.</summary>
    public required IReadOnlyList<string> Files { get; init; }
    public long TotalBytes { get; init; }
    /// <summary>
    /// SOP Instance UID of every file, in the same order as <see cref="Files"/> ("" when a file has none).
    /// The planner needs them to tell a structure set which new UID each image it points at will get.
    /// </summary>
    public IReadOnlyList<string> SopInstanceUids { get; init; } = [];
    /// <summary>Structure sets only: the Series Instance UIDs of the image series the contours were drawn on.</summary>
    public IReadOnlyList<string> ReferencedSeriesInstanceUids { get; init; } = [];
    /// <summary>Structure sets only: every Frame of Reference UID the structure set refers to.</summary>
    public IReadOnlyList<string> ReferencedFrameOfReferenceUids { get; init; } = [];

    /// <summary>An RT Structure Set: never selected on its own, but it can follow the images it references.</summary>
    public bool IsStructureSet => string.Equals(Modality, UnlinkOptions.StructureSetModality, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for the modalities the unlink method was designed and validated for (CT, MR, PET).
    /// Everything else (RTPLAN, RTDOSE, REG, SR, ...) is reported but never modified. A structure set is the one
    /// exception: it cannot be selected, but <see cref="UnlinkOptions.KeepStructureSetsLinked"/> lets it follow its images.
    /// </summary>
    public bool IsSupported => UnlinkOptions.SupportedModalities.Contains(Modality);
}

/// <summary>Everything the scanner learned about a folder tree.</summary>
public sealed class ScanResult
{
    public required string Root { get; init; }
    public required IReadOnlyList<DicomSeriesInfo> Series { get; init; }
    public int DicomFileCount { get; init; }
    /// <summary>Files that are not DICOM (no DICM header) and were ignored.</summary>
    public int OtherFileCount { get; init; }
    /// <summary>Files with a DICOM header that could not be parsed.</summary>
    public IReadOnlyList<FileFailure> UnreadableFiles { get; init; } = [];
    /// <summary>DICOMDIR index files. They are not updated and will be stale after a rewrite.</summary>
    public IReadOnlyList<string> DicomDirFiles { get; init; } = [];
    public TimeSpan Duration { get; init; }
}

public sealed record FileFailure(string Path, string Error);

public sealed record ScanProgress(int FilesDone, int FilesTotal);

/// <summary>What to change, and on which series.</summary>
public sealed class UnlinkOptions
{
    /// <summary>Modalities the method supports. Matches the original desktop tool.</summary>
    public static readonly IReadOnlySet<string> SupportedModalities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CT", "MR", "PT" };

    public const string StructureSetModality = "RTSTRUCT";

    /// <summary>Give each series a new Frame of Reference UID (0020,0052).</summary>
    public bool NewFrameOfReferenceUid { get; set; } = true;
    /// <summary>Give each series a new Series Instance UID (0020,000E).</summary>
    public bool NewSeriesInstanceUid { get; set; }
    /// <summary>Give every file a new SOP Instance UID (0008,0018). Recommended whenever anything else changes.</summary>
    public bool NewSopInstanceUid { get; set; } = true;

    /// <summary>Modalities to modify, e.g. CT, MR, PT (case-insensitive).</summary>
    public ISet<string> Modalities { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 4DCT: CT series that share a Frame of Reference today keep sharing one (new) Frame of Reference,
    /// so the phases of a 4DCT stay registered to each other.
    /// </summary>
    public bool KeepCtSeriesLinked { get; set; }

    /// <summary>
    /// PET/CT: where a PET and a CT share a Frame of Reference today, the CT and PET series in that frame keep
    /// sharing one (new) Frame of Reference, so a PET stays registered to its attenuation CT. Frames that hold
    /// only CT series are not affected by this option (that is what <see cref="KeepCtSeriesLinked"/> is for).
    /// </summary>
    public bool KeepPetCtLinked { get; set; }

    /// <summary>
    /// Structure sets: an RT Structure Set that references a series being changed is rewritten too, so every
    /// reference inside it (frame of reference, series, images) follows that series to its new UIDs and the
    /// contours stay attached to the images they were drawn on. The structure set keeps its own identifiers.
    /// Without this a structure set is left alone and keeps pointing at the original images.
    /// </summary>
    public bool KeepStructureSetsLinked { get; set; }

    /// <summary>Optional explicit selection. When set, only these series (that also match <see cref="Modalities"/>) are modified.</summary>
    public ISet<string>? SeriesInstanceUids { get; set; }

    /// <summary>
    /// Other DICOM tags to overwrite in every file of every changed series, e.g. PatientID. Each tag at most once;
    /// see <see cref="DicomTagCatalog"/> for which tags are allowed and how values are checked.
    /// </summary>
    public IList<TagEdit> TagEdits { get; set; } = [];

    public bool ChangesAnything => NewFrameOfReferenceUid || NewSeriesInstanceUid || NewSopInstanceUid || TagEdits.Count > 0;
}

/// <summary>
/// "Set this tag to this value". <paramref name="Tag"/> is a DICOM keyword (PatientID) or a tag number
/// ((0010,0020) or 00100020). An empty <paramref name="Value"/> empties the tag; multi-valued tags take
/// their values separated by a backslash, as in DICOM itself.
/// </summary>
public sealed record TagEdit(string Tag, string Value);

/// <summary>A <see cref="TagEdit"/> the planner has resolved against the DICOM dictionary and validated.</summary>
public sealed record PlannedTagEdit(DicomTag Tag, string Keyword, string Value);

/// <summary>Why a structure set is, or is not, part of a plan.</summary>
public enum StructureSetStatus
{
    /// <summary>Its images are being changed and its references follow them.</summary>
    Follows,
    /// <summary>Its images are being changed but <see cref="UnlinkOptions.KeepStructureSetsLinked"/> is off: it will keep pointing at the originals.</summary>
    LeftBehind,
    /// <summary>Its images are in the study but are not being changed, so nothing it points at moves.</summary>
    ImagesNotSelected,
    /// <summary>The images it references are not in the scanned study.</summary>
    ImagesNotFound,
}

/// <summary>
/// Old UID → new UID for everything a structure set points at. Only the series it follows are in here, so a
/// reference to anything else is left exactly as it was.
/// </summary>
public sealed class StructureSetReferences
{
    /// <summary>Series Instance UIDs (as they are today) of the image series this structure set follows.</summary>
    public required IReadOnlySet<string> FollowedSeries { get; init; }
    /// <summary>Old → new Series Instance UID, for followed series that get a new one.</summary>
    public required IReadOnlyDictionary<string, string> Series { get; init; }
    /// <summary>Old Series Instance UID → the new Frame of Reference UID of that series.</summary>
    public required IReadOnlyDictionary<string, string> FrameBySeries { get; init; }
    /// <summary>Old → new Frame of Reference UID; used where the structure set names a frame without naming a series.</summary>
    public required IReadOnlyDictionary<string, string> Frames { get; init; }
    /// <summary>Old → new SOP Instance UID of every image in the followed series.</summary>
    public required IReadOnlyDictionary<string, string> SopInstances { get; init; }
}

/// <summary>The decided change for one series.</summary>
public sealed class SeriesPlan
{
    public required DicomSeriesInfo Series { get; init; }
    /// <summary>
    /// New Frame of Reference UID, or null when it is left alone. For a structure set this is the frame its
    /// references move to (it has no frame of its own), which is what previews and the report show.
    /// </summary>
    public string? NewFrameOfReferenceUid { get; init; }
    /// <summary>New Series Instance UID, or null when it is left alone.</summary>
    public string? NewSeriesInstanceUid { get; init; }
    public bool NewSopInstanceUids { get; init; }
    /// <summary>
    /// Old → new SOP Instance UID for the files of this series, decided up front. Only set for an image series
    /// that a planned structure set points at: the structure set and the images must agree on the new UIDs no
    /// matter which is rewritten first. Every other series draws a fresh UID per file while it is rewritten.
    /// </summary>
    public IReadOnlyDictionary<string, string>? SopInstanceUidMap { get; init; }
    /// <summary>Set for a structure set that follows its images: what to repoint. Null for an image series.</summary>
    public StructureSetReferences? References { get; init; }
    /// <summary>Tags to overwrite in every file of this series (already checked by the planner).</summary>
    public IReadOnlyList<PlannedTagEdit> TagEdits { get; init; } = [];
    /// <summary>
    /// Series that must be rewritten together or not at all share a unit: a structure set and the images it
    /// follows. Cancelling is only honoured between units. The series of one unit are adjacent in the plan.
    /// </summary>
    public int Unit { get; init; }

    public bool IsStructureSet => References is not null;
}

/// <summary>The full set of decided changes; produced by <see cref="UnlinkPlanner"/>, executed by <see cref="UnlinkRunner"/>.</summary>
public sealed class UnlinkPlan
{
    public required IReadOnlyList<SeriesPlan> Series { get; init; }
    public int FileCount => Series.Sum(s => s.Series.Files.Count);
    /// <summary>Every structure set in the scan (by Series Instance UID) and what happens to it under this plan.</summary>
    public IReadOnlyDictionary<string, StructureSetStatus> StructureSets { get; init; } = new Dictionary<string, StructureSetStatus>();
}

public sealed record UnlinkProgress(
    int SeriesDone, int SeriesTotal,
    int FilesDone, int FilesTotal,
    string CurrentSeriesDescription);

public sealed class UnlinkResult
{
    public int SeriesChanged { get; init; }
    public int FilesChanged { get; init; }
    public IReadOnlyList<FileFailure> Failures { get; init; } = [];
    public bool Cancelled { get; init; }
    public TimeSpan Duration { get; init; }
}
