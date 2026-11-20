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
