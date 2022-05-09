using System;
using FellowOakDicom;
using System.Threading;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using Unzip_And_Unlink.Services;
using itk.simple;
using DicomFolderParser;

namespace Unzip_And_Unlink
{
    class NewFrameOfReferenceClass
    {
        ImageSeriesReader series_reader;
        public NewFrameOfReferenceClass()
        {
            series_reader = new ImageSeriesReader();
        }
        public void UpdateFrameOfReference(string base_directory, string directory)
        {
            Dictionary<string, VectorString> series_instance_uids = new Dictionary<string, VectorString>();
            VectorString dicom_series_ids = ImageSeriesReader.GetGDCMSeriesIDs(directory);
            foreach (string dicom_series_id in dicom_series_ids)
            {
                VectorString dicom_names = ImageSeriesReader.GetGDCMSeriesFileNames(directory, dicom_series_id);
                string dicom_file = dicom_names[0];
                try
                {
                    var file = DicomFile.Open(dicom_file, FileReadOption.ReadAll);
                    if (file.Dataset.Contains(DicomTag.Modality))
                    {
                        if (!file.Dataset.GetString(DicomTag.Modality).ToLower().Contains("mr"))
                        {
                            series_instance_uids.Add(file.Dataset.GetString(DicomTag.SeriesInstanceUID), dicom_names);
                            continue;
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }
            foreach (string dicom_series_instance_uid in series_instance_uids.Keys)
            {
                DicomUID new_frame_UID = DicomUIDGenerator.GenerateDerivedFromUUID();
                foreach (string dicom_file in series_instance_uids[dicom_series_instance_uid])
                {
                    var file = DicomFile.Open(dicom_file, FileReadOption.ReadAll);
                    if (file.Dataset.Contains(DicomTag.FrameOfReferenceUID))
                    {
                        file.Dataset.AddOrUpdate(DicomTag.FrameOfReferenceUID, new_frame_UID);
                        file.Save(dicom_file);
                    }
                }
            }
        }
    }
    class Program
    {
        public bool folder_changed;
        public DicomParser dicomParser;
        static List<string> default_file_paths = new List<string> { @"\\ucsdhc-varis2\radonc$\00plans\Unzip_Unlink", @"\\ro-ariaimg-v\VA_DATA$\DICOM\Unzip_Unlink_DONOTDELETE" };
        ///

        static bool IsFileLocked(FileInfo file)
        {
            FileStream stream = null;
            try
            {
                stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                //the file is unavailable because it is:
                //still being written to
                //or being processed by another thread
                //or does not exist (has already been processed)
                return true;
            }
            finally
            {
                if (stream != null)
                    stream.Close();
            }

            //file is not locked
            return false;
        }
        static void UpdatedFrameOfReference(string base_directory, string directory)
        {
            string status_file, uid, overall_status;
            FolderWatcher folder_watcher_class = new FolderWatcher(directory);
            status_file = Path.Join(directory, "NewFrameOfRef.txt");
            overall_status = Path.Join(base_directory, $"UpdatingFrameOfRef_{Path.GetFileName(directory)}.txt");
            Thread.Sleep(3000);
            while (folder_watcher_class.Folder_Changed)
            {
                folder_watcher_class.Folder_Changed = false;
                Console.WriteLine("Waiting for files to be fully transferred...");
                Thread.Sleep(5000);
            }
            if (File.Exists(status_file))
            {
                return;
            }
            if (!File.Exists(overall_status))
            {
                FileStream fid_overallstatus = File.OpenWrite(overall_status);
                fid_overallstatus.Close();
            }
            DicomParser dicomParser = new DicomParser();
            Console.WriteLine("Parsing DICOM files...");
            dicomParser.ParseDirectory(directory);

            if (dicomParser.series_instance_uids.Count > 0)
            {
                Console.WriteLine("Updating frames of reference...");
            }
            if (File.Exists(overall_status))
            {
                File.Delete(overall_status);
            }
            if (had_files)
            {
                FileStream fid = File.OpenWrite(status_file);
                fid.Close();
                MoveFolder(moving_directory: Path.Join(base_directory, "Finished"), current_folder: directory);
                Console.WriteLine("Finished!");
                Console.WriteLine("Running...");
            }
        }
        static void NewFrameOfReference(string base_directory, string directory)
        {
            string[] all_files;
            bool had_files;
            string status_file, uid, overall_status;
            List<string> all_series_uids = new List<string>();
            List<DicomUID> frame_of_reference_uids = new List<DicomUID>();
            DicomUID new_frame_UID;
            FolderWatcher folder_watcher_class = new FolderWatcher(directory);
            status_file = Path.Join(directory, "NewFrameOfRef.txt");
            overall_status = Path.Join(base_directory, $"UpdatingFrameOfRef_{Path.GetFileName(directory)}.txt");
            Thread.Sleep(3000);
            while (folder_watcher_class.Folder_Changed)
            {
                folder_watcher_class.Folder_Changed = false;
                Console.WriteLine("Waiting for files to be fully transferred...");
                Thread.Sleep(5000);
            }
            if (File.Exists(status_file))
            {
                return;
            }
            if (!File.Exists(overall_status))
            {
                FileStream fid_overallstatus = File.OpenWrite(overall_status);
                fid_overallstatus.Close();
            }
            all_files = Directory.GetFiles(directory, "*.dcm");
            Console.WriteLine("Updating frames of reference...");
            had_files = false;
            foreach (string dicom_file in all_files)
            {
                if (dicom_file.EndsWith(".dcm"))
                {
                    had_files = true;
                    try
                    {
                        var file = DicomFile.Open(dicom_file, FileReadOption.ReadAll);
                        if (file.Dataset.Contains(DicomTag.Modality))
                        {
                            if (!file.Dataset.GetString(DicomTag.Modality).ToLower().Contains("mr"))
                            {
                                continue;
                            }
                        }
                        if (file.Dataset.Contains(DicomTag.FrameOfReferenceUID))
                        {
                            string series_uid = file.Dataset.GetString(DicomTag.SeriesInstanceUID);
                            if (!all_series_uids.Contains(series_uid))
                            {
                                all_series_uids.Add(series_uid);
                                frame_of_reference_uids.Add(DicomUIDGenerator.GenerateDerivedFromUUID());
                            }
                            new_frame_UID = frame_of_reference_uids[all_series_uids.IndexOf(series_uid)];
                            file.Dataset.AddOrUpdate(DicomTag.FrameOfReferenceUID, new_frame_UID);
                            file.Save(dicom_file);
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
            if (File.Exists(overall_status))
            {
                File.Delete(overall_status);
            }
            if (had_files)
            {
                FileStream fid = File.OpenWrite(status_file);
                fid.Close();
                MoveFolder(moving_directory: Path.Join(base_directory, "Finished"), current_folder: directory);
                Console.WriteLine("Finished!");
                Console.WriteLine("Running...");
            }
        }
        static void NewFrameOfReferenceDirectory(string base_directory)
        {
            string[] dicom_files;
            string status_file;
            string[] all_directories = Directory.GetDirectories(base_directory, "*", SearchOption.AllDirectories);
            foreach (string directory in all_directories)
            {
                if (directory.Contains("Finished"))
                {
                    continue;
                }
                dicom_files = Directory.GetFiles(directory, "*.dcm");
                if (dicom_files.Length > 0)
                {
                    status_file = Path.Join(directory, "NewFrameOfRef.txt");
                    if (File.Exists(status_file))
                    {
                        MoveFolder(moving_directory: Path.Join(base_directory, "Finished"), current_folder: directory);
                        continue;
                    }
                    NewFrameOfReference(base_directory, directory);
                }
            }

        }
        static void RenameFolder(string unzipped_file_directory)
        {
            string[] dicom_files = Directory.GetFiles(unzipped_file_directory, "*.dcm");
            string base_directory = Path.GetFullPath(Path.Join(unzipped_file_directory, ".."));
            foreach (string dicom_file in dicom_files)
            {
                try
                {
                    var file = DicomFile.Open(dicom_file);
                    string patient_name = file.Dataset.GetString(DicomTag.PatientName).Replace('^', '_');
                    try
                    {
                        Directory.Move(unzipped_file_directory, Path.Join(base_directory, patient_name));
                    }
                    catch
                    {
                        continue;
                    }
                    Console.WriteLine("Finished!");
                    break;
                }
                catch
                {
                    continue;
                }
            }
        }
        static void MoveFolder(string moving_directory, string current_folder)
        {
            string folder_name = Path.GetFileName(current_folder);
            if (!Directory.Exists(moving_directory))
            {
                Directory.CreateDirectory(moving_directory);
            }
            Directory.Move(current_folder, Path.Combine(moving_directory, folder_name));
        }
        static void UnzipFiles(string zip_file_directory)
        {
            string[] all_files = Directory.GetFiles(zip_file_directory, "*.zip", SearchOption.AllDirectories);
            string overall_status;
            foreach (string zip_file in all_files)
            {
                FileInfo zip_file_info = new FileInfo(zip_file);
                Thread.Sleep(3000);
                bool move_on = false;
                int tries = 0;
                Thread.Sleep(3000);
                while (IsFileLocked(zip_file_info))
                {
                    Console.WriteLine("Waiting for file to be fully transferred...");
                    tries += 1;
                    Thread.Sleep(3000);
                    if (tries > 2)
                    {
                        move_on = true;
                        break;
                    }
                }
                if (move_on)
                {
                    Console.WriteLine("Taking too long, will come back and try again...");
                    continue;
                }
                string file_name = Path.GetFileName(zip_file);
                string output_dir = Path.Join(Path.GetDirectoryName(zip_file), file_name.Substring(0, file_name.Length - 4));
                overall_status = Path.Join(zip_file_directory, $"ExtractingZip.txt");
                if (!File.Exists(overall_status))
                {
                    FileStream fid_overallstatus = File.OpenWrite(overall_status);
                    fid_overallstatus.Close();
                }
                if (!Directory.Exists(output_dir))
                {
                    Directory.CreateDirectory(output_dir);
                    Console.WriteLine("Extracting...");
                    ZipFile.ExtractToDirectory(zip_file, output_dir);
                    Console.WriteLine("Renaming Folder...");
                    RenameFolder(output_dir);
                    File.Delete(zip_file);
                }
                else
                {
                    Console.WriteLine("Renaming Folder...");
                    RenameFolder(output_dir);
                    File.Delete(zip_file);
                }
                if (File.Exists(overall_status))
                {
                    File.Delete(overall_status);
                }
                Console.WriteLine("Running...");
                Thread.Sleep(3000);
            }
        }
        static void CheckDownPath(string file_path)
        {
            if (Directory.Exists(file_path))
            {
                UnzipFiles(file_path);
                NewFrameOfReferenceDirectory(file_path);
            }
        }
        static void Main(string[] args)
        {
            Console.WriteLine("Running...");
            while (true)
            {
                List<string> file_paths = new List<string> { };
                foreach (string file_path in default_file_paths)
                {
                    file_paths.Add(file_path);
                }

                string file_paths_file = Path.Join(".", $"FilePaths.txt");
                if (!File.Exists(file_paths_file))
                {
                    try
                    {
                        StreamWriter fid = new(file_paths_file);
                        foreach (string file_path in default_file_paths)
                        {
                            fid.WriteLine($"{file_path}");
                        }
                        fid.Close();
                    }
                    catch
                    {
                        int x = 5;
                    }
                }
                else
                {
                    try
                    {
                        string all_file_paths = File.ReadAllText(file_paths_file);
                        foreach (string file_path in all_file_paths.Split("\r\n"))
                        {
                            if (!file_paths.Contains(file_path))
                            {
                                file_paths.Add(file_path);
                            }
                        }
                    }
                    catch
                    {
                        Console.WriteLine("Couldn't read the FilePaths.txt file...");
                        Thread.Sleep(3000);
                    }
                }
                // First lets unzip the life images
                foreach (string file_path in file_paths)
                {
                    Thread.Sleep(3000);
                    try
                    {
                        CheckDownPath(file_path);
                    }
                    catch
                    {
                        continue;
                    }
                    // down_folder(file_path);
                }
            }
        }
    }
}
