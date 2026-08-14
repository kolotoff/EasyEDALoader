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
            Warnings = result.Warnings.ToArray();
            Errors = result.Errors.ToArray();
        }

        public int ComponentCount { get; }
        public int FileCount { get; }
        public int PrimitiveCount { get; }
        public IReadOnlyList<string> OutputFiles { get; }
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
            Func<bool> isCancellationRequested = null)
        {
            if (board == null)
                throw new ArgumentNullException(nameof(board));

            return new PcbShapeSvgExportResult(
                PcbShapeSvgExporter.ExportBoard(
                    board,
                    folder,
                    includePads,
                    progress,
                    isCancellationRequested));
        }

        public static IReadOnlyList<string> PredictBoardOutputFiles(
            IPCB_Board board,
            string folder)
        {
            if (board == null)
                throw new ArgumentNullException(nameof(board));

            return PcbShapeSvgExporter.PredictBoardOutputFiles(board, folder);
        }
    }
}
