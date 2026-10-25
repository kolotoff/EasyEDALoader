using System;
using System.IO;

namespace EasyEDA_Loader
{
    internal sealed class ImportLog
    {
        private readonly object sync = new object();
        public string LogPath { get; }
        public int ErrorCount { get; private set; }
        public bool WriteFailed { get; private set; }

        public ImportLog(string logPath = null)
        {
            LogPath = logPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AltiumEE", "import-errors.log");
        }

        public void Error(string context, Exception exception)
        {
            lock (sync)
            {
                ErrorCount++;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(LogPath)));
                    File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
                }
                catch (Exception)
                {
                    WriteFailed = true;
                }
            }
        }
    }
}
