using Altium.Edp.Classes;
using DXP;
using PCB;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace EasyEDA_Loader.TronstolE1ScanGerber
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class PluginFactory
    {
        public object InvokeOutputGenerator() => new TronstolE1ScanGerberOutputGenerator();
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class TronstolE1ScanGerberOutputGenerator : OutputGenerator
    {
        private const string GeneratorName = "Tronstol E1 Scan Gerber";
        private string outputDirectory;

        public TronstolE1ScanGerberOutputGenerator() : base(GeneratorName) { }

        protected override void InternalSetOutputPath(string targetFolder) => outputDirectory = targetFolder;

        protected override void InternalPredictOutputFilenames(IStrings filenames)
        {
            filenames?.Add(ResolveOutputFilePath());
        }

        protected override bool InternalRunGenerator()
        {
            string staging = Path.Combine(Path.GetTempPath(), "TronstolE1Scan-" + Guid.NewGuid().ToString("N"));
            bool completed = false;
            try
            {
                ScanExportTrace.Write("Begin export: " + DocumentPath + "; staging: " + staging);
                IClient client = DXP.GlobalVars.Client;
                if (client == null || string.IsNullOrWhiteSpace(DocumentPath))
                    throw new InvalidOperationException("A PCB document is required.");
                client.StartServer("PCB");
                var server = client.GetServerModuleByName("PCB") as IPCB_ServerInterface;
                var board = server?.Internal_GetPCBBoardByPath(DocumentPath) as IPCB_Board
                    ?? server?.Internal_LoadPCBBoardByPath(DocumentPath) as IPCB_Board;
                if (board == null)
                    throw new InvalidOperationException("Unable to load PCB document: " + DocumentPath);

                Directory.CreateDirectory(staging);
                NativeFabricationExport.Export(client, board, DocumentPath, staging);
                string outputFile = ResolveOutputFilePath();
                Notify_BeginGeneratingOutputFile(outputFile);
                try { ScanGerberArchive.Create(staging, outputFile); }
                finally { Notify_FinishGeneratingOutputFile(outputFile); }
                ScanExportTrace.Write("ZIP complete: " + outputFile);
                completed = true;
                return true;
            }
            catch (Exception ex)
            {
                ScanExportTrace.Write("Export failed: " + ex);
                ScanExportTrace.Write("Failed export retained at: " + staging);
                HandleOutputerError(GeneratorName + " generation failed: " + ex.Message);
                return false;
            }
            finally
            {
                // A unique, private temporary directory; never delete the output folder.
                try { if (completed && Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private string ResolveOutputFilePath()
        {
            string folder = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.GetDirectoryName(DocumentPath) : outputDirectory;
            if (string.IsNullOrWhiteSpace(folder)) folder = Environment.CurrentDirectory;
            string name = ExplicitTargetFilename;
            if (string.IsNullOrWhiteSpace(name))
                name = (TargetPrefix ?? string.Empty) + Path.GetFileNameWithoutExtension(DocumentPath) + " " + GeneratorName;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";
            return Path.IsPathRooted(name) ? name : Path.Combine(folder, name);
        }
    }
}
