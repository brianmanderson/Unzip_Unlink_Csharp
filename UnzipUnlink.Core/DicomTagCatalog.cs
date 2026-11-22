using System.Globalization;
using FellowOakDicom;

namespace UnzipUnlink.Core;

/// <summary>One DICOM tag the user may overwrite, as shown in the searchable tag list.</summary>
/// <param name="Keyword">Dictionary keyword, e.g. PatientID. This is what a <see cref="TagEdit"/> carries.</param>
/// <param name="Name">Human name, e.g. "Patient ID".</param>
/// <param name="Tag">Group and element, e.g. "(0010,0020)".</param>
/// <param name="Vr">Value representation code, e.g. LO.</param>
public sealed record EditableTag(string Keyword, string Name, string Tag, string Vr);

/// <summary>
/// The DICOM tags that may be overwritten with a value the user types, and the checks on such a value.
/// Every front end takes its tag list from here, so they all offer the same tags and reject the same input.
/// </summary>
public static class DicomTagCatalog
{
    /// <summary>
    /// Value representations that cannot be typed as one line of text (sequences, binary data), plus UI: every
    /// identifier this tool manages is a UID, and overwriting one by hand (SOP Class, Study Instance, ...) breaks
    /// the links the rest of the tool takes care to keep consistent.
    /// </summary>
    private static readonly HashSet<string> ExcludedVrs = new(StringComparer.Ordinal)
    {
        "SQ", "OB", "OW", "OF", "OD", "OL", "OV", "UN", "UI", "AT", "NONE",
    };

    /// <summary>Text VRs that hold exactly one value, where a backslash is ordinary text and not a separator.</summary>
    private static readonly HashSet<string> SingleValueVrs = new(StringComparer.Ordinal) { "LT", "ST", "UT", "UR" };

    private static readonly Lazy<(IReadOnlyList<EditableTag> List, Dictionary<string, (DicomTag Tag, EditableTag Info)> ByKey)> Catalog = new(Build);

    /// <summary>Every editable tag, sorted by keyword.</summary>
    public static IReadOnlyList<EditableTag> Editable => Catalog.Value.List;

    /// <summary>
    /// The tags whose keyword, name or number contains <paramref name="text"/> (case-insensitive; spaces, commas
    /// and brackets in a tag number are optional). Keyword matches that start with the text come first.
    /// </summary>
    public static IReadOnlyList<EditableTag> Search(string? text, int max = 50)
    {
        string filter = (text ?? "").Trim();
        if (filter.Length == 0)
        {
            return Editable.Take(max).ToList();
        }
        string digits = Compact(filter);
        return Editable
            .Where(t => t.Keyword.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (digits.Length > 0 && Compact(t.Tag).Contains(digits, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => t.Keyword.StartsWith(filter, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(t => t.Keyword, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    /// <summary>Finds an editable tag by keyword or by number ("(0010,0020)", "0010,0020" or "00100020").</summary>
    public static bool TryResolve(string? keywordOrTag, out DicomTag tag, out EditableTag info)
    {
        string key = (keywordOrTag ?? "").Trim();
        var byKey = Catalog.Value.ByKey;
        if (byKey.TryGetValue(key, out var found) || (Compact(key) is { Length: 8 } number && byKey.TryGetValue(number, out found)))
        {
            (tag, info) = found;
            return true;
        }
        tag = null!;
        info = null!;
        return false;
    }

    /// <summary>
    /// Checks a list of edits before anything is written. Returns one entry per edit, in order: null when the
    /// edit is fine, otherwise a sentence for the user saying what is wrong with it.
    /// </summary>
    public static IReadOnlyList<string?> Validate(IEnumerable<TagEdit> edits)
    {
        var errors = new List<string?>();
        var seen = new HashSet<DicomTag>();
        foreach (TagEdit edit in edits)
        {
            string? error = Validate(edit);
            if (error is null && TryResolve(edit.Tag, out DicomTag tag, out EditableTag info) && !seen.Add(tag))
            {
                error = $"{info.Keyword} is listed more than once.";
            }
            errors.Add(error);
        }
        return errors;
    }

    /// <summary>Null when <paramref name="edit"/> can be applied, otherwise why not.</summary>
    public static string? Validate(TagEdit edit)
    {
        if (!TryResolve(edit.Tag, out DicomTag tag, out EditableTag info))
        {
            return string.IsNullOrWhiteSpace(edit.Tag) ? "Choose a tag." : $"\"{edit.Tag}\" is not a tag that can be changed here.";
        }
        string value = edit.Value ?? "";
        if (value.Any(c => c > 0x7E || (c < 0x20 && !(SingleValueVrs.Contains(info.Vr) && c is '\r' or '\n' or '\t'))))
        {
            // The file's character set is not changed, so anything outside plain ASCII could be written as the wrong letter.
            return "Use plain letters, digits and punctuation only (no accents or special symbols).";
        }
        try
        {
            // fo-dicom applies the VR rules (length, allowed characters, date / time / number formats) when a
            // value is added to a new dataset (they validate by default); adding it to a scratch one is the check.
            Apply(new DicomDataset(), tag, info.Vr, value);
            return null;
        }
        catch (Exception ex) when (ex is DicomValidationException or FormatException or OverflowException or ArgumentException or DicomDataException)
        {
            return $"Not a valid value for {info.Keyword} ({Describe(info.Vr)}).";
        }
    }

    /// <summary>
    /// Resolves and validates <paramref name="edits"/> for the planner. Throws <see cref="ArgumentException"/>
    /// with the first problem, so a plan is never created from input that would fail half-way through a run.
    /// </summary>
    internal static IReadOnlyList<PlannedTagEdit> Plan(IEnumerable<TagEdit> edits)
    {
        List<TagEdit> list = edits.ToList();
        IReadOnlyList<string?> errors = Validate(list);
        var planned = new List<PlannedTagEdit>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            if (errors[i] is { } error)
            {
                throw new ArgumentException(error);
            }
            TryResolve(list[i].Tag, out DicomTag tag, out EditableTag info);
            planned.Add(new PlannedTagEdit(tag, info.Keyword, list[i].Value ?? ""));
        }
        return planned;
    }

    /// <summary>Writes one planned edit into a dataset: replaces the tag, or adds it when the file does not have it.</summary>
    internal static void Apply(DicomDataset dataset, PlannedTagEdit edit)
    {
        string vr = TryResolve(edit.Keyword, out _, out EditableTag info) ? info.Vr : "";
        Apply(dataset, edit.Tag, vr, edit.Value);
    }

    private static void Apply(DicomDataset dataset, DicomTag tag, string vr, string value)
    {
        string[] values = value.Length == 0 ? [] : SingleValueVrs.Contains(vr) ? [value] : value.Split('\\');
        dataset.AddOrUpdate(tag, values);
    }

    private static string Describe(string vr) => vr switch
    {
        "DA" => "a date as YYYYMMDD",
        "TM" => "a time as HHMMSS",
        "DT" => "a date and time as YYYYMMDDHHMMSS",
        "AS" => "an age such as 045Y",
        "IS" => "a whole number",
        "DS" => "a number",
        "US" or "UL" or "UV" => "a whole number, zero or above",
        "SS" or "SL" or "SV" => "a whole number",
        "FL" or "FD" => "a number",
        "CS" => "up to 16 capital letters, digits, spaces or underscores",
        "PN" => "a name, parts separated by ^",
        "SH" => "up to 16 characters",
        "LO" => "up to 64 characters",
        "AE" => "up to 16 characters",
        _ => $"type {vr}",
    };

    /// <summary>Hex digits only, so "(0010,0020)", "0010 0020" and "00100020" compare equal.</summary>
    private static string Compact(string text)
    {
        Span<char> buffer = stackalloc char[Math.Min(text.Length, 16)];
        int n = 0;
        foreach (char c in text)
        {
            if (c is '(' or ')' or ',' or ' ')
            {
                continue;
            }
            if (!Uri.IsHexDigit(c) || n == buffer.Length)
            {
                return "";
            }
            buffer[n++] = char.ToUpperInvariant(c);
        }
        return new string(buffer[..n]);
    }

    private static (IReadOnlyList<EditableTag>, Dictionary<string, (DicomTag, EditableTag)>) Build()
    {
        var list = new List<EditableTag>();
        var byKey = new Dictionary<string, (DicomTag, EditableTag)>(StringComparer.OrdinalIgnoreCase);
        foreach (DicomDictionaryEntry entry in DicomDictionary.Default)
        {
            DicomTag? tag = entry?.Tag;
            // Skip ranges (masked), private tags, group lengths, the file meta group, item delimiters and retired tags.
            if (entry is null || tag is null || entry.MaskTag is not null || tag.IsPrivate || entry.IsRetired
                || tag.Element == 0x0000 || tag.Group <= 0x0002 || tag.Group == 0xFFFE
                || string.IsNullOrEmpty(entry.Keyword))
            {
                continue;
            }
            string vr = entry.ValueRepresentations is { Length: > 0 } vrs ? vrs[0].Code : "";
            // Specific Character Set decides how every text value in the file is decoded: not something to retype.
            if (vr.Length == 0 || ExcludedVrs.Contains(vr) || tag == DicomTag.SpecificCharacterSet || byKey.ContainsKey(entry.Keyword))
            {
                continue;
            }
            string number = string.Create(CultureInfo.InvariantCulture, $"({tag.Group:X4},{tag.Element:X4})");
            var info = new EditableTag(entry.Keyword, entry.Name ?? entry.Keyword, number, vr);
            list.Add(info);
            byKey[entry.Keyword] = (tag, info);
            byKey[Compact(number)] = (tag, info);
        }
        list.Sort((a, b) => string.Compare(a.Keyword, b.Keyword, StringComparison.OrdinalIgnoreCase));
        return (list, byKey);
    }
}

/// <summary>What a tag holds today in one series, sampled from its first and last file.</summary>
/// <param name="Value">The value in the first file; null when the tag is absent there.</param>
/// <param name="Varies">True when the last file of the series holds something different.</param>
public sealed record CurrentTagValue(string? Value, bool Varies);

/// <summary>Reads what tags hold today, so a preview can show "old value → new value".</summary>
public static class TagValueReader
{
    /// <summary>
    /// The current value of each of <paramref name="tags"/> (keywords or numbers) in one file; null where the tag
    /// is absent or is not an editable tag. Reading stops before the image data.
    /// </summary>
    public static IReadOnlyList<string?> Read(Stream stream, IReadOnlyList<string> tags)
    {
        var resolved = tags.Select(t => DicomTagCatalog.TryResolve(t, out DicomTag tag, out _) ? tag : null).ToList();
        ushort lastGroup = resolved.Where(t => t is not null).Select(t => t!.Group).DefaultIfEmpty((ushort)0).Max();
        DicomFile file = DicomFile.Open(stream, DicomEncoding.Default,
            state => state.SequenceDepth == 0 && state.Tag.Group > lastGroup, FileReadOption.SkipLargeTags);
        return resolved.Select(tag =>
        {
            try
            {
                return tag is not null && file.Dataset.Contains(tag) && file.Dataset.TryGetString(tag, out string value)
                    ? (value ?? "").TrimEnd('\0', ' ')
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }).ToList();
    }

    /// <summary>
    /// For a scan on disk: the current values per series (keyed by Series Instance UID), in the order of
    /// <paramref name="tags"/>. A series whose files cannot be read is left out. Only the first and last file of each series are opened, which is enough to say
    /// "this value differs between images" for the tags people actually edit without reading a whole study.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<CurrentTagValue>> ReadForScan(
        ScanResult scan, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IReadOnlyList<CurrentTagValue>>(StringComparer.Ordinal);
        foreach (DicomSeriesInfo series in scan.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string?>? first = ReadFile(series.Files[0], tags);
            IReadOnlyList<string?>? last = series.Files.Count > 1 ? ReadFile(series.Files[^1], tags) : first;
            if (first is null || last is null)
            {
                // Unreadable: say nothing about this series rather than claim the tag is not set.
                continue;
            }
            result[series.SeriesInstanceUid] = first.Select((value, i) => new CurrentTagValue(value, value != last[i])).ToList();
        }
        return result;
    }

    private static IReadOnlyList<string?>? ReadFile(string path, IReadOnlyList<string> tags)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 14);
            return Read(stream, tags);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DicomFileException or DicomDataException)
        {
            return null;
        }
    }
}
