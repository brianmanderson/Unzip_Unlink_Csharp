using System.Text;

namespace UnzipUnlink.Core;

/// <summary>Old UID → new UID for every series of an executed plan, as CSV. Contains no patient identifiers.</summary>
public static class UidReport
{
    public const string Header =
        "Modality,SeriesNumber,SeriesDescription,Files,OldSeriesInstanceUID,NewSeriesInstanceUID,OldFrameOfReferenceUID,NewFrameOfReferenceUID,NewSOPInstanceUIDs";

    public static string ToCsv(UnlinkPlan plan)
    {
        var csv = new StringBuilder(Header).Append("\r\n");
        foreach (SeriesPlan item in plan.Series)
        {
            DicomSeriesInfo s = item.Series;
            csv.AppendJoin(',', Escape(s.Modality), Escape(s.SeriesNumber), Escape(s.SeriesDescription), s.Files.Count.ToString(),
                Escape(s.SeriesInstanceUid), Escape(item.NewSeriesInstanceUid ?? "(unchanged)"),
                Escape(s.FrameOfReferenceUid), Escape(item.NewFrameOfReferenceUid ?? "(unchanged)"),
                item.NewSopInstanceUids ? "yes" : "no").Append("\r\n");
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
