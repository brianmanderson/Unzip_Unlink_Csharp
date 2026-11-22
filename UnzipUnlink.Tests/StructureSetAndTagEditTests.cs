using FellowOakDicom;
using UnzipUnlink.Core;

namespace UnzipUnlink.Tests;

public class StructureSetTests
{
    private static UnlinkOptions Options(bool keepStructureSets = true, bool frame = true, bool series = true, bool sop = true) => new()
    {
        NewFrameOfReferenceUid = frame,
        NewSeriesInstanceUid = series,
        NewSopInstanceUid = sop,
        Modalities = new HashSet<string>(["CT", "MR", "PT"], StringComparer.OrdinalIgnoreCase),
        KeepStructureSetsLinked = keepStructureSets,
    };

    /// <summary>Everything a structure set file points at, read back from disk.</summary>
    private sealed record References(HashSet<string> Frames, HashSet<string> RoiFrames, HashSet<string> Series, HashSet<string> SeriesImages, HashSet<string> ContourImages);

    private static References ReadReferences(string path)
    {
        DicomDataset ds = DicomFile.Open(path, FileReadOption.ReadAll).Dataset;
        HashSet<string> frames = [], roiFrames = [], series = [], seriesImages = [], contourImages = [];
        foreach (DicomDataset frameItem in ds.GetSequence(DicomTag.ReferencedFrameOfReferenceSequence))
        {
            frames.Add(frameItem.GetString(DicomTag.FrameOfReferenceUID));
            if (!frameItem.TryGetSequence(DicomTag.RTReferencedStudySequence, out DicomSequence studies))
            {
                continue;
            }
            foreach (DicomDataset seriesItem in studies.SelectMany(s => s.GetSequence(DicomTag.RTReferencedSeriesSequence)))
            {
                series.Add(seriesItem.GetString(DicomTag.SeriesInstanceUID));
                seriesImages.UnionWith(seriesItem.GetSequence(DicomTag.ContourImageSequence).Select(i => i.GetString(DicomTag.ReferencedSOPInstanceUID)));
            }
        }
        roiFrames.UnionWith(ds.GetSequence(DicomTag.StructureSetROISequence).Select(r => r.GetString(DicomTag.ReferencedFrameOfReferenceUID)));
        contourImages.UnionWith(ds.GetSequence(DicomTag.ROIContourSequence)
            .SelectMany(r => r.GetSequence(DicomTag.ContourSequence))
            .SelectMany(c => c.GetSequence(DicomTag.ContourImageSequence))
            .Select(i => i.GetString(DicomTag.ReferencedSOPInstanceUID)));
        return new References(frames, roiFrames, series, seriesImages, contourImages);
    }

    private static string FileOf(ScanResult scan, string seriesUid) => scan.Series.Single(s => s.SeriesInstanceUid == seriesUid).Files.Single();

    [Fact]
    public void Scan_records_what_a_structure_set_references()
    {
        using var study = new SyntheticStudy();
        string frame = SyntheticStudy.NewUid();
        string ct = study.AddSeries("CT", frame, slices: 3, subfolder: "ct");
        string rs = study.AddStructureSet(ct);

        ScanResult scan = DicomScanner.Scan(study.Root);

        DicomSeriesInfo structureSet = scan.Series.Single(s => s.SeriesInstanceUid == rs);
        Assert.True(structureSet.IsStructureSet);
        Assert.False(structureSet.IsSupported);
        Assert.Equal([ct], structureSet.ReferencedSeriesInstanceUids);
        Assert.Equal([frame], structureSet.ReferencedFrameOfReferenceUids);
        // Shown in the same link group as its images.
        Assert.Equal(frame, structureSet.FrameOfReferenceUid);
        DicomSeriesInfo images = scan.Series.Single(s => s.SeriesInstanceUid == ct);
        Assert.Equal(3, images.SopInstanceUids.Distinct().Count());
        Assert.All(images.Files.Zip(images.SopInstanceUids), pair =>
            Assert.Equal(DicomFile.Open(pair.First).Dataset.GetString(DicomTag.SOPInstanceUID), pair.Second));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public void Every_reference_in_a_followed_structure_set_resolves_to_the_rewritten_images(bool frame, bool series, bool sop)
    {
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 4, subfolder: "ct");
        string rs = study.AddStructureSet(ct);
        ScanResult before = DicomScanner.Scan(study.Root);
        string structureFile = FileOf(before, rs);
        DicomDataset original = DicomFile.Open(structureFile, FileReadOption.ReadAll).Dataset.Clone();

        UnlinkPlan plan = UnlinkPlanner.Create(before, Options(frame: frame, series: series, sop: sop));
        UnlinkResult result = UnlinkRunner.Run(plan);

        Assert.Empty(result.Failures);
        Assert.Equal(2, result.SeriesChanged);
        Assert.Equal(StructureSetStatus.Follows, plan.StructureSets[rs]);
        ScanResult after = DicomScanner.Scan(study.Root);
        DicomSeriesInfo images = after.Series.Single(s => s.Modality == "CT");
        References refs = ReadReferences(structureFile);

        Assert.Equal([images.FrameOfReferenceUid], refs.Frames);
        Assert.Equal([images.FrameOfReferenceUid], refs.RoiFrames);
        Assert.Equal([images.SeriesInstanceUid], refs.Series);
        Assert.Equal(images.SopInstanceUids.ToHashSet(), refs.SeriesImages);
        Assert.Equal(images.SopInstanceUids.ToHashSet(), refs.ContourImages);
        // ...and they really moved when asked to.
        DicomSeriesInfo imagesBefore = before.Series.Single(s => s.Modality == "CT");
        Assert.Equal(frame, images.FrameOfReferenceUid != imagesBefore.FrameOfReferenceUid);
        Assert.Equal(series, images.SeriesInstanceUid != imagesBefore.SeriesInstanceUid);
        Assert.Equal(sop, !images.SopInstanceUids.Intersect(imagesBefore.SopInstanceUids).Any());

        // The structure set keeps its own identity, and nothing but the references changed.
        DicomDataset now = DicomFile.Open(structureFile, FileReadOption.ReadAll).Dataset;
        Assert.Equal(original.GetString(DicomTag.SOPInstanceUID), now.GetString(DicomTag.SOPInstanceUID));
        Assert.Equal(rs, now.GetString(DicomTag.SeriesInstanceUID));
        Assert.Equal(original.GetString(DicomTag.StructureSetLabel), now.GetString(DicomTag.StructureSetLabel));
        Assert.Equal(
            original.GetSequence(DicomTag.ROIContourSequence).Single().GetSequence(DicomTag.ContourSequence).First().GetValues<string>(DicomTag.ContourData),
            now.GetSequence(DicomTag.ROIContourSequence).Single().GetSequence(DicomTag.ContourSequence).First().GetValues<string>(DicomTag.ContourData));
    }

    [Fact]
    public void A_structure_set_whose_images_are_not_selected_is_byte_identical_afterwards()
    {
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2, subfolder: "ct");
        string other = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2, subfolder: "other");
        string rs = study.AddStructureSet(ct);
        ScanResult scan = DicomScanner.Scan(study.Root);
        byte[] original = File.ReadAllBytes(FileOf(scan, rs));
        UnlinkOptions options = Options();
        options.SeriesInstanceUids = new HashSet<string> { other };

        UnlinkPlan plan = UnlinkPlanner.Create(scan, options);
        UnlinkRunner.Run(plan);

        Assert.Equal(other, Assert.Single(plan.Series).Series.SeriesInstanceUid);
        Assert.Equal(StructureSetStatus.ImagesNotSelected, plan.StructureSets[rs]);
        Assert.Equal(original, File.ReadAllBytes(FileOf(scan, rs)));
    }

    [Fact]
    public void A_structure_set_whose_images_are_not_in_the_study_is_left_alone()
    {
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2, subfolder: "ct");
        string rs = study.AddStructureSet(ct);
        Directory.Delete(Path.Combine(study.Root, "ct"), recursive: true);
        study.AddSeries("MR", SyntheticStudy.NewUid(), slices: 2, subfolder: "mr");
        ScanResult scan = DicomScanner.Scan(study.Root);
        byte[] original = File.ReadAllBytes(FileOf(scan, rs));

        UnlinkPlan plan = UnlinkPlanner.Create(scan, Options());
        UnlinkRunner.Run(plan);

        Assert.Equal(StructureSetStatus.ImagesNotFound, plan.StructureSets[rs]);
        Assert.DoesNotContain(plan.Series, p => p.IsStructureSet);
        Assert.Equal(original, File.ReadAllBytes(FileOf(scan, rs)));
    }

    [Fact]
    public void With_the_option_off_a_structure_set_is_never_touched()
    {
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2, subfolder: "ct");
        string rs = study.AddStructureSet(ct);
        ScanResult scan = DicomScanner.Scan(study.Root);
        byte[] original = File.ReadAllBytes(FileOf(scan, rs));

        UnlinkPlan plan = UnlinkPlanner.Create(scan, Options(keepStructureSets: false));
        UnlinkRunner.Run(plan);

        SeriesPlan only = Assert.Single(plan.Series);
        Assert.Equal(ct, only.Series.SeriesInstanceUid);
        Assert.Null(only.SopInstanceUidMap);
        Assert.Equal(StructureSetStatus.LeftBehind, plan.StructureSets[rs]);
        Assert.Equal(original, File.ReadAllBytes(FileOf(scan, rs)));
    }

    [Fact]
    public void A_structure_set_that_names_no_series_follows_the_images_in_its_frame_of_reference()
    {
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2, subfolder: "ct");
        string rs = study.AddStructureSet(ct, nameTheSeries: false);
        ScanResult scan = DicomScanner.Scan(study.Root);

        UnlinkRunner.Run(UnlinkPlanner.Create(scan, Options()));

        DicomSeriesInfo images = DicomScanner.Scan(study.Root).Series.Single(s => s.Modality == "CT");
        References refs = ReadReferences(FileOf(scan, rs));
        Assert.Equal([images.FrameOfReferenceUid], refs.Frames);
        Assert.Equal([images.FrameOfReferenceUid], refs.RoiFrames);
        Assert.Equal(images.SopInstanceUids.ToHashSet(), refs.ContourImages);
    }

    [Fact]
    public void A_structure_set_follows_its_own_series_when_a_shared_frame_is_split()
    {
        // Two CTs registered to each other, contours on the second. Unlinking gives each CT its own frame;
        // the structure set must end up in the frame of the CT it was drawn on, not the other one.
        using var study = new SyntheticStudy();
        string frame = SyntheticStudy.NewUid();
        study.AddSeries("CT", frame, slices: 2, subfolder: "a");
        string drawnOn = study.AddSeries("CT", frame, slices: 2, subfolder: "b");
        string rs = study.AddStructureSet(drawnOn);
        ScanResult scan = DicomScanner.Scan(study.Root);

        UnlinkPlan plan = UnlinkPlanner.Create(scan, Options(series: false));
        UnlinkRunner.Run(plan);

        ScanResult after = DicomScanner.Scan(study.Root);
        string newFrame = after.Series.Single(s => s.SeriesInstanceUid == drawnOn).FrameOfReferenceUid;
        Assert.Equal(2, after.Series.Where(s => s.Modality == "CT").Select(s => s.FrameOfReferenceUid).Distinct().Count());
        Assert.Equal([newFrame], ReadReferences(FileOf(scan, rs)).Frames);
        Assert.Equal(newFrame, after.Series.Single(s => s.SeriesInstanceUid == rs).FrameOfReferenceUid);
        Assert.Equal(newFrame, plan.Series.Single(p => p.IsStructureSet).NewFrameOfReferenceUid);
    }

    [Fact]
    public void A_structure_set_is_planned_right_after_its_images_in_the_same_unit()
    {
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2, subfolder: "ct");
        study.AddSeries("MR", SyntheticStudy.NewUid(), slices: 2, subfolder: "mr");
        string rs = study.AddStructureSet(ct);

        UnlinkPlan plan = UnlinkPlanner.Create(DicomScanner.Scan(study.Root), Options());

        List<string> order = plan.Series.Select(p => p.Series.SeriesInstanceUid).ToList();
        Assert.Equal(order.IndexOf(ct) + 1, order.IndexOf(rs));
        Assert.Equal(plan.Series[order.IndexOf(ct)].Unit, plan.Series[order.IndexOf(rs)].Unit);
        Assert.Equal(2, plan.Series.Select(p => p.Unit).Distinct().Count());
    }

    [Fact]
    public void Cancelling_never_separates_a_structure_set_from_the_images_it_follows()
    {
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 3, subfolder: "ct");
        string rs = study.AddStructureSet(ct);
        study.AddSeries("MR", SyntheticStudy.NewUid(), slices: 3, subfolder: "mr");
        ScanResult scan = DicomScanner.Scan(study.Root);
        // Planned in the order given: CT first, so its unit (CT + structure set) is the one in progress.
        UnlinkPlan plan = UnlinkPlanner.Create(scan.Series.OrderBy(s => s.Modality, StringComparer.Ordinal), Options());
        Assert.Equal(ct, plan.Series[0].Series.SeriesInstanceUid);

        // Cancel as soon as the first series (the CT) reports: the structure set is the next series in the plan.
        using var cts = new CancellationTokenSource();
        UnlinkResult result = UnlinkRunner.Run(plan, new SynchronousProgress<UnlinkProgress>(_ => cts.Cancel()), cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(2, result.SeriesChanged);
        ScanResult after = DicomScanner.Scan(study.Root);
        DicomSeriesInfo images = after.Series.Single(s => s.Modality == "CT");
        References refs = ReadReferences(FileOf(scan, rs));
        Assert.Equal([images.SeriesInstanceUid], refs.Series);
        Assert.Equal(images.SopInstanceUids.ToHashSet(), refs.ContourImages);
        // The MR, a unit of its own, was not started.
        Assert.Equal(scan.Series.Single(s => s.Modality == "MR").SeriesInstanceUid, after.Series.Single(s => s.Modality == "MR").SeriesInstanceUid);
    }

    [Fact]
    public void Stream_rewrites_agree_whichever_of_structure_set_and_images_goes_first()
    {
        // The browser edition rewrites file by file through the stream API, with no shared state between calls.
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 3, subfolder: "ct");
        string rs = study.AddStructureSet(ct);
        ScanResult scan = DicomScanner.Scan(study.Root);
        UnlinkPlan plan = UnlinkPlanner.Create(scan, Options());
        SeriesPlan structurePlan = plan.Series.Single(p => p.IsStructureSet), imagePlan = plan.Series.Single(p => !p.IsStructureSet);

        byte[] Rewrite(string path, SeriesPlan seriesPlan, out string? newSop)
        {
            using var output = new MemoryStream();
            using FileStream input = File.OpenRead(path);
            newSop = UnlinkRunner.Rewrite(input, output, seriesPlan);
            return output.ToArray();
        }

        byte[] structureBytes = Rewrite(structurePlan.Series.Files.Single(), structurePlan, out string? structureSop);
        var imageSops = new HashSet<string>();
        foreach (string path in imagePlan.Series.Files)
        {
            Rewrite(path, imagePlan, out string? sop);
            imageSops.Add(sop!);
        }

        Assert.Null(structureSop);
        string temp = Path.Combine(study.Root, "rewritten-structure-set.bin");
        File.WriteAllBytes(temp, structureBytes);
        Assert.Equal(imageSops, ReadReferences(temp).ContourImages);
        Assert.Equal([imagePlan.NewSeriesInstanceUid!], ReadReferences(temp).Series);
    }

    [Fact]
    public void Uid_report_shows_which_series_a_structure_set_followed()
    {
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2, subfolder: "ct");
        study.AddStructureSet(ct);
        UnlinkOptions options = Options();
        options.TagEdits.Add(new TagEdit("PatientID", "ANON,1"));
        UnlinkPlan plan = UnlinkPlanner.Create(DicomScanner.Scan(study.Root), options);

        string[] lines = UidReport.ToCsv(plan).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.EndsWith(",FollowsSeriesInstanceUIDs,TagEdits", lines[0]);
        Assert.StartsWith("RTSTRUCT,", lines[2]);
        Assert.EndsWith($",{ct},\"PatientID=ANON,1\"", lines[2]);
    }
}

public class TagEditTests
{
    private static UnlinkOptions TagsOnly(params TagEdit[] edits) => new()
    {
        NewFrameOfReferenceUid = false,
        NewSeriesInstanceUid = false,
        NewSopInstanceUid = false,
        Modalities = new HashSet<string>(["CT", "MR", "PT"], StringComparer.OrdinalIgnoreCase),
        KeepStructureSetsLinked = true,
        TagEdits = [.. edits],
    };

    [Fact]
    public void The_catalog_offers_ordinary_tags_and_withholds_the_ones_that_would_break_files()
    {
        IReadOnlyList<EditableTag> tags = DicomTagCatalog.Editable;

        Assert.Contains(tags, t => t is { Keyword: "PatientID", Tag: "(0010,0020)", Vr: "LO" });
        Assert.Contains(tags, t => t.Keyword == "PatientName");
        Assert.Contains(tags, t => t.Keyword == "StudyDescription");
        foreach (string managed in new[] { "SOPInstanceUID", "SeriesInstanceUID", "StudyInstanceUID", "FrameOfReferenceUID", "SOPClassUID" })
        {
            Assert.DoesNotContain(tags, t => t.Keyword == managed);
            Assert.NotNull(DicomTagCatalog.Validate(new TagEdit(managed, "1.2.3")));
        }
        Assert.DoesNotContain(tags, t => t.Vr is "SQ" or "OB" or "OW" or "UI");
        Assert.DoesNotContain(tags, t => t.Keyword is "PixelData" or "ReferencedFrameOfReferenceSequence" or "TransferSyntaxUID" or "SpecificCharacterSet");
        Assert.Equal(tags.Count, tags.Select(t => t.Keyword).Distinct().Count());
    }

    [Theory]
    [InlineData("patientid", "PatientID")]
    [InlineData("Patient ID", "PatientID")]
    [InlineData("0010,0020", "PatientID")]
    [InlineData("(0010,0020)", "PatientID")]
    [InlineData("00100010", "PatientName")]
    [InlineData("accession", "AccessionNumber")]
    public void Search_matches_keyword_name_and_tag_number(string text, string expected)
    {
        Assert.Contains(DicomTagCatalog.Search(text), t => t.Keyword == expected);
    }

    [Fact]
    public void Search_puts_keywords_that_start_with_the_text_first()
    {
        Assert.StartsWith("PatientID", DicomTagCatalog.Search("PatientID")[0].Keyword);
        Assert.Empty(DicomTagCatalog.Search("no such tag anywhere"));
    }

    [Theory]
    [InlineData("PatientID", "ANON001")]
    [InlineData("PatientID", "")]
    [InlineData("(0010,0020)", "ANON001")]
    [InlineData("PatientName", "DOE^JANE")]
    [InlineData("StudyDate", "20240131")]
    [InlineData("PatientBirthDate", "")]
    [InlineData("SeriesNumber", "12")]
    [InlineData("PatientWeight", "70.5")]
    [InlineData("ImageType", "DERIVED\\SECONDARY")]
    [InlineData("Rows", "512")]
    public void Valid_values_are_accepted(string tag, string value)
    {
        Assert.Null(DicomTagCatalog.Validate(new TagEdit(tag, value)));
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("NotATag", "x")]
    [InlineData("StudyDate", "31/01/2024")]
    [InlineData("StudyDate", "2024")]
    [InlineData("SeriesNumber", "twelve")]
    [InlineData("PatientWeight", "heavy")]
    [InlineData("Rows", "-1")]
    [InlineData("Rows", "many")]
    [InlineData("Modality", "lowercase")]
    [InlineData("PatientSex", "THIS IS FAR TOO LONG FOR A CODE STRING")]
    [InlineData("PatientID", "Zoë")]
    [InlineData("AccessionNumber", "MUCH-TOO-LONG-FOR-SIXTEEN-CHARACTERS")]
    public void Invalid_values_are_rejected_with_a_reason(string tag, string value)
    {
        Assert.False(string.IsNullOrWhiteSpace(DicomTagCatalog.Validate(new TagEdit(tag, value))));
    }

    [Fact]
    public void A_tag_may_only_be_edited_once()
    {
        IReadOnlyList<string?> errors = DicomTagCatalog.Validate(
            [new TagEdit("PatientID", "A"), new TagEdit("PatientName", "B"), new TagEdit("(0010,0020)", "C")]);

        Assert.Null(errors[0]);
        Assert.Null(errors[1]);
        Assert.Contains("more than once", errors[2]);
    }

    [Fact]
    public void An_invalid_edit_is_rejected_before_any_file_is_written()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2);
        ScanResult scan = DicomScanner.Scan(study.Root);
        Dictionary<string, byte[]> original = Directory.GetFiles(study.Root).ToDictionary(f => f, File.ReadAllBytes);
        UnlinkOptions options = TagsOnly(new TagEdit("PatientID", "FINE"), new TagEdit("StudyDate", "tomorrow"));
        options.NewSopInstanceUid = true;

        Assert.Throws<ArgumentException>(() => UnlinkPlanner.Create(scan, options));

        Assert.All(original, kv => Assert.Equal(kv.Value, File.ReadAllBytes(kv.Key)));
    }

    [Fact]
    public void A_run_that_only_edits_tags_changes_every_file_and_no_identifier()
    {
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 3, subfolder: "ct");
        study.AddSeries("MR", SyntheticStudy.NewUid(), slices: 2, subfolder: "mr");
        string rs = study.AddStructureSet(ct);
        string sr = study.AddSeries("SR", null, slices: 1, subfolder: "sr");
        ScanResult before = DicomScanner.Scan(study.Root);
        UnlinkOptions options = TagsOnly(
            new TagEdit("PatientID", "ANON001"),
            new TagEdit("PatientName", "ANON^PATIENT"),
            new TagEdit("AccessionNumber", "ACC1"),          // absent from the synthetic files: must be added
            new TagEdit("StudyDescription", ""));            // emptied
        Assert.True(options.ChangesAnything);

        UnlinkPlan plan = UnlinkPlanner.Create(before, options);
        UnlinkResult result = UnlinkRunner.Run(plan);

        Assert.Empty(result.Failures);
        Assert.Equal(3, result.SeriesChanged);   // CT, MR and the structure set that follows the CT
        Assert.Equal(6, result.FilesChanged);
        ScanResult after = DicomScanner.Scan(study.Root);
        foreach (DicomSeriesInfo series in after.Series.Where(s => s.SeriesInstanceUid != sr))
        {
            foreach (string file in series.Files)
            {
                DicomDataset ds = DicomFile.Open(file).Dataset;
                Assert.Equal("ANON001", ds.GetString(DicomTag.PatientID));
                Assert.Equal("ANON^PATIENT", ds.GetString(DicomTag.PatientName));
                Assert.Equal("ACC1", ds.GetString(DicomTag.AccessionNumber));
                Assert.True(ds.Contains(DicomTag.StudyDescription));
                Assert.Equal("", ds.GetSingleValueOrDefault(DicomTag.StudyDescription, ""));
            }
        }
        // Identifiers are untouched, and so is anything that was not selected.
        Assert.Equal(
            before.Series.Select(s => (s.SeriesInstanceUid, s.FrameOfReferenceUid, string.Join("|", s.SopInstanceUids.Order()))).Order(),
            after.Series.Select(s => (s.SeriesInstanceUid, s.FrameOfReferenceUid, string.Join("|", s.SopInstanceUids.Order()))).Order());
        Assert.Equal("SYN-001", DicomFile.Open(after.Series.Single(s => s.SeriesInstanceUid == sr).Files[0]).Dataset.GetString(DicomTag.PatientID));
        Assert.Equal([ct], DicomScanner.Scan(study.Root).Series.Single(s => s.SeriesInstanceUid == rs).ReferencedSeriesInstanceUids);
    }

    [Fact]
    public void Tag_edits_ride_along_with_new_uids_and_leave_the_pixels_alone()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 2);
        ScanResult scan = DicomScanner.Scan(study.Root);
        Dictionary<string, byte[]> pixels = scan.Series[0].Files.ToDictionary(
            f => f, f => DicomFile.Open(f, FileReadOption.ReadAll).Dataset.GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Data);
        UnlinkOptions options = TagsOnly(new TagEdit("PatientID", "ANON002"), new TagEdit("SeriesDescription", "Unlinked copy"));
        options.NewFrameOfReferenceUid = options.NewSopInstanceUid = true;

        UnlinkRunner.Run(UnlinkPlanner.Create(scan, options));

        DicomSeriesInfo after = DicomScanner.Scan(study.Root).Series.Single();
        Assert.Equal("ANON002", after.PatientId);
        Assert.Equal("Unlinked copy", after.SeriesDescription);
        Assert.NotEqual(scan.Series[0].FrameOfReferenceUid, after.FrameOfReferenceUid);
        Assert.Empty(after.SopInstanceUids.Intersect(scan.Series[0].SopInstanceUids));
        Assert.All(pixels, kv => Assert.Equal(kv.Value,
            DicomFile.Open(kv.Key, FileReadOption.ReadAll).Dataset.GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Data));
    }

    [Fact]
    public void Nothing_is_planned_for_tag_edits_when_no_series_is_selected()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 1);
        UnlinkOptions options = TagsOnly(new TagEdit("PatientID", "X"));
        options.Modalities.Clear();

        Assert.Empty(UnlinkPlanner.Create(DicomScanner.Scan(study.Root), options).Series);
    }

    [Fact]
    public void Current_values_are_read_per_series_and_flag_values_that_differ_between_images()
    {
        using var study = new SyntheticStudy();
        string ct = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 3);
        ScanResult scan = DicomScanner.Scan(study.Root);

        IReadOnlyList<CurrentTagValue> values = TagValueReader.ReadForScan(scan, ["PatientID", "InstanceNumber", "AccessionNumber", "NotATag"])[ct];

        Assert.Equal(new CurrentTagValue("SYN-001", false), values[0]);
        Assert.True(values[1].Varies);
        Assert.Equal(new CurrentTagValue(null, false), values[2]);
        Assert.Equal(new CurrentTagValue(null, false), values[3]);
    }
}
