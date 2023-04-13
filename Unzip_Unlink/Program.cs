using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FolderWatcher;
using NewFrameOfReferenceClass;
using UnzipClass;

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
                        string file = file_path.Split('\r')[0];
                        if (!file_paths.Contains(file))
                        {
                            file_paths.Add(file);
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
                    if (Directory.Exists(file_path))
                    {
                        if (File.Exists(Path.Combine(file_path, "Terminate.txt")))
                        {
                            return;
                        }
                        Thread.Sleep(3000);
                        try
                        {
                            UnzipUtils.UnzipFiles(file_path);
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
                        try
                        {
                            UnlinkUtils.Runner(file_path);
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }
                Thread.Sleep(3000);
            }
        }
    }
}
