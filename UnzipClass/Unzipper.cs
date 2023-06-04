using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnzipClass
{
    public class Unzipper
    {
        public static bool IsFileLocked(FileInfo file)
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
        public static void UnzipFilesInDirectory(string zip_file_directory)
        {
            string[] all_files = Directory.GetFiles(zip_file_directory, "*.zip", SearchOption.AllDirectories);
            foreach (string zip_file in all_files)
            {
                FileInfo zip_file_info = new FileInfo(zip_file);
                Thread.Sleep(3000);
                int tries = 0;
                Thread.Sleep(3000);
                while (IsFileLocked(zip_file_info))
                {
                    Console.WriteLine("Waiting for file to be fully transferred...");
                    tries += 1;
                    Thread.Sleep(3000);
                    if (tries > 2)
                    {
                        continue;
                    }
                }
                string file_name = Path.GetFileName(zip_file);
                string output_dir = Path.Combine(Path.GetDirectoryName(zip_file), file_name.Substring(0, file_name.Length - 4));
                if (!Directory.Exists(output_dir))
                {
                    Directory.CreateDirectory(output_dir);
                    Console.WriteLine("Extracting...");
                    ZipFile.ExtractToDirectory(zip_file, output_dir);
                    File.Delete(zip_file);
                }
            }
        }
        public static void UnzipFile(string zip_file)
        {
            FileInfo zip_file_info = new FileInfo(zip_file);
            Thread.Sleep(6000);
            while (IsFileLocked(zip_file_info))
            {
                Console.WriteLine("Waiting for file to be fully transferred...");
                Thread.Sleep(3000);
            }
            string file_name = Path.GetFileName(zip_file);
            string output_dir = Path.Combine(Path.GetDirectoryName(zip_file), file_name.Substring(0, file_name.Length - 4));
            if (!Directory.Exists(output_dir))
            {
                Directory.CreateDirectory(output_dir);
                Console.WriteLine("Extracting...");
                ZipFile.ExtractToDirectory(zip_file, output_dir);
                File.Delete(zip_file);
            }
        }
    }
}
