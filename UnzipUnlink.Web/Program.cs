using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using UnzipUnlink.Core;
using UnzipUnlink.Web;

// A published copy keeps wwwroot beside the exe; serve it from there no matter which folder the app is
// started from (a shortcut's "Start in", a service, a scheduled task...).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : null,
});
builder.Services.Configure<WebOptions>(builder.Configuration.GetSection("UnzipUnlink"));
builder.Services.AddSingleton<JobManager>();
builder.Services.AddHostedService(services => services.GetRequiredService<JobManager>());
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

// --- Where to listen -------------------------------------------------------------------------------
// On a PC the default port may already belong to something else, or to a copy of this app that is already
// open. Sort that out before starting rather than dying with a socket error.
PortChoice listen = PortPicker.Resolve(builder.Configuration);
if (listen.AlreadyRunning)
{
    Console.WriteLine($"Unzip & Unlink is already running at {listen.Url} - opening that copy instead of starting another.");
    if (builder.Configuration.GetValue("UnzipUnlink:OpenBrowser", true) && Environment.UserInteractive)
    {
        try { Process.Start(new ProcessStartInfo(listen.Url) { UseShellExecute = true }); } catch { /* the URL is printed above */ }
    }
    return;
}
if (listen.Note is not null)
{
    Console.WriteLine(listen.Note);
}
builder.WebHost.UseUrls(listen.Url);

var app = builder.Build();
WebOptions options = app.Services.GetRequiredService<IOptions<WebOptions>>().Value;
string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.0.0";

// --- Hardening -------------------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    IHeaderDictionary headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        headers.CacheControl = "no-store";
        // Every state-changing call must carry a custom header. Browsers refuse to let another web site
        // send one without a CORS pre-flight (which we never approve), so a page the user happens to have
        // open elsewhere cannot drive this app behind their back.
        if (!HttpMethods.IsGet(context.Request.Method) && context.Request.Headers["X-UnzipUnlink"] != "1")
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Missing X-UnzipUnlink header." });
            return;
        }
    }
    else
    {
        // Always revalidate the page and its scripts so an upgraded server is never driven by a stale UI.
        headers.CacheControl = "no-cache";
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// --- API -------------------------------------------------------------------------------------------
RouteGroupBuilder api = app.MapGroup("/api");

api.MapGet("/info", (HttpContext context) => Results.Json(new
{
    version,
    machine = Environment.MachineName,
    localFolders = Access.AllowsLocalFolders(context, options),
    // The page words itself differently when the browser and the app share a computer: nothing leaves it then.
    sameMachine = Access.IsSameMachine(context),
    retentionMinutes = options.RetentionMinutes,
    maxUploadBytes = options.MaxUploadBytes,
    supportedModalities = UnlinkOptions.SupportedModalities.OrderBy(m => m),
}));

// The DICOM tags that can be overwritten with a typed value; the page filters this list as the user types.
api.MapGet("/tags", () => Results.Json(DicomTagCatalog.Editable.Select(t => new { keyword = t.Keyword, name = t.Name, tag = t.Tag, vr = t.Vr })));

// Folder picker for "a folder on this machine".
api.MapGet("/fs", (HttpContext context, string? path) =>
{
    if (!Access.AllowsLocalFolders(context, options))
    {
        return Api.Error(StatusCodes.Status403Forbidden, "Browsing folders on the server is only available from the machine it runs on.");
    }
    return FolderBrowser.List(path);
});

api.MapPost("/jobs/upload", (JobManager jobs, CreateUploadRequest? request) =>
{
    Job job = jobs.CreateUpload(request?.Name);
    return Results.Json(new { id = job.Id });
});

api.MapPut("/jobs/{id}/files", async (HttpContext context, JobManager jobs, string id, string path) =>
{
    if (jobs.Find(id) is not { IsUpload: true } job)
    {
        return Api.NotFound();
    }
    if (job.State != JobState.Created)
    {
        return Api.Error(StatusCodes.Status409Conflict, "This job is no longer accepting files.");
    }
    string? target = Api.SafeCombine(job.ContentRoot, path);
    if (target is null)
    {
        return Api.Error(StatusCodes.Status400BadRequest, "Invalid file path.");
    }
    long remaining = options.MaxUploadBytes - Interlocked.Read(ref job.UploadedBytes);
    if (remaining <= 0 || context.Request.ContentLength > remaining)
    {
        return Api.Error(StatusCodes.Status413PayloadTooLarge, $"This upload is larger than the {options.MaxUploadMegabytes:N0} MB limit set on the server.");
    }
    // Lift Kestrel's default 30 MB body cap for this request only, down to what the job has left.
    if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
    {
        sizeLimit.MaxRequestBodySize = remaining;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    try
    {
        await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            await context.Request.Body.CopyToAsync(file, context.RequestAborted);
        }
    }
    catch (Exception ex) when (ex is BadHttpRequestException or OperationCanceledException or IOException)
    {
        try { File.Delete(target); } catch { /* best effort */ }
        return ex is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }
            ? Api.Error(StatusCodes.Status413PayloadTooLarge, $"This upload is larger than the {options.MaxUploadMegabytes:N0} MB limit set on the server.")
            : Api.Error(StatusCodes.Status400BadRequest, "The upload was interrupted.");
    }
    Interlocked.Add(ref job.UploadedBytes, new FileInfo(target).Length);
    Interlocked.Increment(ref job.UploadedFiles);
    job.Touch();
    return Results.NoContent();
});

api.MapPost("/jobs/{id}/prepare", (JobManager jobs, string id) =>
{
    if (jobs.Find(id) is not { } job)
    {
        return Api.NotFound();
    }
    return jobs.StartPrepare(job) ? Results.Accepted() : Api.Busy();
});

api.MapPost("/jobs/local", (HttpContext context, JobManager jobs, CreateLocalRequest request) =>
{
    if (!Access.AllowsLocalFolders(context, options))
    {
        return Api.Error(StatusCodes.Status403Forbidden, "Working on a folder on the server is only available from the machine it runs on.");
    }
    if (string.IsNullOrWhiteSpace(request.Path) || !Path.IsPathFullyQualified(request.Path.Trim().Trim('"')))
    {
        return Api.Error(StatusCodes.Status400BadRequest, "Enter the full path of a folder or .zip, for example C:\\Exports\\Study.");
    }
    string path = request.Path.Trim().Trim('"');
    if (!Directory.Exists(path) && !(File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
    {
        return Api.Error(StatusCodes.Status400BadRequest, "That folder or .zip does not exist on this machine.");
    }
    Job job = jobs.CreateLocal(path);
    jobs.StartPrepare(job);
    return Results.Json(new { id = job.Id });
});

api.MapGet("/jobs/{id}", (JobManager jobs, string id) =>
    jobs.Find(id) is { } job ? Results.Json(JobView.From(job)) : Api.NotFound());

// "What would happen" - cheap enough to call on every option change.
api.MapPost("/jobs/{id}/preview", (JobManager jobs, string id, ChangeRequest request) =>
{
    if (jobs.Find(id) is not { Scan: { } scan })
    {
        return Api.NotFound();
    }
    return Results.Json(JobView.Preview(scan, request));
});

// What the given tags hold today in each series (first and last file), so the page can show "old -> new".
api.MapPost("/jobs/{id}/tag-values", (JobManager jobs, string id, TagValuesRequest request) =>
{
    if (jobs.Find(id) is not { Scan: { } scan })
    {
        return Api.NotFound();
    }
    string[] tags = (request.Tags ?? []).Take(50).ToArray();
    return Results.Json(TagValueReader.ReadForScan(scan, tags).ToDictionary(
        kv => kv.Key,
        kv => kv.Value.Select(v => new { value = v.Value, varies = v.Varies })));
});

api.MapPost("/jobs/{id}/run", (JobManager jobs, string id, ChangeRequest request) =>
{
    if (jobs.Find(id) is not { } job)
    {
        return Api.NotFound();
    }
    if (job.Scan is null)
    {
        return Api.Error(StatusCodes.Status409Conflict, "This job has not been scanned yet.");
    }
    if (request.TagErrors().FirstOrDefault(e => e is not null) is { } tagError)
    {
        return Api.Error(StatusCodes.Status400BadRequest, tagError);
    }
    if (UnlinkPlanner.Create(job.Scan, request.ToOptions()).Series.Count == 0)
    {
        return Api.Error(StatusCodes.Status400BadRequest, "Nothing is selected to change.");
    }
    return jobs.StartRun(job, request) ? Results.Accepted() : Api.Busy();
});

api.MapPost("/jobs/{id}/cancel", (JobManager jobs, string id) =>
{
    if (jobs.Find(id) is not { } job)
    {
        return Api.NotFound();
    }
    job.Cancellation?.Cancel();
    return Results.Accepted();
});

api.MapDelete("/jobs/{id}", (JobManager jobs, string id) =>
{
    if (jobs.Find(id) is { } job)
    {
        jobs.Remove(job);
    }
    return Results.NoContent();
});

// The rewritten study as a .zip (upload jobs).
api.MapGet("/jobs/{id}/download", async (HttpContext context, JobManager jobs, string id) =>
{
    if (jobs.Find(id) is not { IsUpload: true } job)
    {
        return Api.NotFound();
    }
    if (job.State is JobState.Preparing or JobState.Running)
    {
        return Api.Busy();
    }
    // ZipArchive finishes with small synchronous writes; allow them for this response only.
    if (context.Features.Get<IHttpBodyControlFeature>() is { } bodyControl)
    {
        bodyControl.AllowSynchronousIO = true;
    }
    context.Response.ContentType = "application/zip";
    context.Response.Headers.ContentDisposition = $"attachment; filename=\"{job.Name}_unlinked.zip\"";
    // The zip writer emits thousands of small chunks and Kestrel flushes to the network on every write, so
    // without a large buffer in between a 300 MB study takes minutes instead of seconds.
    await using (var buffered = new BufferedStream(context.Response.Body, 4 << 20))
    {
        await ZipTools.WriteZipAsync(job.ContentRoot, buffered, context.RequestAborted);
        await buffered.FlushAsync(context.RequestAborted);
    }
    job.Touch();
    return Results.Empty;
});

// Old UID -> new UID for every rewritten series, plus the tag values the user typed. No patient details are copied from the files.
api.MapGet("/jobs/{id}/report.csv", (JobManager jobs, string id) =>
{
    if (jobs.Find(id) is not { ExecutedPlan: { } plan } job)
    {
        return Api.NotFound();
    }
    return Results.File(Encoding.UTF8.GetBytes(UidReport.ToCsv(plan)), "text/csv", $"{job.Name}_uid_report.csv");
});

// --- Start -----------------------------------------------------------------------------------------
string Browsable(string url) =>
    url.Replace("://0.0.0.0", "://localhost").Replace("://[::]", "://localhost").Replace("://+", "://localhost").Replace("://*", "://localhost");

void OpenBrowser(string url)
{
    if (!options.OpenBrowser || !Environment.UserInteractive)
    {
        return;
    }
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not open a browser automatically ({ex.Message}). Open {url} yourself.");
    }
}

app.Lifetime.ApplicationStarted.Register(() =>
{
    string url = Browsable(app.Urls.FirstOrDefault() ?? "http://localhost:5080");
    app.Logger.LogInformation("Unzip & Unlink {Version} is running at {Url} (uploads are held in {Workspace}). Press Ctrl+C to stop.",
        version, url, app.Services.GetRequiredService<JobManager>().WorkspaceRoot);
    OpenBrowser(url);
});

try
{
    app.Run();
}
catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
{
    // Only reached when automatic port selection is off or could not help (for example a fixed server
    // address). The host and its logger are already torn down, so write to the console directly.
    Console.Error.WriteLine($"{listen.Url} is already in use by another program, so Unzip & Unlink could not start.");
    Console.Error.WriteLine("Start it on a free port with --urls http://localhost:<port>.");
    Environment.ExitCode = 1;
}

// ---------------------------------------------------------------------------------------------------

public sealed record CreateUploadRequest(string? Name);
public sealed record CreateLocalRequest(string Path);
public sealed record TagValuesRequest(string[]? Tags);

/// <summary>The address the app will listen on, and whether a copy of it is already there.</summary>
internal sealed record PortChoice(string Url, bool AlreadyRunning, string? Note);

/// <summary>
/// For a plain <c>http://localhost:port</c> address: if the port is taken by a running copy of this app, say
/// so; if it is taken by something else, move to the next free port. Any other address (several URLs, HTTPS,
/// a network interface) is an administrator's deliberate choice and is used exactly as configured.
/// </summary>
internal static class PortPicker
{
    private const int PortsToTry = 20;

    public static PortChoice Resolve(IConfiguration configuration)
    {
        string configured = (configuration["Urls"] ?? "http://localhost:5080").Trim();
        if (!configuration.GetValue("UnzipUnlink:AutoPort", true)
            || configured.Contains(';')
            || !Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || uri.Host is not ("localhost" or "127.0.0.1"))
        {
            return new PortChoice(configured, false, null);
        }
        for (int port = uri.Port; port < uri.Port + PortsToTry && port <= IPEndPoint.MaxPort; port++)
        {
            string url = $"http://{uri.Host}:{port}";
            if (IsFree(port))
            {
                string? note = port == uri.Port ? null
                    : $"Port {uri.Port} is being used by another program, so Unzip & Unlink is using {url} instead.";
                return new PortChoice(url, false, note);
            }
            if (IsThisApp(url))
            {
                return new PortChoice(url, true, null);
            }
        }
        return new PortChoice(configured, false, null);
    }

    private static bool IsFree(int port)
    {
        // "localhost" binds both loopback families, so both must be free.
        foreach (IPAddress address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(address, port);
                listener.Start();
                listener.Stop();
            }
            catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressFamilyNotSupported)
            {
                // No IPv6 on this machine: nothing to collide with.
            }
            catch (System.Net.Sockets.SocketException)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsThisApp(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using HttpResponseMessage response = client.GetAsync(url + "/api/info").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode
                && response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"supportedModalities\"", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Decides who may point the server at its own disks.</summary>
internal static class Access
{
    public static bool AllowsLocalFolders(HttpContext context, WebOptions options) => options.LocalFolders.ToLowerInvariant() switch
    {
        "always" => true,
        "never" => false,
        _ => IsSameMachine(context),
    };

    /// <summary>
    /// True only for a browser on the server itself: loopback address, a localhost Host header (which defeats
    /// DNS rebinding) and no sign of a reverse proxy relaying somebody else's request.
    /// </summary>
    public static bool IsSameMachine(HttpContext context)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            return false;
        }
        if (context.Request.Headers.ContainsKey("X-Forwarded-For") || context.Request.Headers.ContainsKey("Forwarded"))
        {
            return false;
        }
        string host = context.Request.Host.Host;
        return host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }
}

internal static class Api
{
    public static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
    public static IResult NotFound() => Error(StatusCodes.Status404NotFound, "This job no longer exists on the server. It may have expired - please start again.");
    public static IResult Busy() => Error(StatusCodes.Status409Conflict, "This job is busy. Wait for the current step to finish.");

    /// <summary>Resolves an uploaded file's relative path inside <paramref name="root"/>, or null if it tries to leave it.</summary>
    public static string? SafeCombine(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }
        string[] segments = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        char[] invalid = Path.GetInvalidFileNameChars();
        if (segments.Length == 0 || segments.Any(s => s is "." or ".." || s.IndexOfAny(invalid) >= 0 || s.Contains(':')))
        {
            return null;
        }
        string rootFull = Path.GetFullPath(root);
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(rootFull, Path.Combine(segments)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public static string Csv(string value)
    {
        // Neutralise spreadsheet formula injection as well as quoting.
        if (value.Length > 0 && "=+-@".Contains(value[0]))
        {
            value = "'" + value;
        }
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}

/// <summary>Lists drives and sub-folders for the in-browser folder picker.</summary>
internal static class FolderBrowser
{
    public static IResult List(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var roots = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new { name = DriveLabel(d), path = d.RootDirectory.FullName, kind = "drive" })
                .ToList();
            var shortcuts = new[]
                {
                    ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                    ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                    ("Downloads", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
                }
                .Where(s => s.Item2.Length > 0 && Directory.Exists(s.Item2))
                .Select(s => new { name = s.Item1, path = s.Item2, kind = "shortcut" });
            return Results.Json(new { path = "", parent = (string?)null, entries = shortcuts.Concat(roots) });
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return Api.Error(StatusCodes.Status400BadRequest, "That is not a valid path.");
        }
        if (!Directory.Exists(full))
        {
            return Api.Error(StatusCodes.Status404NotFound, "That folder does not exist.");
        }
        try
        {
            var info = new DirectoryInfo(full);
            var folders = info.EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new { name = d.Name, path = d.FullName, kind = "folder" });
            var zips = info.EnumerateFiles("*.zip")
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new { name = f.Name, path = f.FullName, kind = "zip" });
            return Results.Json(new { path = full, parent = info.Parent?.FullName ?? "", entries = folders.Concat(zips).ToList() });
        }
        catch (UnauthorizedAccessException)
        {
            return Api.Error(StatusCodes.Status403Forbidden, "Windows did not allow access to that folder.");
        }
        catch (IOException ex)
        {
            return Api.Error(StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static string DriveLabel(DriveInfo drive)
    {
        string root = drive.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            return string.IsNullOrWhiteSpace(drive.VolumeLabel) ? root : $"{root} ({drive.VolumeLabel})";
        }
        catch
        {
            return root;
        }
    }
}

/// <summary>Shapes jobs for the browser. Deliberately small: no file paths of individual images are sent.</summary>
internal static class JobView
{
    public static object From(Job job) => new
    {
        id = job.Id,
        kind = job.IsUpload ? "upload" : "local",
        name = job.Name,
        state = job.State,
        phase = job.Phase,
        progress = new { done = job.ProgressDone, total = job.ProgressTotal },
        error = job.Error,
        notes = job.Notes,
        location = job.IsUpload ? null : job.ContentRoot,
        uploaded = new { files = job.UploadedFiles, bytes = Interlocked.Read(ref job.UploadedBytes) },
        scan = job.Scan is { } scan ? ScanView(scan) : null,
        result = job.Result is { } result ? new
        {
            seriesChanged = result.SeriesChanged,
            filesChanged = result.FilesChanged,
            cancelled = result.Cancelled,
            seconds = Math.Round(result.Duration.TotalSeconds, 1),
            failures = result.Failures.Take(200).Select(f => new { file = Path.GetFileName(f.Path), error = f.Error }),
            failureCount = result.Failures.Count,
            outputFolder = job.OutputFolder,
            canDownload = job.IsUpload,
        } : null,
    };

    private static object ScanView(ScanResult scan)
    {
        Dictionary<string, int> frames = FrameKeys(scan.Series.Select(s => s.FrameOfReferenceUid));
        return new
        {
            fileCount = scan.DicomFileCount,
            otherFileCount = scan.OtherFileCount,
            unreadableCount = scan.UnreadableFiles.Count,
            unreadable = scan.UnreadableFiles.Take(20).Select(f => new { file = Path.GetFileName(f.Path), error = f.Error }),
            dicomDirCount = scan.DicomDirFiles.Count,
            seconds = Math.Round(scan.Duration.TotalSeconds, 1),
            series = scan.Series.Select(s => new
            {
                uid = s.SeriesInstanceUid,
                modality = s.Modality,
                supported = s.IsSupported,
                number = s.SeriesNumber,
                description = s.SeriesDescription,
                files = s.Files.Count,
                bytes = s.TotalBytes,
                patient = s.PatientName.Replace('^', ' ').Trim(),
                patientId = s.PatientId,
                study = s.StudyInstanceUid,
                studyDescription = s.StudyDescription,
                studyDate = s.StudyDate,
                frame = s.FrameOfReferenceUid.Length == 0 ? (int?)null : frames[s.FrameOfReferenceUid],
                mixedFrames = s.DistinctFrameOfReferenceCount > 1,
                structureSet = s.IsStructureSet,
                // A structure set: the image series in this study it was drawn on (empty when they are not here).
                follows = s.IsStructureSet ? UnlinkPlanner.ImagesOf(s, scan.Series).Select(i => i.SeriesInstanceUid) : [],
            }),
        };
    }

    /// <summary>
    /// For every series: is it selected, and which frame of reference will it be in afterwards? Frames are
    /// returned as small integers so the browser can show which series end up registered to each other.
    /// </summary>
    public static object Preview(ScanResult scan, ChangeRequest request)
    {
        // An edit that is still being typed must not blank the preview: plan without the edits until they are valid.
        IReadOnlyList<string?> tagErrors = request.TagErrors();
        bool tagsValid = tagErrors.All(e => e is null);
        UnlinkPlan plan = UnlinkPlanner.Create(scan, request.ToOptions(withTagEdits: tagsValid));
        Dictionary<string, SeriesPlan> planned = plan.Series.ToDictionary(p => p.Series.SeriesInstanceUid, StringComparer.Ordinal);
        string After(DicomSeriesInfo s) =>
            planned.TryGetValue(s.SeriesInstanceUid, out SeriesPlan? p) && p.NewFrameOfReferenceUid is { } changed ? changed : s.FrameOfReferenceUid;
        Dictionary<string, int> frames = FrameKeys(scan.Series.Select(After));
        return new
        {
            seriesCount = plan.Series.Count,
            fileCount = plan.FileCount,
            tagErrors,
            series = scan.Series.ToDictionary(
                s => s.SeriesInstanceUid,
                s => new
                {
                    selected = planned.ContainsKey(s.SeriesInstanceUid),
                    frame = After(s).Length == 0 ? (int?)null : frames[After(s)],
                    // Structure sets only: follows | leftBehind | imagesNotSelected | imagesNotFound.
                    structureSet = plan.StructureSets.TryGetValue(s.SeriesInstanceUid, out StructureSetStatus status) ? status : (StructureSetStatus?)null,
                }),
        };
    }

    private static Dictionary<string, int> FrameKeys(IEnumerable<string> frames)
    {
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string frame in frames)
        {
            if (frame.Length > 0 && !keys.ContainsKey(frame))
            {
                keys.Add(frame, keys.Count);
            }
        }
        return keys;
    }
}

/// <summary>Exposed so the test project can host the app in-memory.</summary>
public partial class Program;
