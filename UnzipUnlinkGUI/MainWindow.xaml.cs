using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UnzipUnlinkGUI.ViewModels;
using UnzipUnlinkGUI.Windows;

namespace UnzipUnlinkGUI;

/// <summary>View only: drag-and-drop, keyboard niceties and opening the About dialog. The logic is in <see cref="MainViewModel"/>.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutPage { Owner = this }.ShowDialog();

    // ------------------------------------------------------------ drag and drop

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool acceptable = _viewModel.CanChooseData && DroppedPath(e) is not null;
        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        _viewModel.IsDragOver = acceptable;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => _viewModel.IsDragOver = false;

    private void Window_Drop(object sender, DragEventArgs e)
    {
        _viewModel.IsDragOver = false;
        e.Handled = true;
        if (_viewModel.CanChooseData && DroppedPath(e) is string path)
        {
            _ = _viewModel.LoadAsync(path);
        }
    }

    /// <summary>The single folder or .zip being dragged, or null for anything else.</summary>
    private static string? DroppedPath(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths)
        {
            return null;
        }
        string path = paths[0];
        bool isZip = string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
        return isZip || Directory.Exists(path) ? path : null;
    }

    // ------------------------------------------------------------ keyboard

    /// <summary>Space ticks / unticks every selected series, like a list of checkboxes should.</summary>
    private void SeriesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.OriginalSource is CheckBox)
        {
            return;
        }
        List<SeriesRowViewModel> rows = SeriesGrid.SelectedItems.OfType<SeriesRowViewModel>().Where(r => r.CanToggle).ToList();
        if (rows.Count == 0)
        {
            return;
        }
        bool include = !rows.All(r => r.IsIncluded);
        foreach (SeriesRowViewModel row in rows)
        {
            row.IsIncluded = include;
        }
        e.Handled = true;
    }

    private void ConfirmOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(() => ConfirmButton.Focus());
        }
    }
}
