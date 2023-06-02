using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnzipClass;
using FellowOakDicom;

namespace Unzip_Unlink
{
    internal class UnzipUtils
    {
        public static void RenameFolder(string unzipped_file_directory)
        {
            string[] dicom_files = Directory.GetFiles(unzipped_file_directory, "*.dcm");
            string base_directory = Path.GetFullPath(Path.Combine(unzipped_file_directory, ".."));
            foreach (string dicom_file in dicom_files)
            {
                try
                {
                    var file = DicomFile.Open(dicom_file);
                    string patient_name = file.Dataset.GetString(DicomTag.PatientName).Replace('^', '_');
                    try
                    {
                        Directory.Move(unzipped_file_directory, Path.Combine(base_directory, patient_name));
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
        public static void UnzipFile(string zip_file, string zip_file_directory)
        {
            string overall_status;
            FileInfo zip_file_info = new FileInfo(zip_file);
            Thread.Sleep(3000);
            bool move_on = false;
            int tries = 0;
            Thread.Sleep(3000);
            while (Unzipper.IsFileLocked(zip_file_info))
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
                return;
            }
            overall_status = Path.Combine(zip_file_directory, $"ExtractingZip.txt");
            if (!File.Exists(overall_status))
            {
                FileStream fid_overallstatus = File.OpenWrite(overall_status);
                fid_overallstatus.Close();
            }
            Unzipper.UnzipFile(zip_file);
            string file_name = Path.GetFileName(zip_file);
            string output_dir = Path.Combine(Path.GetDirectoryName(zip_file), file_name.Substring(0, file_name.Length - 4));
            if (Directory.Exists(output_dir))
            {
                Console.WriteLine("Renaming Folder...");
                RenameFolder(output_dir);
            }
            if (File.Exists(overall_status))
            {
                File.Delete(overall_status);
            }
        }
        public static void UnzipFiles(string zip_file_directory)
        {
            string[] all_files = Directory.GetFiles(zip_file_directory, "*.zip", SearchOption.AllDirectories);
            foreach (string zip_file in all_files)
            {
                UnzipFile(zip_file, zip_file_directory);
                Console.WriteLine("Running...");
                Thread.Sleep(3000);
            }
        }
    }
}
