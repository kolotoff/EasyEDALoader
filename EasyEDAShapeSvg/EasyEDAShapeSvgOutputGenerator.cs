using DXP;
using PCB;
using Altium.Edp.Classes;
using EasyEDA_Loader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EasyEDA_Loader.EasyEDAShapeSvg
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class EasyEDAShapeSvgOutputGenerator : OutputGenerator
    {
        private const string GeneratorName = "SVG Shapes";
        private readonly EasyEDAShapeSvgSettings settings;
        private string outputDirectory;

        public EasyEDAShapeSvgOutputGenerator()
            : base(GeneratorName)
        {
            settings = new EasyEDAShapeSvgSettings();
            OutputSettings = settings;
        }

        protected override void InternalSetOutputPath(string targetFolder)
        {
            outputDirectory = targetFolder;
        }

        protected override void InternalPredictOutputFilenames(IStrings filenames)
        {
            if (filenames == null)
                return;

            try
            {
                IPCB_Board board = LoadBoard(DocumentPath);
                if (board == null)
                    return;

                foreach (string filePath in PcbShapeSvgExportService.PredictBoardOutputFiles(board, ResolveOutputDirectory()))
                    filenames.Add(filePath);
            }
            catch
            {
                // Output-job filename prediction is advisory. The generator reports
                // the actual files after a successful export.
            }
        }

        protected override bool InternalRunPropertiesForm()
        {
            using (var form = new EasyEDAShapeSvgPropertiesForm(settings.IncludePads, settings.CheckPadGeometry))
            {
                if (form.ShowDialog() != DialogResult.OK)
                    return false;

                settings.IncludePads = form.IncludePads;
                settings.CheckPadGeometry = form.CheckPadGeometry;
                return true;
            }
        }

        protected override bool InternalRunGenerator()
        {
            string folder = ResolveOutputDirectory();
            ShapeExportResultAdapter exportResult = null;
            try
            {
                IPCB_Board board = LoadBoard(DocumentPath);
                if (board == null)
                    throw new InvalidOperationException("Unable to load PCB document: " + DocumentPath);

                Directory.CreateDirectory(folder);
                using (var progressForm = new ShapeExportProgressForm())
                {
                    progressForm.Show();
                    progressForm.Report(new ShapeExportProgress
                    {
                        Message = "Preparing SVG export...",
                        Detail = folder
                    });

                    IReadOnlyList<string> predictedFiles;
                    try
                    {
                        predictedFiles = PcbShapeSvgExportService.PredictBoardOutputFiles(board, folder);
                    }
                    catch
                    {
                        predictedFiles = Array.Empty<string>();
                    }
                    foreach (string filePath in predictedFiles)
                        Notify_BeginGeneratingOutputFile(filePath);

                    PcbShapeSvgExportResult result;
                    try
                    {
                        result = PcbShapeSvgExportService.ExportBoard(
                            board,
                            folder,
                            settings.IncludePads,
                            progressForm.Report,
                            () =>
                            {
                                progressForm.Pump();
                                return progressForm.IsCancellationRequested;
                            },
                            settings.CheckPadGeometry);
                    }
                    finally
                    {
                        foreach (string filePath in predictedFiles)
                            Notify_FinishGeneratingOutputFile(filePath);
                    }

                    progressForm.Report(new ShapeExportProgress
                    {
                        Message = "SVG export complete",
                        Detail = result.FileCount + " file(s)",
                        Percent = 100
                    });

                    exportResult = new ShapeExportResultAdapter(result);
                }

                if (exportResult.Errors.Count > 0)
                    HandleOutputerError("EasyEDA Shape SVG export completed with errors: " + string.Join(" | ", exportResult.Errors));
                if (exportResult.Warnings.Count > 0)
                    ShowExportWarnings(exportResult.Warnings, exportResult.DiagnosticsPath);

                return exportResult.FileCount > 0;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                HandleOutputerError("EasyEDA Shape SVG generation failed: " + ex.Message);
                return false;
            }
        }

        private sealed class ShapeExportResultAdapter
        {
            public ShapeExportResultAdapter(PcbShapeSvgExportResult result)
            {
                FileCount = result.FileCount;
                DiagnosticsPath = result.DiagnosticsPath;
                Errors = result.Errors;
                Warnings = result.Warnings;
            }

            public int FileCount { get; }
            public string DiagnosticsPath { get; }
            public IReadOnlyList<string> Errors { get; }
            public IReadOnlyList<string> Warnings { get; }
        }

        private static void ShowExportWarnings(IReadOnlyList<string> warnings, string diagnosticsPath)
        {
            string report = string.Join(Environment.NewLine, warnings);
            string summary = "SVG export completed with warnings. Select text or copy the complete report.";
            if (!string.IsNullOrWhiteSpace(diagnosticsPath))
                summary += Environment.NewLine + "Debug file: " + diagnosticsPath;
            using (var dialog = new ShapeExportReportForm(
                "SVG Shapes Warnings",
                summary,
                report))
            {
                dialog.ShowDialog();
            }
        }

        private string ResolveOutputDirectory()
        {
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                return outputDirectory;

            string documentDirectory = Path.GetDirectoryName(DocumentPath);
            return string.IsNullOrWhiteSpace(documentDirectory)
                ? Environment.CurrentDirectory
                : documentDirectory;
        }

        private static IPCB_Board LoadBoard(string documentPath)
        {
            if (string.IsNullOrWhiteSpace(documentPath))
                return null;

            IClient client = DXP.GlobalVars.Client;
            if (client == null)
                return null;

            client.StartServer("PCB");
            var pcbServer = client.GetServerModuleByName("PCB") as IPCB_ServerInterface;
            if (pcbServer == null)
                return null;

            return pcbServer.Internal_GetPCBBoardByPath(documentPath) as IPCB_Board
                ?? pcbServer.Internal_LoadPCBBoardByPath(documentPath) as IPCB_Board;
        }
    }
}
