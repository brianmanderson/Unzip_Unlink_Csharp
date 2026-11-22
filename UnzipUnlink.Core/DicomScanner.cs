using System.Collections.Concurrent;
using System.Diagnostics;
using FellowOakDicom;

namespace UnzipUnlink.Core;

/// <summary>
/// Walks a folder tree and groups every DICOM file by Series Instance UID.
/// Only headers are read (reading stops before the image data), so this is fast even on big studies.
/// </summary>
/// <remarks>
/// Two entry points share one implementation: <see cref="Scan(string, IProgress{ScanProgress}?, CancellationToken)"/>
/// for a folder on disk, and <see cref="ReadHeader"/> + <see cref="ScanBuilder"/> for callers that hold the
/// bytes themselves (the in-browser WebAssembly edition). The folder scan is built from the stream-based pieces.
/// </remarks>
public static class DicomScanner
{
    private const string MediaStorageDirectorySopClass = "1.2.840.10008.1.3.10";
    private const int PreambleLength = 128;

    public static ScanResult Scan(string root, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Folder not found: {root}");
        }
        var stopwatch = Stopwatch.StartNew();
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);

        var builder = new ScanBuilder();
        int done = 0;
        var reportGate = new ProgressGate();

        Parallel.ForEach(files, ParallelSettings.For(cancellationToken), path =>
        {
            try
            {
                HeaderReadResult read;
                try
                {
                    using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 14);
                    read = ReadHeader(stream);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    read = HeaderReadResult.Unreadable(ex.Message);
                }
                builder.Add(path, read);
            }
            finally
            {
                int now = Interlocked.Increment(ref done);
                if (reportGate.ShouldReport(now, files.Length))
                {
                    progress?.Report(new ScanProgress(now, files.Length));
                }
            }
        });
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress(files.Length, files.Length));
        return builder.Build(root, stopwatch.Elapsed);
    }

    /// <summary>
    /// Reads the identifying header of one file from a seekable stream positioned at the start of the file.
    /// Never throws for bad content: a file without the DICM marker is <see cref="HeaderReadKind.NotDicom"/>,
    /// one that cannot be parsed is <see cref="HeaderReadKind.Unreadable"/>.
    /// The stream may hold only the beginning of a large file, because reading stops before the image data;
    /// pass the size of the whole file in <paramref name="length"/> in that case.
    /// </summary>
    public static HeaderReadResult ReadHeader(Stream stream, long? length = null)
    {
        try
        {
            long fileLength = length ?? stream.Length;
            if (!HasDicmMarker(stream))
            {
                return HeaderReadResult.NotDicom;
            }
            stream.Position = 0;
            // Everything the scan needs lives in groups 0002-0020; stopping after them skips the image data.
            DicomFile file = DicomFile.Open(stream, DicomEncoding.Default,
                state => state.SequenceDepth == 0 && state.Tag.Group > 0x0020, FileReadOption.SkipLargeTags);
            if (file.FileMetaInfo?.MediaStorageSOPClassUID?.UID == MediaStorageDirectorySopClass)
            {
                return HeaderReadResult.DicomDir;
            }
            DicomDataset ds = file.Dataset;
            string seriesUid = Get(ds, DicomTag.SeriesInstanceUID);
            if (seriesUid.Length == 0)
            {
                return HeaderReadResult.Unreadable("No Series Instance UID");
            }
            string modality = Get(ds, DicomTag.Modality).ToUpperInvariant();
            List<string> referencedFrames = [], referencedSeries = [];
            if (modality == UnlinkOptions.StructureSetModality)
            {
                // What a structure set points at sits in group 3006, past where the read above stopped.
                if (fileLength > stream.Length)
                {
                    // Only the start of the file was supplied and the references may lie beyond it.
                    return HeaderReadResult.Unreadable("The whole file is needed to read a structure set's references.");
                }
                stream.Position = 0;
                DicomDataset full = DicomFile.Open(stream, DicomEncoding.Default,
                    state => state.SequenceDepth == 0 && (state.Tag.Group > 0x3006 || (state.Tag.Group == 0x3006 && state.Tag.Element > 0x0010)),
                    FileReadOption.SkipLargeTags).Dataset;
                ReadStructureSetReferences(full, referencedFrames, referencedSeries);
            }
            return new HeaderReadResult(HeaderReadKind.Image, new DicomFileHeader(
                seriesUid,
                modality,
                Get(ds, DicomTag.FrameOfReferenceUID),
                Get(ds, DicomTag.StudyInstanceUID),
                Get(ds, DicomTag.PatientName),
                Get(ds, DicomTag.PatientID),
                Get(ds, DicomTag.StudyDescription),
                Get(ds, DicomTag.StudyDate),
                Get(ds, DicomTag.SeriesDescription),
                Get(ds, DicomTag.SeriesNumber),
                GetInt(ds, DicomTag.InstanceNumber),
                fileLength,
                Get(ds, DicomTag.SOPInstanceUID),
                string.Join('\\', referencedFrames),
                string.Join('\\', referencedSeries)), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HeaderReadResult.Unreadable(ex.Message);
        }
    }

    /// <summary>
    /// Collects the frames of reference and image series an RT Structure Set refers to
    /// (Referenced Frame of Reference Sequence → RT Referenced Study → RT Referenced Series).
    /// </summary>
    private static void ReadStructureSetReferences(DicomDataset ds, List<string> frames, List<string> series)
    {
        foreach (DicomDataset frameItem in Items(ds, DicomTag.ReferencedFrameOfReferenceSequence))
        {
            AddDistinct(frames, Get(frameItem, DicomTag.FrameOfReferenceUID));
            foreach (DicomDataset study in Items(frameItem, DicomTag.RTReferencedStudySequence))
            {
                foreach (DicomDataset item in Items(study, DicomTag.RTReferencedSeriesSequence))
                {
                    AddDistinct(series, Get(item, DicomTag.SeriesInstanceUID));
                }
            }
        }
    }

    internal static IEnumerable<DicomDataset> Items(DicomDataset ds, DicomTag sequence) =>
        ds.TryGetSequence(sequence, out DicomSequence found) ? found.Items : [];

    private static void AddDistinct(List<string> list, string value)
    {
        if (value.Length > 0 && !list.Contains(value))
        {
            list.Add(value);
        }
    }

    /// <summary>Same test as fo-dicom's <c>DicomFile.HasValidHeader</c>: a 128-byte preamble followed by "DICM".</summary>
    private static bool HasDicmMarker(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[PreambleLength + 4];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n == 0)
            {
                return false;
            }
            read += n;
        }
        return buffer[PreambleLength..].SequenceEqual("DICM"u8);
    }

    internal static DicomSeriesInfo BuildSeries(IGrouping<string, (string Key, DicomFileHeader Header)> group)
    {
        // Order files by instance number, then path, so "the first file" is stable between runs.
        var ordered = group
            .OrderBy(h => h.Header.InstanceNumber ?? int.MaxValue)
            .ThenBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        DicomFileHeader first = ordered[0].Header;
        List<string> referencedFrames = SplitUids(ordered.Select(h => h.Header.ReferencedFrameOfReferenceUids));
        return new DicomSeriesInfo
        {
            SeriesInstanceUid = group.Key,
            Modality = first.Modality,
            // A structure set carries no frame of its own; show it with the frame it was drawn in.
            FrameOfReferenceUid = first.FrameOfReferenceUid.Length > 0 ? first.FrameOfReferenceUid : referencedFrames.FirstOrDefault() ?? "",
            DistinctFrameOfReferenceCount = ordered.Select(h => h.Header.FrameOfReferenceUid).Distinct(StringComparer.Ordinal).Count(),
            StudyInstanceUid = first.StudyInstanceUid,
            PatientName = first.PatientName,
            PatientId = first.PatientId,
            StudyDescription = first.StudyDescription,
            StudyDate = first.StudyDate,
            SeriesDescription = first.SeriesDescription,
            SeriesNumber = first.SeriesNumber,
            Files = ordered.Select(h => h.Key).ToList(),
            TotalBytes = ordered.Sum(h => h.Header.Length),
            SopInstanceUids = ordered.Select(h => h.Header.SopInstanceUid).ToList(),
            ReferencedFrameOfReferenceUids = referencedFrames,
            ReferencedSeriesInstanceUids = SplitUids(ordered.Select(h => h.Header.ReferencedSeriesInstanceUids)),
        };
    }

    private static List<string> SplitUids(IEnumerable<string> joined) =>
        joined.SelectMany(j => j.Split('\\', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).ToList();

    internal static int SeriesNumberSortKey(string seriesNumber) =>
        int.TryParse(seriesNumber, out int n) ? n : int.MaxValue;

    private static string Get(DicomDataset ds, DicomTag tag)
    {
        try
        {
            return ds.TryGetString(tag, out string value) ? (value ?? "").Trim().TrimEnd('\0') : "";
        }
        catch
        {
            return "";
        }
    }

    private static int? GetInt(DicomDataset ds, DicomTag tag) =>
        int.TryParse(Get(ds, tag), out int value) ? value : null;
}

/// <summary>What <see cref="DicomScanner.ReadHeader"/> found in one file.</summary>
public enum HeaderReadKind { Image, NotDicom, DicomDir, Unreadable }

/// <summary>The identifying values of one DICOM file.</summary>
public sealed record DicomFileHeader(
    string SeriesInstanceUid, string Modality, string FrameOfReferenceUid, string StudyInstanceUid,
    string PatientName, string PatientId, string StudyDescription, string StudyDate,
    string SeriesDescription, string SeriesNumber, int? InstanceNumber, long Length,
    string SopInstanceUid = "",
    // Structure sets only. Several UIDs are joined with a backslash, as DICOM does, so the record keeps value equality.
    string ReferencedFrameOfReferenceUids = "",
    string ReferencedSeriesInstanceUids = "");

public sealed record HeaderReadResult(HeaderReadKind Kind, DicomFileHeader? Header, string? Error)
{
    public static readonly HeaderReadResult NotDicom = new(HeaderReadKind.NotDicom, null, null);
    public static readonly HeaderReadResult DicomDir = new(HeaderReadKind.DicomDir, null, null);
    public static HeaderReadResult Unreadable(string error) => new(HeaderReadKind.Unreadable, null, error);
}

/// <summary>
/// Collects per-file header results and groups them into a <see cref="ScanResult"/>. Thread-safe.
/// A file is identified by a caller-chosen key: the absolute path for a folder scan, or any unique name
/// (such as the relative path inside a dropped folder or .zip) when the caller supplies the bytes.
/// That key is what ends up in <see cref="DicomSeriesInfo.Files"/>.
/// </summary>
public sealed class ScanBuilder
{
    private readonly ConcurrentBag<(string Key, DicomFileHeader Header)> _headers = [];
    private readonly ConcurrentBag<FileFailure> _unreadable = [];
    private readonly ConcurrentBag<string> _dicomDirs = [];
    private int _other;

    public void Add(string key, HeaderReadResult read)
    {
        switch (read.Kind)
        {
            case HeaderReadKind.Image:
                _headers.Add((key, read.Header!));
                break;
            case HeaderReadKind.DicomDir:
                _dicomDirs.Add(key);
                break;
            case HeaderReadKind.Unreadable:
                _unreadable.Add(new FileFailure(key, read.Error ?? "Unreadable"));
                break;
            default:
                Interlocked.Increment(ref _other);
                break;
        }
    }

    public ScanResult Build(string root, TimeSpan duration)
    {
        var series = _headers
            .GroupBy(h => h.Header.SeriesInstanceUid, StringComparer.Ordinal)
            .Select(DicomScanner.BuildSeries)
            .OrderBy(s => s.PatientName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.StudyDate, StringComparer.Ordinal)
            .ThenBy(s => s.StudyInstanceUid, StringComparer.Ordinal)
            .ThenBy(s => DicomScanner.SeriesNumberSortKey(s.SeriesNumber))
            .ThenBy(s => s.SeriesInstanceUid, StringComparer.Ordinal)
            .ToList();

        return new ScanResult
        {
            Root = root,
            Series = series,
            DicomFileCount = _headers.Count,
            OtherFileCount = _other,
            UnreadableFiles = _unreadable.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            DicomDirFiles = _dicomDirs.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            Duration = duration,
        };
    }
}

internal static class ParallelSettings
{
    /// <summary>Capped so a study of very large multi-frame files cannot exhaust memory.</summary>
    public static ParallelOptions For(CancellationToken cancellationToken) => new()
    {
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8),
        CancellationToken = cancellationToken,
    };
}

/// <summary>Throttles progress callbacks to roughly 200 updates per operation.</summary>
internal sealed class ProgressGate
{
    public bool ShouldReport(int done, int total)
    {
        int step = Math.Max(1, total / 200);
        return done % step == 0 || done == total;
    }
}
