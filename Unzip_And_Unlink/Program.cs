using System;
using FellowOakDicom;
using System.Threading;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;


namespace Unzip_And_Unlink
{
    class Program
    {
        static string[] file_paths = { @"\\ucsdhc-varis2\radonc$\00plans\Unzip_Unlink" };
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
        static void NewFrameOfReference(string base_directory)
        {
            string[] all_directories = Directory.GetDirectories(base_directory);
            string[] all_files;
            string status_file, uid, overall_status;

            foreach (string directory in all_directories)
            {
                List<string> all_series_uids = new List<string>();
                List<DicomUID> frame_of_reference_uids = new List<DicomUID>();
                DicomUID new_frame_UID;
                status_file = Path.Join(directory, "NewFrameOfRef.txt");
                if (File.Exists(status_file))
                {
                    continue;
                }
                overall_status = Path.Join(base_directory, $"UpdatingFrameOfRef_{Path.GetFileName(directory)}.txt");
                if (!File.Exists(overall_status))
                {
                    FileStream fid_overallstatus = File.OpenWrite(overall_status);
                    fid_overallstatus.Close();
                }
                all_files = Directory.GetFiles(directory);
                Thread.Sleep(3000);
                while (all_files.Length != Directory.GetFiles(directory).Length)
                {
                    Console.WriteLine("Waiting for files to be fully transferred...");
                    all_files = Directory.GetFiles(directory);
                    Thread.Sleep(3000);
                }
                Console.WriteLine("Updating frames of reference...");
                foreach (string dicom_file in all_files)
                {
                    if (dicom_file.EndsWith(".dcm"))
                    {
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
                FileStream fid = File.OpenWrite(status_file);
                fid.Close();
                Console.WriteLine("Finished!");
                if (File.Exists(overall_status))
                {
                    File.Delete(overall_status);
                }
                Console.WriteLine("Running...");
            }
        }
        static void RenameFolder(string base_directory, string unzipped_file_directory)
        {
            string[] all_files = Directory.GetFiles(unzipped_file_directory);
            foreach (string dicom_file in all_files)
            {
                if (dicom_file.EndsWith(".dcm"))
                {
                    try
                    {
                        var file = DicomFile.Open(dicom_file);
                        string patient_name = file.Dataset.GetString(DicomTag.PatientName).Replace('^', '_');
                        Directory.Move(unzipped_file_directory, Path.Join(base_directory, patient_name));
                        Console.WriteLine("Finished!");
                        break;
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
        }
        static void UnzipFiles(string zip_file_directory)
        {
            string[] all_files = Directory.GetFiles(zip_file_directory, ".zip");
            string overall_status;
            foreach (string zip_file in all_files)
            {
                FileInfo zip_file_info = new FileInfo(zip_file);
                Thread.Sleep(1000);
                while (IsFileLocked(zip_file_info))
                {
                    Console.WriteLine("Waiting for file to be fully transferred...");
                    Thread.Sleep(3000);
                }
            if (zip_file.EndsWith(".zip"))
            {
                string file_name = Path.GetFileName(zip_file);
                string output_dir = Path.Join(zip_file_directory, file_name.Substring(0, file_name.Length - 4));
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
                    RenameFolder(zip_file_directory, output_dir);
                    File.Delete(zip_file);
                }
                else
                {
                    Console.WriteLine("Renaming Folder...");
                    RenameFolder(zip_file_directory, output_dir);
                    File.Delete(zip_file);
                }
                if (File.Exists(overall_status))
                {
                    File.Delete(overall_status);
                }
                Console.WriteLine("Running...");
            }
            }
        }
        static void CheckFolder(string file_path)
        {
            Thread.Sleep(1000);
            if (Directory.Exists(file_path))
            {
                UnzipFiles(zip_file_directory: file_path);
                NewFrameOfReference(file_path);
            }
        }
        static void down_folder(string file_path)
        {
            string[] all_directories = Directory.GetDirectories(file_path);
            foreach (string directory in all_directories)
            {
                CheckFolder(directory);
            }
        }
        static void Main(string[] args)
        {
            Console.WriteLine("Running...");
            while (true)
            {
                // First lets unzip the life images
                foreach (string file_path in file_paths)
                {
                    CheckFolder(file_path);
                    down_folder(file_path);
                }
            }
        }
    }
}
