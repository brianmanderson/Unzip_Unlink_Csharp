using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using UnzipUnlink.Core;

namespace UnzipUnlink.Tests;

/// <summary>
/// The stream-based entry points (used by the in-browser WebAssembly edition) must give exactly the same results
/// as the path-based ones (used by the server and desktop apps): same scan, byte-identical rewrites.
/// </summary>
public class StreamApiTests
{
    public static TheoryData<string, bool, bool, bool> Variants() => new()
    {
        { "CT", true, false, true },
        { "CT", true, true, true },
        { "MR", true, false, false },
        { "PT", false, true, true },
        { "MR", false, false, true },
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void Stream_rewrite_is_byte_identical_to_path_rewrite(string modality, bool frame, bool series, bool sop)
    {
        using var study = new SyntheticStudy();
        study.AddSeries(modality, SyntheticStudy.NewUid(), slices: 2);
        DicomUID? newFrame = frame ? DicomUIDGenerator.GenerateDerivedFromUUID() : null;
        DicomUID? newSeries = series ? DicomUIDGenerator.GenerateDerivedFromUUID() : null;
        DicomUID? newSop = sop ? DicomUIDGenerator.GenerateDerivedFromUUID() : null;

        foreach (string path in Directory.GetFiles(study.Root))
        {
            byte[] original = File.ReadAllBytes(path);
            using var output = new MemoryStream();
            UnlinkRunner.Rewrite(new MemoryStream(original, writable: false), output, newFrame, newSeries, newSop);

            UnlinkRunner.RewriteFile(path, newFrame, newSeries, newSop);
            byte[] fromPath = File.ReadAllBytes(path);

            Assert.Equal(fromPath, output.ToArray());
            Assert.NotEqual(original, fromPath);
        }
    }

    [Fact]
    public void Stream_rewrite_changes_only_the_requested_uids()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 1);
        string path = Directory.GetFiles(study.Root).Single();
        DicomFile before = DicomFile.Open(path, FileReadOption.ReadAll);
        var plan = new SeriesPlan
        {
            Series = DicomScanner.Scan(study.Root).Series.Single(),
            NewFrameOfReferenceUid = SyntheticStudy.NewUid(),
            NewSeriesInstanceUid = null,
            NewSopInstanceUids = true,
        };

        using var output = new MemoryStream();
        string? newSop;
        using (FileStream input = File.OpenRead(path))
        {
            newSop = UnlinkRunner.Rewrite(input, output, plan);
        }
        output.Position = 0;
        DicomFile after = DicomFile.Open(output, FileReadOption.ReadAll);

        Assert.NotNull(newSop);
        Assert.Equal(newSop, after.Dataset.GetString(DicomTag.SOPInstanceUID));
        Assert.Equal(newSop, after.FileMetaInfo.MediaStorageSOPInstanceUID.UID);
        Assert.Equal(plan.NewFrameOfReferenceUid, after.Dataset.GetString(DicomTag.FrameOfReferenceUID));
        Assert.Equal(before.Dataset.GetString(DicomTag.SeriesInstanceUID), after.Dataset.GetString(DicomTag.SeriesInstanceUID));
        // Every other element is unchanged.
        var changed = new HashSet<DicomTag> { DicomTag.SOPInstanceUID, DicomTag.FrameOfReferenceUID };
        foreach (DicomItem item in before.Dataset.Where(i => !changed.Contains(i.Tag)))
        {
            Assert.True(after.Dataset.Contains(item.Tag), $"{item.Tag} missing");
            if (item is DicomElement element)
            {
                Assert.Equal(element.Buffer.Data, ((DicomElement)after.Dataset.GetDicomItem<DicomItem>(item.Tag)).Buffer.Data);
            }
        }
    }

    [Fact]
    public void Scanning_streams_gives_the_same_result_as_scanning_the_folder()
    {
        using var study = new SyntheticStudy();
        string frame = SyntheticStudy.NewUid();
        study.AddSeries("CT", frame, slices: 4, subfolder: "a");
        study.AddSeries("CT", frame, slices: 3, subfolder: "b", description: "phase 2");
        study.AddSeries("PT", frame, slices: 2, subfolder: "pet");
        study.AddSeries("MR", SyntheticStudy.NewUid(), slices: 2, subfolder: "mr");
        study.AddSeries("RTSTRUCT", frame, slices: 1, subfolder: "rt");
        study.AddTextFile("readme.txt");

        ScanResult fromFolder = DicomScanner.Scan(study.Root);

        var builder = new ScanBuilder();
        foreach (string path in Directory.GetFiles(study.Root, "*", SearchOption.AllDirectories))
        {
            // Keys are relative paths, as in the browser.
            string key = Path.GetRelativePath(study.Root, path).Replace('\\', '/');
            builder.Add(key, DicomScanner.ReadHeader(new MemoryStream(File.ReadAllBytes(path), writable: false)));
        }
        ScanResult fromStreams = builder.Build("", TimeSpan.Zero);

        Assert.Equal(fromFolder.DicomFileCount, fromStreams.DicomFileCount);
        Assert.Equal(fromFolder.OtherFileCount, fromStreams.OtherFileCount);
        Assert.Equal(fromFolder.Series.Count, fromStreams.Series.Count);
        for (int i = 0; i < fromFolder.Series.Count; i++)
        {
            DicomSeriesInfo a = fromFolder.Series[i], b = fromStreams.Series[i];
            Assert.Equal(a.SeriesInstanceUid, b.SeriesInstanceUid);
            Assert.Equal(a.Modality, b.Modality);
            Assert.Equal(a.FrameOfReferenceUid, b.FrameOfReferenceUid);
            Assert.Equal(a.SeriesDescription, b.SeriesDescription);
            Assert.Equal(a.TotalBytes, b.TotalBytes);
            Assert.Equal(
                a.Files.Select(f => Path.GetRelativePath(study.Root, f).Replace('\\', '/')),
                b.Files);
        }

        // The same options give the same plan shape either way.
        var options = new UnlinkOptions
        {
            Modalities = new HashSet<string>(["CT", "PT", "MR"], StringComparer.OrdinalIgnoreCase),
            KeepCtSeriesLinked = true,
            KeepPetCtLinked = true,
        };
        UnlinkPlan planA = UnlinkPlanner.Create(fromFolder, options), planB = UnlinkPlanner.Create(fromStreams, options);
        Assert.Equal(planA.FileCount, planB.FileCount);
        Assert.Equal(
            planA.Series.GroupBy(p => p.NewFrameOfReferenceUid).Select(g => g.Count()).Order(),
            planB.Series.GroupBy(p => p.NewFrameOfReferenceUid).Select(g => g.Count()).Order());
    }

    [Fact]
    public void ReadHeader_needs_only_the_start_of_a_large_file()
    {
        using var study = new SyntheticStudy();
        string frame = SyntheticStudy.NewUid();
        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
            { DicomTag.SOPInstanceUID, SyntheticStudy.NewUid() },
            { DicomTag.StudyInstanceUID, SyntheticStudy.NewUid() },
            { DicomTag.SeriesInstanceUID, SyntheticStudy.NewUid() },
            { DicomTag.FrameOfReferenceUID, frame },
            { DicomTag.Modality, "CT" },
            { DicomTag.InstanceNumber, "7" },
            { DicomTag.Rows, (ushort)512 }, { DicomTag.Columns, (ushort)512 },
            { DicomTag.BitsAllocated, (ushort)16 }, { DicomTag.BitsStored, (ushort)16 }, { DicomTag.HighBit, (ushort)15 },
            { DicomTag.PixelRepresentation, (ushort)0 }, { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
        };
        ds.Add(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(new byte[512 * 512 * 2])));
        string path = Path.Combine(study.Root, "big.dcm");
        new DicomFile(ds).Save(path);
        byte[] all = File.ReadAllBytes(path);

        HeaderReadResult full = DicomScanner.ReadHeader(new MemoryStream(all));
        HeaderReadResult start = DicomScanner.ReadHeader(new MemoryStream(all, 0, 16 * 1024), all.Length);

        Assert.Equal(HeaderReadKind.Image, start.Kind);
        Assert.Equal(full.Header, start.Header);
        Assert.Equal(frame, start.Header!.FrameOfReferenceUid);
        Assert.Equal(7, start.Header.InstanceNumber);
        Assert.Equal(all.Length, start.Header.Length);
    }

    [Fact]
    public void ReadHeader_classifies_non_dicom_and_damaged_content()
    {
        Assert.Equal(HeaderReadKind.NotDicom, DicomScanner.ReadHeader(new MemoryStream("hello"u8.ToArray())).Kind);
        Assert.Equal(HeaderReadKind.NotDicom, DicomScanner.ReadHeader(new MemoryStream([])).Kind);
        byte[] damaged = new byte[400];
        "DICM"u8.CopyTo(damaged.AsSpan(128));
        damaged.AsSpan(132).Fill(0xFF);
        Assert.Equal(HeaderReadKind.Unreadable, DicomScanner.ReadHeader(new MemoryStream(damaged)).Kind);
    }

    [Fact]
    public void Uid_report_lists_old_and_new_uids_per_series()
    {
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2, description: "=cmd, \"quoted\"");
        UnlinkPlan plan = UnlinkPlanner.Create(DicomScanner.Scan(study.Root), new UnlinkOptions
        {
            Modalities = new HashSet<string>(["CT"], StringComparer.OrdinalIgnoreCase),
        });

        string[] lines = UidReport.ToCsv(plan).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(UidReport.Header, lines[0]);
        Assert.Equal(2, lines.Length);
        Assert.Contains(ct, lines[1]);
        Assert.Contains(plan.Series[0].NewFrameOfReferenceUid!, lines[1]);
        Assert.Contains("\"'=cmd, \"\"quoted\"\"\"", lines[1]);
        Assert.Contains("(unchanged)", lines[1]);
    }
}
