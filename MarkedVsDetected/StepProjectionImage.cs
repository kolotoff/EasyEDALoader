namespace EasyEDA_Loader
{
    public sealed class StepProjectionImage
    {
        public string ViewName { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] RgbaBytes { get; set; }
    }
}
