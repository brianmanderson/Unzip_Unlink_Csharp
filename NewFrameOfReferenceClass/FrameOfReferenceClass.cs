using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using itk.simple;
using FellowOakDicom;

namespace NewFrameOfReferenceClass
{
    public class FrameOfReferenceClass
    {
        public Dictionary<string, VectorString> series_instance_uids_dict = new Dictionary<string, VectorString>();
        public VectorString dicom_series_instance_uids;
        Dictionary<string, DicomUID> series_instance_dict = new Dictionary<string, DicomUID>();
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

        }
    }
}
