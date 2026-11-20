using System.Collections.ObjectModel;
using System.Windows.Input;
using UnzipUnlink.Core;
using UnzipUnlinkGUI.Services;

namespace UnzipUnlinkGUI.ViewModels;

public enum Stage
{
    /// <summary>Nothing chosen yet.</summary>
    Empty,
    /// <summary>Extracting, waiting for a transfer to settle, or scanning.</summary>
    Loading,
    /// <summary>Scan shown; the user is reviewing and choosing options.</summary>
    Ready,
    /// <summary>Rewriting files.</summary>
    Running,
    /// <summary>Run finished; the result is on screen.</summary>
    Done,
}

/// <summary>
/// The whole guided flow: choose data → review → choose changes → run. All DICOM work is delegated to
/// <c>UnzipUnlink.Core</c>; this class only holds state and decides what the window shows.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private const string NetworkHintText =
        "This is a network location. Copying the data to a local drive first is much faster.";

    private readonly IUserDialogs _dialogs;
    private Stage _stage = Stage.Empty;
    private CancellationTokenSource? _cts;
    private int _operation;
    private ScanResult? _scan;
    private bool _updating;
    private bool _showingResult;
    private bool _rescanning;
    private string? _lastPath;

    private string _sourcePath = "";
    private string _folderPath = "";
    private string _sourceNote = "";
    private string _networkHint = "";
    private string _loadStatus = "";
    private string _loadDetail = "";
    private double _loadProgress;
    private bool _isLoadIndeterminate = true;
    private bool _isCancelling;
    private bool _isDragOver;
    private string _errorMessage = "";
    private bool _canRetry;
    private string _summaryText = "";

    private bool _changeFrame = true;
    private bool _changeSeries;
    private bool _changeSop = true;
    private bool _keepCt;
    private bool _keepPetCt;
    private int _plannedSeries;
    private int _plannedFiles;
    private string _runDisabledReason = "";
    private bool _isConfirming;

    private double _runProgress;
    private string _runStatus = "";
    private string _runDetail = "";

    private string _resultTitle = "";
    private string _resultDetail = "";
    private string _resultNote = "";
    private bool _resultIsSuccess = true;

    public MainViewModel(IUserDialogs dialogs)
    {
        _dialogs = dialogs;
        Ct = new ModalityToggleViewModel("CT", OnModalityRequested);
        Mr = new ModalityToggleViewModel("MR", OnModalityRequested);
        Pet = new ModalityToggleViewModel("PT", OnModalityRequested);
        Modalities = [Ct, Mr, Pet];

        ChooseFolderCommand = new RelayCommand(ChooseFolder);
        ChooseZipCommand = new RelayCommand(ChooseZip);
        CancelCommand = new RelayCommand(Cancel);
        RunCommand = new RelayCommand(AskToRun);
        ConfirmRunCommand = new RelayCommand(() =>
        {
            if (IsConfirming)
            {
                _ = RunAsync();
            }
        });
        CancelConfirmCommand = new RelayCommand(() => IsConfirming = false);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        ProcessAnotherCommand = new RelayCommand(ProcessAnother);
        ContinueCommand = new RelayCommand(ContinueInFolder);
        RetryCommand = new RelayCommand(Retry);
        DismissErrorCommand = new RelayCommand(ClearError);
    }

    // ---------------------------------------------------------------- commands

    public ICommand ChooseFolderCommand { get; }
    public ICommand ChooseZipCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand ConfirmRunCommand { get; }
    public ICommand CancelConfirmCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ProcessAnotherCommand { get; }
    public ICommand ContinueCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand DismissErrorCommand { get; }

    // ---------------------------------------------------------------- stage

    public Stage Stage
    {
        get => _stage;
        private set
        {
            _stage = value;
            _isCancelling = false;
            foreach (SeriesRowViewModel row in Rows)
            {
                row.CanToggle = value == Stage.Ready;
            }
            RaiseAllPropertiesChanged();
        }
    }

    public bool IsEmpty => _stage == Stage.Empty;
    public bool IsLoading => _stage == Stage.Loading;
    public bool IsReady => _stage == Stage.Ready;
    public bool IsRunning => _stage == Stage.Running;
    public bool IsDone => _stage == Stage.Done;
    public bool IsBusy => _stage is Stage.Loading or Stage.Running;

    public bool CanChooseData => !IsBusy && !IsConfirming;
    public bool CanEditOptions => IsReady && !IsConfirming;
    public bool CanCancel => IsBusy && !_isCancelling;
    public bool IsNotConfirming => !IsConfirming;
    public bool ShowGrid => !IsLoading && Rows.Count > 0;
    public bool ShowNoSeries => (IsReady || IsDone) && Rows.Count == 0;
    public bool ShowRunPrompt => IsReady || IsLoading;

    // ---------------------------------------------------------------- step 1: data

    /// <summary>What the user picked (a folder or a .zip).</summary>
    public string SourcePath
    {
        get => _sourcePath;
        private set { if (Set(ref _sourcePath, value)) { OnPropertyChanged(nameof(DisplayPath)); } }
    }

    /// <summary>The folder that is scanned and rewritten.</summary>
    public string FolderPath
    {
        get => _folderPath;
        private set { if (Set(ref _folderPath, value)) { OnPropertyChanged(nameof(DisplayFolder)); } }
    }

    /// <summary>Screenshots only: text shown instead of the real paths.</summary>
    public string? DisplayPathOverride { get; set; }
    public string DisplayPath => DisplayPathOverride ?? _sourcePath;
    public string DisplayFolder => DisplayPathOverride ?? _folderPath;

    public string SourceNote { get => _sourceNote; private set => Set(ref _sourceNote, value); }
    public string NetworkHint { get => _networkHint; private set => Set(ref _networkHint, value); }
    public bool IsDragOver { get => _isDragOver; set => Set(ref _isDragOver, value); }

    public string LoadStatus { get => _loadStatus; private set => Set(ref _loadStatus, value); }
    public string LoadDetail { get => _loadDetail; private set => Set(ref _loadDetail, value); }
    public double LoadProgress { get => _loadProgress; private set => Set(ref _loadProgress, value); }
    public bool IsLoadIndeterminate { get => _isLoadIndeterminate; private set => Set(ref _isLoadIndeterminate, value); }

    public string ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }
    public bool CanRetry { get => _canRetry; private set => Set(ref _canRetry, value); }

    // ---------------------------------------------------------------- step 2: review

    public ObservableCollection<SeriesRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public string SummaryText { get => _summaryText; private set => Set(ref _summaryText, value); }

    public string FirstBadgeHeader => _showingResult ? "Before" : "Now";
    public string SecondBadgeHeader => _showingResult ? "Now" : "After";
    public string ReviewHelp =>
        _showingResult
            ? "“Before” is how the series were linked when you started; “Now” is what is on disk after the run."
        : Rows.Any(r => !r.FirstBadge.IsMuted)
            ? "Series with the same letter share a frame of reference, so other systems treat them as registered to each other. “After” previews what the run will do."
            : "No two series here share a frame of reference, so none of them are linked to each other today. New IDs still make them import as independent copies.";

    // ---------------------------------------------------------------- step 3: options

    public bool ChangeFrame
    {
        get => _changeFrame;
        set
        {
            if (Set(ref _changeFrame, value))
            {
                RecommendSop(value);
                OnOptionsChanged();
            }
        }
    }

    public bool ChangeSeries
    {
        get => _changeSeries;
        set
        {
            if (Set(ref _changeSeries, value))
            {
                RecommendSop(value);
                OnOptionsChanged();
            }
        }
    }

    /// <summary>Switched on automatically with either of the others; the user may still switch it off.</summary>
    public bool ChangeSop
    {
        get => _changeSop;
        set { if (Set(ref _changeSop, value)) { OnOptionsChanged(); } }
    }

    public bool ShowSopWarning => (_changeFrame || _changeSeries) && !_changeSop;

    public ModalityToggleViewModel Ct { get; }
    public ModalityToggleViewModel Mr { get; }
    public ModalityToggleViewModel Pet { get; }
    public IReadOnlyList<ModalityToggleViewModel> Modalities { get; }

    /// <summary>4DCT: CT series sharing a frame of reference keep sharing a (new) one.</summary>
    public bool KeepCt
    {
        get => _keepCt;
        set { if (Set(ref _keepCt, value)) { OnOptionsChanged(); } }
    }

    /// <summary>PET/CT: a PET and the CT in its frame of reference keep sharing a (new) one.</summary>
    public bool KeepPetCt
    {
        get => _keepPetCt;
        set { if (Set(ref _keepPetCt, value)) { OnOptionsChanged(); } }
    }

    public bool CanKeepCt => _changeFrame && Ct.IsOn;
    public bool CanKeepPetCt => _changeFrame && Ct.IsOn && Pet.IsOn;

    // ---------------------------------------------------------------- step 4: run

    public int PlannedSeries { get => _plannedSeries; private set => Set(ref _plannedSeries, value); }
    public int PlannedFiles { get => _plannedFiles; private set => Set(ref _plannedFiles, value); }
    public bool CanRun => IsReady && !IsConfirming && _plannedSeries > 0;

    public string RunButtonText => _plannedSeries > 0
        ? $"Unlink {_plannedSeries:N0} series ({Count(_plannedFiles, "file")})"
        : "Unlink";

    /// <summary>Why the run button is disabled ("" when it is not).</summary>
    public string RunDisabledReason { get => _runDisabledReason; private set => Set(ref _runDisabledReason, value); }

    public string RunHint =>
        IsLoading ? "Waiting for the scan to finish."
        : _runDisabledReason.Length > 0 ? _runDisabledReason
        : $"{Count(_plannedFiles, "file")} will be rewritten in place. Work on a copy if you need the originals.";

    public bool IsConfirming
    {
        get => _isConfirming;
        private set
        {
            if (Set(ref _isConfirming, value))
            {
                OnPropertyChanged(nameof(IsNotConfirming));
                OnPropertyChanged(nameof(CanChooseData));
                OnPropertyChanged(nameof(CanEditOptions));
                OnPropertyChanged(nameof(CanRun));
            }
        }
    }

    public string ConfirmTitle => $"Unlink {_plannedSeries:N0} series?";
    public string ConfirmDetail => $"{Count(_plannedFiles, "file")} in this folder will be rewritten in place:";
    public string ConfirmChanges
    {
        get
        {
            var parts = new List<string>();
            if (_changeFrame) { parts.Add("Frame of Reference UID"); }
            if (_changeSeries) { parts.Add("Series Instance UID"); }
            if (_changeSop) { parts.Add("SOP Instance UID"); }
            return "New " + string.Join(", ", parts);
        }
    }

    public double RunProgress { get => _runProgress; private set => Set(ref _runProgress, value); }
    public string RunStatus { get => _runStatus; private set => Set(ref _runStatus, value); }
    public string RunDetail { get => _runDetail; private set => Set(ref _runDetail, value); }

    // ---------------------------------------------------------------- result

    public string ResultTitle { get => _resultTitle; private set => Set(ref _resultTitle, value); }
    public string ResultDetail { get => _resultDetail; private set => Set(ref _resultDetail, value); }
    public string ResultNote { get => _resultNote; private set => Set(ref _resultNote, value); }
    public bool ResultIsSuccess { get => _resultIsSuccess; private set => Set(ref _resultIsSuccess, value); }
    /// <summary>Plain list of what the run replaced, e.g. "New Frame of Reference UID for every series".</summary>
    public ObservableCollection<string> ResultChanges { get; } = [];
    public ObservableCollection<FailureViewModel> Failures { get; } = [];
    public bool HasFailures => Failures.Count > 0;
    public string FailuresTitle => $"{Count(Failures.Count, "file")} could not be rewritten";
    /// <summary>The last run's raw outcome (for tests and the hidden command-line mode).</summary>
    public UnlinkResult? LastResult { get; private set; }

    // ================================================================ choosing data

    private void ChooseFolder()
    {
        if (!CanChooseData)
        {
            return;
        }
        string? folder = _dialogs.PickFolder();
        if (folder is not null)
        {
            _ = LoadAsync(folder);
        }
    }

    private void ChooseZip()
    {
        if (!CanChooseData)
        {
            return;
        }
        string? zip = _dialogs.PickZip();
        if (zip is not null)
        {
            _ = LoadAsync(zip);
        }
    }

    private void Retry()
    {
        if (_lastPath is not null && CanChooseData)
        {
            _ = LoadAsync(_lastPath);
        }
    }

    /// <summary>
    /// Opens a folder or a .zip: extracts the zip next to itself if needed, waits until no more files are
    /// arriving, then scans. Never throws - problems end up in <see cref="ErrorMessage"/>.
    /// </summary>
    public async Task LoadAsync(string path)
    {
        if (IsBusy)
        {
            return;
        }
        IsConfirming = false;
        ClearError();

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            ShowError($"That path cannot be used: {ex.Message}");
            return;
        }
        bool isZip = File.Exists(full) && string.Equals(Path.GetExtension(full), ".zip", StringComparison.OrdinalIgnoreCase);
        if (!isZip && !Directory.Exists(full))
        {
            ShowError(File.Exists(full)
                ? "Choose a folder or a .zip file - other kinds of file cannot be opened here."
                : $"Not found: {full}");
            return;
        }

        _lastPath = full;
        CancellationToken token = BeginOperation(out int operation);
        ClearData();
        SourcePath = full;
        FolderPath = isZip ? "" : full;
        NetworkHint = IsNetworkPath(full) ? NetworkHintText : "";
        LoadStatus = isZip ? "Opening the .zip…" : "Checking the folder…";
        Stage = Stage.Loading;

        string? partialFolder = null;
        try
        {
            string folder = full;
            bool extractedNow = false;
            if (isZip)
            {
                folder = ZipTools.DefaultExtractFolder(full);
                string zipName = Path.GetFileName(full);
                if (Directory.Exists(folder))
                {
                    SourceNote = $"Using the folder “{Path.GetFileName(folder)}” that already exists next to the .zip - it was not extracted again.";
                }
                else
                {
                    partialFolder = folder;
                    LoadStatus = $"Extracting {zipName}…";
                    var zipProgress = new Progress<ZipProgress>(p =>
                    {
                        if (operation != _operation || !IsLoading)
                        {
                            return;
                        }
                        IsLoadIndeterminate = false;
                        LoadProgress = Fraction(p.EntriesDone, p.EntriesTotal);
                        LoadDetail = $"{p.EntriesDone:N0} of {p.EntriesTotal:N0} files";
                    });
                    await Task.Run(() => ZipTools.Extract(full, folder, zipProgress, token), token);
                    token.ThrowIfCancellationRequested();
                    partialFolder = null;
                    extractedNow = true;
                    SourceNote = $"Extracted to the folder “{Path.GetFileName(folder)}” next to the .zip. The .zip was kept.";
                }
            }
            FolderPath = folder;

            if (!extractedNow)
            {
                // A folder that was just extracted here is complete by construction; anything else may
                // still be arriving (network copy, export from another system).
                LoadStatus = "Checking that no files are still being copied…";
                LoadDetail = "";
                IsLoadIndeterminate = true;
                bool stable = await Task.Run(() => TransferGuard.WaitUntilStable(folder, cancellationToken: token), token);
                if (!stable)
                {
                    ClearData();
                    Stage = Stage.Empty;
                    ShowError("Files are still arriving in this folder. Wait until the copy has finished, then try again.", canRetry: true);
                    return;
                }
            }

            LoadStatus = "Looking for DICOM series…";
            LoadDetail = "";
            LoadProgress = 0;
            IsLoadIndeterminate = true;
            var scanProgress = new Progress<ScanProgress>(p =>
            {
                if (operation != _operation || !IsLoading)
                {
                    return;
                }
                IsLoadIndeterminate = false;
                LoadProgress = Fraction(p.FilesDone, p.FilesTotal);
                LoadDetail = $"{p.FilesDone:N0} of {p.FilesTotal:N0} files read";
            });
            ScanResult scan = await Task.Run(() => DicomScanner.Scan(folder, scanProgress, token), token);
            token.ThrowIfCancellationRequested();

            ApplyScan(scan);
            Stage = Stage.Ready;
        }
        catch (OperationCanceledException)
        {
            await DeletePartialExtractAsync(partialFolder);
            ClearData();
            Stage = Stage.Empty;
        }
        catch (Exception ex)
        {
            await DeletePartialExtractAsync(partialFolder);
            ClearData();
            Stage = Stage.Empty;
            ShowError($"{(isZip ? "The .zip could not be opened" : "The folder could not be read")}: {ex.Message}", canRetry: true);
        }
    }

    /// <summary>Shows an already-made scan (used for the built-in demo data).</summary>
    public void ShowScan(ScanResult scan, string displayPath)
    {
        if (IsBusy)
        {
            return;
        }
        ClearError();
        ClearData();
        SourcePath = displayPath;
        FolderPath = scan.Root;
        ApplyScan(scan);
        Stage = Stage.Ready;
    }

    private void Cancel()
    {
        if (!CanCancel)
        {
            return;
        }
        _isCancelling = true;
        _cts?.Cancel();
        if (IsLoading)
        {
            LoadStatus = "Cancelling…";
        }
        else
        {
            RunStatus = "Stopping once the current series is finished…";
        }
        OnPropertyChanged(nameof(CanCancel));
    }

    private void ProcessAnother()
    {
        if (IsBusy)
        {
            return;
        }
        ClearError();
        ClearData();
        Stage = Stage.Empty;
    }

    private void ContinueInFolder()
    {
        if (!IsDone || _scan is null)
        {
            return;
        }
        ClearError();
        ApplyScan(_scan);
        Stage = Stage.Ready;
    }

    private void OpenFolder()
    {
        try
        {
            _dialogs.OpenFolder(FolderPath);
        }
        catch (Exception ex)
        {
            ShowError($"The folder could not be opened: {ex.Message}");
        }
    }

    // ================================================================ review + options

    private void ApplyScan(ScanResult scan)
    {
        _scan = scan;
        _showingResult = false;
        _updating = true;
        try
        {
            Rows.Clear();
            foreach (DicomSeriesInfo series in scan.Series)
            {
                var row = new SeriesRowViewModel(series, OnRowIncludedChanged)
                {
                    Note = series.IsSupported ? "" : "left unchanged",
                    CanToggle = _stage == Stage.Ready,
                };
                row.SetIncluded(series.IsSupported);
                Rows.Add(row);
            }
        }
        finally
        {
            _updating = false;
        }
        Failures.Clear();
        LastResult = null;
        DescribeScan(scan, afterRun: false);
        UpdateToggles();
        SuggestKeepTogether(scan);
        OnOptionsChanged();
    }

    /// <summary>
    /// Pre-ticks "Keep together" when the scan really contains such a group: two or more CT series in one
    /// frame of reference (4DCT), or a PET and a CT in one frame (PET/CT). OnOptionsChanged clears them
    /// again if they are not available (for example when frames are not being replaced).
    /// </summary>
    private void SuggestKeepTogether(ScanResult scan)
    {
        List<IGrouping<string, DicomSeriesInfo>> frames = scan.Series
            .Where(s => s.FrameOfReferenceUid.Length > 0 && (IsCode(s, "CT") || IsCode(s, "PT")))
            .GroupBy(s => s.FrameOfReferenceUid, StringComparer.Ordinal)
            .ToList();
        _keepCt = frames.Any(g => g.Count(s => IsCode(s, "CT")) > 1);
        _keepPetCt = frames.Any(g => g.Any(s => IsCode(s, "CT")) && g.Any(s => IsCode(s, "PT")));
        OnPropertyChanged(nameof(KeepCt));
        OnPropertyChanged(nameof(KeepPetCt));
    }

    private static bool IsCode(DicomSeriesInfo series, string modality) =>
        string.Equals(series.Modality, modality, StringComparison.OrdinalIgnoreCase);

    private void DescribeScan(ScanResult scan, bool afterRun)
    {
        int patients = scan.Series
            .Select(s => s.PatientId.Length > 0 ? s.PatientId : s.PatientName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        SummaryText = scan.Series.Count == 0
            ? ""
            : $"{scan.Series.Count:N0} series · {Count(scan.DicomFileCount, "image")} · {Count(patients, "patient")}";

        Warnings.Clear();
        if (scan.DicomDirFiles.Count > 0)
        {
            Warnings.Add(afterRun
                ? "The DICOMDIR index in this folder is now out of date."
                : "DICOMDIR index will be out of date after the rewrite.");
        }
        if (scan.UnreadableFiles.Count > 0)
        {
            Warnings.Add($"{Count(scan.UnreadableFiles.Count, "file")} looked like DICOM but could not be read, and will be left as-is.");
        }
        int mixed = scan.Series.Count(s => s.DistinctFrameOfReferenceCount > 1);
        if (mixed > 0)
        {
            Warnings.Add(mixed == 1
                ? "1 series contains images with more than one frame of reference."
                : $"{mixed:N0} series contain images with more than one frame of reference.");
        }
        if (patients > 1)
        {
            Warnings.Add($"This folder holds images from {patients} patients.");
        }
    }

    private void OnRowIncludedChanged(SeriesRowViewModel row)
    {
        if (_updating)
        {
            return;
        }
        UpdateToggles();
        OnOptionsChanged();
    }

    /// <summary>A CT / MR / PET switch was clicked: tick or untick every series of that modality.</summary>
    private void OnModalityRequested(ModalityToggleViewModel toggle, bool include)
    {
        _updating = true;
        try
        {
            foreach (SeriesRowViewModel row in Rows.Where(r => IsModality(r, toggle)))
            {
                row.SetIncluded(include);
            }
        }
        finally
        {
            _updating = false;
        }
        UpdateToggles();
        OnOptionsChanged();
    }

    private void UpdateToggles()
    {
        foreach (ModalityToggleViewModel toggle in Modalities)
        {
            int total = 0, selected = 0;
            foreach (SeriesRowViewModel row in Rows.Where(r => IsModality(r, toggle)))
            {
                total++;
                if (row.IsIncluded)
                {
                    selected++;
                }
            }
            toggle.Update(selected, total);
        }
    }

    private static bool IsModality(SeriesRowViewModel row, ModalityToggleViewModel toggle) =>
        string.Equals(row.Modality, toggle.Code, StringComparison.OrdinalIgnoreCase);

    private void RecommendSop(bool otherSwitchedOn)
    {
        if (otherSwitchedOn && !_changeSop)
        {
            _changeSop = true;
            OnPropertyChanged(nameof(ChangeSop));
        }
    }

    private void OnOptionsChanged()
    {
        if (_updating)
        {
            return;
        }
        // "Keep together" only makes sense while its modalities are selected and frames are being replaced.
        if (!CanKeepCt && _keepCt)
        {
            _keepCt = false;
            OnPropertyChanged(nameof(KeepCt));
        }
        if (!CanKeepPetCt && _keepPetCt)
        {
            _keepPetCt = false;
            OnPropertyChanged(nameof(KeepPetCt));
        }
        OnPropertyChanged(nameof(CanKeepCt));
        OnPropertyChanged(nameof(CanKeepPetCt));
        OnPropertyChanged(nameof(ShowSopWarning));
        RefreshPlan();
    }

    /// <summary>The options exactly as Core will receive them: modalities and the explicit series selection agree.</summary>
    public UnlinkOptions BuildOptions()
    {
        var options = new UnlinkOptions
        {
            NewFrameOfReferenceUid = _changeFrame,
            NewSeriesInstanceUid = _changeSeries,
            NewSopInstanceUid = _changeSop,
            KeepCtSeriesLinked = _keepCt && CanKeepCt,
            KeepPetCtLinked = _keepPetCt && CanKeepPetCt,
            SeriesInstanceUids = Rows.Where(r => r.IsIncluded)
                                     .Select(r => r.Series.SeriesInstanceUid)
                                     .ToHashSet(StringComparer.Ordinal),
        };
        foreach (ModalityToggleViewModel toggle in Modalities.Where(m => m.IsOn))
        {
            options.Modalities.Add(toggle.Code);
        }
        return options;
    }

    /// <summary>Re-plans with Core and refreshes the Now / After badges and the run button.</summary>
    private void RefreshPlan()
    {
        if (_scan is null || _showingResult)
        {
            return;
        }
        UnlinkOptions options = BuildOptions();
        UnlinkPlan plan = UnlinkPlanner.Create(_scan, options);
        Dictionary<string, SeriesPlan> planned = plan.Series.ToDictionary(p => p.Series.SeriesInstanceUid, StringComparer.Ordinal);

        var frames = new List<(string Before, string After)>(Rows.Count);
        foreach (SeriesRowViewModel row in Rows)
        {
            string now = row.Series.FrameOfReferenceUid;
            string after = planned.TryGetValue(row.Series.SeriesInstanceUid, out SeriesPlan? seriesPlan)
                ? seriesPlan.NewFrameOfReferenceUid ?? now
                : now;
            frames.Add((now, after));
        }
        ApplyBadges(frames);

        PlannedSeries = plan.Series.Count;
        PlannedFiles = plan.FileCount;
        RunDisabledReason =
            plan.Series.Count > 0 ? ""
            : _scan.Series.Count == 0 ? "No DICOM series were found in this folder."
            : !_scan.Series.Any(s => s.IsSupported) ? "Only CT, MR and PET series can be unlinked, and none were found."
            : !options.ChangesAnything ? "Choose at least one thing to change."
            : "Tick at least one series to unlink.";
        OnPropertyChanged(nameof(ReviewHelp));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunButtonText));
        OnPropertyChanged(nameof(RunHint));
        OnPropertyChanged(nameof(ConfirmTitle));
        OnPropertyChanged(nameof(ConfirmDetail));
        OnPropertyChanged(nameof(ConfirmChanges));
    }

    private void ApplyBadges(IReadOnlyList<(string Before, string After)> frames)
    {
        (LinkBadge[] first, LinkBadge[] second) = LinkGroups.Assign(frames);
        for (int i = 0; i < Rows.Count; i++)
        {
            Rows[i].FirstBadge = first[i];
            Rows[i].SecondBadge = second[i];
        }
    }

    // ================================================================ running

    private void AskToRun()
    {
        if (CanRun)
        {
            IsConfirming = true;
        }
    }

    /// <summary>
    /// Rewrites the selected series in place, then rescans so the grid shows what is really on disk.
    /// The confirmation step lives in the window; this method runs straight away.
    /// </summary>
    public async Task RunAsync()
    {
        if (!IsReady || _scan is null)
        {
            return;
        }
        IsConfirming = false;
        ClearError();
        UnlinkPlan plan = UnlinkPlanner.Create(_scan, BuildOptions());
        if (plan.Series.Count == 0)
        {
            return;
        }
        string folder = FolderPath;

        // Remember how every series was linked, keyed by the Series Instance UID it will have afterwards,
        // so the result grid can show Before next to Now.
        Dictionary<string, SeriesPlan> planned = plan.Series.ToDictionary(p => p.Series.SeriesInstanceUid, StringComparer.Ordinal);
        var before = new Dictionary<string, (string Frame, bool Planned)>(StringComparer.Ordinal);
        foreach (SeriesRowViewModel row in Rows)
        {
            string uid = row.Series.SeriesInstanceUid;
            bool isPlanned = planned.TryGetValue(uid, out SeriesPlan? seriesPlan);
            before[isPlanned ? seriesPlan!.NewSeriesInstanceUid ?? uid : uid] = (row.Series.FrameOfReferenceUid, isPlanned);
        }

        CancellationToken token = BeginOperation(out int operation);
        RunProgress = 0;
        RunStatus = "Starting…";
        RunDetail = $"0 of {Count(plan.FileCount, "file")}";
        Stage = Stage.Running;
        try
        {
            var progress = new Progress<UnlinkProgress>(p =>
            {
                if (operation != _operation || !IsRunning || _rescanning)
                {
                    return;
                }
                RunProgress = Fraction(p.FilesDone, p.FilesTotal);
                if (p.CurrentSeriesDescription.Length > 0 && !_isCancelling)
                {
                    RunStatus = p.CurrentSeriesDescription;
                }
                RunDetail = $"{p.FilesDone:N0} of {Count(p.FilesTotal, "file")} · series {Math.Min(p.SeriesDone + 1, p.SeriesTotal)} of {p.SeriesTotal}";
            });
            UnlinkResult result = await Task.Run(() => UnlinkRunner.Run(plan, progress, token));

            _rescanning = true;
            RunStatus = "Checking the result…";
            RunDetail = "";
            ScanResult? rescan = null;
            string? rescanError = null;
            try
            {
                rescan = await Task.Run(() => DicomScanner.Scan(folder));
            }
            catch (Exception ex)
            {
                rescanError = ex.Message;
            }

            ApplyResult(result, plan, rescan, before);
            Stage = Stage.Done;
            if (rescanError is not null)
            {
                ShowError($"The files were rewritten, but the folder could not be read again to show the result: {rescanError}");
            }
        }
        catch (Exception ex)
        {
            Stage = Stage.Ready;
            ShowError($"The run stopped unexpectedly: {ex.Message}");
        }
        finally
        {
            _rescanning = false;
        }
    }

    private void ApplyResult(
        UnlinkResult result, UnlinkPlan plan, ScanResult? rescan,
        Dictionary<string, (string Frame, bool Planned)> before)
    {
        LastResult = result;
        _showingResult = true;

        if (rescan is not null)
        {
            _scan = rescan;
            var frames = new List<(string Before, string After)>(rescan.Series.Count);
            Rows.Clear();
            foreach (DicomSeriesInfo series in rescan.Series)
            {
                bool known = before.TryGetValue(series.SeriesInstanceUid, out (string Frame, bool Planned) was);
                var row = new SeriesRowViewModel(series)
                {
                    Note = !series.IsSupported ? "left unchanged" : known && was.Planned ? "" : "not selected",
                };
                row.SetIncluded(known && was.Planned);
                Rows.Add(row);
                frames.Add((known ? was.Frame : "", series.FrameOfReferenceUid));
            }
            ApplyBadges(frames);
            DescribeScan(rescan, afterRun: true);
        }

        Failures.Clear();
        foreach (FileFailure failure in result.Failures)
        {
            Failures.Add(new FailureViewModel(Path.GetFileName(failure.Path), failure.Path, failure.Error));
        }

        ResultChanges.Clear();
        if (result.FilesChanged > 0)
        {
            if (plan.Series.Any(s => s.NewFrameOfReferenceUid is not null))
            {
                ResultChanges.Add("New Frame of Reference UID");
            }
            if (plan.Series.Any(s => s.NewSeriesInstanceUid is not null))
            {
                ResultChanges.Add("New Series Instance UID");
            }
            if (plan.Series.Any(s => s.NewSopInstanceUids))
            {
                ResultChanges.Add("New SOP Instance UID for every image");
            }
        }

        string duration = FormatDuration(result.Duration);
        if (result.Cancelled)
        {
            ResultIsSuccess = false;
            ResultTitle = "Stopped before finishing";
            ResultDetail = $"{result.SeriesChanged:N0} of {plan.Series.Count:N0} series ({result.FilesChanged:N0} of {Count(plan.FileCount, "file")}) rewritten in {duration}.";
            ResultNote = "The series already rewritten are complete; the remaining series were not touched. Unlink the folder again to do the rest.";
        }
        else if (result.Failures.Count > 0)
        {
            ResultIsSuccess = false;
            ResultTitle = "Finished with problems";
            ResultDetail = $"{result.FilesChanged:N0} of {Count(plan.FileCount, "file")} in {result.SeriesChanged:N0} series rewritten in {duration}.";
            ResultNote = "The files listed below were left exactly as they were.";
        }
        else
        {
            ResultIsSuccess = true;
            ResultTitle = "Unlinking complete";
            ResultDetail = $"{result.SeriesChanged:N0} series · {Count(result.FilesChanged, "file")} rewritten in {duration}.";
            ResultNote = "The series now have new IDs and can be imported as independent copies.";
        }
    }

    // ================================================================ helpers

    private CancellationToken BeginOperation(out int operation)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        operation = ++_operation;
        return _cts.Token;
    }

    private void ClearData()
    {
        _scan = null;
        _showingResult = false;
        Rows.Clear();
        Warnings.Clear();
        Failures.Clear();
        ResultChanges.Clear();
        LastResult = null;
        SummaryText = "";
        SourcePath = "";
        FolderPath = "";
        SourceNote = "";
        NetworkHint = "";
        LoadStatus = "";
        LoadDetail = "";
        LoadProgress = 0;
        IsLoadIndeterminate = true;
        PlannedSeries = 0;
        PlannedFiles = 0;
        RunDisabledReason = "";
        UpdateToggles();
    }

    /// <summary>Puts a problem in front of the user instead of letting it take the app down.</summary>
    public void ShowError(string message, bool canRetry = false)
    {
        ErrorMessage = message;
        CanRetry = canRetry && _lastPath is not null;
    }

    private void ClearError()
    {
        ErrorMessage = "";
        CanRetry = false;
    }

    /// <summary>A cancelled or failed extraction must not leave a half-filled folder that would be reused next time.</summary>
    private static async Task DeletePartialExtractAsync(string? folder)
    {
        if (folder is null)
        {
            return;
        }
        await Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch
            {
                // Best effort: whatever is left is reported as "already exists" next time.
            }
        });
    }

    internal static bool IsNetworkPath(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return true;
            }
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    private static double Fraction(int done, int total) => total <= 0 ? 0 : Math.Clamp((double)done / total, 0, 1);

    private static string Count(int number, string noun) => number == 1 ? $"1 {noun}" : $"{number:N0} {noun}s";

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds < 60
            ? $"{duration.TotalSeconds:0.0} s"
            : $"{(int)duration.TotalMinutes} min {duration.Seconds} s";
}
