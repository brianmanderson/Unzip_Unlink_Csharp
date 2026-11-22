using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using UnzipUnlink.Core;

namespace UnzipUnlink.Tests;

/// <summary>The I/O choices that make runs on a network share faster must not change what is written.</summary>
public class NetworkSpeedTests
{
    [Theory]
    [InlineData(@"\\server\share\study", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\?\C:\data", false)]
    [InlineData(@"C:\data\study", false)]
    [InlineData("", false)]
    public void Network_paths_are_recognised(string path, bool expected)
    {
        // C: is assumed to be a local disk, as it is on every machine these tests run on.
        Assert.Equal(expected, StorageInfo.IsNetworkPath(path));
    }

    [Fact]
    public void More_files_are_kept_in_flight_on_a_network_share_than_on_a_local_disk()
    {
        Assert.InRange(ParallelSettings.DegreeFor(network: false), 1, 8);
        Assert.InRange(ParallelSettings.DegreeFor(network: true), 16, 32);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_item_is_processed_exactly_once(bool dedicatedThreads)
    {
        int[] items = Enumerable.Range(0, 500).ToArray();
        int[] seen = new int[items.Length];

        ParallelSettings.ForEach(items, degree: 24, dedicatedThreads, CancellationToken.None, i => Interlocked.Increment(ref seen[i]));

        Assert.All(seen, count => Assert.Equal(1, count));
    }

    [Fact]
    public void Dedicated_threads_reach_the_requested_number_in_flight_and_never_exceed_it()
    {
        // Every worker blocks until all of them have arrived, which only happens if all 12 really run at once.
        using var allArrived = new Barrier(12);
        int inFlight = 0, peak = 0;

        ParallelSettings.ForEach(Enumerable.Range(0, 12).ToArray(), degree: 12, dedicatedThreads: true, CancellationToken.None, _ =>
        {
            int now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            Assert.True(allArrived.SignalAndWait(TimeSpan.FromSeconds(20)));
            Interlocked.Decrement(ref inFlight);
        });

        Assert.Equal(12, peak);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cancelling_stops_handing_out_items_and_throws(bool dedicatedThreads)
    {
        using var cts = new CancellationTokenSource();
        int done = 0;

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ParallelSettings.ForEach(Enumerable.Range(0, 10_000).ToArray(), degree: 4, dedicatedThreads, cts.Token, _ =>
            {
                if (Interlocked.Increment(ref done) == 20)
                {
                    cts.Cancel();
                }
                Thread.Sleep(1);
            }));

        Assert.InRange(done, 20, 200);
    }

    [Fact]
    public void A_failure_on_a_dedicated_thread_reaches_the_caller()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ParallelSettings.ForEach(Enumerable.Range(0, 100).ToArray(), degree: 8, dedicatedThreads: true, CancellationToken.None, i =>
            {
                if (i == 37)
                {
                    throw new InvalidOperationException("boom");
                }
            }));

        Assert.Equal("boom", error.Message);
    }

    [Theory]
    [InlineData(64 * 1024)]                              // read and written whole
    [InlineData((int)UnlinkRunner.WholeFileLimit + 4096)] // streamed
    public void Whole_file_and_streamed_rewrites_write_the_same_bytes_as_the_stream_api(int pixelBytes)
    {
        using var study = new SyntheticStudy();
        string path = Path.Combine(study.Root, "image.dcm");
        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
            { DicomTag.SOPInstanceUID, SyntheticStudy.NewUid() },
            { DicomTag.StudyInstanceUID, SyntheticStudy.NewUid() },
            { DicomTag.SeriesInstanceUID, SyntheticStudy.NewUid() },
            { DicomTag.FrameOfReferenceUID, SyntheticStudy.NewUid() },
            { DicomTag.Modality, "CT" },
            { DicomTag.PatientID, "SYN-001" },
        };
        byte[] pixels = new byte[pixelBytes];
        new Random(7).NextBytes(pixels);
        ds.Add(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(pixels)));
        new DicomFile(ds).Save(path);
        byte[] original = File.ReadAllBytes(path);
        Assert.Equal(pixelBytes > UnlinkRunner.WholeFileLimit, original.Length > UnlinkRunner.WholeFileLimit);
        DicomUID frame = DicomUIDGenerator.GenerateDerivedFromUUID(), series = DicomUIDGenerator.GenerateDerivedFromUUID(), sop = DicomUIDGenerator.GenerateDerivedFromUUID();
        using var expected = new MemoryStream();
        UnlinkRunner.Rewrite(new MemoryStream(original, writable: false), expected, frame, series, sop);

        UnlinkRunner.RewriteFile(path, frame, series, sop);

        Assert.Equal(expected.ToArray(), File.ReadAllBytes(path));
        Assert.Equal(["image.dcm"], Directory.GetFiles(study.Root).Select(Path.GetFileName));   // no temp file left behind
        Assert.Equal(pixels, DicomFile.Open(path, FileReadOption.ReadAll).Dataset.GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Data);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
