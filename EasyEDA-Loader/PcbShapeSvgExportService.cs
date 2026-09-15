using PCB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyEDA_Loader
{
    public sealed class PcbShapeSvgExportResult
    {
        internal PcbShapeSvgExportResult(ShapeExportResult result)
        {
            ComponentCount = result.ComponentCount;
            FileCount = result.FileCount;
            PrimitiveCount = result.PrimitiveCount;
            OutputFiles = result.OutputFiles.ToArray();
            DiagnosticsPath = result.DiagnosticsPath;
            Warnings = result.Warnings.ToArray();
            Errors = result.Errors.ToArray();
        }

        public int ComponentCount { get; }
        public int FileCount { get; }
        public int PrimitiveCount { get; }
        public IReadOnlyList<string> OutputFiles { get; }
        public string DiagnosticsPath { get; }
        public IReadOnlyList<string> Warnings { get; }
        public IReadOnlyList<string> Errors { get; }
    }

    public static class PcbShapeSvgExportService
    {
        public static PcbShapeSvgExportResult ExportBoard(
            IPCB_Board board,
            string folder,
            bool includePads,
            Action<ShapeExportProgress> progress = null,
            Func<bool> isCancellationRequested = null,
            bool checkPadGeometry = true)
        {
            if (board == null)
                throw new ArgumentNullException(nameof(board));

            return new PcbShapeSvgExportResult(
                PcbShapeSvgExporter.ExportBoard(
                    board,
                    folder,
                    includePads,
                    progress,
                    isCancellationRequested,
                    checkPadGeometry));
        }

        public static IReadOnlyList<string> PredictBoardOutputFiles(
            IPCB_Board board,
            string folder)
        {
            if (board == null)
                throw new ArgumentNullException(nameof(board));

            return PcbShapeSvgExporter.PredictBoardOutputFiles(board, folder);
        }

        public static PcbShapeSvgExportResult ExportComponent(
            IPCB_Board board,
            string designator,
            string outputPath)
        {
            if (board == null)
                throw new ArgumentNullException(nameof(board));

            return new PcbShapeSvgExportResult(
                PcbShapeSvgExporter.ExportBoardComponent(board, designator, outputPath));
        }

        public static PcbShapeSvgExportResult ExportCurrentLibraryFootprint(
            IPCB_Library pcbLibrary,
            string outputPath)
        {
            if (pcbLibrary == null)
                throw new ArgumentNullException(nameof(pcbLibrary));

            return new PcbShapeSvgExportResult(
                PcbShapeSvgExporter.ExportCurrentPcbLibraryFootprint(pcbLibrary, outputPath));
        }

        public static PcbShapeSvgExportResult ExportBoardAssembly(
            IPCB_Board board,
            string outputPath,
            bool bottom,
            bool mirrorBottom = true)
        {
            if (board == null)
                throw new ArgumentNullException(nameof(board));

            return new PcbShapeSvgExportResult(
                PcbShapeSvgExporter.ExportBoardAssembly(board, outputPath, bottom, mirrorBottom));
        }
    }
}
