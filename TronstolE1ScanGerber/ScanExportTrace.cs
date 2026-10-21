using System;
using System.IO;

namespace EasyEDA_Loader.TronstolE1ScanGerber
{
    internal static class ScanExportTrace
    {
        public static void Write(string message)
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyEDA-Loader");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "TronstolE1ScanGerber.log"),
                    DateTimeOffset.Now.ToString("O") + " [" + Environment.ProcessId + "] " + message + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
