using System;
using System.IO;
using System.Windows.Forms;

namespace StepProjectionMarker
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string projectionDirectory = args.Length > 0
                ? Path.GetFullPath(args[0])
                : FindDefaultProjectionDirectory();

            string markedDirectory = args.Length > 1
                ? Path.GetFullPath(args[1])
                : GetDefaultMarkedDirectory(projectionDirectory);

            Application.Run(new ProjectionMarkerForm(projectionDirectory, markedDirectory));
        }

        private static string FindDefaultProjectionDirectory()
        {
            string relativeProjectionDirectory = Path.Combine("Test", "StepCleaner", "Data", "Projection");
            string[] searchRoots =
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory
            };

            foreach (string searchRoot in searchRoots)
            {
                string currentDirectory = Path.GetFullPath(searchRoot);
                while (!string.IsNullOrEmpty(currentDirectory))
                {
                    string testProjectionDirectory = Path.Combine(currentDirectory, relativeProjectionDirectory);
                    if (Directory.Exists(testProjectionDirectory))
                        return testProjectionDirectory;

                    DirectoryInfo parent = Directory.GetParent(currentDirectory);
                    if (parent == null)
                        break;

                    currentDirectory = parent.FullName;
                }
            }

            return Path.GetFullPath(relativeProjectionDirectory);
        }

        private static string GetDefaultMarkedDirectory(string projectionDirectory)
        {
            string fullProjectionDirectory = Path.GetFullPath(projectionDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string parent = Directory.GetParent(fullProjectionDirectory)?.FullName;
            if (!string.IsNullOrEmpty(parent) &&
                string.Equals(Path.GetFileName(fullProjectionDirectory), "Projection", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(parent, "Marked");

            return Path.Combine(fullProjectionDirectory, "Marked");
        }
    }
}
