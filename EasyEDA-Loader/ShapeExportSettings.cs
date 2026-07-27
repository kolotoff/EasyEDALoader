using System;
using System.IO;

namespace EasyEDA_Loader
{
    internal static class ShapeExportSettings
    {
        private const string FolderFileName = "shape-export-folder.txt";
        private const string LibraryFolderFileName = "shape-export-library-folder.txt";
        private const string DiagnosticsFileName = "shape-export-diagnostics.txt";

        public static string LoadLastFolder()
        {
            return LoadFolder(FolderFileName);
        }

        public static string LoadLastLibraryFolder()
        {
            return LoadFolder(LibraryFolderFileName);
        }

        private static string LoadFolder(string fileName)
        {
            try
            {
                string path = SettingsPath(fileName);
                if (!File.Exists(path))
                    return "";

                string folder = File.ReadAllText(path).Trim();
                return Directory.Exists(folder) ? folder : "";
            }
            catch
            {
                return "";
            }
        }

        public static void SaveLastFolder(string folder)
        {
            SaveFolder(FolderFileName, folder, "shape export");
        }

        public static void SaveLastLibraryFolder(string folder)
        {
            SaveFolder(LibraryFolderFileName, folder, "PCB library selection");
        }

        private static void SaveFolder(string fileName, string folder, string description)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return;

            try
            {
                string path = SettingsPath(fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, folder.Trim());
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("Could not save last " + description + " folder: " + ex.Message);
            }
        }

        public static bool LoadDiagnosticsEnabled()
        {
            try
            {
                string path = SettingsPath(DiagnosticsFileName);
                if (!File.Exists(path))
                    return false;

                string value = File.ReadAllText(path).Trim();
                return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "enabled", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string SettingsPath(string fileName)
        {
            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApplicationData))
                localApplicationData = Path.GetTempPath();

            return Path.Combine(localApplicationData, "EasyEDA-Loader", fileName);
        }
    }
}
