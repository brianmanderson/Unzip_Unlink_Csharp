using System;
using FellowOakDicom;
using System.Threading;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using itk.simple;
using System.Threading.Tasks;


namespace Unzip_And_Unlink.Services
{
    class NewFrameOfReferenceClass
    {
        Dictionary<string, DicomUID> series_instance_dict = new Dictionary<string, DicomUID>();
        public NewFrameOfReferenceClass()
        {
        }
        public void make_series_instance_dict(VectorString series_instance_uids)
        {
            DicomUID new_uid;
            foreach (string series_instance_uid in series_instance_uids)
            {
                if (!this.series_instance_dict.ContainsKey(series_instance_uid))
                {
                    new_uid = DicomUIDGenerator.GenerateDerivedFromUUID();
                    this.series_instance_dict.Add(series_instance_uid, new_uid);
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
