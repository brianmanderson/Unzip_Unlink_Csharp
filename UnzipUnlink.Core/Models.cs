namespace UnzipUnlink.Core;

/// <summary>One DICOM series found while scanning a folder tree.</summary>
public sealed class DicomSeriesInfo
{
    public required string SeriesInstanceUid { get; init; }
    public string Modality { get; init; } = "";
    /// <summary>Frame of Reference UID of the first file in the series ("" when absent).</summary>
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
    /// True for the modalities the unlink method was designed and validated for (CT, MR, PET).
    /// Everything else (RTSTRUCT, RTPLAN, REG, SR, ...) is reported but never modified.
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

    /// <summary>Optional explicit selection. When set, only these series (that also match <see cref="Modalities"/>) are modified.</summary>
    public ISet<string>? SeriesInstanceUids { get; set; }

    public bool ChangesAnything => NewFrameOfReferenceUid || NewSeriesInstanceUid || NewSopInstanceUid;
}

/// <summary>The decided change for one series.</summary>
public sealed class SeriesPlan
{
    public required DicomSeriesInfo Series { get; init; }
    /// <summary>New Frame of Reference UID, or null when it is left alone.</summary>
    public string? NewFrameOfReferenceUid { get; init; }
    /// <summary>New Series Instance UID, or null when it is left alone.</summary>
    public string? NewSeriesInstanceUid { get; init; }
    public bool NewSopInstanceUids { get; init; }
}

/// <summary>The full set of decided changes; produced by <see cref="UnlinkPlanner"/>, executed by <see cref="UnlinkRunner"/>.</summary>
public sealed class UnlinkPlan
{
    public required IReadOnlyList<SeriesPlan> Series { get; init; }
    public int FileCount => Series.Sum(s => s.Series.Files.Count);
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
