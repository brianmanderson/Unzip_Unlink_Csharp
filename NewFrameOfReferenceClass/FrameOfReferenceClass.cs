using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using itk.simple;
using FellowOakDicom;
using System.IO;

namespace NewFrameOfReferenceClass
{
    public class FrameOfReferenceClass
    {
        public Dictionary<string, VectorString> series_instance_uids_dict = new Dictionary<string, VectorString>();
        public VectorString dicom_series_instance_uids;
        Dictionary<string, DicomUID> series_instance_dict = new Dictionary<string, DicomUID>();
        private ImageFileReader image_reader;
        public FrameOfReferenceClass()
        {
        }
        public void __reset__()
        {
            series_instance_uids_dict = new Dictionary<string, VectorString>();
            series_instance_dict = new Dictionary<string, DicomUID>();
        }
        public void Characterize_Directory(string directory)
        {
            dicom_series_instance_uids = ImageSeriesReader.GetGDCMSeriesIDs(directory);
            foreach (string series_instance_uid in dicom_series_instance_uids)
            {
                VectorString dicom_names = ImageSeriesReader.GetGDCMSeriesFileNames(directory, series_instance_uid);
                series_instance_uids_dict.Add(series_instance_uid, dicom_names);
                if (!series_instance_dict.ContainsKey(series_instance_uid))
                {
                    DicomUID new_uid = DicomUIDGenerator.GenerateDerivedFromUUID();
                    series_instance_dict.Add(series_instance_uid, new_uid);
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
                    modality = "null";
                    continue;
                }
                if (modality.ToLower().Contains("mr"))
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
        public void ReWriteFrameOfReference(string directory)
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
    }
}
