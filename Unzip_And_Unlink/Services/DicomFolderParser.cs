using System;
using System.Collections.Generic;
using itk.simple;


namespace Unzip_And_Unlink.Services
{
    public class DicomParser
    {
        public Dictionary<string, VectorString> series_instance_uids_dict = new Dictionary<string, VectorString>();
        public VectorString dicom_series_instance_uids;
        public DicomParser()
        {
        }
        public void __reset__()
        {
            series_instance_uids_dict = new Dictionary<string, VectorString>();
        }
        public void GetSeriesInstanceUIDs(string directory)
        {
            dicom_series_instance_uids = ImageSeriesReader.GetGDCMSeriesIDs(directory);
        }
        public void GetFileNames(string directory)
        {
            foreach (string dicom_series_id in dicom_series_instance_uids)
            {
                VectorString dicom_names = ImageSeriesReader.GetGDCMSeriesFileNames(directory, dicom_series_id);
                series_instance_uids_dict.Add(dicom_series_id, dicom_names);
            }
        }
        public void ParseDirectory(string directory)
        {
            dicom_series_instance_uids = ImageSeriesReader.GetGDCMSeriesIDs(directory);
            foreach (string dicom_series_id in dicom_series_instance_uids)
            {
                VectorString dicom_names = ImageSeriesReader.GetGDCMSeriesFileNames(directory, dicom_series_id);
                series_instance_uids_dict.Add(dicom_series_id, dicom_names);
            }
        }
    }
}
