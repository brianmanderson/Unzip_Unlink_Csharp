using System.IO.Compression;
using FellowOakDicom;
using UnzipUnlink.Core;

namespace UnzipUnlink.BrowserCheck;

/// <summary>
/// Verifies what the browser edition produced from data/SyntheticDemo (DemoStudy) with the default options, using
/// the native engine as the reference: the same files, the expected links after the run, new and unique SOP Instance
/// UIDs, untouched objects byte-identical, and every rewritten file byte-identical to what the native engine writes
/// for the same new UIDs and tag edits (so no other byte changed). The defaults keep a structure set with its
/// images, so the RTSTRUCT is checked too: it keeps its own identifiers, every reference in it resolves to a
/// rewritten image, and it too is byte-identical to the native engine's output for the same mapping.
/// </summary>
internal static class Verifier
{
    /// <param name="originalStudy">The original DemoStudy folder.</param>
    /// <param name="output">The downloaded .zip (holding DemoStudy/...) or the rewritten / copied folder itself.</param>
    /// <param name="tagEdits">"Keyword=value" tag edits the run was given, expected in every rewritten file.</param>
    public static int Run(string originalStudy, string output, IReadOnlyList<string> tagEdits)
    {
        IReadOnlyList<PlannedTagEdit> edits = DicomTagCatalog.Plan(tagEdits.Select(e => e.Split('=', 2)).Select(e => new TagEdit(e[0], e.Length > 1 ? e[1] : "")));
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

            int rewritten = 0, identical = 0, repointed = 0;
            var sops = new HashSet<string>(StringComparer.Ordinal);
            // Old -> new, as actually written, for the structure set check below.
            var newSopOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var newFrameOfSeries = new Dictionary<string, string>(StringComparer.Ordinal);
            var structureSets = new List<string>();
            foreach (string rel in originals.Intersect(outputs))
            {
                byte[] before = File.ReadAllBytes(Path.Combine(originalStudy, rel));
                byte[] after = File.ReadAllBytes(Path.Combine(result, rel));
                HeaderReadResult header = DicomScanner.ReadHeader(new MemoryStream(before));
                bool supported = header.Kind == HeaderReadKind.Image && UnlinkOptions.SupportedModalities.Contains(header.Header!.Modality);
                if (header.Kind == HeaderReadKind.Image && header.Header!.Modality == UnlinkOptions.StructureSetModality)
                {
                    structureSets.Add(rel);   // checked once every image's new identifiers are known
                    continue;
                }
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
                foreach (PlannedTagEdit edit in edits)
                {
                    Check(now.GetSingleValueOrDefault(edit.Tag, "") == edit.Value, $"{rel}: {edit.Keyword} is not \"{edit.Value}\"");
                }
                // Every other byte: the native engine, given the same new UIDs and tag edits, must write exactly this file.
                string oldSop = was.GetString(DicomTag.SOPInstanceUID);
                using var expected = new MemoryStream();
                UnlinkRunner.Rewrite(new MemoryStream(before), expected, new SeriesPlan
                {
                    Series = new DicomSeriesInfo { SeriesInstanceUid = header.Header!.SeriesInstanceUid, Files = [] },
                    NewFrameOfReferenceUid = frame,
                    NewSopInstanceUids = true,
                    SopInstanceUidMap = new Dictionary<string, string> { [oldSop] = sop },
                    TagEdits = edits,
                });
                Check(expected.ToArray().AsSpan().SequenceEqual(after), $"{rel}: differs from the native engine's output beyond the new UIDs and tag edits");
                newSopOf[oldSop] = sop;
                newFrameOfSeries[header.Header!.SeriesInstanceUid] = frame;
                rewritten++;
            }

            // Structure sets: with the defaults each one follows the images it was drawn on.
            ScanResult originalScan = DicomScanner.Scan(originalStudy);
            foreach (string rel in structureSets)
            {
                byte[] before = File.ReadAllBytes(Path.Combine(originalStudy, rel));
                byte[] after = File.ReadAllBytes(Path.Combine(result, rel));
                DicomSeriesInfo info = originalScan.Series.Single(s => s.Files.Contains(Path.Combine(originalStudy, rel.Replace('/', Path.DirectorySeparatorChar))));
                List<DicomSeriesInfo> images = UnlinkPlanner.ImagesOf(info, originalScan.Series);
                if (images.Count == 0)
                {
                    Check(before.AsSpan().SequenceEqual(after), $"{rel}: its images are not in the study, so it should be byte-identical but differs");
                    identical++;
                    continue;
                }
                DicomDataset was = DicomFile.Open(new MemoryStream(before), FileReadOption.ReadAll).Dataset;
                DicomDataset now = DicomFile.Open(new MemoryStream(after), FileReadOption.ReadAll).Dataset;
                Check(now.GetString(DicomTag.SOPInstanceUID) == was.GetString(DicomTag.SOPInstanceUID), $"{rel}: the structure set's own SOP Instance UID changed");
                Check(now.GetString(DicomTag.SeriesInstanceUID) == was.GetString(DicomTag.SeriesInstanceUID), $"{rel}: the structure set's own Series Instance UID changed");
                List<string> imageRefs = ImageReferences(now).ToList();
                // The demo structure set is a stub that names only a frame of reference; a full one also names its images.
                Check(imageRefs.All(sops.Contains), $"{rel}: {imageRefs.Count(r => !sops.Contains(r))} of {imageRefs.Count} image references do not resolve to a rewritten image");
                HashSet<string> expectedFrames = images.Select(i => newFrameOfSeries.GetValueOrDefault(i.SeriesInstanceUid, "?")).ToHashSet();
                List<string> frames = FrameReferences(now).ToList();
                Check(frames.Count > 0 && frames.All(expectedFrames.Contains), $"{rel}: a frame of reference in the structure set is not the new frame of its images");
                // Byte for byte what the native engine writes for the same old -> new mapping.
                using var expected = new MemoryStream();
                UnlinkRunner.Rewrite(new MemoryStream(before), expected, new SeriesPlan
                {
                    Series = info,
                    References = new StructureSetReferences
                    {
                        FollowedSeries = images.Select(i => i.SeriesInstanceUid).ToHashSet(),
                        Series = new Dictionary<string, string>(),
                        FrameBySeries = images.Where(i => newFrameOfSeries.ContainsKey(i.SeriesInstanceUid)).ToDictionary(i => i.SeriesInstanceUid, i => newFrameOfSeries[i.SeriesInstanceUid]),
                        Frames = images.Where(i => i.FrameOfReferenceUid.Length > 0 && newFrameOfSeries.ContainsKey(i.SeriesInstanceUid))
                            .GroupBy(i => i.FrameOfReferenceUid).ToDictionary(g => g.Key, g => newFrameOfSeries[g.First().SeriesInstanceUid]),
                        SopInstances = newSopOf,
                    },
                    TagEdits = edits,
                });
                Check(expected.ToArray().AsSpan().SequenceEqual(after), $"{rel}: differs from the native engine's output for the same references and tag edits");
                repointed++;
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
            Check(repointed == 1, $"the structure set should have followed its images (repointed {repointed})");
            Console.WriteLine($"verify: {outputs.Length} files, {rewritten} rewritten, {repointed} structure set(s) repointed, {identical} untouched and byte-identical, " +
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

    /// <summary>Every Referenced SOP Instance UID inside a Contour Image Sequence, wherever it sits in the structure set.</summary>
    private static IEnumerable<string> ImageReferences(DicomDataset ds)
    {
        foreach (DicomSequence sequence in ds.OfType<DicomSequence>())
        {
            foreach (DicomDataset item in sequence.Items)
            {
                if (sequence.Tag == DicomTag.ContourImageSequence)
                {
                    yield return item.GetString(DicomTag.ReferencedSOPInstanceUID);
                }
                foreach (string nested in ImageReferences(item))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>The frames of reference a structure set names: at the top level (some systems), per referenced frame and per ROI.</summary>
    private static IEnumerable<string> FrameReferences(DicomDataset ds)
    {
        if (ds.TryGetString(DicomTag.FrameOfReferenceUID, out string topLevel))
        {
            yield return topLevel;
        }
        if (ds.TryGetSequence(DicomTag.ReferencedFrameOfReferenceSequence, out DicomSequence frames))
        {
            foreach (DicomDataset item in frames.Items)
            {
                yield return item.GetString(DicomTag.FrameOfReferenceUID);
            }
        }
        if (ds.TryGetSequence(DicomTag.StructureSetROISequence, out DicomSequence rois))
        {
            foreach (DicomDataset item in rois.Items)
            {
                yield return item.GetString(DicomTag.ReferencedFrameOfReferenceUID);
            }
        }
    }
}
