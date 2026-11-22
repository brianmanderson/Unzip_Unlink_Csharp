using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using UnzipUnlink.Core;
using UnzipUnlink.Web;

namespace UnzipUnlink.Tests;

/// <summary>Hosts the web app in memory and drives it the way the browser does.</summary>
public sealed class WebApiTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "UnzipUnlinkTests", "web-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    private HttpClient CreateClient(string localFolders = "LoopbackOnly", bool withHeader = true)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("UnzipUnlink:WorkspaceRoot", _workspace);
            builder.UseSetting("UnzipUnlink:OpenBrowser", "false");
            builder.UseSetting("UnzipUnlink:AutoPort", "false");   // the in-memory host never binds a port
            builder.UseSetting("UnzipUnlink:LocalFolders", localFolders);
        });
        _disposables.Add(factory);
        HttpClient client = factory.CreateClient();
        if (withHeader)
        {
            client.DefaultRequestHeaders.Add("X-UnzipUnlink", "1");
        }
        return client;
    }

    private static object AllChanges(params string[] modalities) => new
    {
        frameOfReference = true,
        seriesUid = true,
        sopUid = true,
        modalities,
        keepCtLinked = false,
        keepPetCtLinked = false,
        seriesUids = (string[]?)null,
    };

    private static async Task<JsonElement> WaitFor(HttpClient client, string id, params string[] states)
    {
        for (int i = 0; i < 300; i++)
        {
            JsonElement job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}");
            string state = job.GetProperty("state").GetString()!;
            if (states.Contains(state))
            {
                return job;
            }
            Assert.True(state != "failed", $"Job failed: {job.GetProperty("error")}");
            await Task.Delay(50);
        }
        throw new TimeoutException("Job did not reach " + string.Join("/", states));
    }

    [Fact]
    public async Task Uploaded_zip_is_unlinked_and_downloaded()
    {
        using var study = new SyntheticStudy();
        string frame = SyntheticStudy.NewUid();
        string ctUid = study.AddSeries("CT", frame, slices: 3, subfolder: "src/ct");
        string mrUid = study.AddSeries("MR", frame, slices: 2, subfolder: "src/mr");
        string zip = Path.Combine(study.Root, "export.zip");
        ZipFile.CreateFromDirectory(Path.Combine(study.Root, "src"), zip);
        HttpClient client = CreateClient();

        JsonElement created = await (await client.PostAsJsonAsync("/api/jobs/upload", new { name = "My Study" })).Content.ReadFromJsonAsync<JsonElement>();
        string id = created.GetProperty("id").GetString()!;
        using (var body = new StreamContent(File.OpenRead(zip)))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"/api/jobs/{id}/files?path=export.zip", body)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/jobs/{id}/prepare", null)).StatusCode);
        JsonElement ready = await WaitFor(client, id, "ready");
        Assert.Equal(5, ready.GetProperty("scan").GetProperty("fileCount").GetInt32());
        Assert.Equal(2, ready.GetProperty("scan").GetProperty("series").GetArrayLength());

        // The preview says both series share a frame now and only the CT will change.
        JsonElement preview = await (await client.PostAsJsonAsync($"/api/jobs/{id}/preview", AllChanges("CT"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, preview.GetProperty("seriesCount").GetInt32());
        Assert.True(preview.GetProperty("series").GetProperty(ctUid).GetProperty("selected").GetBoolean());
        Assert.False(preview.GetProperty("series").GetProperty(mrUid).GetProperty("selected").GetBoolean());
        Assert.NotEqual(
            preview.GetProperty("series").GetProperty(ctUid).GetProperty("frame").GetInt32(),
            preview.GetProperty("series").GetProperty(mrUid).GetProperty("frame").GetInt32());

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/jobs/{id}/run", AllChanges("CT"))).StatusCode);
        JsonElement done = await WaitFor(client, id, "done");
        Assert.Equal(3, done.GetProperty("result").GetProperty("filesChanged").GetInt32());
        Assert.Equal(0, done.GetProperty("result").GetProperty("failureCount").GetInt32());

        // Download, unpack and check the result with the scanner.
        string downloaded = Path.Combine(study.Root, "result.zip");
        HttpResponseMessage download = await client.GetAsync($"/api/jobs/{id}/download");
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        Assert.Contains("My Study_unlinked.zip", download.Content.Headers.ContentDisposition?.ToString());
        await File.WriteAllBytesAsync(downloaded, await download.Content.ReadAsByteArrayAsync());
        string unpacked = Path.Combine(study.Root, "result");
        ZipTools.Extract(downloaded, unpacked);
        ScanResult after = DicomScanner.Scan(unpacked);
        DicomSeriesInfo ct = after.Series.Single(s => s.Modality == "CT"), mr = after.Series.Single(s => s.Modality == "MR");
        Assert.NotEqual(ctUid, ct.SeriesInstanceUid);
        Assert.NotEqual(frame, ct.FrameOfReferenceUid);
        Assert.Equal(mrUid, mr.SeriesInstanceUid);
        Assert.Equal(frame, mr.FrameOfReferenceUid);

        string report = await client.GetStringAsync($"/api/jobs/{id}/report.csv");
        Assert.Contains(ctUid, report);
        Assert.Contains(ct.SeriesInstanceUid, report);
        Assert.DoesNotContain("SYNTHETIC", report);   // no patient identifiers in the report

        // Deleting the job removes the uploaded data from disk.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/jobs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/jobs/{id}")).StatusCode);
        string[] JobFolders() => Directory.GetDirectories(_workspace, id, SearchOption.AllDirectories);
        for (int i = 0; i < 100 && JobFolders().Length > 0; i++)
        {
            await Task.Delay(50);
        }
        Assert.Empty(JobFolders());
    }

    [Fact]
    public async Task A_second_copy_of_the_app_does_not_disturb_the_first_ones_uploads()
    {
        HttpClient first = CreateClient();
        JsonElement created = await (await first.PostAsJsonAsync("/api/jobs/upload", new { })).Content.ReadFromJsonAsync<JsonElement>();
        string id = created.GetProperty("id").GetString()!;
        await first.PutAsync($"/api/jobs/{id}/files?path=a.bin", new ByteArrayContent([1, 2, 3]));
        string uploaded = Assert.Single(Directory.GetFiles(_workspace, "a.bin", SearchOption.AllDirectories));

        // Starting another instance on the same workspace root must only clean up abandoned folders.
        HttpClient second = CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/info")).StatusCode);

        Assert.True(File.Exists(uploaded));
        Assert.Equal(2, Directory.GetDirectories(_workspace, "run-*").Length);
    }

    [Fact]
    public async Task Stopping_the_app_deletes_everything_that_was_uploaded()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("UnzipUnlink:WorkspaceRoot", _workspace);
            builder.UseSetting("UnzipUnlink:OpenBrowser", "false");
            builder.UseSetting("UnzipUnlink:AutoPort", "false");   // the in-memory host never binds a port
        });
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-UnzipUnlink", "1");
        JsonElement created = await (await client.PostAsJsonAsync("/api/jobs/upload", new { })).Content.ReadFromJsonAsync<JsonElement>();
        await client.PutAsync($"/api/jobs/{created.GetProperty("id").GetString()}/files?path=a.bin", new ByteArrayContent([1, 2, 3]));
        Assert.Single(Directory.GetFiles(_workspace, "a.bin", SearchOption.AllDirectories));

        await factory.DisposeAsync();

        Assert.Empty(Directory.GetFiles(_workspace, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(_workspace, "run-*"));
    }

    [Fact]
    public async Task Workspaces_abandoned_by_a_crashed_run_are_removed_at_startup()
    {
        string abandoned = Path.Combine(_workspace, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(abandoned, "job", "content"));
        await File.WriteAllTextAsync(Path.Combine(abandoned, ".in-use"), "");
        await File.WriteAllTextAsync(Path.Combine(abandoned, "job", "content", "left-behind.dcm"), "x");

        HttpClient client = CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/info")).StatusCode);

        Assert.False(Directory.Exists(abandoned));
    }

    [Fact]
    public async Task State_changing_calls_need_the_custom_header()
    {
        HttpClient client = CreateClient(withHeader: false);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/jobs/upload", new { name = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/info")).StatusCode);
    }

    [Theory]
    [InlineData("../../escape.dcm")]
    [InlineData("..\\escape.dcm")]
    [InlineData("C:\\Windows\\escape.dcm")]
    [InlineData("a/../../escape.dcm")]
    [InlineData("")]
    public async Task Upload_paths_cannot_leave_the_job_workspace(string path)
    {
        HttpClient client = CreateClient();
        JsonElement created = await (await client.PostAsJsonAsync("/api/jobs/upload", new { })).Content.ReadFromJsonAsync<JsonElement>();
        string id = created.GetProperty("id").GetString()!;

        HttpResponseMessage response = await client.PutAsync($"/api/jobs/{id}/files?path={Uri.EscapeDataString(path)}", new ByteArrayContent([1, 2, 3]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Directory.GetFiles(_workspace, "escape.dcm", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_workspace)!, "escape.dcm")));
    }

    [Fact]
    public async Task Server_folders_are_refused_to_callers_that_are_not_on_the_server()
    {
        // The in-memory test client has no loopback address, exactly like a remote browser.
        HttpClient client = CreateClient("LoopbackOnly");

        JsonElement info = await client.GetFromJsonAsync<JsonElement>("/api/info");
        Assert.False(info.GetProperty("localFolders").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/fs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/jobs/local", new { path = Path.GetTempPath() })).StatusCode);
    }

    [Fact]
    public async Task Local_folder_can_be_unlinked_into_a_separate_copy()
    {
        using var study = new SyntheticStudy();
        string frame = SyntheticStudy.NewUid();
        string source = Path.Combine(study.Root, "Export");
        string mrUid = study.AddSeries("MR", frame, slices: 3, subfolder: "Export/mr");
        HttpClient client = CreateClient("Always");

        JsonElement created = await (await client.PostAsJsonAsync("/api/jobs/local", new { path = source })).Content.ReadFromJsonAsync<JsonElement>();
        string id = created.GetProperty("id").GetString()!;
        await WaitFor(client, id, "ready");
        var request = new { frameOfReference = true, seriesUid = true, sopUid = true, modalities = new[] { "MR" }, keepCtLinked = false, keepPetCtLinked = false, seriesUids = (string[]?)null, workOnCopy = true };
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/jobs/{id}/run", request)).StatusCode);
        JsonElement done = await WaitFor(client, id, "done");

        string output = done.GetProperty("result").GetProperty("outputFolder").GetString()!;
        Assert.Equal(source + "_unlinked", output);
        Assert.Equal(mrUid, DicomScanner.Scan(source).Series.Single().SeriesInstanceUid);          // originals untouched
        DicomSeriesInfo copy = DicomScanner.Scan(output).Series.Single();
        Assert.NotEqual(mrUid, copy.SeriesInstanceUid);
        Assert.NotEqual(frame, copy.FrameOfReferenceUid);
        // Local jobs have nothing to download and deleting the job leaves the folders alone.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/jobs/{id}/download")).StatusCode);
        await client.DeleteAsync($"/api/jobs/{id}");
        Assert.True(Directory.Exists(source) && Directory.Exists(output));
    }

    [Fact]
    public async Task Run_is_refused_when_nothing_is_selected()
    {
        using var study = new SyntheticStudy();
        study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 1, subfolder: "Export");
        HttpClient client = CreateClient("Always");
        JsonElement created = await (await client.PostAsJsonAsync("/api/jobs/local", new { path = Path.Combine(study.Root, "Export") })).Content.ReadFromJsonAsync<JsonElement>();
        string id = created.GetProperty("id").GetString()!;
        await WaitFor(client, id, "ready");

        HttpResponseMessage response = await client.PostAsJsonAsync($"/api/jobs/{id}/run", AllChanges("MR"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Tag_list_offers_ordinary_tags_and_no_identifiers()
    {
        HttpClient client = CreateClient();

        JsonElement tags = await client.GetFromJsonAsync<JsonElement>("/api/tags");

        List<JsonElement> list = tags.EnumerateArray().ToList();
        JsonElement patientId = list.Single(t => t.GetProperty("keyword").GetString() == "PatientID");
        Assert.Equal("(0010,0020)", patientId.GetProperty("tag").GetString());
        Assert.Equal("Patient ID", patientId.GetProperty("name").GetString());
        Assert.DoesNotContain(list, t => t.GetProperty("vr").GetString() is "UI" or "SQ");
    }

    [Fact]
    public async Task Structure_set_follows_its_images_and_tag_edits_are_checked_then_applied()
    {
        using var study = new SyntheticStudy();
        string ctUid = study.AddSeries("CT", SyntheticStudy.NewUid(), slices: 3, subfolder: "Export/ct");
        string rsUid = study.AddStructureSet(ctUid, subfolder: "Export/rs");
        string folder = Path.Combine(study.Root, "Export");
        HttpClient client = CreateClient("Always");
        JsonElement created = await (await client.PostAsJsonAsync("/api/jobs/local", new { path = folder })).Content.ReadFromJsonAsync<JsonElement>();
        string id = created.GetProperty("id").GetString()!;
        JsonElement ready = await WaitFor(client, id, "ready");

        // The scan says which images the structure set was drawn on.
        JsonElement rsRow = ready.GetProperty("scan").GetProperty("series").EnumerateArray().Single(s => s.GetProperty("uid").GetString() == rsUid);
        Assert.True(rsRow.GetProperty("structureSet").GetBoolean());
        Assert.Equal(ctUid, rsRow.GetProperty("follows").EnumerateArray().Single().GetString());

        object Request(bool keep, params (string Tag, string Value)[] edits) => new
        {
            frameOfReference = true,
            seriesUid = true,
            sopUid = true,
            modalities = new[] { "CT" },
            keepCtLinked = false,
            keepPetCtLinked = false,
            seriesUids = (string[]?)null,
            keepStructureSetsLinked = keep,
            tagEdits = edits.Select(e => new { tag = e.Tag, value = e.Value }),
        };
        async Task<JsonElement> Preview(object request) =>
            await (await client.PostAsJsonAsync($"/api/jobs/{id}/preview", request)).Content.ReadFromJsonAsync<JsonElement>();

        // Off: only the CT is planned and the preview warns that the structure set is left behind.
        JsonElement off = await Preview(Request(keep: false));
        Assert.Equal(1, off.GetProperty("seriesCount").GetInt32());
        Assert.Equal("leftBehind", off.GetProperty("series").GetProperty(rsUid).GetProperty("structureSet").GetString());

        // On, with one bad value: the error comes back against that edit and the run is refused.
        object bad = Request(keep: true, ("PatientID", "ANON001"), ("StudyDate", "tomorrow"));
        JsonElement invalid = await Preview(bad);
        Assert.Equal(JsonValueKind.Null, invalid.GetProperty("tagErrors")[0].ValueKind);
        Assert.Contains("StudyDate", invalid.GetProperty("tagErrors")[1].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/jobs/{id}/run", bad)).StatusCode);

        object good = Request(keep: true, ("PatientID", "ANON001"), ("StudyDate", "20300101"));
        JsonElement preview = await Preview(good);
        Assert.Equal(2, preview.GetProperty("seriesCount").GetInt32());
        Assert.Equal(4, preview.GetProperty("fileCount").GetInt32());
        Assert.Equal("follows", preview.GetProperty("series").GetProperty(rsUid).GetProperty("structureSet").GetString());
        Assert.Equal(
            preview.GetProperty("series").GetProperty(ctUid).GetProperty("frame").GetInt32(),
            preview.GetProperty("series").GetProperty(rsUid).GetProperty("frame").GetInt32());

        // What the tags hold today, for the "old -> new" preview.
        JsonElement values = await (await client.PostAsJsonAsync($"/api/jobs/{id}/tag-values", new { tags = new[] { "PatientID", "AccessionNumber" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SYN-001", values.GetProperty(ctUid)[0].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, values.GetProperty(ctUid)[1].GetProperty("value").ValueKind);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/jobs/{id}/run", good)).StatusCode);
        JsonElement done = await WaitFor(client, id, "done");
        Assert.Equal(4, done.GetProperty("result").GetProperty("filesChanged").GetInt32());
        Assert.Equal(0, done.GetProperty("result").GetProperty("failureCount").GetInt32());

        ScanResult after = DicomScanner.Scan(folder);
        DicomSeriesInfo ct = after.Series.Single(s => s.Modality == "CT"), rs = after.Series.Single(s => s.Modality == "RTSTRUCT");
        Assert.NotEqual(ctUid, ct.SeriesInstanceUid);
        Assert.Equal(rsUid, rs.SeriesInstanceUid);
        Assert.Equal([ct.SeriesInstanceUid], rs.ReferencedSeriesInstanceUids);
        Assert.Equal([ct.FrameOfReferenceUid], rs.ReferencedFrameOfReferenceUids);
        Assert.All(after.Series, s =>
        {
            Assert.Equal("ANON001", s.PatientId);
            Assert.Equal("20300101", s.StudyDate);
        });

        string[] report = (await client.GetStringAsync($"/api/jobs/{id}/report.csv")).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(UidReport.Header, report[0]);
        Assert.Equal(3, report.Length);
        Assert.Contains(report, line => line.StartsWith("RTSTRUCT,") && line.Contains(ctUid) && line.EndsWith("PatientID=ANON001; StudyDate=20300101"));
        Assert.DoesNotContain("SYN-001", string.Join('\n', report));   // old patient identifiers never reach the report
    }

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}
