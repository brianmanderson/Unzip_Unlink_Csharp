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
using System.Threading;


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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
        public event PropertyChangedEventHandler PropertyChanged;
        string zip_file;
        private readonly List<string> modalities;
        private readonly List<DicomTag> tags;
        public bool is_network;
        public MainWindow()
        {
            InitializeComponent();
            modalities = new List<string>();
            tags = new List<DicomTag>();
            HideText();
            LabelText = "Status:";
            Binding StatusBinding = new Binding("LabelText")
            {
                Source = this
            };
            StatusLabel.SetBinding(Label.ContentProperty, StatusBinding);


            Binding RecommendBinding = new Binding("RecommendText")
            {
                Source = this
            };
            RecommendLabel.SetBinding(Label.ContentProperty, RecommendBinding);

            ProgressText = "";
            Binding ProgressBinding = new Binding("ProgressText")
            {
                Source = this
            };
            ProgressLabel.SetBinding(Label.ContentProperty, ProgressBinding);

            ProgressCounterfiles = 0;
            Binding ProgressBindingfiles = new Binding("ProgressCounterfiles")
            {
                Source = this
            };
            FilesProgressBar.SetBinding(ProgressBar.ValueProperty, ProgressBindingfiles);

            ProgressCounterfolders = 0;
            Binding ProgressBindingfolders = new Binding("ProgressCounterfolders")
            {
                Source = this
            };
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
            Dictionary<string, List<string>> series_instance_dict = new Dictionary<string, List<string>>();
            foreach (string dicom_series_instance_uid in dicomParser.dicom_series_instance_uids)
            {
                string modality, frame_of_reference;
                VectorString dicom_names = dicomParser.series_instance_uids_dict[dicom_series_instance_uid];
                DicomUID uid = dicomParser.series_instance_dict[dicom_series_instance_uid];
                dicomParser.image_reader.SetFileName(dicom_names[0]);
                frame_of_reference = "";
                modality = "null";
                try
                {
                    dicomParser.image_reader.ReadImageInformation();
                    modality = dicomParser.image_reader.GetMetaData("0008|0060");
                    frame_of_reference = dicomParser.image_reader.GetMetaData("0020|0052");
                }
                catch
                {
                    modality = "null";
                }
                if (modalities.Contains(modality.ToLower()))
                {
                    series_instance_dict.Add(dicom_series_instance_uid, new List<string>() { modality, frame_of_reference });
                }
            }
            LabelText = "Changing!";
            float folder_counter = 0;
            float total_folders = series_instance_dict.Count;
            Dictionary<string, List<DicomUID>> dicom_series_instance_dict = new Dictionary<string, List<DicomUID>>();
            List<DicomUID> new_uids;
            List<DicomUID> existing_frame_of_ref_uids = new List<DicomUID>();
            foreach (string dicom_series_instance_uid in series_instance_dict.Keys)
            {
                new_uids = new List<DicomUID>();
                for (int i = 0; i < tags.Count; i++)
                {
                    new_uids.Add(DicomUIDGenerator.GenerateDerivedFromUUID());
                }
                dicom_series_instance_dict.Add(dicom_series_instance_uid, new_uids);
            }
            if (modalities.Contains("ct*"))
            {
                if (tags.Contains(DicomTag.FrameOfReferenceUID))
                {
                    Dictionary<string, DicomUID> old_FoR_to_new_dict = new Dictionary<string, DicomUID>();
                    foreach (string dicom_series_instance_uid in series_instance_dict.Keys)
                    {
                        string modality = series_instance_dict[dicom_series_instance_uid][0];
                        if (modality == "CT")
                        {
                            string frame_of_reference = series_instance_dict[dicom_series_instance_uid][1];
                            if (!old_FoR_to_new_dict.ContainsKey(frame_of_reference))
                            {
                                DicomUID new_frame_of_referece = DicomUIDGenerator.GenerateDerivedFromUUID();
                                old_FoR_to_new_dict.Add(frame_of_reference, new_frame_of_referece);
                            }
                            new_uids = dicom_series_instance_dict[dicom_series_instance_uid];
                            new_uids[tags.IndexOf(DicomTag.FrameOfReferenceUID)] = old_FoR_to_new_dict[frame_of_reference];
                        }
                    }
                }
            }
            foreach (string dicom_series_instance_uid in series_instance_dict.Keys)
            {
                ProgressCounterfolders = (folder_counter + 1) / total_folders * 100;
                folder_counter++;
                VectorString dicom_names = dicomParser.series_instance_uids_dict[dicom_series_instance_uid];
                float file_counter = 0;
                float total_files = dicom_names.Count;
                new_uids = dicom_series_instance_dict[dicom_series_instance_uid];
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
                        using (StreamWriter outputFile = new StreamWriter(Path.Combine(".", $"Failed_{dicom_file}.txt")))
                        {
                            outputFile.WriteLine("Test");
                        }
                    }
                });
            }
        }
        public void CheckNetwork(string path)
        {
            DriveInfo info = new DriveInfo(Path.GetPathRoot(path));
            is_network = false;
            if (info.DriveType == DriveType.Network)
            {
                is_network = true;
                RecommendText = "Highly recommend copying this locally to speed up process";
            }
        }
        private static void CopyFilesRecursively(string sourcePath, string targetPath)
        {
            //Now Create all of the directories
            foreach (string dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourcePath, targetPath));
            }

            //Copy all the files & Replaces any files with the same name
            foreach (string newPath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
            {
                File.Copy(newPath, newPath.Replace(sourcePath, targetPath), true);
            }
        }
        public async Task Unzip(string zip_file)
        {
            string copy_location = Path.Combine(".", "LocalData");
            if (!Directory.Exists(copy_location))
            {
                Directory.CreateDirectory(copy_location);
            }
            foreach (string file in Directory.GetFiles(copy_location))
            {
                File.Delete(file);
            }
            File.Copy(zip_file, Path.Combine(copy_location, Path.GetFileName(zip_file)));
            await Task.Run(() =>
            {
                LabelText = $"Unzipping: {Path.GetFileName(zip_file)}!";
                UnzipUtils.UnzipFile(Path.Combine(copy_location, Path.GetFileName(zip_file)), copy_location);
                LabelText = $"Finished unzipping: {Path.GetFileName(zip_file)}!";
            });
            CopyFilesRecursively(copy_location, Path.GetDirectoryName(zip_file));
            Directory.Delete(copy_location, true);
        }

        public async Task Unlink(string selected_folder, List<DicomTag> tags, List<string> modalities)
        {
            string copy_location = Path.Combine(".", "LocalData");
            if (!Directory.Exists(copy_location))
            {
                Directory.CreateDirectory(copy_location);
            }
            foreach (string file in Directory.GetFiles(copy_location))
            {
                File.Delete(file);
            }
            CopyFilesRecursively(selected_folder, copy_location);
            await Task.Run(() =>
            {
                LabelText = "Unlinking files";
                ReWriteFrameOfReference(copy_location, tags, modalities);
                LabelText = "Completed!";
            });
            CopyFilesRecursively(copy_location, selected_folder);
            Directory.Delete(copy_location, true);
        }
        public async void UnzipButton_Click(object sender, RoutedEventArgs e)
        {
            DisableButtons();
            CommonOpenFileDialog dialog = new CommonOpenFileDialog("*.zip")
            {
                InitialDirectory = ".",
                IsFolderPicker = false
            };
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
            CommonOpenFileDialog dialog = new CommonOpenFileDialog("*.zip")
            {
                InitialDirectory = ".",
                IsFolderPicker = false
            };
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
            CommonOpenFileDialog dialog = new CommonOpenFileDialog("*.zip")
            {
                InitialDirectory = ".",
                IsFolderPicker = true
            };
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
        private void Add_or_Remove_tag(DicomTag tag, bool add)
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
        private void Add_or_Remove_modality(string modality, bool add)
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
        private void SOPInstanceCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            bool sop_instance_uid_bool = (bool)SOPInstanceUID_CheckBox.IsChecked;
            Add_or_Remove_tag(DicomTag.SOPInstanceUID, sop_instance_uid_bool);
        }
        private void ThingCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            bool for_UID_bool, series_uid_bool, ct_bool, ct_4d_bool, mr_bool, pet_bool;
            for_UID_bool = (bool)FoR_CheckBox.IsChecked;
            series_uid_bool = (bool)SeriesUID_CheckBox.IsChecked;
            Add_or_Remove_tag(DicomTag.FrameOfReferenceUID, for_UID_bool);


            Add_or_Remove_tag(DicomTag.SeriesInstanceUID, series_uid_bool);
            ct_bool = (bool)CT_CheckBox.IsChecked;
            Add_or_Remove_modality("ct", ct_bool);
            ct_4d_bool = (bool)CT4D_CheckBox.IsChecked;
            mr_bool = (bool)MR_CheckBox.IsChecked;
            pet_bool = (bool)PET_CheckBox.IsChecked;
            CT4D_CheckBox.IsEnabled = false;
            CT_CheckBox.IsEnabled = true;
            if (ct_bool)
            {
                CT4D_CheckBox.IsEnabled = true;
            }
            if (ct_4d_bool)
            {
                CT_CheckBox.IsEnabled = false;
            }
            Add_or_Remove_modality("ct*", ct_4d_bool);
            Add_or_Remove_modality("mr", mr_bool);
            Add_or_Remove_modality("pt", pet_bool);

            UnzipandUnlinkButton.IsEnabled = false;
            UnlinkButton.IsEnabled = false;
            if (for_UID_bool || series_uid_bool)
            {
                SOPInstanceUID_CheckBox.IsChecked = true;
                if (ct_bool || mr_bool || pet_bool)
                {
                    Add_or_Remove_tag(DicomTag.SOPInstanceUID, true);
                    UnzipandUnlinkButton.IsEnabled = true;
                    UnlinkButton.IsEnabled = true;
                }
            }
            else
            {
                SOPInstanceUID_CheckBox.IsChecked = false;
                Add_or_Remove_tag(DicomTag.SOPInstanceUID, false);
            }
        }
    }
}
