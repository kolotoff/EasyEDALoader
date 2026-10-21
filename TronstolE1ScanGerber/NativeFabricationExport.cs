extern alias NativeDxpClasses;

using DXP;
using EDP;
using PCB;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace EasyEDA_Loader.TronstolE1ScanGerber
{
    internal static class NativeFabricationExport
    {
        // Altium V7 layer IDs, serialized in the same format as native Gerber OutJobs.
        internal const string GerberParameters =
            "Record=GerberView|GerberUnit=Metric|NumberOfDecimals=6|EmbeddedApertures=True"
            + "|LeadingAndTrailingZeroesMode=SuppressLeadingZeroes|OriginPosition=Relative"
            + "|CentrePlots=False|Panelize=False|SoftwareArcs=False|G54OnApertureChange=False"
            + "|GenerateReports=False|GenerateDRCRulesFile=False|GenerateReliefShapes=True"
            + "|FlashPadShapes=True|FlashAllFills=False|NoRegularPolygons=False"
            + "|IncludeUnconnectedMidLayerPads=False|PlotPositivePlaneLayers=False"
            + "|PlotUsedDrillDrawingLayerPairs=False|PlotUsedDrillGuideLayerPairs=False"
            + "|MirrorDrillDrawingPlots=False|MirrorDrillGuidePlots=False"
            + "|AddToAllPlots.Set=SerializeLayerHash.Version~2,ClassName~TPlotLayerStateArray"
            + "|AddToAllLayerClasses.Set= |LayerClassesPlot.Set= |LayerClassesMirror.Set= "
            + "|Mirror.Set=SerializeLayerHash.Version~2,ClassName~TPlotLayerStateArray"
            + "|Plot.Set=SerializeLayerHash.Version~2,ClassName~TPlotLayerStateArray,"
            + "16777217~1,16842751~1,16973830~1,16973831~1,16973834~1,16973835~1";

        internal const string DrillParameters =
            "Record=DrillView|Units=Metric|NumberOfUnits=4|NumberOfDecimals=6"
            + "|ZeroesMode=SuppressTrailingZeroes|OriginPosition=Relative"
            + "|GenerateDrilledSlotsG85=True|GenerateEIADrillFile=False"
            + "|GenerateSeparatePlatedNonPlatedFiles=False|GenerateSeparateViaTypeFiles=False"
            + "|GenerateToolsByDrillSymbols=False|GenerateBoardEdgeRout=False"
            + "|OptimizeChangeLocationCommands=False";

        public static void Export(IClient client, IPCB_Board board, string documentPath, string staging)
        {
            string jobPath = Path.Combine(staging, "Scan.OutJob");
            File.WriteAllText(jobPath, BuildJob(documentPath), new UTF8Encoding(false));
            object jobDocument = client.Internal_OpenDocument("OUTPUTJOB", jobPath);
            if (jobDocument == null) throw new InvalidOperationException("Unable to open temporary fabrication OutJob.");
            try
            {
                var workspace = client.Internal_GetDXPWorkspace() as IWorkspace;
                var job = workspace?.Internal_DM_GetOutputJobDocumentByPath(jobPath) as IWSM_OutputJobDocument;
                if (job == null) throw new InvalidOperationException("Unable to load temporary fabrication output settings.");
                if (job.GetState_OutputerCount() != 2)
                    throw new InvalidOperationException("Temporary fabrication OutJob must contain Gerber and NC Drill outputs.");
                for (int i = 0; i < job.GetState_OutputerCount(); i++)
                {
                    var output = job.Internal_GetState_Outputer(i) as IOutputer;
                    if (output == null) throw new InvalidOperationException("Native fabrication output is unavailable.");
                    output.DM_SetDocumentPath(documentPath);
                    string generator = output.DM_GeneratorName();
                    if (generator != "Gerber" && generator != "NC Drill")
                        throw new InvalidOperationException("Unexpected native fabrication generator: " + generator);
                    string folder = Path.Combine(staging, generator == "Gerber" ? "gerber" : "drill");
                    Directory.CreateDirectory(folder);
                    // The SDK's IDispatch declaration takes an immutable string,
                    // but Altium writes the exported settings back into this buffer.
                    // Passing string.Empty corrupts memory on return from Gerber.
                    // Use Altium's typed interface with its mutable StringBuilder.
                    var nativeOutput = NativeDxpClasses::DXP.DxpSdkLibrary.GetHardInterfaceByDispatch<RT_Workspace.IOutputer>(output);
                    if (nativeOutput == null)
                        throw new InvalidOperationException("Typed fabrication output interface is unavailable: " + generator);
                    var parameterOverrides = CreateParameterOverrides();
                    ScanExportTrace.Write("Begin native " + generator);
                    if (!nativeOutput.DM_Generate_OutputFilesTo(folder + Path.DirectorySeparatorChar, parameterOverrides))
                        throw new InvalidOperationException(output.DM_GeneratorName() + " export failed.");
                    ScanExportTrace.Write("Finished native " + generator);
                }
                // Use the actual board shape, not arbitrary keepout tracks or its bounding box.
                // The geometric contour includes the SDK's tessellation of curved edges.
                File.WriteAllText(Path.Combine(staging, "outline.gko"), ReadOutline(board), Encoding.ASCII);
                EnsureEmptyLayers(board, staging);
            }
            finally { client.CloseDocument(jobDocument); }
        }

        internal static StringBuilder CreateParameterOverrides() => new StringBuilder(65536);

        private static void EnsureEmptyLayers(IPCB_Board board, string staging)
        {
            string gerberFolder = Path.Combine(staging, "gerber");
            bool top = false, bottom = false, hasHoles = false;
            IPCB_BoardIterator iterator = board.Internal_BoardIterator_Create() as IPCB_BoardIterator;
            if (iterator == null) throw new InvalidOperationException("Unable to inspect PCB legend layers.");
            try
            {
                iterator.SetState_FilterAll();
                object current = iterator.Internal_FirstPCBObject();
                while (current != null)
                {
                    if (current is IPCB_Primitive primitive)
                    {
                        int layer = primitive.Internal_GetState_Layer();
                        if (layer == (int)TLayerConstant.eTopOverlay) top = true;
                        if (layer == (int)TLayerConstant.eBottomOverlay) bottom = true;
                        // SDK PCB interfaces all use the IDispatch IID, so an
                        // 'is IPCB_Pad' cast also succeeds for tracks and text.
                        // Check the native object ID before invoking pad/via members.
                        int objectId = primitive.Internal_GetState_ObjectID();
                        if (objectId == (int)TObjectId.ePadObject && ((IPCB_Pad)current).GetState_HoleSize() > 0)
                            hasHoles = true;
                        if (objectId == (int)TObjectId.eViaObject && ((IPCB_Via)current).GetState_HoleSize() > 0)
                            hasHoles = true;
                    }
                    current = iterator.Internal_NextPCBObject();
                }
            }
            finally { board.BoardIterator_Destroy(ref iterator); }
            foreach (string extension in new[] { ".gto", ".gbo" })
            {
                bool hasArtwork = extension == ".gto" ? top : bottom;
                bool hasFile = Array.Exists(Directory.GetFiles(gerberFolder, "*", SearchOption.AllDirectories),
                    p => Path.GetExtension(p).Equals(extension, StringComparison.OrdinalIgnoreCase));
                if (!hasArtwork && !hasFile)
                    File.WriteAllText(Path.Combine(gerberFolder, "empty" + extension), ScanGerberArchive.EmptyGerber, Encoding.ASCII);
            }
            // A hole-free PCB still needs a shared drill layer in Scan. Gerber is
            // explicitly supported for .drl and can represent an empty layer.
            if (!hasHoles)
            {
                string drillFolder = Path.Combine(staging, "drill");
                bool hasProgram = Array.Exists(Directory.GetFiles(drillFolder, "*", SearchOption.AllDirectories),
                    p => (Path.GetExtension(p).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(p).Equals(".drl", StringComparison.OrdinalIgnoreCase))
                        && File.ReadLines(p).Take(10).Any(line => line.Trim() == "M48" || line.Contains("%FS")));
                if (!hasProgram)
                    File.WriteAllText(Path.Combine(drillFolder, "empty.drl"), ScanGerberArchive.EmptyGerber, Encoding.ASCII);
            }
        }

        internal static string BuildJob(string documentPath)
        {
            if (documentPath.IndexOfAny(new[] { '\r', '\n', '|' }) >= 0)
                throw new ArgumentException("PCB path contains characters unsupported by OutJob settings.");
            var job = new StringBuilder("[OutputJobFile]\r\nVersion=1.0\r\n\r\n[OutputGroup1]\r\nVariantScope=0\r\nVariantName=[No Variations]\r\n");
            string[] types = { "Gerber", "NC Drill" };
            string[] parameters = { GerberParameters, DrillParameters };
            for (int i = 0; i < types.Length; i++)
            {
                string n = (i + 1).ToString(CultureInfo.InvariantCulture);
                job.Append("OutputType" + n + "=" + types[i] + "\r\nOutputName" + n + "=" + types[i]
                    + "\r\nOutputCategory" + n + "=Fabrication\r\nOutputDocumentPath" + n + "=" + documentPath
                    + "\r\nOutputEnabled" + n + "=1\r\nConfiguration" + n + "_Name1=OutputConfigurationParameter1"
                    + "\r\nConfiguration" + n + "_Item1=" + parameters[i] + "|DocumentPath=" + documentPath + "\r\n");
            }
            return job.ToString();
        }

        private static string ReadOutline(IPCB_Board board)
        {
            var outline = board.Internal_GetState_BoardOutline() as IPCB_BoardOutline;
            var polygon = outline?.Internal_BoardOutline_GeometricPolygon() as IPCB_GeometricPolygon;
            if (polygon == null || polygon.GetState_Count() < 1)
                throw new InvalidOperationException("A closed PCB board outline is required.");
            var text = new StringBuilder("G04 PCB board perimeter*\n%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.01*%\nD10*\nG01*\n");
            for (int c = 0; c < polygon.GetState_Count(); c++)
            {
                var contour = polygon.Internal_GetState_Contour(c) as IPCB_Contour;
                int count = contour?.GetState_Count() ?? 0;
                if (count < 3 || count > 1000000) throw new InvalidOperationException("Invalid PCB board contour.");
                for (int p = 0; p <= count; p++)
                {
                    int index = p % count;
                    text.Append("X" + Coordinate((long)contour.GetState_PointX(index) - board.GetState_XOrigin())
                        + "Y" + Coordinate((long)contour.GetState_PointY(index) - board.GetState_YOrigin())
                        + (p == 0 ? "D02*\n" : "D01*\n"));
                }
            }
            return text.Append("M02*\n").ToString();
        }

        private static string Coordinate(long coord)
        {
            // Altium coordinates are 1/10000 mil; avoid Int32 subtraction overflow.
            long value = (long)Math.Round(coord * 2.54, MidpointRounding.AwayFromZero);
            if (Math.Abs(value) >= 10000000000L) throw new InvalidOperationException("PCB coordinate exceeds Gerber 4:6 format.");
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
