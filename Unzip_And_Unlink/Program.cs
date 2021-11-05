using System;
using FellowOakDicom.Imaging;

namespace Unzip_And_Unlink
{
    class Program
    {
        static void Main(string[] args)
        {
            FellowOakDicom.DicomFile.Open(@"test.dcm");
            Console.WriteLine("Hello World!");
        }
    }
}
