using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Readers;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace EasyEDA_Loader
{
    internal static class JlcCamSource
    {
        internal const int MaxEntries = 10000;
        internal const long MaxSingleEntryBytes = 128L * 1024 * 1024;
        internal const long MaxTotalBytes = 512L * 1024 * 1024;

        public static JlcCamAnalysisSession OpenArchive(string archivePath)
        {
            if (!string.Equals(Path.GetExtension(archivePath), ".rar", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("JLCCAM archive must be a .rar file.");
            string tempRoot = Path.Combine(Path.GetTempPath(), "EasyEDA-Loader", "JLCCAM", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                ExtractRar(archivePath, tempRoot);
                JlcCamAnalysisSession session = OpenFolder(tempRoot);
                session.SourcePath = archivePath;
                session.TemporaryRoot = tempRoot;
                return session;
            }
            catch { CleanupTemporaryRoot(tempRoot); throw; }
        }

        public static JlcCamAnalysisSession OpenFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) throw new DirectoryNotFoundException("JLCCAM production folder was not found.");
            string root = FindPackageRoot(folder);
            return new JlcCamAnalysisSession { SourcePath = folder, PackageRoot = root };
        }

        public static string FindPackageRoot(string selectedRoot)
        {
            var candidates = new List<string>();
            foreach (string directory in new[] { selectedRoot }.Concat(Directory.EnumerateDirectories(selectedRoot, "*", SearchOption.AllDirectories)))
            {
                try
                {
                    bool hasOk = Directory.EnumerateDirectories(directory).Any(p => string.Equals(Path.GetFileName(p), "ok", StringComparison.OrdinalIgnoreCase));
                    bool hasYg = Directory.EnumerateDirectories(directory).Any(p => string.Equals(Path.GetFileName(p), "YG", StringComparison.OrdinalIgnoreCase));
                    if (hasOk && hasYg) candidates.Add(directory);
                }
                catch (UnauthorizedAccessException) { }
            }
            if (candidates.Count != 1) throw new InvalidDataException(candidates.Count == 0 ? "No JLCCAM package root with sibling ok and YG folders was found." : "More than one JLCCAM package root was found; select one package only.");
            return candidates[0];
        }

        public static string FindLayer(string directory, string layer)
        {
            if (!Directory.Exists(directory)) return null;
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(path => string.Equals(Path.GetFileName(path), layer, StringComparison.OrdinalIgnoreCase));
        }

        public static string FindOriginalOutline(JlcCamAnalysisSession session)
        {
            string yg = GetChildDirectory(session.PackageRoot, "YG");
            string outline = FindOutlineInDirectory(yg);
            if (outline != null) { session.OriginalOutlinePath = outline; return outline; }
            string zip = Directory.EnumerateFiles(yg, "*.zip", SearchOption.AllDirectories).SingleOrDefault();
            if (zip == null) throw new InvalidDataException("No original board outline (.GKO, .GM1, or .GML) was found in YG.");
            string root = session.TemporaryRoot ?? Path.Combine(Path.GetTempPath(), "EasyEDA-Loader", "JLCCAM", Guid.NewGuid().ToString("N"));
            if (session.TemporaryRoot == null) { Directory.CreateDirectory(root); session.TemporaryRoot = root; }
            string zipRoot = Path.Combine(root, "YG-original"); Directory.CreateDirectory(zipRoot);
            using (ZipArchive archive = ZipFile.OpenRead(zip))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string target = SafeTargetPath(zipRoot, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (entry.Length > MaxSingleEntryBytes) throw new InvalidDataException("Original YG ZIP entry exceeds " + MaxSingleEntryBytes + " bytes.");
                    entry.ExtractToFile(target, false);
                }
            }
            outline = FindOutlineInDirectory(zipRoot);
            if (outline == null) throw new InvalidDataException("No original board outline (.GKO, .GM1, or .GML) was found in the YG ZIP.");
            session.OriginalOutlinePath = outline; return outline;
        }

        public static string GetChildDirectory(string root, string name)
        {
            string result = Directory.EnumerateDirectories(root).SingleOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
            if (result == null) throw new InvalidDataException("JLCCAM package is missing '" + name + "'.");
            return result;
        }

        public static void CleanupTemporaryRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string tempBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EasyEDA-Loader", "JLCCAM")) + Path.DirectorySeparatorChar;
                string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (Exception ex) { EasyEDALoaderModule.Trace("JLCCAM temporary cleanup failed: " + ex.Message); }
        }

        private static void ExtractRar(string archivePath, string tempRoot)
        {
            if (!File.Exists(archivePath)) throw new FileNotFoundException("JLCCAM archive was not found.", archivePath);
            using (IArchive archive = RarArchive.OpenArchive(archivePath, new ReaderOptions()))
            {
                long total = 0; int count = 0;
                foreach (IArchiveEntry entry in archive.Entries)
                {
                    if (entry.IsDirectory) continue;
                    if (++count > MaxEntries) throw new InvalidDataException("JLCCAM archive exceeds " + MaxEntries + " entries.");
                    if (entry.Size > MaxSingleEntryBytes) throw new InvalidDataException("JLCCAM archive entry exceeds " + MaxSingleEntryBytes + " bytes.");
                    total += entry.Size; if (total > MaxTotalBytes) throw new InvalidDataException("JLCCAM archive exceeds " + MaxTotalBytes + " uncompressed bytes.");
                    string target = SafeTargetPath(tempRoot, entry.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream input = entry.OpenEntryStream())
                    using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                }
            }
        }

        private static string FindOutlineInDirectory(string root)
        {
            IEnumerable<string> files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
            foreach (string extension in new[] { ".GKO", ".GM1", ".GML" })
            {
                string[] matches = files.Where(p => string.Equals(Path.GetExtension(p), extension, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 1) return matches[0];
                if (matches.Length > 1) throw new InvalidDataException("Multiple equally plausible original " + extension + " outlines were found.");
            }
            return null;
        }

        private static string SafeTargetPath(string root, string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName) || Path.IsPathRooted(entryName) || entryName.IndexOf(':') >= 0) throw new InvalidDataException("Archive contains an unsafe entry path: " + entryName);
            string target = Path.GetFullPath(Path.Combine(root, entryName));
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive entry escapes the extraction directory: " + entryName);
            return target;
        }
    }
}
