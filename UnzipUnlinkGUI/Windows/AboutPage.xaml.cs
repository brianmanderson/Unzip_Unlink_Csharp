using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace UnzipUnlinkGUI.Windows;

public partial class AboutPage : Window
{
    public AboutPage()
    {
        InitializeComponent();
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"Version {version.ToString(3)}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Opens one of the fixed links above in the default browser / mail program.</summary>
    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (e.Uri.Scheme != Uri.UriSchemeHttps && e.Uri.Scheme != Uri.UriSchemeMailto)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The link could not be opened:\n{e.Uri.AbsoluteUri}\n\n{ex.Message}", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
