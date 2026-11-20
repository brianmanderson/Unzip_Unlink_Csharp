using System.Windows;
using UnzipUnlinkGUI.Diagnostics;
using UnzipUnlinkGUI.Services;
using UnzipUnlinkGUI.ViewModels;

namespace UnzipUnlinkGUI;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Nothing may take the app down: whatever goes wrong is shown in the window instead.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            _viewModel?.ShowError($"Something went wrong: {args.Exception.Message}");
        };
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        HeadlessOptions? headless = HeadlessOptions.TryParse(e.Args);
        if (headless?.Theme is string theme)
        {
#pragma warning disable WPF0001 // ThemeMode is still marked experimental in WPF
            ThemeMode = theme == "dark" ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        }

        _viewModel = new MainViewModel(new UserDialogs());
        var window = new MainWindow(_viewModel);
        MainWindow = window;

        if (headless is null)
        {
            window.Show();
            // "Open with" / dropping a folder or .zip on the .exe.
            if (e.Args.Length == 1 && !e.Args[0].StartsWith("--", StringComparison.Ordinal))
            {
                await _viewModel.LoadAsync(e.Args[0]);
            }
            return;
        }

        // Hidden mode used for screenshots and end-to-end checks; see Diagnostics/HeadlessRunner.cs.
        Shutdown(await HeadlessRunner.RunAsync(window, _viewModel, headless));
    }
}
