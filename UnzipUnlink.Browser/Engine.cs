using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using FellowOakDicom;
using UnzipUnlink.Core;

namespace UnzipUnlink.Browser;

/// <summary>
/// The in-browser engine. Called from wwwroot/engine-worker.js, one file at a time: JavaScript reads the bytes
/// (from a picked file, a dropped folder, a .zip entry or the File System Access API) and hands them in; the
/// engine never touches a file system. One study is held at a time. Single-threaded by design: browser
/// WebAssembly has no threads here, so nothing in this class blocks or uses Parallel.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class Engine
{
    private static ScanBuilder? _builder;
    private static Stopwatch? _scanClock;
    private static ScanResult? _scan;
    private static UnlinkPlan? _plan;

    /// <summary>Starts a new study and forgets the previous one.</summary>
    [JSExport]
    public static void Reset()
    {
        _builder = new ScanBuilder();
        _scanClock = Stopwatch.StartNew();
        _scan = null;
        _plan = null;
    }

    /// <summary>
    /// Reads the header of one file. <paramref name="bytes"/> may be only the start of the file
    /// (<paramref name="partial"/> = true). Returns 0 image, 1 not DICOM, 2 DICOMDIR, 3 unreadable, or
    /// 4 "need the whole file" (a partial read that failed; the caller retries with every byte).
    /// </summary>
    [JSExport]
    public static int AddFile(string key, byte[] bytes, double length, bool partial)
    {
        ScanBuilder builder = _builder ?? throw new InvalidOperationException("Call Reset first.");
        using var stream = new MemoryStream(bytes, writable: false);
        HeaderReadResult read = DicomScanner.ReadHeader(stream, (long)length);
        if (partial && read.Kind == HeaderReadKind.Unreadable)
        {
            return 4;
        }
        builder.Add(key, read);
        return (int)read.Kind;
    }

    /// <summary>Groups everything added so far into series. Returns the scan as JSON (same shape as the server's).</summary>
    [JSExport]
    public static string FinishScan()
    {
        ScanBuilder builder = _builder ?? throw new InvalidOperationException("Call Reset first.");
        _scan = builder.Build("", _scanClock?.Elapsed ?? TimeSpan.Zero);
        _plan = null;
        return JsonSerializer.Serialize(Views.Scan(_scan), ViewJson.Default.ScanView);
    }

    /// <summary>What would happen with these options (nothing is written). JSON in, JSON out.</summary>
    [JSExport]
    public static string Preview(string requestJson)
    {
        ScanResult scan = _scan ?? throw new InvalidOperationException("Nothing has been scanned.");
        return JsonSerializer.Serialize(Views.Preview(scan, ParseRequest(requestJson)), ViewJson.Default.PreviewView);
    }

    /// <summary>Fixes the plan (new UIDs are drawn now) and returns, per series, the files to rewrite.</summary>
    [JSExport]
    public static string CreatePlan(string requestJson)
    {
        ScanResult scan = _scan ?? throw new InvalidOperationException("Nothing has been scanned.");
        _plan = UnlinkPlanner.Create(scan, ParseRequest(requestJson).ToOptions());
        return JsonSerializer.Serialize(Views.Plan(_plan), ViewJson.Default.PlanView);
    }

    /// <summary>Rewrites one file of planned series <paramref name="seriesIndex"/> and returns the new bytes.</summary>
    [JSExport]
    public static byte[] Rewrite(int seriesIndex, byte[] input)
    {
        UnlinkPlan plan = _plan ?? throw new InvalidOperationException("No plan.");
        using var source = new MemoryStream(input, writable: false);
        using var output = new MemoryStream(input.Length + 512);
        UnlinkRunner.Rewrite(source, output, plan.Series[seriesIndex]);
        return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
    }

    /// <summary>The UID report (.csv) for the current plan.</summary>
    [JSExport]
    public static string Report() => UidReport.ToCsv(_plan ?? throw new InvalidOperationException("No plan."));

    /// <summary>
    /// Self-check hook: rewrites with caller-chosen UIDs (empty string = leave alone) so a test can compare the
    /// output byte for byte with the native engine. Not used by the app itself.
    /// </summary>
    [JSExport]
    public static byte[] RewriteWithUids(byte[] input, string frame, string series, string sop)
    {
        using var source = new MemoryStream(input, writable: false);
        using var output = new MemoryStream(input.Length + 512);
        UnlinkRunner.Rewrite(source, output, Uid(frame), Uid(series), Uid(sop));
        return output.ToArray();
    }

    /// <summary>Managed heap size in bytes, for the diagnostics shown by the self-test.</summary>
    [JSExport]
    public static double ManagedMemory() => GC.GetTotalMemory(false);

    private static DicomUID? Uid(string value) =>
        value.Length == 0 ? null : new DicomUID(value, "Generated", DicomUidType.Unknown);

    private static ChangeRequest ParseRequest(string json) =>
        JsonSerializer.Deserialize(json, ViewJson.Default.ChangeRequest) ?? throw new ArgumentException("Empty request.");
}

/// <summary>What the page asked to change. Mirrors the server's ChangeRequest.</summary>
public sealed record ChangeRequest(
    bool FrameOfReference, bool SeriesUid, bool SopUid, string[]? Modalities,
    bool KeepCtLinked, bool KeepPetCtLinked, string[]? SeriesUids)
{
    public UnlinkOptions ToOptions() => new()
    {
        NewFrameOfReferenceUid = FrameOfReference,
        NewSeriesInstanceUid = SeriesUid,
        NewSopInstanceUid = SopUid,
        Modalities = new HashSet<string>(Modalities ?? [], StringComparer.OrdinalIgnoreCase),
        KeepCtSeriesLinked = KeepCtLinked,
        KeepPetCtLinked = KeepPetCtLinked,
        SeriesInstanceUids = SeriesUids is null ? null : new HashSet<string>(SeriesUids, StringComparer.Ordinal),
    };
}

public sealed record FileProblem(string File, string Error);

public sealed record SeriesView(
    string Uid, string Modality, bool Supported, string Number, string Description, int Files, long Bytes,
    string Patient, string PatientId, string Study, string StudyDescription, string StudyDate, int? Frame, bool MixedFrames);

public sealed record ScanView(
    int FileCount, int OtherFileCount, int UnreadableCount, IReadOnlyList<FileProblem> Unreadable,
    int DicomDirCount, double Seconds, IReadOnlyList<SeriesView> Series);

public sealed record PreviewSeries(bool Selected, int? Frame);

public sealed record PreviewView(int SeriesCount, int FileCount, Dictionary<string, PreviewSeries> Series);

public sealed record PlannedSeries(string Label, IReadOnlyList<string> Files);

public sealed record PlanView(int SeriesCount, int FileCount, IReadOnlyList<PlannedSeries> Series);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScanView))]
[JsonSerializable(typeof(PreviewView))]
[JsonSerializable(typeof(PlanView))]
[JsonSerializable(typeof(ChangeRequest))]
internal sealed partial class ViewJson : JsonSerializerContext;

/// <summary>Shapes results for the page exactly like UnzipUnlink.Web's JobView, so both front ends render the same data.</summary>
internal static class Views
{
    public static ScanView Scan(ScanResult scan)
    {
        Dictionary<string, int> frames = FrameKeys(scan.Series.Select(s => s.FrameOfReferenceUid));
        return new ScanView(
            scan.DicomFileCount,
            scan.OtherFileCount,
            scan.UnreadableFiles.Count,
            scan.UnreadableFiles.Take(20).Select(f => new FileProblem(FileName(f.Path), f.Error)).ToList(),
            scan.DicomDirFiles.Count,
            Math.Round(scan.Duration.TotalSeconds, 1),
            scan.Series.Select(s => new SeriesView(
                s.SeriesInstanceUid, s.Modality, s.IsSupported, s.SeriesNumber, s.SeriesDescription, s.Files.Count, s.TotalBytes,
                s.PatientName.Replace('^', ' ').Trim(), s.PatientId, s.StudyInstanceUid, s.StudyDescription, s.StudyDate,
                s.FrameOfReferenceUid.Length == 0 ? null : frames[s.FrameOfReferenceUid],
                s.DistinctFrameOfReferenceCount > 1)).ToList());
    }

    public static PreviewView Preview(ScanResult scan, ChangeRequest request)
    {
        UnlinkPlan plan = UnlinkPlanner.Create(scan, request.ToOptions());
        Dictionary<string, SeriesPlan> planned = plan.Series.ToDictionary(p => p.Series.SeriesInstanceUid, StringComparer.Ordinal);
        string After(DicomSeriesInfo s) =>
            planned.TryGetValue(s.SeriesInstanceUid, out SeriesPlan? p) && p.NewFrameOfReferenceUid is { } changed ? changed : s.FrameOfReferenceUid;
        Dictionary<string, int> frames = FrameKeys(scan.Series.Select(After));
        return new PreviewView(
            plan.Series.Count,
            plan.FileCount,
            scan.Series.ToDictionary(
                s => s.SeriesInstanceUid,
                s => new PreviewSeries(planned.ContainsKey(s.SeriesInstanceUid), After(s).Length == 0 ? null : frames[After(s)])));
    }

    public static PlanView Plan(UnlinkPlan plan) => new(
        plan.Series.Count,
        plan.FileCount,
        plan.Series.Select(p => new PlannedSeries(
            $"{p.Series.Modality} · {(p.Series.SeriesDescription.Length > 0 ? p.Series.SeriesDescription : "(no description)")}",
            p.Series.Files)).ToList());

    private static string FileName(string key) => key[(key.LastIndexOfAny(['/', '\\']) + 1)..];

    private static Dictionary<string, int> FrameKeys(IEnumerable<string> frames)
    {
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string frame in frames)
        {
            if (frame.Length > 0 && !keys.ContainsKey(frame))
            {
                keys.Add(frame, keys.Count);
            }
        }
        return keys;
    }
}
