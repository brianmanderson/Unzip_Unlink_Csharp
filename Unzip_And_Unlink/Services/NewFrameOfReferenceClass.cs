using System;
using FellowOakDicom;
using System.Threading;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using Unzip_And_Unlink.Services;
using itk.simple;
using DicomFolderParser;


namespace Unzip_And_Unlink.Services
{
    class Test
    {
        ImageSeriesReader series_reader;
        public Test()
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
    class NewFrameOfReferenceClass
    {
        public NewFrameOfReferenceClass()
        {
        }
        public void ReWriteFrameOfReference(VectorString dicom_files)
        {
            DicomUID new_uid = DicomUIDGenerator.GenerateDerivedFromUUID();
            bool is_mr = false;
            try
            {
                var file = DicomFile.Open(dicom_files[0], FileReadOption.ReadAll);
                if (file.Dataset.Contains(DicomTag.Modality))
                {
                    if (file.Dataset.GetString(DicomTag.Modality).ToLower().Contains("mr"))
                    {
                        is_mr = true;
                    }
                }
            }
            catch
            {

            }
            if (is_mr)
            {
                foreach (string dicom_file in dicom_files)
                {
                    try
                    {
                        var file = DicomFile.Open(dicom_file, FileReadOption.ReadAll);
                        if (file.Dataset.Contains(DicomTag.Modality))
                        {
                            if (!file.Dataset.GetString(DicomTag.Modality).ToLower().Contains("mr"))
                            {
                                is_mr = false;
                                break;
                            }
                        }
                        file.Dataset.AddOrUpdate(DicomTag.FrameOfReferenceUID, new_uid);
                        file.Save(dicom_file);
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
