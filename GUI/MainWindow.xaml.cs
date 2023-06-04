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
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private string labelText;
        private bool file_selected;
        public string LabelText
        {
            get { return labelText; }
            set
            {
                labelText = value;
                OnPropertyChanged("LabelText");
            }
        }
        protected virtual void OnPropertyChanged(string property)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(property));
        }
        public event PropertyChangedEventHandler PropertyChanged;
        string zip_file;
        public MainWindow()
        {
            InitializeComponent();
            LabelText = "Status:";
            Binding StatusBinding = new Binding("LabelText");
            StatusBinding.Source = this;
            StatusLabel.SetBinding(Label.ContentProperty, StatusBinding);
        }
        private void UnzipButton_Click(object sender, RoutedEventArgs e)
        {
            CommonOpenFileDialog dialog = new CommonOpenFileDialog("*.zip");
            dialog.InitialDirectory = ".";
            dialog.IsFolderPicker = false;
            file_selected = false;
            LabelText = "Unzipping...";
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                file_selected = true;
                zip_file = dialog.FileName;
                string zip_directory = Path.GetDirectoryName(zip_file);
                LabelText = $"Unzipping: {Path.GetFileName(zip_file)}!";
                UnzipUtils.UnzipFile(zip_file, zip_directory);
                LabelText = $"Finished unzipping: {Path.GetFileName(zip_file)}!";
            }
        }

        private void UnzipandUnlinkButton_Click(object sender, RoutedEventArgs e)
        {
            CommonOpenFileDialog dialog = new CommonOpenFileDialog("*.zip");
            dialog.InitialDirectory = ".";
            dialog.IsFolderPicker = false;
            file_selected = false;
            LabelText = "Unzipping...";
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                file_selected = true;
                zip_file = dialog.FileName;
                string file_name = Path.GetFileName(zip_file);
                string base_directory = Path.GetDirectoryName(dialog.FileName);
                LabelText = $"Unzipping: {file_name}";
                Unzipper.UnzipFile(zip_file);
                LabelText = $"Unlinking MR";
                string selected_folder = Path.Combine(base_directory, file_name.Substring(0, file_name.Length - 4));
                bool run = UnlinkUtils.WatchFolder(selected_folder);
                if (run)
                {
                    FrameOfReferenceClass dicomParser = new FrameOfReferenceClass();
                    LabelText = "Unlinking files";
                    dicomParser.Characterize_Directory(selected_folder);
                    dicomParser.ReWriteFrameOfReference();
                    LabelText = "Completed!";
                }
            }
        }

        private void UnlinkButton_Click(object sender, RoutedEventArgs e)
        {
            CommonOpenFileDialog dialog = new CommonOpenFileDialog("*.zip");
            dialog.InitialDirectory = ".";
            dialog.IsFolderPicker = true;
            StatusLabel.Content = "";
            LabelText = "Unlinking...";
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                string selected_folder = dialog.FileName;
                bool run = UnlinkUtils.WatchFolder(selected_folder);
                if (run)
                {
                    FrameOfReferenceClass dicomParser = new FrameOfReferenceClass();
                    LabelText = "Unlinking files";
                    dicomParser.Characterize_Directory(selected_folder);
                    dicomParser.ReWriteFrameOfReference();
                    LabelText = "Completed!";
                }
            }
        }
    }
}
