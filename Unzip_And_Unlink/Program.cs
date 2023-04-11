using System;
using FellowOakDicom;
using System.Threading;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using Unzip_And_Unlink.Services;
using itk.simple;

namespace Unzip_And_Unlink
{
    class Program
    {
        public bool folder_changed;
        public DicomParser dicomParser;
        static List<string> default_file_paths = new List<string> { @"\\ucsdhc-varis2\radonc$\00plans\Unzip_Unlink", @"\\ro-ariaimg-v\VA_DATA$\DICOM\Unzip_Unlink_DONOTDELETE" };
        // static List<string> default_file_paths = new List<string> { @"O:\BMAnderson\Testing_Unzip_Unlink" };
        ///
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
                    if (Directory.Exists(file_path))
                    {
                        if (File.Exists(Path.Join(file_path, "Terminate.txt")))
                        {
                            return;
                        }
                        Thread.Sleep(3000);
                        try
                        {
                            Utils.UnzipFiles(file_path);
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
                        if (File.Exists(Path.Join(file_path, "Terminate.txt")))
                        {
                            return;
                        }
                        Thread.Sleep(3000);
                        try
                        {
                            Utils.NewFrameOfReferenceDirectory(file_path);
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
