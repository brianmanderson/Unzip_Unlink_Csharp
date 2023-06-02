using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System;
using System.Windows;
using System.Windows.Data;
using System.Threading;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Controls;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Collections.ObjectModel;
using Unzip_Unlink;
using UnzipClass;
using NewFrameOfReferenceClass;

namespace GUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        bool file_selected;
        string zip_file;
        public MainWindow()
        {
            InitializeComponent();
        }

        private void UnzipButton_Click(object sender, RoutedEventArgs e)
        {
            CommonOpenFileDialog dialog = new CommonOpenFileDialog("*.zip");
            dialog.InitialDirectory = ".";
            dialog.IsFolderPicker = false;
            file_selected = false;
            UnzipLabel.Content = "";
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                zip_file = dialog.FileName;
                string zip_directory = Path.GetDirectoryName(zip_file);
                UnzipLabel.Content = $"Unzipping: {zip_file}";
                file_selected = true;
                UnzipUtils.UnzipFile(zip_file, zip_directory);
            }
        }

        private void UnzipandUnlinkButton_Click(object sender, RoutedEventArgs e)
        {
            CommonOpenFileDialog dialog = new CommonOpenFileDialog("*.zip");
            dialog.InitialDirectory = ".";
            dialog.IsFolderPicker = false;
            file_selected = false;
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                zip_file = dialog.FileName;
                string file_name = Path.GetFileName(zip_file);
                string base_directory = Path.GetDirectoryName(file_name);
                UnzipUnlinkLabel.Content = $"Unzipping: {file_name}";
                file_selected = true;
                Unzipper.UnzipFile(zip_file);
                UnzipUnlinkLabel.Content = $"Unlinking MR";
                string selected_folder = Path.Combine(base_directory, file_name.Substring(0, file_name.Length - 4));
                bool run = UnlinkUtils.WatchFolder(selected_folder);
                if (run)
                {
                    FrameOfReferenceClass dicomParser = new FrameOfReferenceClass();
                    UnzipUnlinkLabel.Content = "Unlinking files";
                    dicomParser.Characterize_Directory(selected_folder);
                    dicomParser.ReWriteFrameOfReference();
                    UnzipUnlinkLabel.Content = "Completed!";
                }
            }
        }

        private void UnlinkButton_Click(object sender, RoutedEventArgs e)
        {
            CommonOpenFileDialog dialog = new CommonOpenFileDialog("*.zip");
            dialog.InitialDirectory = ".";
            dialog.IsFolderPicker = true;
            UnzipLabel.Content = "";
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                string selected_folder = dialog.FileName;
                bool run = UnlinkUtils.WatchFolder(selected_folder);
                if (run)
                {
                    FrameOfReferenceClass dicomParser = new FrameOfReferenceClass();
                    UnlinkLabel.Content = "Unlinking files";
                    dicomParser.Characterize_Directory(selected_folder);
                    dicomParser.ReWriteFrameOfReference();
                    UnlinkLabel.Content = "Completed!";
                }
            }
        }
    }
}
