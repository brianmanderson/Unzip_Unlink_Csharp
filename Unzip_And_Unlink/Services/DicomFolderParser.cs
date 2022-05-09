using System;
using System.Collections.Generic;
using itk.simple;


namespace Unzip_And_Unlink.Services
{
    public class DicomParser
    {
        public Dictionary<string, VectorString> series_instance_uids = new Dictionary<string, VectorString>();
        VectorString dicom_series_ids;
        public DicomParser()
        {
        }
        public void __reset__()
        {
            series_instance_uids = new Dictionary<string, VectorString>();
        }
        public void GetSeriesInstanceUIDs(string directory)
        {
            dicom_series_ids = ImageSeriesReader.GetGDCMSeriesIDs(directory);
        }
        public void GetFileNames(string directory)
        {
            foreach (string dicom_series_id in dicom_series_ids)
            {
                VectorString dicom_names = ImageSeriesReader.GetGDCMSeriesFileNames(directory, dicom_series_id);
                series_instance_uids.Add(dicom_series_id, dicom_names);
            }
        }
        public void ParseDirectory(string directory)
        {
            VectorString dicom_series_ids = ImageSeriesReader.GetGDCMSeriesIDs(directory);
            foreach (string dicom_series_id in dicom_series_ids)
            {
                VectorString dicom_names = ImageSeriesReader.GetGDCMSeriesFileNames(directory, dicom_series_id);
                series_instance_uids.Add(dicom_series_id, dicom_names);
            }
        }
    }
}
