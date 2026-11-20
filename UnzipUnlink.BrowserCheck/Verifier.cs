using System.IO.Compression;
using FellowOakDicom;
using UnzipUnlink.Core;

namespace UnzipUnlink.BrowserCheck;

/// <summary>
/// Verifies what the browser edition produced from data/SyntheticDemo (DemoStudy) with the default options, using
/// the native engine as the reference: the same files, the expected links after the run, new and unique SOP Instance
/// UIDs, untouched objects byte-identical, and every rewritten file byte-identical to what the native engine writes
/// for the same new UIDs (so no other byte changed).
/// </summary>
internal static class Verifier
{
    /// <param name="originalStudy">The original DemoStudy folder.</param>
    /// <param name="output">The downloaded .zip (holding DemoStudy/...) or the rewritten / copied folder itself.</param>
    public static int Run(string originalStudy, string output)
    {
        string work = Path.Combine(Path.GetTempPath(), "uu-verify-" + Guid.NewGuid().ToString("N"));
        var problems = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok)
            {
                problems.Add(message);
            }
        }

        try
        {
            string result = output;
            if (File.Exists(output))
            {
                ZipFile.ExtractToDirectory(output, work);   // .NET checks each entry's CRC-32 while extracting
                result = Path.Combine(work, new DirectoryInfo(originalStudy).Name);
            }

            static string[] Relative(string root) => !Directory.Exists(root) ? [] : Directory
                .GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal).ToArray();
            string[] originals = Relative(originalStudy);
            string[] outputs = Relative(result);
            Check(originals.SequenceEqual(outputs), $"file lists differ: {originals.Length} original vs {outputs.Length} in the result");

            int rewritten = 0, identical = 0;
            var sops = new HashSet<string>(StringComparer.Ordinal);
            foreach (string rel in originals.Intersect(outputs))
            {
                byte[] before = File.ReadAllBytes(Path.Combine(originalStudy, rel));
                byte[] after = File.ReadAllBytes(Path.Combine(result, rel));
                HeaderReadResult header = DicomScanner.ReadHeader(new MemoryStream(before));
                bool supported = header.Kind == HeaderReadKind.Image && UnlinkOptions.SupportedModalities.Contains(header.Header!.Modality);
                if (!supported)
                {
                    Check(before.AsSpan().SequenceEqual(after), $"{rel}: should be byte-identical but differs");
                    identical++;
                    continue;
                }
                DicomDataset was = DicomFile.Open(new MemoryStream(before)).Dataset;
                DicomFile nowFile = DicomFile.Open(new MemoryStream(after));
                DicomDataset now = nowFile.Dataset;
                string sop = now.GetString(DicomTag.SOPInstanceUID);
                Check(sop != was.GetString(DicomTag.SOPInstanceUID), $"{rel}: SOP Instance UID not changed");
                Check(sops.Add(sop), $"{rel}: SOP Instance UID {sop} is not unique");
                Check(nowFile.FileMetaInfo.MediaStorageSOPInstanceUID.UID == sop, $"{rel}: file meta Media Storage SOP Instance UID does not match");
                Check(now.GetString(DicomTag.SeriesInstanceUID) == was.GetString(DicomTag.SeriesInstanceUID), $"{rel}: Series Instance UID changed, but the default leaves it");
                string frame = now.GetString(DicomTag.FrameOfReferenceUID);
                Check(frame != was.GetString(DicomTag.FrameOfReferenceUID), $"{rel}: Frame of Reference UID not changed");
                // Every other byte: the native engine, given the same new UIDs, must write exactly this file.
                using var expected = new MemoryStream();
                UnlinkRunner.Rewrite(new MemoryStream(before), expected, Uid(frame), null, Uid(sop));
                Check(expected.ToArray().AsSpan().SequenceEqual(after), $"{rel}: differs from the native engine's output beyond the new UIDs");
                rewritten++;
            }

            // Links after the run, per series folder.
            ScanResult scan = DicomScanner.Scan(result);
            string Folder(DicomSeriesInfo s) => Path.GetRelativePath(result, Path.GetDirectoryName(s.Files[0])!).Replace('\\', '/');
            Dictionary<string, string> frameOf = scan.Series.Where(s => s.IsSupported).ToDictionary(Folder, s => s.FrameOfReferenceUid);
            Dictionary<string, int> members = frameOf.Values.GroupBy(f => f).ToDictionary(g => g.Key, g => g.Count());
            int MembersOf(string folder) => frameOf.TryGetValue(folder, out string? f) ? members[f] : -1;

            List<string> fourD = frameOf.Where(kv => kv.Key.StartsWith("4DCT/", StringComparison.Ordinal)).Select(kv => kv.Value).Distinct().ToList();
            Check(frameOf.Keys.Count(k => k.StartsWith("4DCT/", StringComparison.Ordinal)) == 11 && fourD.Count == 1,
                $"the 11 4DCT series should share one frame of reference (found {fourD.Count})");
            Check(fourD.Count == 1 && members[fourD[0]] == 11, "the 4DCT frame of reference should hold only the 4DCT series");
            Check(frameOf.ContainsKey("PETCT/CT") && frameOf.ContainsKey("PETCT/PET") && frameOf["PETCT/CT"] == frameOf["PETCT/PET"],
                "the PET/CT pair should share one frame of reference");
            Check(MembersOf("PETCT/CT") == 2, "the PET/CT frame of reference should hold exactly the pair");
            foreach (string alone in new[] { "CT_PLAN", "MR/T1", "MR/T2", "MR/DWI" })
            {
                Check(MembersOf(alone) == 1, $"{alone} should stand alone");
            }
            Check(scan.Series.Count(s => s.Modality == "RTSTRUCT") == 1, "the RTSTRUCT is missing");

            string groups = string.Join("; ", frameOf.GroupBy(kv => kv.Value).Select(g => string.Join("+", g.Select(x => x.Key).Order(StringComparer.Ordinal))));
            Console.WriteLine($"verify: {outputs.Length} files, {rewritten} rewritten, {identical} untouched and byte-identical, " +
                $"{scan.Series.Count} series, {members.Count} frames of reference after: {groups}");
            foreach (string problem in problems.Take(30))
            {
                Console.WriteLine("PROBLEM " + problem);
            }
            Console.WriteLine(problems.Count == 0 ? "VERIFY OK" : $"VERIFY FAILED ({problems.Count} problems)");
            return problems.Count == 0 ? 0 : 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, true);
                }
            }
            catch
            {
                // Temp clean-up is best effort.
            }
        }
    }

    private static DicomUID Uid(string uid) => new(uid, "Generated", DicomUidType.Unknown);
}
