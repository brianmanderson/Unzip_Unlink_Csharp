﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using itk.simple;
using FellowOakDicom;
using System.IO;
using System.Windows;

namespace NewFrameOfReferenceClass
{
    public class FrameOfReferenceClass
    {
        public Dictionary<string, VectorString> series_instance_uids_dict = new Dictionary<string, VectorString>();
        public List<string> dicom_series_instance_uids;
        public Dictionary<string, DicomUID> series_instance_dict = new Dictionary<string, DicomUID>();
        public ImageFileReader image_reader = new ImageFileReader();
        public FrameOfReferenceClass()
        {
            dicom_series_instance_uids = new List<string>();
        }
        public void __reset__()
        {
            series_instance_uids_dict = new Dictionary<string, VectorString>();
            series_instance_dict = new Dictionary<string, DicomUID>();
            dicom_series_instance_uids = new List<string>();
        }
        public void Characterize_Directory(string directory)
        {
            VectorString uids = ImageSeriesReader.GetGDCMSeriesIDs(directory);
            foreach (string series_instance_uid in uids)
            {
                dicom_series_instance_uids.Add(series_instance_uid);
                VectorString dicom_names = ImageSeriesReader.GetGDCMSeriesFileNames(directory, series_instance_uid);
                series_instance_uids_dict.Add(series_instance_uid, dicom_names);
                if (!series_instance_dict.ContainsKey(series_instance_uid))
                {
                    DicomUID new_uid = DicomUIDGenerator.GenerateDerivedFromUUID();
                    series_instance_dict.Add(series_instance_uid, new_uid);
                }
            }
            foreach (string subdirectory in Directory.GetDirectories(directory))
            {
                Characterize_Directory(subdirectory);
            }
        }
        public void ReWriteFrameOfReference(string modality_override)
        {
            foreach (string dicom_series_instance_uid in dicom_series_instance_uids)
            {
                string modality;
                VectorString dicom_names = series_instance_uids_dict[dicom_series_instance_uid];
                DicomUID uid = series_instance_dict[dicom_series_instance_uid];
                image_reader.SetFileName(dicom_names[0]);
                try
                {
                    image_reader.ReadImageInformation();
                    modality = image_reader.GetMetaData("0008|0060");
                }
                catch
                {
                    modality = "null";
                    continue;
                }
                if (modality.ToLower().Contains(modality_override.ToLower()))
                {
                    Parallel.ForEach(dicom_names, dicom_file =>
                    {
                        try
                        {
                            var file = DicomFile.Open(dicom_file, FileReadOption.ReadAll);
                            file.Dataset.AddOrUpdate(DicomTag.FrameOfReferenceUID, uid);
                            file.Save(dicom_file);
                        }
                        catch
                        {
                        }
                    });
                }
            }
        }
        public void ReWriteFrameOfReference()
        {
            foreach (string dicom_series_instance_uid in dicom_series_instance_uids)
            {
                string modality;
                VectorString dicom_names = series_instance_uids_dict[dicom_series_instance_uid];
                DicomUID uid = series_instance_dict[dicom_series_instance_uid];
                image_reader.SetFileName(dicom_names[0]);
                try
                {
                    image_reader.ReadImageInformation();
                    modality = image_reader.GetMetaData("0008|0060");
                }
                catch
                {
                    continue;
                }
                Parallel.ForEach(dicom_names, dicom_file =>
                {
                    try
                    {
                        var file = DicomFile.Open(dicom_file, FileReadOption.ReadAll);
                        file.Dataset.AddOrUpdate(DicomTag.FrameOfReferenceUID, uid);
                        file.Save(dicom_file);
                    }
                    catch
                    {
                    }
                });
            }
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
                Parallel.ForEach(dicom_files, dicom_file =>
                {
                    try
                    {
                        var file = DicomFile.Open(dicom_file, FileReadOption.ReadAll);
                        file.Dataset.AddOrUpdate(DicomTag.FrameOfReferenceUID, new_uid);
                        file.Save(dicom_file);
                    }
                    catch
                    {
                    }
                });
            }
        }
        public void ReWriteFrameOfReference(Dictionary<string, DicomUID> temp_series_instance_dict, List<string> dicom_files)
        {
            DicomUID new_uid;
            Parallel.ForEach(dicom_files, dicom_file =>
            {
                try
                {
                    var file = DicomFile.Open(dicom_file, FileReadOption.ReadAll);
                    if (file.Dataset.Contains(DicomTag.Modality))
                    {
                        if (file.Dataset.GetString(DicomTag.Modality).ToLower().Contains("mr"))
                        {
                            string series_uid = file.Dataset.GetString(DicomTag.SeriesInstanceUID);
                            if (temp_series_instance_dict.ContainsKey(series_uid))
                            {
                                new_uid = temp_series_instance_dict[series_uid];
                                file.Dataset.AddOrUpdate(DicomTag.FrameOfReferenceUID, new_uid);
                                file.Save(dicom_file);
                            }
                        }
                    }
                }
                catch
                {
                }
            });
        }
        public void ReWriteFrameOfReference(List<string> dicom_files)
        {
            DicomUID new_uid;
            Parallel.ForEach(dicom_files, dicom_file =>
            {
                try
                {
                    var file = DicomFile.Open(dicom_file, FileReadOption.ReadAll);
                    if (file.Dataset.Contains(DicomTag.Modality))
                    {
                        if (file.Dataset.GetString(DicomTag.Modality).ToLower().Contains("mr"))
                        {
                            string series_uid = file.Dataset.GetString(DicomTag.SeriesInstanceUID);
                            if (series_instance_dict.ContainsKey(series_uid))
                            {
                                new_uid = series_instance_dict[series_uid];
                                file.Dataset.AddOrUpdate(DicomTag.FrameOfReferenceUID, new_uid);
                                file.Save(dicom_file);
                            }
                        }
                    }
                }
                catch
                {
                }
            });
        }
        public void ReWriteFrameOfReferenceDirectory(string directory)
        {
            DicomUID new_uid;
            string[] dicom_files = Directory.GetFiles(directory, "*.dcm");
            Parallel.ForEach(dicom_files, dicom_file =>
            {
                try
                {
                    var file = DicomFile.Open(dicom_file, FileReadOption.ReadAll);
                    if (file.Dataset.Contains(DicomTag.Modality))
                    {
                        string series_uid = file.Dataset.GetString(DicomTag.SeriesInstanceUID);
                        if (series_instance_dict.ContainsKey(series_uid))
                        {
                            new_uid = series_instance_dict[series_uid];
                            file.Dataset.AddOrUpdate(DicomTag.FrameOfReferenceUID, new_uid);
                            file.Save(dicom_file);
                        }
                    }
                }
                catch
                {
                }
            });
        }
        public void ReWriteFrameOfReferenceDirectory(string directory, string modality_override)
        {
            DicomUID new_uid;
            string[] dicom_files = Directory.GetFiles(directory, "*.dcm");
            Parallel.ForEach(dicom_files, dicom_file =>
            {
                try
                {
                    var file = DicomFile.Open(dicom_file, FileReadOption.ReadAll);
                    if (file.Dataset.Contains(DicomTag.Modality))
                    {
                        if (file.Dataset.GetString(DicomTag.Modality).ToLower().Contains(modality_override.ToLower()))
                        {
                            string series_uid = file.Dataset.GetString(DicomTag.SeriesInstanceUID);
                            if (series_instance_dict.ContainsKey(series_uid))
                            {
                                new_uid = series_instance_dict[series_uid];
                                file.Dataset.AddOrUpdate(DicomTag.FrameOfReferenceUID, new_uid);
                                file.Save(dicom_file);
                            }
                        }
                    }
                }
                catch
                {
                }
            });
        }
    }
}
