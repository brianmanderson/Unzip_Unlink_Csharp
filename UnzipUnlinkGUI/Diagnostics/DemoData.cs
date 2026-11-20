using UnzipUnlink.Core;

namespace UnzipUnlinkGUI.Diagnostics;

/// <summary>
/// A made-up study (no real patient, no files on disk) used by <c>--demo</c> so documentation screenshots
/// can show CT, 4DCT, PET/CT, MR and unsupported series without touching anyone's data.
/// </summary>
public static class DemoData
{
    public const string DisplayPath = @"D:\Imaging\Example_Study";

    public static ScanResult Create()
    {
        const string planningFrame = "1.2.826.0.1.3680043.10.1.1";
        const string fourDFrame = "1.2.826.0.1.3680043.10.1.2";
        const string petFrame = "1.2.826.0.1.3680043.10.1.3";
        const string mrFrame = "1.2.826.0.1.3680043.10.1.4";

        var series = new List<DicomSeriesInfo>
        {
            Series(1, "CT", "2", "Planning CT 2.5 mm", planningFrame, 184),
            Series(2, "RTSTRUCT", "1", "Structures", "", 1),
            Series(3, "CT", "401", "4D CT phase 0%", fourDFrame, 152),
            Series(4, "CT", "402", "4D CT phase 25%", fourDFrame, 152),
            Series(5, "CT", "403", "4D CT phase 50%", fourDFrame, 152),
            Series(6, "CT", "404", "4D CT phase 75%", fourDFrame, 152),
            Series(7, "CT", "5", "CT attenuation correction", petFrame, 299),
            Series(8, "PT", "6", "PET whole body", petFrame, 299),
            Series(9, "MR", "7", "T1 axial post contrast", mrFrame, 176),
            Series(10, "MR", "8", "T2 axial FLAIR", mrFrame, 176),
            Series(11, "MR", "9", "Diffusion b1000", mrFrame, 60),
            Series(12, "REG", "10", "MR to CT registration", planningFrame, 1),
        };
        return new ScanResult
        {
            Root = DisplayPath,
            Series = series,
            DicomFileCount = series.Sum(s => s.Files.Count),
            DicomDirFiles = [Path.Combine(DisplayPath, "DICOMDIR")],
        };
    }

    private static DicomSeriesInfo Series(int index, string modality, string number, string description, string frame, int images) => new()
    {
        SeriesInstanceUid = $"1.2.826.0.1.3680043.10.2.{index}",
        Modality = modality,
        SeriesNumber = number,
        SeriesDescription = description,
        FrameOfReferenceUid = frame,
        DistinctFrameOfReferenceCount = frame.Length > 0 ? 1 : 0,
        StudyInstanceUid = "1.2.826.0.1.3680043.10.3.1",
        PatientId = "EXAMPLE",
        PatientName = "Example^Patient",
        Files = Enumerable.Range(1, images).Select(i => Path.Combine(DisplayPath, $"series{index:00}", $"{i:0000}.dcm")).ToList(),
    };
}
