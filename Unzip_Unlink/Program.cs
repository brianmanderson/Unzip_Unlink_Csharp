﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FolderWatcher;
using NewFrameOfReferenceClass;

namespace Unzip_Unlink
{
    internal class Program
    {
        public bool folder_changed;
        static string file_paths_file = Path.Combine(".", $"FilePaths.txt");
        public FrameOfReferenceClass FrameChanger = new FrameOfReferenceClass();
        static List<string> default_file_paths = new List<string> { @"C:\Users\b5anderson\Temp" };
        // static List<string> default_file_paths = new List<string> { @"O:\BMAnderson\Testing_Unzip_Unlink" };
        static void update_file_paths()
        {
            List<string> file_paths = new List<string> { };
            foreach (string file_path in default_file_paths)
            {
                file_paths.Add(file_path);
            }
            if (!File.Exists(file_paths_file))
            {
                try
                {
                    StreamWriter fid = new StreamWriter(file_paths_file);
                    foreach (string file_path in default_file_paths)
                    {
                        fid.WriteLine($"{file_path}");
                    }
                    fid.Close();
                }
                catch
                {
                }
            }
        }
        public void Runner(string base_directory)
        {
            string[] dicom_files;
            string status_file, overall_status, parsing_status, moving_status;
            string[] all_directories = Directory.GetDirectories(base_directory, "*", SearchOption.AllDirectories);
            foreach (string directory in all_directories)
            {
                moving_status = Path.Combine(base_directory, $"Cannot move '{Path.GetFileName(directory)}' delete in NewFinished folder.txt");
                overall_status = Path.Combine(base_directory, $"UpdatingFrameOfRef_{Path.GetFileName(directory)}.txt");
                parsing_status = Path.Combine(base_directory, $"Parsing_{Path.GetFileName(directory)}.txt");
                if (directory.Contains("NewFinished"))
                {
                    continue;
                }
                dicom_files = Directory.GetFiles(directory, "*.dcm");
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
        }
        static void Main(string[] args)
        {
            Console.WriteLine("Running...");
            while (true)
            {
                update_file_paths();
                List<string> file_paths = new List<string> { };
                try
                {
                    string all_file_paths = File.ReadAllText(file_paths_file);
                    foreach (string file_path in all_file_paths.Split('\n'))
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
                // First lets unzip the life images
                foreach (string file_path in file_paths)
                {
                    continue;
                    if (Directory.Exists(file_path))
                    {
                        if (File.Exists(Path.Combine(file_path, "Terminate.txt")))
                        {
                            return;
                        }
                        Thread.Sleep(3000);
                        try
                        {
                            //Utils.UnzipFiles(file_path);
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }
                // Second update to new frame of reference
                foreach (string file_path in file_paths)
                {
                    if (Directory.Exists(file_path))
                    {
                        if (File.Exists(Path.Combine(file_path, "Terminate.txt")))
                        {
                            return;
                        }
                        Thread.Sleep(3000);
                        try
                        {
                            Utils.NewFrameOfReferenceDirectory(file_path);
                            FrameOfReferenceClass frameOfReferenceClass = new FrameOfReferenceClass();
                            frameOfReferenceClass.Characterize_Directory(file_path);

                        }
                        catch
                        {
                            continue;
                        }
                    }
                }
            }
        }
    }
}