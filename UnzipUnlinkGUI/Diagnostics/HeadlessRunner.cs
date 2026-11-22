using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FellowOakDicom;
using UnzipUnlink.Core;
using UnzipUnlinkGUI.ViewModels;
using UnzipUnlinkGUI.Windows;

namespace UnzipUnlinkGUI.Diagnostics;

/// <summary>
/// Hidden command-line mode. It drives the real window and view-model without anyone clicking, so the
/// layout can be captured as PNGs and a whole unlink can be checked end to end:
/// <code>
/// UnzipUnlinkGUI.exe --screenshot out.png [--source folder-or-zip | --demo] [--theme light|dark]
///                    [--run] [--report out.json] [--state confirm|about|dragover] [--mask-path text]
///                    [--series-uid] [--no-frame] [--no-sop] [--keep-ct] [--keep-petct] [--no-keep]
///                    [--tag Keyword=value]...
///                    [--shot-loading a.png] [--shot-running b.png] [--scale 1.5]
///                    [--cancel-loading] [--cancel-running] [--size 900x640] [--state selected]
/// </code>
/// <c>--run</c> rewrites the files under <c>--source</c> in place, exactly like pressing Unlink.
/// </summary>
public sealed class HeadlessOptions
{
    public string? Screenshot { get; private set; }
    public string? Source { get; private set; }
    public string? Theme { get; private set; }
    public string? Report { get; private set; }
    public string? State { get; private set; }
    public string? MaskPath { get; private set; }
    public string? ShotLoading { get; private set; }
    public string? ShotRunning { get; private set; }
    public double Scale { get; private set; } = 1;
    public bool Run { get; private set; }
    public bool Demo { get; private set; }
    public bool SeriesUid { get; private set; }
    public bool NoFrame { get; private set; }
    public bool NoSop { get; private set; }
    public bool KeepCt { get; private set; }
    public bool KeepPetCt { get; private set; }
    /// <summary>"Keyword=value" tag edits to add before the screenshot or run.</summary>
    public List<string> Tags { get; } = [];
    public bool NoKeep { get; private set; }
    public bool CancelLoading { get; private set; }
    public bool CancelRunning { get; private set; }
    public double? Width { get; private set; }
    public double? Height { get; private set; }

    /// <summary>Null unless the command line asks for the hidden mode.</summary>
    public static HeadlessOptions? TryParse(string[] args)
    {
        if (!args.Contains("--screenshot") && !args.Contains("--report"))
        {
            return null;
        }
        var options = new HeadlessOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--screenshot": options.Screenshot = Next(); break;
                case "--source": options.Source = Next(); break;
                case "--theme": options.Theme = Next().ToLowerInvariant(); break;
                case "--report": options.Report = Next(); break;
                case "--state": options.State = Next().ToLowerInvariant(); break;
                case "--mask-path": options.MaskPath = Next(); break;
                case "--shot-loading": options.ShotLoading = Next(); break;
                case "--shot-running": options.ShotRunning = Next(); break;
                case "--scale": options.Scale = double.TryParse(Next(), System.Globalization.CultureInfo.InvariantCulture, out double s) ? Math.Clamp(s, 0.5, 4) : 1; break;
                case "--run": options.Run = true; break;
                case "--demo": options.Demo = true; break;
                case "--series-uid": options.SeriesUid = true; break;
                case "--no-frame": options.NoFrame = true; break;
                case "--no-sop": options.NoSop = true; break;
                case "--keep-ct": options.KeepCt = true; break;
                case "--keep-petct": options.KeepPetCt = true; break;
                case "--no-keep": options.NoKeep = true; break;
                case "--tag": options.Tags.Add(Next()); break;
                case "--cancel-loading": options.CancelLoading = true; break;
                case "--cancel-running": options.CancelRunning = true; break;
                case "--size":
                    string[] size = Next().Split('x');
                    if (size.Length == 2 && double.TryParse(size[0], out double w) && double.TryParse(size[1], out double h))
                    {
                        options.Width = w;
                        options.Height = h;
                    }
                    break;
            }
        }
        return options;
    }
}

public static class HeadlessRunner
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Returns the process exit code: 0 = fine, 1 = crashed, 2 = the window showed an error, 3 = files failed.</summary>
    public static async Task<int> RunAsync(MainWindow window, MainViewModel viewModel, HeadlessOptions options)
    {
        var report = new Dictionary<string, object?>();
        int exitCode = 0;
        try
        {
            // Off-screen and never activated, so it does not flash up while someone is working.
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            if (options.Width is double width && options.Height is double height)
            {
                window.Width = width;
                window.Height = height;
            }
            window.Show();
            await Settle(window.Dispatcher);

            viewModel.DisplayPathOverride = options.MaskPath;
            FrameworkElement root = window.RootGrid;
            bool loadingShot = false, runningShot = false;
            PropertyChangedEventHandler watcher = (_, e) =>
            {
                // Press Cancel part-way through, the way a user would.
                if (options.CancelLoading && viewModel.IsLoading && viewModel.CanCancel
                    && e.PropertyName == nameof(MainViewModel.LoadProgress) && viewModel.LoadProgress >= 0.3)
                {
                    viewModel.CancelCommand.Execute(null);
                }
                if (options.CancelRunning && viewModel.IsRunning && viewModel.CanCancel
                    && e.PropertyName == nameof(MainViewModel.RunProgress) && viewModel.RunProgress >= 0.3)
                {
                    viewModel.CancelCommand.Execute(null);
                }
                if (options.ShotLoading is not null && !loadingShot && viewModel.IsLoading
                    && e.PropertyName == nameof(MainViewModel.LoadProgress) && viewModel.LoadProgress >= 0.4)
                {
                    loadingShot = true;
                    SavePng(root, options.ShotLoading, options.Scale);
                }
                if (options.ShotRunning is not null && !runningShot && viewModel.IsRunning
                    && e.PropertyName == nameof(MainViewModel.RunProgress) && viewModel.RunProgress >= 0.4)
                {
                    runningShot = true;
                    SavePng(root, options.ShotRunning, options.Scale);
                }
            };
            viewModel.PropertyChanged += watcher;

            if (options.Demo)
            {
                viewModel.ShowScan(DemoData.Create(), DemoData.DisplayPath);
            }
            else if (!string.IsNullOrEmpty(options.Source))
            {
                await viewModel.LoadAsync(options.Source);
            }

            if (options.NoFrame) { viewModel.ChangeFrame = false; }
            if (options.SeriesUid) { viewModel.ChangeSeries = true; }
            if (options.NoSop) { viewModel.ChangeSop = false; }
            if (options.NoKeep) { viewModel.KeepCt = false; viewModel.KeepPetCt = false; viewModel.KeepStructures = false; }
            foreach (string tag in options.Tags)
            {
                string[] parts = tag.Split('=', 2);
                if (UnzipUnlink.Core.DicomTagCatalog.TryResolve(parts[0], out _, out UnzipUnlink.Core.EditableTag? info))
                {
                    viewModel.AddTagEdit(info, parts.Length > 1 ? parts[1] : "");
                }
            }
            if (options.KeepCt) { viewModel.KeepCt = true; }
            if (options.KeepPetCt) { viewModel.KeepPetCt = true; }

            report["stageAfterLoad"] = viewModel.Stage.ToString();
            report["sourceNote"] = viewModel.SourceNote;
            report["runButton"] = viewModel.RunButtonText;
            report["keepCt"] = viewModel.KeepCt;
            report["keepPetCt"] = viewModel.KeepPetCt;
            report["keepStructures"] = viewModel.KeepStructures;
            report["canRun"] = viewModel.CanRun;
            report["rowsBeforeRun"] = DescribeRows(viewModel);

            if (options.Run && viewModel.IsReady && !options.Demo)
            {
                string folder = viewModel.FolderPath;
                List<SeriesSnapshot> before = Snapshot(DicomScanner.Scan(folder));
                await viewModel.RunAsync();
                List<SeriesSnapshot> after = Snapshot(DicomScanner.Scan(folder));
                report["verification"] = Compare(before, after);
                UnlinkResult? result = viewModel.LastResult;
                report["result"] = result is null ? null : new
                {
                    result.SeriesChanged,
                    result.FilesChanged,
                    Failures = result.Failures.Count,
                    result.Cancelled,
                    Seconds = Math.Round(result.Duration.TotalSeconds, 2),
                };
                report["resultTitle"] = viewModel.ResultTitle;
                report["resultDetail"] = viewModel.ResultDetail;
                report["rowsAfterRun"] = DescribeRows(viewModel);
                if (result is null || result.Failures.Count > 0 || result.Cancelled)
                {
                    exitCode = 3;
                }
            }

            FrameworkElement target = root;
            switch (options.State)
            {
                case "confirm":
                    viewModel.RunCommand.Execute(null);
                    break;
                case "dragover":
                    viewModel.IsDragOver = true;
                    break;
                case "selected":
                    window.SeriesGrid.SelectedIndex = Math.Min(2, viewModel.Rows.Count - 1);
                    break;
                case "about":
                    var about = new AboutPage
                    {
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -20000,
                        Top = -20000,
                    };
                    about.Show();
                    target = about.RootGrid;
                    break;
            }

            viewModel.PropertyChanged -= watcher;
            await Settle(window.Dispatcher);
            await Task.Delay(600);
            await Settle(window.Dispatcher);

            report["stage"] = viewModel.Stage.ToString();
            report["error"] = viewModel.ErrorMessage;
            report["summary"] = viewModel.SummaryText;
            report["warnings"] = viewModel.Warnings.ToList();
            if (viewModel.ErrorMessage.Length > 0 && exitCode == 0)
            {
                exitCode = 2;
            }
            if (!string.IsNullOrEmpty(options.Screenshot))
            {
                SavePng(target, options.Screenshot, options.Scale);
            }
        }
        catch (Exception ex)
        {
            report["crash"] = ex.ToString();
            exitCode = 1;
        }

        report["exitCode"] = exitCode;
        string? reportPath = options.Report ?? (exitCode == 1 && options.Screenshot is not null ? options.Screenshot + ".error.json" : null);
        if (reportPath is not null)
        {
            try
            {
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, Json));
            }
            catch
            {
                // Nothing sensible left to do in a windowless run.
            }
        }
        return exitCode;
    }

    private static async Task Settle(Dispatcher dispatcher) =>
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    /// <summary>Renders an element exactly as laid out (independent of where the window is on screen).</summary>
    private static void SavePng(FrameworkElement element, string path, double scale)
    {
        element.UpdateLayout();
        double width = element.ActualWidth, height = element.ActualHeight;
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None }, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    // ---------------------------------------------------------------- report (UIDs and counts only - no patient details)

    private sealed record SeriesSnapshot(string SeriesUid, string Modality, bool Supported, string Frame, int Files, string FirstFile, string FirstSop);

    private static List<SeriesSnapshot> Snapshot(ScanResult scan) =>
        scan.Series.Select(s => new SeriesSnapshot(
            s.SeriesInstanceUid, s.Modality, s.IsSupported, s.FrameOfReferenceUid, s.Files.Count, s.Files[0], ReadSop(s.Files[0]))).ToList();

    private static string ReadSop(string path)
    {
        try
        {
            return DicomFile.Open(path, FileReadOption.SkipLargeTags).Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "");
        }
        catch (Exception ex)
        {
            return "unreadable: " + ex.Message;
        }
    }

    /// <summary>Pairs every series from before the run with the series that now owns its first file.</summary>
    private static object Compare(List<SeriesSnapshot> before, List<SeriesSnapshot> after)
    {
        var details = new List<object>();
        int seriesChanged = 0, frameChanged = 0, sopChanged = 0, untouched = 0, supported = 0;
        foreach (SeriesSnapshot old in before)
        {
            SeriesSnapshot? now = after.FirstOrDefault(a => string.Equals(a.FirstFile, old.FirstFile, StringComparison.OrdinalIgnoreCase));
            bool newSeries = now is not null && now.SeriesUid != old.SeriesUid;
            bool newFrame = now is not null && now.Frame != old.Frame;
            bool newSop = now is not null && now.FirstSop != old.FirstSop;
            if (old.Supported)
            {
                supported++;
                if (newSeries) { seriesChanged++; }
                if (newFrame) { frameChanged++; }
                if (newSop) { sopChanged++; }
            }
            else if (now is not null && !newSeries && !newFrame && !newSop)
            {
                untouched++;
            }
            details.Add(new
            {
                old.Modality,
                old.Files,
                FilesNow = now?.Files,
                SeriesUidChanged = newSeries,
                FrameChanged = newFrame,
                SopChanged = newSop,
            });
        }
        List<SeriesSnapshot> supportedNow = after.Where(a => a.Supported).ToList();
        return new
        {
            SeriesBefore = before.Count,
            SeriesAfter = after.Count,
            SupportedSeries = supported,
            SupportedWithNewSeriesUid = seriesChanged,
            SupportedWithNewFrame = frameChanged,
            SupportedWithNewSop = sopChanged,
            UnsupportedSeries = before.Count - supported,
            UnsupportedLeftUntouched = untouched,
            DistinctFramesBeforeAmongSupported = before.Where(b => b.Supported).Select(b => b.Frame).Distinct().Count(),
            DistinctFramesAfterAmongSupported = supportedNow.Select(a => a.Frame).Distinct().Count(),
            FilesBefore = before.Sum(b => b.Files),
            FilesAfter = after.Sum(a => a.Files),
            Series = details,
        };
    }

    private static object DescribeRows(MainViewModel viewModel) =>
        viewModel.Rows.Select(r => new
        {
            Type = r.ModalityLabel,
            Images = r.ImageCount,
            r.IsIncluded,
            First = r.FirstBadge.Text,
            Second = r.SecondBadge.Text,
            r.Note,
        }).ToList();
}
