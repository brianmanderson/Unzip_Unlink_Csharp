using System.Text;

namespace UnzipUnlink.Core;

/// <summary>
/// Old UID → new UID for every series of an executed plan, as CSV. Nothing is read back from the files, so the
/// only values in here that are not UIDs are the ones the user typed for a tag edit.
/// </summary>
public static class UidReport
{
    public const string Header =
        "Modality,SeriesNumber,SeriesDescription,Files,OldSeriesInstanceUID,NewSeriesInstanceUID,OldFrameOfReferenceUID,NewFrameOfReferenceUID,NewSOPInstanceUIDs,FollowsSeriesInstanceUIDs,TagEdits";

    public static string ToCsv(UnlinkPlan plan)
    {
        var csv = new StringBuilder(Header).Append("\r\n");
        foreach (SeriesPlan item in plan.Series)
        {
            DicomSeriesInfo s = item.Series;
            csv.AppendJoin(',', Escape(s.Modality), Escape(s.SeriesNumber), Escape(s.SeriesDescription), s.Files.Count.ToString(),
                Escape(s.SeriesInstanceUid), Escape(item.NewSeriesInstanceUid ?? "(unchanged)"),
                Escape(s.FrameOfReferenceUid), Escape(item.NewFrameOfReferenceUid ?? "(unchanged)"),
                item.NewSopInstanceUids ? "yes" : "no",
                // A structure set: the image series (old UIDs) whose new identifiers its references now carry.
                Escape(item.References is { } references ? string.Join(' ', references.FollowedSeries.Order(StringComparer.Ordinal)) : ""),
                Escape(string.Join("; ", item.TagEdits.Select(e => $"{e.Keyword}={e.Value}")))).Append("\r\n");
        }
        return csv.ToString();
    }

    /// <summary>Quotes a CSV field and neutralises spreadsheet formula injection.</summary>
    public static string Escape(string value)
    {
        if (value.Length > 0 && "=+-@".Contains(value[0]))
        {
            value = "'" + value;
        }
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
