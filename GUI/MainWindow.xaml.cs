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
using System.Threading.Tasks;

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
        private void DisableButtons()
        {
            UnzipandUnlinkButton.IsEnabled = false;
            UnzipButton.IsEnabled = false;
            UnlinkButton.IsEnabled = false;
        }
        private void EnableButtons()
        {
            UnzipandUnlinkButton.IsEnabled = true;
            UnzipButton.IsEnabled = true;
            UnlinkButton.IsEnabled = true;
        }
        private async Task Unzip(string zip_file)
        {
            await Task.Run(() =>
            {
                LabelText = $"Unzipping: {Path.GetFileName(zip_file)}!";
                string zip_directory = Path.GetDirectoryName(zip_file);
                UnzipUtils.UnzipFile(zip_file, zip_directory);
                LabelText = $"Finished unzipping: {Path.GetFileName(zip_file)}!";
            });
        }
        private async Task Unlink(string selected_folder)
        {
            await Task.Run(() =>
            {
                LabelText = "Unlinking files";
                FrameOfReferenceClass dicomParser = new FrameOfReferenceClass();
                dicomParser.Characterize_Directory(selected_folder);
                dicomParser.ReWriteFrameOfReference();
                LabelText = "Completed!";
            });
        }
        private async void UnzipButton_Click(object sender, RoutedEventArgs e)
        {
            DisableButtons();
            CommonOpenFileDialog dialog = new CommonOpenFileDialog("*.zip");
            dialog.InitialDirectory = ".";
            dialog.IsFolderPicker = false;
            file_selected = false;
            ProgressBar.Visibility = Visibility.Hidden;
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                file_selected = true;
            }
            if (!file_selected)
            {
                EnableButtons();
                return;
            }
            zip_file = dialog.FileName;
            await Unzip(zip_file);
            EnableButtons();
        }

        private async void UnzipandUnlinkButton_Click(object sender, RoutedEventArgs e)
        {
            DisableButtons();
            CommonOpenFileDialog dialog = new CommonOpenFileDialog("*.zip");
            dialog.InitialDirectory = ".";
            dialog.IsFolderPicker = false;
            file_selected = false;
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                file_selected = true;
            }
            if (file_selected)
            {
                zip_file = dialog.FileName;
                string file_name = Path.GetFileName(zip_file);
                string base_directory = Path.GetDirectoryName(dialog.FileName);
                LabelText = $"Unzipping: {file_name}";
                await Unzip(zip_file);
                LabelText = $"Unlinking MR";
                string selected_folder = Path.Combine(base_directory, file_name.Substring(0, file_name.Length - 4));
                bool run = UnlinkUtils.WatchFolder(selected_folder);
                if (run)
                {
                    await Unlink(selected_folder);
                }
            }
            EnableButtons();
        }

        private async void UnlinkButton_Click(object sender, RoutedEventArgs e)
        {
            DisableButtons();
            CommonOpenFileDialog dialog = new CommonOpenFileDialog("*.zip");
            dialog.InitialDirectory = ".";
            dialog.IsFolderPicker = true;
            file_selected = false;
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                file_selected = true;
            }
            if (file_selected)
            {
                string selected_folder = dialog.FileName;
                bool run = UnlinkUtils.WatchFolder(selected_folder);
                if (run)
                {
                    await Unlink(selected_folder);
                }
                else
                {
                    LabelText = "Files changed while watching... please try again";
                }
            }
            EnableButtons();
        }
    }
}
