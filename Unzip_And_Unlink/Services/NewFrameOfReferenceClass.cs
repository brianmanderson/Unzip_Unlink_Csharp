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
    class NewFrameOfReferenceClass
    {
        public NewFrameOfReferenceClass()
        {
        }
        static void ReWriteFrameOfReference(VectorString dicom_files)
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
