using System.Windows.Media;
using UnzipUnlink.Core;

namespace UnzipUnlinkGUI.ViewModels;

/// <summary>One line of the review grid.</summary>
public sealed class SeriesRowViewModel : ObservableObject
{
    private static readonly Brush CtBrush = LinkGroups.Frozen("#2563EB");
    private static readonly Brush MrBrush = LinkGroups.Frozen("#7C3AED");
    private static readonly Brush PetBrush = LinkGroups.Frozen("#D97706");
    private static readonly Brush OtherBrush = LinkGroups.Frozen("#64748B");

    private readonly Action<SeriesRowViewModel>? _includedChanged;
    private bool _isIncluded;
    private bool _canToggle;
    private string _note = "";
    private LinkBadge _firstBadge = LinkBadge.None;
    private LinkBadge _secondBadge = LinkBadge.None;

    public SeriesRowViewModel(DicomSeriesInfo series, Action<SeriesRowViewModel>? includedChanged = null)
    {
        Series = series;
        _includedChanged = includedChanged;
    }

    public DicomSeriesInfo Series { get; }

    public bool IsSupported => Series.IsSupported;
    public string Modality => Series.Modality;
    public string ModalityLabel => ModalityDisplayName(Series.Modality);
    public Brush ModalityBrush => BrushFor(Series.Modality);
    public string SeriesNumber => Series.SeriesNumber;
    public string Description => Series.SeriesDescription.Length > 0 ? Series.SeriesDescription : "(no description)";
    public int ImageCount => Series.Files.Count;
    public string ImageCountText => $"{Series.Files.Count:N0}";

    /// <summary>Ticked rows are the ones the run will rewrite.</summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (!IsSupported)
            {
                value = false;
            }
            if (Set(ref _isIncluded, value))
            {
                _includedChanged?.Invoke(this);
            }
        }
    }

    /// <summary>Changes the tick without telling the owner (used while the owner updates many rows at once).</summary>
    internal void SetIncluded(bool value)
    {
        if (_isIncluded != (value && IsSupported))
        {
            _isIncluded = value && IsSupported;
            OnPropertyChanged(nameof(IsIncluded));
        }
    }

    public bool CanToggle
    {
        get => _canToggle;
        internal set => Set(ref _canToggle, value && IsSupported);
    }

    /// <summary>Short remark shown after the description, e.g. "left unchanged".</summary>
    public string Note
    {
        get => _note;
        internal set
        {
            if (Set(ref _note, value))
            {
                OnPropertyChanged(nameof(NoteSuffix));
            }
        }
    }

    public string NoteSuffix => _note.Length > 0 ? $"  ·  {_note}" : "";

    public LinkBadge FirstBadge
    {
        get => _firstBadge;
        internal set => Set(ref _firstBadge, value);
    }

    public LinkBadge SecondBadge
    {
        get => _secondBadge;
        internal set => Set(ref _secondBadge, value);
    }

    public static string ModalityDisplayName(string modality) => modality switch
    {
        "PT" => "PET",
        "" => "?",
        _ => modality,
    };

    public static Brush BrushFor(string modality) => modality switch
    {
        "CT" => CtBrush,
        "MR" => MrBrush,
        "PT" or "PET" => PetBrush,
        _ => OtherBrush,
    };
}

/// <summary>One of the CT / MR / PET switches under "Which images".</summary>
public sealed class ModalityToggleViewModel : ObservableObject
{
    private readonly Action<ModalityToggleViewModel, bool> _requested;
    private int _total;
    private int _selected;

    public ModalityToggleViewModel(string code, Action<ModalityToggleViewModel, bool> requested)
    {
        Code = code;
        _requested = requested;
    }

    /// <summary>DICOM modality code: CT, MR or PT.</summary>
    public string Code { get; }
    public string ModalityLabel => SeriesRowViewModel.ModalityDisplayName(Code);
    public Brush ModalityBrush => SeriesRowViewModel.BrushFor(Code);

    public int Total => _total;
    public int Selected => _selected;
    public bool IsAvailable => _total > 0;
    /// <summary>True when at least one series of this modality is ticked.</summary>
    public bool IsOn => _selected > 0;

    /// <summary>All ticked = true, none = false, some = indeterminate.</summary>
    public bool? IsChecked
    {
        get => _selected == 0 ? false : _selected == _total ? true : null;
        set
        {
            if (value is bool wanted)
            {
                _requested(this, wanted);
            }
        }
    }

    public string CountText =>
        _total == 0 ? "none found"
        : _selected == _total || _selected == 0 ? $"{_total} series"
        : $"{_selected} of {_total} series";

    internal void Update(int selected, int total)
    {
        _selected = selected;
        _total = total;
        RaiseAllPropertiesChanged();
    }
}

public sealed record FailureViewModel(string FileName, string Path, string Error);
