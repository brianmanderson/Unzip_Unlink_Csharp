using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using UnzipUnlink.Core;
using UnzipUnlink.BrowserCheck;

// Commands used by browser-check.mjs:
//   verify    <original DemoStudy folder> <output .zip or folder> [Keyword=value ...]
//                                                                  checks a browser result against the native engine
//   reference <input> <output> <frame> <series> <sop>              native path-based rewrite ("" = leave alone)
//   generate  <folder> <series> <slices>                           synthetic CT study, ~150 KB per file
//   fakezip   <output.zip> <claimedBytes>                          tiny .zip whose directory claims a huge study
return args.FirstOrDefault() switch
{
    "verify" => Verifier.Run(args[1], args[2], args[3..]),
    "reference" => Reference(args[1], args[2], args[3], args[4], args[5]),
    "generate" => Generate(args[1], int.Parse(args[2]), int.Parse(args[3])),
    "fakezip" => FakeZip(args[1], long.Parse(args[2])),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: verify | reference | generate | fakezip (see Program.cs)");
    return 2;
}

static int Reference(string input, string output, string frame, string series, string sop)
{
    // The path-based RewriteFile that the server and desktop apps use, on a copy of the input.
    File.Copy(input, output, overwrite: true);
    UnlinkRunner.RewriteFile(output, Uid(frame), Uid(series), Uid(sop));
    return 0;
}

static DicomUID? Uid(string value) => value.Length == 0 ? null : new DicomUID(value, "Generated", DicomUidType.Unknown);

static int Generate(string root, int seriesCount, int slices)
{
    string study = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
    string sharedFrame = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
    var random = new Random(42);
    for (int s = 0; s < seriesCount; s++)
    {
        string series = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        bool phase = s < seriesCount / 2;
        string frame = phase ? sharedFrame : DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        string folder = Path.Combine(root, $"series{s:000}");
        Directory.CreateDirectory(folder);
        for (int i = 1; i <= slices; i++)
        {
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
                { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID().UID },
                { DicomTag.StudyInstanceUID, study },
                { DicomTag.SeriesInstanceUID, series },
                { DicomTag.FrameOfReferenceUID, frame },
                { DicomTag.Modality, "CT" },
                { DicomTag.PatientName, "SYNTHETIC^LARGE" },
                { DicomTag.PatientID, "SYN-LARGE" },
                { DicomTag.StudyDate, "20260101" },
                { DicomTag.SeriesDescription, phase ? $"4DCT phase {s}" : $"CT {s}" },
                { DicomTag.SeriesNumber, (s + 1).ToString() },
                { DicomTag.InstanceNumber, i.ToString() },
                { DicomTag.Rows, (ushort)272 }, { DicomTag.Columns, (ushort)272 },
                { DicomTag.BitsAllocated, (ushort)16 }, { DicomTag.BitsStored, (ushort)12 }, { DicomTag.HighBit, (ushort)11 },
                { DicomTag.PixelRepresentation, (ushort)0 }, { DicomTag.SamplesPerPixel, (ushort)1 },
                { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
            };
            byte[] pixels = new byte[272 * 272 * 2];
            for (int p = 0; p < pixels.Length; p += 2)
            {
                int v = 1000 + (int)(300 * Math.Sin(p * 0.001 + i)) + random.Next(40);
                pixels[p] = (byte)v;
                pixels[p + 1] = (byte)(v >> 8);
            }
            ds.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(pixels)));
            new DicomFile(ds).Save(Path.Combine(folder, $"{i:0000}.dcm"));
        }
    }
    return 0;
}

static int FakeZip(string path, long claimed)
{
    // Two stored entries whose central directory claims claimed/2 bytes each. Only the size check reads it.
    using var output = new BinaryWriter(File.Create(path));
    var central = new MemoryStream();
    using var cd = new BinaryWriter(central);
    for (int i = 0; i < 2; i++)
    {
        byte[] name = System.Text.Encoding.ASCII.GetBytes($"big/{i}.dcm");
        uint offset = (uint)output.BaseStream.Position;
        output.Write(0x04034b50u); output.Write((ushort)20); output.Write((ushort)0); output.Write((ushort)0);
        output.Write(0u); output.Write(0u); output.Write(0u); output.Write(0u);
        output.Write((ushort)name.Length); output.Write((ushort)0); output.Write(name);
        cd.Write(0x02014b50u); cd.Write((ushort)20); cd.Write((ushort)20); cd.Write((ushort)0); cd.Write((ushort)0);
        cd.Write(0u); cd.Write(0u); cd.Write(0u); cd.Write((uint)(claimed / 2));
        cd.Write((ushort)name.Length); cd.Write((ushort)0); cd.Write((ushort)0); cd.Write((ushort)0); cd.Write((ushort)0);
        cd.Write(0u); cd.Write(offset); cd.Write(name);
    }
    uint cdOffset = (uint)output.BaseStream.Position;
    output.Write(central.ToArray());
    output.Write(0x06054b50u); output.Write((ushort)0); output.Write((ushort)0); output.Write((ushort)2); output.Write((ushort)2);
    output.Write((uint)central.Length); output.Write(cdOffset); output.Write((ushort)0);
    return 0;
}
