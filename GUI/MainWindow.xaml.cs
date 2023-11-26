using System.IO;
using System.Collections.Generic;
using UnzipUnlinkGUI.Windows;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.ComponentModel;
using Unzip_Unlink;
using NewFrameOfReferenceClass;
using System.Threading.Tasks;
using itk.simple;
using FellowOakDicom;
using System.Linq;

namespace UnzipUnlinkGUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private float progressCounterfiles;
        public float ProgressCounterfiles
        {
            get { return progressCounterfiles; }
            set
            {
                progressCounterfiles = value;
                OnPropertyChanged("ProgressCounterfiles");
            }
        }
        private float progressCounterfolders;
        public float ProgressCounterfolders
        {
            get { return progressCounterfolders; }
            set
            {
                progressCounterfolders = value;
                OnPropertyChanged("ProgressCounterfolders");
            }
        }
        private bool file_selected;
        private string progressText;
        public string ProgressText
        {
            get { return progressText; }
            set
            {
                progressText = value;
                OnPropertyChanged("ProgressText");
            }
        }
        private string labelText;
        public string LabelText
        {
            get { return labelText; }
            set
            {
                labelText = value;
                OnPropertyChanged("LabelText");
            }
        }
        private string recommendText;
        public string RecommendText
        {
            get { return recommendText; }
            set
            {
                recommendText = value;
                OnPropertyChanged("RecommendText");
            }
        }
        protected virtual void OnPropertyChanged(string property)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(property));
        }
        public event PropertyChangedEventHandler PropertyChanged;
        string zip_file;
        private List<string> modalities;
        private List<DicomTag> tags;
        public MainWindow()
        {
            InitializeComponent();
            modalities = new List<string>();
            tags = new List<DicomTag>();
            HideText();
            LabelText = "Status:";
            Binding StatusBinding = new Binding("LabelText");
            StatusBinding.Source = this;
            StatusLabel.SetBinding(Label.ContentProperty, StatusBinding);


            Binding RecommendBinding = new Binding("RecommendText");
            RecommendBinding.Source = this;
            RecommendLabel.SetBinding(Label.ContentProperty, RecommendBinding);

            ProgressText = "";
            Binding ProgressBinding = new Binding("ProgressText");
            ProgressBinding.Source = this;
            ProgressLabel.SetBinding(Label.ContentProperty, ProgressBinding);

            ProgressCounterfiles = 0;
            Binding ProgressBindingfiles = new Binding("ProgressCounterfiles");
            ProgressBindingfiles.Source = this;
            FilesProgressBar.SetBinding(ProgressBar.ValueProperty, ProgressBindingfiles);

            ProgressCounterfolders = 0;
            Binding ProgressBindingfolders = new Binding("ProgressCounterfolders");
            ProgressBindingfolders.Source = this;
            FolderProgressBar.SetBinding(ProgressBar.ValueProperty, ProgressBindingfolders);
        }
        private void HideText()
        {
            FilesProgressBar.Visibility = Visibility.Hidden;
            FolderProgressBar.Visibility = Visibility.Hidden;
            FilesTextBlock.Visibility = Visibility.Hidden;
            ProgressText = "";
            ProgressCounterfiles = 0;
            ProgressCounterfolders = 0;
        }
        private void DisableButtons()
        {
            UnzipandUnlinkButton.IsEnabled = false;
            UnzipButton.IsEnabled = false;
            UnlinkButton.IsEnabled = false;
            HideText();
        }
        private void EnableButtons()
        {
            RecommendText = "";
            UnzipandUnlinkButton.IsEnabled = true;
            UnzipButton.IsEnabled = true;
            UnlinkButton.IsEnabled = true;
        }

        public void ReWriteFrameOfReference(string selected_folder, List<DicomTag> tags, List<string> modalities)
        {
            FrameOfReferenceClass dicomParser = new FrameOfReferenceClass();
            LabelText = "Characterizing directory...Please wait";
            ProgressText = "Overall Progress";
            dicomParser.Characterize_Directory(selected_folder);
            List<string> mr_series_uids = new List<string>();
            foreach (string dicom_series_instance_uid in dicomParser.dicom_series_instance_uids)
            {
                string modality;
                VectorString dicom_names = dicomParser.series_instance_uids_dict[dicom_series_instance_uid];
                DicomUID uid = dicomParser.series_instance_dict[dicom_series_instance_uid];
                dicomParser.image_reader.SetFileName(dicom_names[0]);
                try
                {
                    dicomParser.image_reader.ReadImageInformation();
                    modality = dicomParser.image_reader.GetMetaData("0008|0060");
                }
                catch
                {
                    modality = "null";
                    continue;
                }
                if (modalities.Contains(modality.ToLower()))
                {
                    mr_series_uids.Add(dicom_series_instance_uid);
                }
            }
            LabelText = "Changing!";
            float folder_counter = 0;
            float total_folders = mr_series_uids.Count;
            foreach (string dicom_series_instance_uid in mr_series_uids)
            {
                folder_counter++;
                ProgressCounterfolders = (folder_counter + 1) / total_folders * 100;
                VectorString dicom_names = dicomParser.series_instance_uids_dict[dicom_series_instance_uid];
                float file_counter = 0;
                float total_files = dicom_names.Count;
                List<DicomUID> new_uids = new List<DicomUID>();
                foreach (DicomTag tag in tags)
                {
                    new_uids.Add(DicomUIDGenerator.GenerateDerivedFromUUID());
                }
                Parallel.ForEach(dicom_names, dicom_file =>
                {
                    file_counter++;
                    ProgressCounterfiles = (file_counter + 1) / total_files * 100;
                    try
                    {
                        var file = DicomFile.Open(dicom_file, FileReadOption.ReadAll);
                        for (int i = 0; i < tags.Count; i++)
                        {
                            file.Dataset.AddOrUpdate(tags[i], new_uids[i]);
                        }
                        file.Save(dicom_file);
                    }
                    catch
                    {
                    }
                });
            }
        }
        public void CheckNetwork(string path)
        {
            DriveInfo info = new DriveInfo(Path.GetPathRoot(path));
            if (info.DriveType == DriveType.Network)
            {
                RecommendText = "Highly recommend copying this locally to speed up process!";
            }
        }
        public async Task Unzip(string zip_file)
        {
            await Task.Run(() =>
            {
                LabelText = $"Unzipping: {Path.GetFileName(zip_file)}!";
                string zip_directory = Path.GetDirectoryName(zip_file);
                UnzipUtils.UnzipFile(zip_file, zip_directory);
                LabelText = $"Finished unzipping: {Path.GetFileName(zip_file)}!";
            });
        }

        public async Task Unlink(string selected_folder, List<DicomTag> tags, List<string> modalities)
        {
            await Task.Run(() =>
            {
                LabelText = "Unlinking files";
                ReWriteFrameOfReference(selected_folder, tags, modalities);
                LabelText = "Completed!";
            });
        }
        public async void UnzipButton_Click(object sender, RoutedEventArgs e)
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
            if (!file_selected)
            {
                EnableButtons();
                return;
            }
            zip_file = dialog.FileName;
            CheckNetwork(zip_file);
            await Unzip(zip_file);
            EnableButtons();
        }

        public async void UnzipandUnlinkButton_Click(object sender, RoutedEventArgs e)
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
                CheckNetwork(zip_file);
                string file_name = Path.GetFileName(zip_file);
                string base_directory = Path.GetDirectoryName(dialog.FileName);
                LabelText = $"Unzipping: {file_name}";
                await Unzip(zip_file);
                LabelText = $"Unlinking MR";
                string selected_folder = Path.Combine(base_directory, file_name.Substring(0, file_name.Length - 4));
                LabelText = "Checking folder";
                bool run = UnlinkUtils.WatchFolder(selected_folder);
                if (run)
                {
                    FolderProgressBar.Visibility = Visibility.Visible;
                    FilesProgressBar.Visibility = Visibility.Visible;
                    FilesTextBlock.Visibility = Visibility.Visible;
                    await Unlink(selected_folder, tags, modalities);
                }
            }
            EnableButtons();
        }

        public async void UnlinkButton_Click(object sender, RoutedEventArgs e)
        {
            DisableButtons();
            LabelText = "";
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
                CheckNetwork(selected_folder);
                LabelText = "Checking folder";
                bool run = UnlinkUtils.WatchFolder(selected_folder);
                if (run)
                {
                    FolderProgressBar.Visibility = Visibility.Visible;
                    FilesProgressBar.Visibility = Visibility.Visible;
                    FilesTextBlock.Visibility = Visibility.Visible;
                    await Unlink(selected_folder, tags, modalities);
                }
                else
                {
                    LabelText = "Files changed while watching... please try again";
                }
            }
            EnableButtons();
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            AboutPage about_page = new AboutPage();
            about_page.Show();
        }
        private void add_or_remove_tag(DicomTag tag, bool add)
        {
            if (add)
            {
                if (!tags.Contains(tag))
                {
                    tags.Add(tag);
                }
            }
            else
            {
                if (tags.Contains(tag))
                {
                    tags.Remove(tag);
                }
            }
        }
        private void add_or_remove_modality(string modality, bool add)
        {
            if (add)
            {
                if (!modalities.Contains(modality))
                {
                    modalities.Add(modality);
                }
            }
            else
            {
                if (modalities.Contains(modality))
                {
                    modalities.Remove(modality);
                }
            }
        }
        private void ThingCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            add_or_remove_tag(DicomTag.FrameOfReferenceUID, (bool)FoR_CheckBox.IsChecked);
            add_or_remove_tag(DicomTag.SeriesInstanceUID, (bool)SeriesUID_CheckBox.IsChecked);
            add_or_remove_tag(DicomTag.StudyInstanceUID, (bool)StudyUID_CheckBox.IsChecked);
        }
        private void ModalityCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            add_or_remove_modality("ct", (bool)CT_CheckBox.IsChecked);
            add_or_remove_modality("mr", (bool)MR_CheckBox.IsChecked);
            add_or_remove_modality("pt", (bool)PET_CheckBox.IsChecked);
        }
    }
}
