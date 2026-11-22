using FellowOakDicom;
using FellowOakDicom.IO.Buffer;

namespace UnzipUnlink.Tests;

/// <summary>
/// Builds small, entirely synthetic DICOM studies in a temp folder. No real patient data is involved, so
/// these tests are safe to run anywhere (including CI).
/// </summary>
public sealed class SyntheticStudy : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "UnzipUnlinkTests", Guid.NewGuid().ToString("N"));

    public SyntheticStudy() => Directory.CreateDirectory(Root);

    public static string NewUid() => DicomUIDGenerator.GenerateDerivedFromUUID().UID;

    /// <summary>Writes <paramref name="slices"/> files for one series and returns the series UID.</summary>
    public string AddSeries(string modality, string? frameOfReferenceUid, int slices = 3, string? subfolder = null,
        string description = "Synthetic", string? seriesUid = null)
    {
        seriesUid ??= NewUid();
        string folder = subfolder is null ? Root : Path.Combine(Root, subfolder);
        Directory.CreateDirectory(folder);
        for (int i = 1; i <= slices; i++)
        {
            string sop = NewUid();
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, SopClassFor(modality) },
                { DicomTag.SOPInstanceUID, sop },
                { DicomTag.StudyInstanceUID, StudyUid },
                { DicomTag.SeriesInstanceUID, seriesUid },
                { DicomTag.Modality, modality },
                { DicomTag.PatientName, "TEST^SYNTHETIC" },
                { DicomTag.PatientID, "SYN-001" },
                { DicomTag.StudyDescription, "Synthetic study" },
                { DicomTag.StudyDate, "20240102" },
                { DicomTag.SeriesDescription, description },
                { DicomTag.SeriesNumber, "1" },
                { DicomTag.InstanceNumber, i.ToString() },
                { DicomTag.Rows, (ushort)4 },
                { DicomTag.Columns, (ushort)4 },
                { DicomTag.BitsAllocated, (ushort)16 },
                { DicomTag.BitsStored, (ushort)16 },
                { DicomTag.HighBit, (ushort)15 },
                { DicomTag.PixelRepresentation, (ushort)0 },
                { DicomTag.SamplesPerPixel, (ushort)1 },
                { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
            };
            if (frameOfReferenceUid is not null)
            {
                ds.Add(DicomTag.FrameOfReferenceUID, frameOfReferenceUid);
            }
            byte[] pixels = new byte[32];
            for (int p = 0; p < pixels.Length; p++)
            {
                pixels[p] = (byte)(p * i);
            }
            ds.Add(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(pixels)));
            new DicomFile(ds).Save(Path.Combine(folder, sop + ".dcm"));
        }
        return seriesUid;
    }

    /// <summary>
    /// Writes an RT Structure Set drawn on the image series <paramref name="imageSeriesUid"/> (already added to
    /// this study): one ROI with a contour on every image, referencing the series, its frame of reference and
    /// each image by SOP Instance UID in all the places a real structure set does. Returns its Series Instance UID.
    /// </summary>
    public string AddStructureSet(string imageSeriesUid, string subfolder = "rtstruct", bool nameTheSeries = true)
    {
        List<DicomDataset> images = Directory.GetFiles(Root, "*.dcm", SearchOption.AllDirectories)
            .Select(f => DicomFile.Open(f).Dataset)
            .Where(d => d.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "") == imageSeriesUid)
            .ToList();
        string frame = images[0].GetSingleValueOrDefault(DicomTag.FrameOfReferenceUID, "");
        DicomUID imageClass = images[0].GetSingleValue<DicomUID>(DicomTag.SOPClassUID);
        List<string> sops = images.Select(d => d.GetString(DicomTag.SOPInstanceUID)).ToList();

        DicomDataset ImageRef(string sop) => new()
        {
            { DicomTag.ReferencedSOPClassUID, imageClass },
            { DicomTag.ReferencedSOPInstanceUID, sop },
        };
        var frameItem = new DicomDataset { { DicomTag.FrameOfReferenceUID, frame } };
        if (nameTheSeries)
        {
            var seriesItem = new DicomDataset { { DicomTag.SeriesInstanceUID, imageSeriesUid } };
            seriesItem.Add(new DicomSequence(DicomTag.ContourImageSequence, sops.Select(ImageRef).ToArray()));
            var studyItem = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, DicomUID.Parse("1.2.840.10008.3.1.2.3.1") },
                { DicomTag.ReferencedSOPInstanceUID, StudyUid },
            };
            studyItem.Add(new DicomSequence(DicomTag.RTReferencedSeriesSequence, seriesItem));
            frameItem.Add(new DicomSequence(DicomTag.RTReferencedStudySequence, studyItem));
        }
        var roi = new DicomDataset
        {
            { DicomTag.ROINumber, "1" },
            { DicomTag.ReferencedFrameOfReferenceUID, frame },
            { DicomTag.ROIName, "BODY" },
            { DicomTag.ROIGenerationAlgorithm, "MANUAL" },
        };
        var roiContour = new DicomDataset { { DicomTag.ReferencedROINumber, "1" } };
        roiContour.Add(new DicomSequence(DicomTag.ContourSequence, sops.Select(sop =>
        {
            var contour = new DicomDataset
            {
                { DicomTag.ContourGeometricType, "CLOSED_PLANAR" },
                { DicomTag.NumberOfContourPoints, "3" },
                { DicomTag.ContourData, new[] { "0", "0", "0", "1", "0", "0", "0", "1", "0" } },
            };
            contour.Add(new DicomSequence(DicomTag.ContourImageSequence, ImageRef(sop)));
            return contour;
        }).ToArray()));

        string seriesUid = NewUid(), sopUid = NewUid();
        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, DicomUID.RTStructureSetStorage },
            { DicomTag.SOPInstanceUID, sopUid },
            { DicomTag.StudyInstanceUID, StudyUid },
            { DicomTag.SeriesInstanceUID, seriesUid },
            { DicomTag.Modality, "RTSTRUCT" },
            { DicomTag.PatientName, "TEST^SYNTHETIC" },
            { DicomTag.PatientID, "SYN-001" },
            { DicomTag.StudyDescription, "Synthetic study" },
            { DicomTag.StudyDate, "20240102" },
            { DicomTag.SeriesDescription, "Structures" },
            { DicomTag.SeriesNumber, "9" },
            { DicomTag.InstanceNumber, "1" },
            { DicomTag.StructureSetLabel, "SYNTHETIC" },
        };
        ds.Add(new DicomSequence(DicomTag.ReferencedFrameOfReferenceSequence, frameItem));
        ds.Add(new DicomSequence(DicomTag.StructureSetROISequence, roi));
        ds.Add(new DicomSequence(DicomTag.ROIContourSequence, roiContour));
        string folder = Path.Combine(Root, subfolder);
        Directory.CreateDirectory(folder);
        new DicomFile(ds).Save(Path.Combine(folder, sopUid + ".dcm"));
        return seriesUid;
    }

    public string StudyUid { get; } = NewUid();

    public string AddTextFile(string name, string content = "not dicom")
    {
        string path = Path.Combine(Root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static DicomUID SopClassFor(string modality) => modality switch
    {
        "CT" => DicomUID.CTImageStorage,
        "MR" => DicomUID.MRImageStorage,
        "PT" => DicomUID.PositronEmissionTomographyImageStorage,
        "RTSTRUCT" => DicomUID.RTStructureSetStorage,
        _ => DicomUID.SecondaryCaptureImageStorage,
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Temp folder clean-up is best effort.
        }
    }
}
