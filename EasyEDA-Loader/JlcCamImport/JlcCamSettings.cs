using System;
using System.IO;

namespace EasyEDA_Loader
{
    internal static class JlcCamSettings
    {
        public static string LoadArchiveFolder() { return Load("jlccam-archive-folder.txt"); }
        public static string LoadFolder() { return Load("jlccam-folder.txt"); }
        public static void SaveArchiveFolder(string path) { Save("jlccam-archive-folder.txt", path); }
        public static void SaveFolder(string path) { Save("jlccam-folder.txt", path); }
        private static string Load(string name) { try { string file = Path.Combine(Root(), name); string path = File.Exists(file) ? File.ReadAllText(file).Trim() : ""; return Directory.Exists(path) ? path : ""; } catch { return ""; } }
        private static void Save(string name, string path) { try { if (string.IsNullOrWhiteSpace(path)) return; Directory.CreateDirectory(Root()); File.WriteAllText(Path.Combine(Root(), name), path); } catch (Exception ex) { EasyEDALoaderModule.Trace("Could not save JLCCAM folder: " + ex.Message); } }
        private static string Root() { string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); return Path.Combine(string.IsNullOrWhiteSpace(local) ? Path.GetTempPath() : local, "EasyEDA-Loader"); }
    }
}
