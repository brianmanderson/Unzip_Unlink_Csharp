﻿using FellowOakDicom;
using FolderWatcher;
using NewFrameOfReferenceClass;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Unzip_Unlink
{
    public class UnlinkUtils
    {
        public static void MoveFolder(string moving_directory, string current_folder)
        {
            string folder_name = Path.GetFileName(current_folder);
            string status_file = Path.Combine(moving_directory, folder_name, "NewFrameOfRef.txt");
            if (!Directory.Exists(moving_directory))
            {
                Directory.CreateDirectory(moving_directory);
            }
            Directory.Move(current_folder, Path.Combine(moving_directory, folder_name));
            if (File.Exists(status_file))
            {
                File.Delete(status_file);
            }
        }
        public static bool WatchFolder(string directory)
        {
            Watcher folder_watcher_class = new Watcher(directory);
            Thread.Sleep(5000);
            int counter = 0;
            while (folder_watcher_class.Folder_Changed)
            {
                counter++;
                folder_watcher_class.Folder_Changed = false;
                Console.WriteLine("Waiting for files to be fully transferred...");
                Thread.Sleep(5000);
                if (counter > 3)
                {
                    return false;
                }
            }
            return true;
        }
        public static void UpdatedFrameOfReference(string base_directory, string directory)
        {
            string status_file, overall_status, parsing_status;
            status_file = Path.Combine(directory, "NewFrameOfRef.txt");
            overall_status = Path.Combine(base_directory, $"UpdatingFrameOfRef_{Path.GetFileName(directory)}.txt");
            parsing_status = Path.Combine(base_directory, $"Parsing_{Path.GetFileName(directory)}.txt");
            if (!WatchFolder(directory))
            {
                return;
            }
            if (File.Exists(status_file))
            {
                return;
            }
            if (!File.Exists(parsing_status))
            {
                FileStream fid_parsing_status = File.OpenWrite(parsing_status);
                fid_parsing_status.Close();
            }
            Console.WriteLine("Parsing DICOM files...");
            FrameOfReferenceClass dicomParser = new FrameOfReferenceClass();
            dicomParser.Characterize_Directory(directory);
            if (File.Exists(parsing_status))
            {
                File.Delete(parsing_status);
            }
            Console.WriteLine("Updating frames of reference...");
            if (!File.Exists(overall_status))
            {
                FileStream fid_overallstatus = File.OpenWrite(overall_status);
                fid_overallstatus.Close();
            }
            dicomParser.ReWriteFrameOfReference();
            FileStream fid_status = File.OpenWrite(status_file);
            fid_status.Close();
            MoveFolder(moving_directory: Path.Combine(base_directory, "NewFinished"), current_folder: directory);
            Console.WriteLine("Finished!");
            if (File.Exists(overall_status))
            {
                File.Delete(overall_status);
            }
        }
        public static void UpdatedFrameOfReference(string base_directory, string directory, string modality_override)
        {
            string status_file, overall_status, parsing_status;
            status_file = Path.Combine(directory, "NewFrameOfRef.txt");
            overall_status = Path.Combine(base_directory, $"UpdatingFrameOfRef_{Path.GetFileName(directory)}.txt");
            parsing_status = Path.Combine(base_directory, $"Parsing_{Path.GetFileName(directory)}.txt");
            if (!WatchFolder(directory))
            {
                return;
            }
            if (File.Exists(status_file))
            {
                return;
            }
            if (!File.Exists(parsing_status))
            {
                FileStream fid_parsing_status = File.OpenWrite(parsing_status);
                fid_parsing_status.Close();
            }
            Console.WriteLine("Parsing DICOM files...");
            FrameOfReferenceClass dicomParser = new FrameOfReferenceClass();
            dicomParser.Characterize_Directory(directory);
            if (File.Exists(parsing_status))
            {
                File.Delete(parsing_status);
            }
            Console.WriteLine("Updating frames of reference...");
            if (!File.Exists(overall_status))
            {
                FileStream fid_overallstatus = File.OpenWrite(overall_status);
                fid_overallstatus.Close();
            }
            dicomParser.ReWriteFrameOfReference(modality_override: modality_override);
            FileStream fid_status = File.OpenWrite(status_file);
            fid_status.Close();
            MoveFolder(moving_directory: Path.Combine(base_directory, "NewFinished"), current_folder: directory);
            Console.WriteLine("Finished!");
            if (File.Exists(overall_status))
            {
                File.Delete(overall_status);
            }
        }
        public static void RunOnDirectory(string base_directory, string directory)
        {
            string status_file, overall_status, parsing_status, moving_status;
            string[] dicom_files;
            moving_status = Path.Combine(base_directory, $"Cannot move '{Path.GetFileName(directory)}' delete in NewFinished folder.txt");
            overall_status = Path.Combine(base_directory, $"UpdatingFrameOfRef_{Path.GetFileName(directory)}.txt");
            parsing_status = Path.Combine(base_directory, $"Parsing_{Path.GetFileName(directory)}.txt");
            dicom_files = Directory.GetFiles(directory);
            if (dicom_files.Length > 0)
            {
                status_file = Path.Combine(directory, "NewFrameOfRef.txt");
                if (File.Exists(status_file))
                {
                    try
                    {
                        MoveFolder(moving_directory: Path.Combine(base_directory, "NewFinished"), current_folder: directory);
                        if (File.Exists(moving_status))
                        {
                            File.Delete(moving_status);
                        }
                    }
                    catch
                    {
                        if (!File.Exists(moving_status))
                        {
                            FileStream fid_moving_status = File.OpenWrite(moving_status);
                            fid_moving_status.Close();
                        }
                    }
                }
                else
                {
                    UpdatedFrameOfReference(base_directory, directory);
                }
            }
            if (File.Exists(overall_status))
            {
                File.Delete(overall_status);
            }
            if (File.Exists(parsing_status))
            {
                File.Delete(parsing_status);
            }
            if (File.Exists(moving_status))
            {
                File.Delete(moving_status);
            }
        }
        public static void Runner(string base_directory)
        {
            foreach (string direct in Directory.GetDirectories(base_directory))
            {
                if (direct.Contains("NewFinished"))
                {
                    continue;
                }
                string[] all_directories = Directory.GetDirectories(direct, "*", SearchOption.AllDirectories);
                if (all_directories.Length == 0)
                {
                    RunOnDirectory(base_directory, direct);
                }
                else
                {
                    foreach (string directory in all_directories)
                    {
                        RunOnDirectory(base_directory, directory);
                    }
                }
            }
        }
    }
}
