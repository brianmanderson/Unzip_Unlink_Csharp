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
        ChangeRequest request = ParseRequest(requestJson);
        if (request.TagErrors().FirstOrDefault(e => e is not null) is { } tagError)
        {
            throw new ArgumentException(tagError);
        }
        _plan = UnlinkPlanner.Create(scan, request.ToOptions());
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

    /// <summary>Every DICOM tag that can be overwritten with a typed value, as JSON (the same shape as /api/tags on the server).</summary>
    [JSExport]
    public static string Tags() => JsonSerializer.Serialize(
        DicomTagCatalog.Editable.Select(t => new TagView(t.Keyword, t.Name, t.Tag, t.Vr)).ToList(), ViewJson.Default.ListTagView);

    /// <summary>
    /// For every scanned series, the keys of its first and last file: the two the page reads to show what a tag
    /// holds today. JSON: { seriesUid: [firstKey, lastKey] }.
    /// </summary>
    [JSExport]
    public static string SampleFiles()
    {
        ScanResult scan = _scan ?? throw new InvalidOperationException("Nothing has been scanned.");
        return JsonSerializer.Serialize(
            scan.Series.ToDictionary(s => s.SeriesInstanceUid, s => new[] { s.Files[0], s.Files[^1] }), ViewJson.Default.DictionaryStringStringArray);
    }

    /// <summary>
    /// The current value of each tag (a JSON array of keywords) in one file, as a JSON array of strings (null where
    /// absent). Returns "" when the bytes could not be read, e.g. because only the start of the file was supplied.
    /// </summary>
    [JSExport]
    public static string TagValues(byte[] bytes, string tagsJson)
    {
        try
        {
            string[] tags = JsonSerializer.Deserialize(tagsJson, ViewJson.Default.StringArray) ?? [];
            using var stream = new MemoryStream(bytes, writable: false);
            return JsonSerializer.Serialize(TagValueReader.Read(stream, tags).ToList(), ViewJson.Default.ListString);
        }
        catch (Exception)
        {
            return "";
        }
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
    bool KeepCtLinked, bool KeepPetCtLinked, string[]? SeriesUids,
    bool KeepStructureSetsLinked = false, TagEditRequest[]? TagEdits = null)
{
    /// <summary>One entry per tag edit, in order: null when it is fine, otherwise what is wrong with it.</summary>
    public IReadOnlyList<string?> TagErrors() =>
        DicomTagCatalog.Validate((TagEdits ?? []).Select(e => new TagEdit(e.Tag ?? "", e.Value ?? "")));

    /// <param name="withTagEdits">False to plan the UID changes alone, for a preview while a tag edit is still invalid.</param>
    public UnlinkOptions ToOptions(bool withTagEdits = true) => new()
    {
        KeepStructureSetsLinked = KeepStructureSetsLinked,
        TagEdits = withTagEdits ? (TagEdits ?? []).Select(e => new TagEdit(e.Tag ?? "", e.Value ?? "")).ToList() : [],
        NewFrameOfReferenceUid = FrameOfReference,
        NewSeriesInstanceUid = SeriesUid,
        NewSopInstanceUid = SopUid,
        Modalities = new HashSet<string>(Modalities ?? [], StringComparer.OrdinalIgnoreCase),
        KeepCtSeriesLinked = KeepCtLinked,
        KeepPetCtLinked = KeepPetCtLinked,
        SeriesInstanceUids = SeriesUids is null ? null : new HashSet<string>(SeriesUids, StringComparer.Ordinal),
    };
}

/// <summary>"Set this tag (keyword or number) to this value" as the page sends it.</summary>
public sealed record TagEditRequest(string? Tag, string? Value);

public sealed record TagView(string Keyword, string Name, string Tag, string Vr);

public sealed record FileProblem(string File, string Error);

public sealed record SeriesView(
    string Uid, string Modality, bool Supported, string Number, string Description, int Files, long Bytes,
    string Patient, string PatientId, string Study, string StudyDescription, string StudyDate, int? Frame, bool MixedFrames,
    bool StructureSet, IReadOnlyList<string> Follows);

public sealed record ScanView(
    int FileCount, int OtherFileCount, int UnreadableCount, IReadOnlyList<FileProblem> Unreadable,
    int DicomDirCount, double Seconds, IReadOnlyList<SeriesView> Series);

/// <param name="StructureSet">Structure sets only: follows | leftBehind | imagesNotSelected | imagesNotFound.</param>
public sealed record PreviewSeries(bool Selected, int? Frame, string? StructureSet);

public sealed record PreviewView(int SeriesCount, int FileCount, IReadOnlyList<string?> TagErrors, Dictionary<string, PreviewSeries> Series);

/// <param name="Unit">Series with the same unit (a structure set and its images) are rewritten together or not at all.</param>
public sealed record PlannedSeries(string Label, IReadOnlyList<string> Files, int Unit);

public sealed record PlanView(int SeriesCount, int FileCount, IReadOnlyList<PlannedSeries> Series);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScanView))]
[JsonSerializable(typeof(PreviewView))]
[JsonSerializable(typeof(PlanView))]
[JsonSerializable(typeof(ChangeRequest))]
[JsonSerializable(typeof(List<TagView>))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<string?>))]
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
                s.DistinctFrameOfReferenceCount > 1,
                s.IsStructureSet,
                s.IsStructureSet ? UnlinkPlanner.ImagesOf(s, scan.Series).Select(i => i.SeriesInstanceUid).ToList() : [])).ToList());
    }

    public static PreviewView Preview(ScanResult scan, ChangeRequest request)
    {
        // An edit that is still being typed must not blank the preview: plan without the edits until they are valid.
        IReadOnlyList<string?> tagErrors = request.TagErrors();
        UnlinkPlan plan = UnlinkPlanner.Create(scan, request.ToOptions(withTagEdits: tagErrors.All(e => e is null)));
        Dictionary<string, SeriesPlan> planned = plan.Series.ToDictionary(p => p.Series.SeriesInstanceUid, StringComparer.Ordinal);
        string After(DicomSeriesInfo s) =>
            planned.TryGetValue(s.SeriesInstanceUid, out SeriesPlan? p) && p.NewFrameOfReferenceUid is { } changed ? changed : s.FrameOfReferenceUid;
        Dictionary<string, int> frames = FrameKeys(scan.Series.Select(After));
        return new PreviewView(
            plan.Series.Count,
            plan.FileCount,
            tagErrors,
            scan.Series.ToDictionary(
                s => s.SeriesInstanceUid,
                s => new PreviewSeries(
                    planned.ContainsKey(s.SeriesInstanceUid),
                    After(s).Length == 0 ? null : frames[After(s)],
                    plan.StructureSets.TryGetValue(s.SeriesInstanceUid, out StructureSetStatus status) ? StatusName(status) : null)));
    }

    public static PlanView Plan(UnlinkPlan plan) => new(
        plan.Series.Count,
        plan.FileCount,
        plan.Series.Select(p => new PlannedSeries(
            $"{p.Series.Modality} · {(p.Series.SeriesDescription.Length > 0 ? p.Series.SeriesDescription : "(no description)")}",
            p.Series.Files,
            p.Unit)).ToList());

    /// <summary>The names the server JSON uses for <see cref="StructureSetStatus"/> (camelCase).</summary>
    private static string StatusName(StructureSetStatus status) => status switch
    {
        StructureSetStatus.Follows => "follows",
        StructureSetStatus.LeftBehind => "leftBehind",
        StructureSetStatus.ImagesNotSelected => "imagesNotSelected",
        _ => "imagesNotFound",
    };

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
