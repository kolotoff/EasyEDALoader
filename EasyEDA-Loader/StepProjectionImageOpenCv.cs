using OpenCvSharp;
using System;
using System.Runtime.InteropServices;

namespace EasyEDA_Loader
{
    internal static class StepProjectionImageOpenCv
    {
        public static Mat ToBgraMat(StepProjectionImage image)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            if (image.RgbaBytes == null || image.RgbaBytes.Length != image.Width * image.Height * 4)
                throw new ArgumentException("Projection image data is invalid.", nameof(image));

            using (var rgba = new Mat(image.Height, image.Width, MatType.CV_8UC4))
            {
                Marshal.Copy(image.RgbaBytes, 0, rgba.Data, image.RgbaBytes.Length);
                var bgra = new Mat();
                Cv2.CvtColor(rgba, bgra, ColorConversionCodes.RGBA2BGRA);
                return bgra;
            }
        }

        public static Mat ToGrayMat(StepProjectionImage image)
        {
            using (Mat bgra = ToBgraMat(image))
            {
                var gray = new Mat();
                Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
                return gray;
            }
        }
    }
}
