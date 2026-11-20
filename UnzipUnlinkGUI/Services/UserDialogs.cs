using System.Diagnostics;
using Microsoft.Win32;

namespace UnzipUnlinkGUI.Services;

/// <summary>The few things the view-model needs from the desktop, kept behind an interface so it can be tested.</summary>
public interface IUserDialogs
{
    string? PickFolder();
    string? PickZip();
    void OpenFolder(string folder);
}

public sealed class UserDialogs : IUserDialogs
{
    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder that holds the DICOM files",
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickZip()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a .zip of DICOM files",
            Filter = "Zip archives (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void OpenFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
        }
    }
}
