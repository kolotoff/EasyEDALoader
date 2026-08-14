using Altium.Edp.Classes;
using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class TronstolE1OutputGenerator : OutputGenerator
    {
        private const string GeneratorName = "Tronstol E1 PNP";
        private readonly TronstolE1Settings settings;
        private string outputDirectory;

        public TronstolE1OutputGenerator()
            : base(GeneratorName)
        {
            settings = new TronstolE1Settings();
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

            filenames.Add(ResolveOutputFilePath());
        }

        protected override bool InternalRunPropertiesForm()
        {
            using (var form = new TronstolE1PropertiesForm(
                settings.RemoveBgaSuffix,
                settings.RemoveSpaceBgaSuffix,
                settings.SkipNfComponents,
                settings.SkipDnpComponents,
                settings.SkipManualSolderingComponents,
                settings.SkipWaveSolderingComponents,
                settings.SkipTestPoints,
                settings.SkipSolderBridge,
                settings.ExportPanelFiducials,
                settings.ExportBoardDimensions,
                settings.ExportEdgeRailsSize,
                settings.RemoveFootprintFromPartNumber))
            {
                if (form.ShowDialog() != DialogResult.OK)
                    return false;

                settings.RemoveBgaSuffix = form.RemoveBgaSuffix;
                settings.RemoveSpaceBgaSuffix = form.RemoveSpaceBgaSuffix;
                settings.SkipNfComponents = form.SkipNfComponents;
                settings.SkipDnpComponents = form.SkipDnpComponents;
                settings.SkipManualSolderingComponents = form.SkipManualSolderingComponents;
                settings.SkipWaveSolderingComponents = form.SkipWaveSolderingComponents;
                settings.SkipTestPoints = form.SkipTestPoints;
                settings.SkipSolderBridge = form.SkipSolderBridge;
                settings.ExportPanelFiducials = form.ExportPanelFiducials;
                settings.ExportBoardDimensions = form.ExportBoardDimensions;
                settings.ExportEdgeRailsSize = form.ExportEdgeRailsSize;
                settings.RemoveFootprintFromPartNumber = form.RemoveFootprintFromPartNumber;
                return true;
            }
        }

        protected override bool InternalRunGenerator()
        {
            string outputFile = ResolveOutputFilePath();
            string skippedOutputFile = ResolveSkippedOutputFilePath(outputFile);
            try
            {
                IPCB_Board board = LoadBoard(DocumentPath);
                if (board == null)
                    throw new InvalidOperationException("Unable to load PCB document: " + DocumentPath);

                string directory = Path.GetDirectoryName(outputFile);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var skippedPlacements = new List<TronstolE1Placement>();
                var outputPlacements = new List<TronstolE1Placement>(
                    ReadOutputPlacements(board, skippedPlacements));

                Notify_BeginGeneratingOutputFile(outputFile);
                try
                {
                    using (var writer = new StreamWriter(outputFile, false, new UTF8Encoding(false)))
                        TronstolE1Csv.Write(writer, outputPlacements);
                }
                finally
                {
                    Notify_FinishGeneratingOutputFile(outputFile);
                }

                Notify_BeginGeneratingOutputFile(skippedOutputFile);
                try
                {
                    using (var writer = new StreamWriter(skippedOutputFile, false, new UTF8Encoding(false)))
                        TronstolE1Csv.Write(writer, skippedPlacements);
                }
                finally
                {
                    Notify_FinishGeneratingOutputFile(skippedOutputFile);
                }

                return true;
            }
            catch (Exception ex)
            {
                HandleOutputerError("Tronstol E1 PNP generation failed: " + ex.Message);
                return false;
            }
        }

        private string ResolveOutputFilePath()
        {
            string explicitName = ExplicitTargetFilename;
            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                if (Path.IsPathRooted(explicitName))
                    return EnsureCsvExtension(explicitName);
                return Path.Combine(ResolveOutputDirectory(), EnsureCsvExtension(explicitName));
            }

            string boardName = Path.GetFileNameWithoutExtension(DocumentPath);
            if (string.IsNullOrWhiteSpace(boardName))
                boardName = "PCB";

            string prefix = string.IsNullOrWhiteSpace(TargetPrefix) ? string.Empty : TargetPrefix;
            return Path.Combine(
                ResolveOutputDirectory(),
                EnsureCsvExtension(prefix + boardName + " Tronstol E1 PNP"));
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

        private static string ResolveSkippedOutputFilePath(string outputFile)
        {
            string directory = Path.GetDirectoryName(outputFile);
            string name = Path.GetFileNameWithoutExtension(outputFile);
            string extension = Path.GetExtension(outputFile);
            if (string.IsNullOrEmpty(extension))
                extension = ".csv";

            return Path.Combine(directory ?? string.Empty, name + " Skipped" + extension);
        }

        private IEnumerable<TronstolE1Placement> ReadOutputPlacements(
            IPCB_Board board,
            IList<TronstolE1Placement> skippedPlacements)
        {
            BoardCoordinateOrigin origin = ReadBoardCoordinateOrigin(board);
            BoardCoordinateBounds bounds = ReadBoardCoordinateBounds(board, origin);

            if (settings.ExportPanelFiducials)
            {
                foreach (TronstolE1Placement fiducial in ReadPanelFiducials(board, origin, bounds))
                    yield return fiducial;
            }

            if (settings.ExportBoardDimensions)
            {
                double boardThicknessMm = ReadBoardThickness(board);
                foreach (TronstolE1Placement boardInfo in ReadBoardDimensions(bounds, boardThicknessMm))
                    yield return boardInfo;
            }

            if (settings.ExportEdgeRailsSize)
                yield return ReadEdgeRailsSize(board, origin, bounds);

            foreach (TronstolE1Placement placement in ReadPlacements(
                board,
                origin,
                bounds,
                skippedPlacements))
                yield return placement;
        }

        private static string EnsureCsvExtension(string path)
        {
            return string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase)
                ? path
                : path + ".csv";
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

        private static BoardCoordinateOrigin ReadBoardCoordinateOrigin(IPCB_Board board)
        {
            try
            {
                return new BoardCoordinateOrigin(board.GetState_XOrigin(), board.GetState_YOrigin());
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Unable to read PCB coordinate origin for Tronstol E1 PNP rows.",
                    ex);
            }
        }

        private static BoardCoordinateBounds ReadBoardCoordinateBounds(
            IPCB_Board board,
            BoardCoordinateOrigin origin)
        {
            if (!TryGetBoardBounds(board, out int left, out int bottom, out int right, out int top))
            {
                throw new InvalidOperationException(
                    "Unable to read board outline bounds for Tronstol E1 PNP rows.");
            }

            if (right <= left || top <= bottom)
            {
                throw new InvalidOperationException(
                    "Invalid board outline bounds for Tronstol E1 PNP rows.");
            }

            return new BoardCoordinateBounds(
                left - origin.X,
                bottom - origin.Y,
                right - origin.X,
                top - origin.Y);
        }

        private static double ReadBoardThickness(IPCB_Board board)
        {
            IPCB_LayerStack layerStack = board?.Internal_GetState_LayerStack() as IPCB_LayerStack;
            if (layerStack == null)
                throw new InvalidOperationException("Unable to read PCB layer stack for the PCB_Size thickness value.");

            object topDielectric = layerStack.Internal_DielectricTop();
            object bottomDielectric = layerStack.Internal_DielectricBottom();
            if (topDielectric == null || bottomDielectric == null)
                throw new InvalidOperationException("Unable to identify the top and bottom dielectric layers for the PCB_Size thickness value.");

            double thicknessMm = Math.Abs(EDP.Utils.CoordToMMs(
                layerStack.Get_ZTop(topDielectric) - layerStack.Get_ZBottom(bottomDielectric)));
            if (double.IsNaN(thicknessMm) || double.IsInfinity(thicknessMm) || thicknessMm <= 0.0)
                throw new InvalidOperationException("The PCB layer stack does not define a positive PCB thickness for the PCB_Size rotation value.");

            return thicknessMm;
        }

        private static IEnumerable<TronstolE1Placement> ReadBoardDimensions(
            BoardCoordinateBounds bounds,
            double boardThicknessMm)
        {
            double leftMm = EDP.Utils.CoordToMMs(bounds.Left);
            double bottomMm = EDP.Utils.CoordToMMs(bounds.Bottom);
            double widthMm = EDP.Utils.CoordToMMs(bounds.Right - bounds.Left);
            double heightMm = EDP.Utils.CoordToMMs(bounds.Top - bounds.Bottom);

            yield return CreateBoardInfoPlacement(
                "PCB_Size1",
                "Board dimensions",
                "PCB_Size",
                widthMm,
                heightMm,
                false,
                1,
                boardThicknessMm);
            yield return CreateBoardInfoPlacement(
                "PCB_BTLC1",
                "Board bottom left corner",
                "PCB_BTLC",
                leftMm,
                bottomMm,
                false,
                2);
            yield return CreateBoardInfoPlacement(
                "PCB_Size2",
                "Board dimensions",
                "PCB_Size",
                widthMm,
                heightMm,
                true,
                3,
                boardThicknessMm);
            yield return CreateBoardInfoPlacement(
                "PCB_BTLC2",
                "Board bottom left corner",
                "PCB_BTLC",
                leftMm,
                bottomMm,
                true,
                4);
        }

        private static TronstolE1Placement ReadEdgeRailsSize(
            IPCB_Board board,
            BoardCoordinateOrigin origin,
            BoardCoordinateBounds bounds)
        {
            if (!TryGetKeepoutBounds(
                board,
                origin,
                out BoardCoordinateBounds keepoutBounds,
                out KeepoutScanDiagnostics diagnostics))
            {
                throw new InvalidOperationException(
                    "Unable to calculate edge rail size from Keep-Out Layer. " + diagnostics);
            }

            double leftRailMm = ClampRailWidth(
                EDP.Utils.CoordToMMs(bounds.Left - keepoutBounds.Left));
            double bottomRailMm = ClampRailWidth(
                EDP.Utils.CoordToMMs(bounds.Bottom - keepoutBounds.Bottom));

            return CreateBoardInfoPlacement(
                "EdgeRail",
                "Edge rails size",
                "EdgeRail",
                leftRailMm,
                bottomRailMm,
                false,
                5);
        }

        private static double ClampRailWidth(double value)
        {
            const double absentRailToleranceMm = 0.05;
            return value <= absentRailToleranceMm ? 0.0 : value;
        }

        private static TronstolE1Placement CreateBoardInfoPlacement(
            string designator,
            string partNumber,
            string footprint,
            double xMillimeters,
            double yMillimeters,
            bool isBottom,
            int order,
            double rotationValue = 0.0)
        {
            return new TronstolE1Placement
            {
                Designator = designator,
                OriginalPartNumber = partNumber,
                PartNumber = partNumber,
                Manufacturer = string.Empty,
                Description = string.Empty,
                Footprint = footprint,
                Carrier = string.Empty,
                ReelPitch = string.Empty,
                CenterXMillimeters = xMillimeters,
                CenterYMillimeters = yMillimeters,
                IsBottom = isBottom,
                RotationDegrees = rotationValue,
                RotationText = rotationValue == 0.0
                    ? "0.0"
                    : rotationValue.ToString("0.####", CultureInfo.InvariantCulture),
                IsBoardInfo = true,
                BoardInfoOrder = order,
                DisableBottomTransform = true
            };
        }

        private readonly struct BoardCoordinateOrigin
        {
            public BoardCoordinateOrigin(int x, int y)
            {
                X = x;
                Y = y;
            }

            public int X { get; }
            public int Y { get; }
        }

        private readonly struct BoardCoordinateBounds
        {
            public BoardCoordinateBounds(int left, int bottom, int right, int top)
            {
                Left = left;
                Bottom = bottom;
                Right = right;
                Top = top;
            }

            public int Left { get; }
            public int Bottom { get; }
            public int Right { get; }
            public int Top { get; }

            public double MirrorAxisYMillimeters =>
                EDP.Utils.CoordToMMs(Bottom + Top) / 2.0;
        }

        private sealed class KeepoutScanDiagnostics
        {
            public bool LayerFilterApplied { get; set; }
            public int ObjectsScanned { get; set; }
            public int KeepoutCandidates { get; set; }
            public int UsableBounds { get; set; }
            public string Error { get; set; }

            public override string ToString()
            {
                var message = string.Format(
                    CultureInfo.InvariantCulture,
                    "Scanned {0} track/arc object(s); {1} matched Keep-Out; {2} had usable bounds; Keep-Out layer filter applied: {3}.",
                    ObjectsScanned,
                    KeepoutCandidates,
                    UsableBounds,
                    LayerFilterApplied ? "yes" : "no");

                return string.IsNullOrWhiteSpace(Error)
                    ? message
                    : message + " Last error: " + Error;
            }
        }

        private static bool TryGetKeepoutBounds(
            IPCB_Board board,
            BoardCoordinateOrigin origin,
            out BoardCoordinateBounds bounds,
            out KeepoutScanDiagnostics diagnostics)
        {
            bounds = default;
            diagnostics = new KeepoutScanDiagnostics();
            if (board == null)
            {
                diagnostics.Error = "PCB board is null.";
                return false;
            }

            bool found = false;
            int left = 0;
            int bottom = 0;
            int right = 0;
            int top = 0;
            IPCB_BoardIterator iterator = null;
            try
            {
                iterator = board.Internal_BoardIterator_Create() as IPCB_BoardIterator;
                if (iterator == null)
                    return false;

                iterator.AddFilter_ObjectSet(
                    CreateObjectSet(
                        (int)TObjectId.eTrackObject,
                        (int)TObjectId.eArcObject));
                diagnostics.LayerFilterApplied = TryApplyKeepoutLayerFilter(iterator);

                object current = iterator.Internal_FirstPCBObject();
                int count = 0;
                while (current != null)
                {
                    count++;
                    if (count > 10000)
                    {
                        diagnostics.Error = "Keep-Out primitive scan limit exceeded.";
                        return false;
                    }

                    diagnostics.ObjectsScanned++;

                    if ((current is IPCB_Track || current is IPCB_Arc)
                        && current is IPCB_Primitive primitive
                        && IsKeepoutPrimitive(primitive))
                    {
                        diagnostics.KeepoutCandidates++;
                        if (!TryReadKeepoutRailBounds(
                                current,
                                out int primitiveLeft,
                                out int primitiveBottom,
                                out int primitiveRight,
                                out int primitiveTop))
                        {
                            current = iterator.Internal_NextPCBObject();
                            continue;
                        }

                        diagnostics.UsableBounds++;
                        primitiveLeft -= origin.X;
                        primitiveRight -= origin.X;
                        primitiveBottom -= origin.Y;
                        primitiveTop -= origin.Y;

                        if (!found)
                        {
                            left = primitiveLeft;
                            bottom = primitiveBottom;
                            right = primitiveRight;
                            top = primitiveTop;
                            found = true;
                        }
                        else
                        {
                            left = Math.Min(left, primitiveLeft);
                            bottom = Math.Min(bottom, primitiveBottom);
                            right = Math.Max(right, primitiveRight);
                            top = Math.Max(top, primitiveTop);
                        }
                    }

                    current = iterator.Internal_NextPCBObject();
                }
            }
            catch (Exception ex)
            {
                diagnostics.Error = ex.Message;
                return false;
            }
            finally
            {
                if (iterator != null)
                    board.BoardIterator_Destroy(ref iterator);
            }

            if (!found || right <= left || top <= bottom)
            {
                if (string.IsNullOrWhiteSpace(diagnostics.Error))
                    diagnostics.Error = "No bounded Keep-Out Layer tracks/arcs found.";
                return false;
            }

            bounds = new BoardCoordinateBounds(left, bottom, right, top);
            return true;
        }

        private static bool TryApplyKeepoutLayerFilter(IPCB_BoardIterator iterator)
        {
            object layerSet = CreatePcbLayerSet(TLayerConstant.eKeepOutLayer);
            if (layerSet != null && TryInvoke(iterator, "AddFilter_IPCB_LayerSet", layerSet))
                return true;

            try
            {
                return TryInvoke(
                    iterator,
                    "AddFilter_LayerSet",
                    CreateObjectSet(new V7_Layer(TLayerConstant.eKeepOutLayer).Number()));
            }
            catch
            {
                return false;
            }
        }

        private static object CreatePcbLayerSet(TLayerConstant layer)
        {
            try
            {
                object server = GetPcbServer();
                object layerSetUtils = TryInvokeResult(server, "LayerSet")
                    ?? TryInvokeResult(server, "Internal_LayerSet");
                if (layerSetUtils == null)
                    return null;

                var v7Layer = new V7_Layer(layer);
                return TryInvokeResult(layerSetUtils, "Factory", v7Layer)
                    ?? TryInvokeResult(layerSetUtils, "Internal_Factory", v7Layer);
            }
            catch
            {
                return null;
            }
        }

        private static IPCB_ServerInterface GetPcbServer()
        {
            IClient client = DXP.GlobalVars.Client;
            if (client == null)
                return null;

            client.StartServer("PCB");
            return client.GetServerModuleByName("PCB") as IPCB_ServerInterface;
        }

        private static bool IsKeepoutPrimitive(IPCB_Primitive primitive)
        {
            bool isKeepout = false;
            try
            {
                isKeepout = primitive.GetState_IsKeepout();
            }
            catch
            {
            }

            if (TryGetPrimitiveLayerNumber(primitive, out int layerNumber))
                return isKeepout || IsKeepoutLayerNumber(layerNumber);

            return isKeepout;
        }

        private static bool IsKeepoutLayerNumber(int layerNumber)
        {
            const int keepoutLayerNumber = 56;
            if (layerNumber == keepoutLayerNumber)
                return true;

            try
            {
                return layerNumber == new V7_Layer(TLayerConstant.eKeepOutLayer).Number();
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadKeepoutRailBounds(
            object primitive,
            out int left,
            out int bottom,
            out int right,
            out int top)
        {
            left = 0;
            bottom = 0;
            right = 0;
            top = 0;

            if (TryReadTrackCoordinateBounds(primitive, out left, out bottom, out right, out top))
                return true;

            if (TryReadArcCoordinateBounds(primitive, out left, out bottom, out right, out top))
                return true;

            if (primitive is IPCB_Primitive pcbPrimitive
                && TryReadPrimitiveBounds(pcbPrimitive, out left, out bottom, out right, out top))
            {
                return true;
            }

            return false;
        }

        private static bool TryReadTrackCoordinateBounds(
            object primitive,
            out int left,
            out int bottom,
            out int right,
            out int top)
        {
            left = 0;
            bottom = 0;
            right = 0;
            top = 0;

            if (!TryGetIntMember(primitive, out int x1, "GetState_X1", "Internal_GetState_X1", "X1")
                || !TryGetIntMember(primitive, out int y1, "GetState_Y1", "Internal_GetState_Y1", "Y1")
                || !TryGetIntMember(primitive, out int x2, "GetState_X2", "Internal_GetState_X2", "X2")
                || !TryGetIntMember(primitive, out int y2, "GetState_Y2", "Internal_GetState_Y2", "Y2"))
            {
                return false;
            }

            left = Math.Min(x1, x2);
            right = Math.Max(x1, x2);
            bottom = Math.Min(y1, y2);
            top = Math.Max(y1, y2);
            return right > left || top > bottom;
        }

        private static bool TryReadArcCoordinateBounds(
            object primitive,
            out int left,
            out int bottom,
            out int right,
            out int top)
        {
            left = 0;
            bottom = 0;
            right = 0;
            top = 0;

            if (!TryGetIntMember(primitive, out int centerX, "GetState_CenterX", "Internal_GetState_CenterX", "XCenter", "CenterX")
                || !TryGetIntMember(primitive, out int centerY, "GetState_CenterY", "Internal_GetState_CenterY", "YCenter", "CenterY")
                || !TryGetIntMember(primitive, out int radius, "GetState_Radius", "Internal_GetState_Radius", "Radius")
                || radius <= 0)
            {
                return false;
            }

            left = centerX - radius;
            right = centerX + radius;
            bottom = centerY - radius;
            top = centerY + radius;
            return right > left && top > bottom;
        }

        private static bool TryGetBoardBounds(
            IPCB_Board board,
            out int left,
            out int bottom,
            out int right,
            out int top)
        {
            left = 0;
            bottom = 0;
            right = 0;
            top = 0;
            try
            {
                if (board == null)
                    return false;

                object outline;
                try
                {
                    outline = board.Internal_GetState_BoardOutline();
                }
                catch
                {
                    return false;
                }

                return TryGetBoardOutlineBounds(outline, out left, out bottom, out right, out top);
            }
            catch
            {
                left = 0;
                bottom = 0;
                right = 0;
                top = 0;
                return false;
            }
        }

        private static bool TryGetIntMember(object target, out int value, params string[] memberNames)
        {
            value = 0;
            if (target == null || memberNames == null)
                return false;

            foreach (string memberName in memberNames)
            {
                object rawValue = TryInvokeResult(target, memberName);
                if (TryConvertToInt(rawValue, out value))
                    return true;

                rawValue = TryGetPropertyValue(target, memberName);
                if (TryConvertToInt(rawValue, out value))
                    return true;
            }

            return false;
        }

        private static bool TryConvertToInt(object rawValue, out int value)
        {
            value = 0;
            if (rawValue == null)
                return false;

            try
            {
                if (rawValue is int intValue)
                {
                    value = intValue;
                    return true;
                }

                value = Convert.ToInt32(rawValue, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object TryGetPropertyValue(object target, string propertyName)
        {
            if (target == null || string.IsNullOrWhiteSpace(propertyName))
                return null;

            try
            {
                return target.GetType().InvokeMember(
                    propertyName,
                    BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public,
                    null,
                    target,
                    null,
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetBoardOutlineBounds(
            object outline,
            out int left,
            out int bottom,
            out int right,
            out int top)
        {
            left = 0;
            bottom = 0;
            right = 0;
            top = 0;

            if (outline is IPCB_BoardOutline boardOutline
                && TryGetBoardOutlinePolygonBounds(boardOutline, out left, out bottom, out right, out top))
            {
                return true;
            }

            if (!(outline is IPCB_Group group))
                return false;

            bool found = false;
            IPCB_GroupIterator iterator = null;
            try
            {
                iterator = group.Internal_GroupIterator_Create() as IPCB_GroupIterator;
                if (iterator == null)
                    return false;

                iterator.SetState_FilterAll();
                object current = iterator.Internal_FirstPCBObject();
                int count = 0;
                while (current != null)
                {
                    count++;
                    if (count > 10000)
                        return false;

                    if (current is IPCB_Primitive primitive
                        && TryReadPrimitiveBounds(
                            primitive,
                            out int primitiveLeft,
                            out int primitiveBottom,
                            out int primitiveRight,
                            out int primitiveTop))
                    {
                        if (!found)
                        {
                            left = primitiveLeft;
                            bottom = primitiveBottom;
                            right = primitiveRight;
                            top = primitiveTop;
                            found = true;
                        }
                        else
                        {
                            left = Math.Min(left, primitiveLeft);
                            bottom = Math.Min(bottom, primitiveBottom);
                            right = Math.Max(right, primitiveRight);
                            top = Math.Max(top, primitiveTop);
                        }
                    }

                    current = iterator.Internal_NextPCBObject();
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                if (iterator != null)
                    group.GroupIterator_Destroy(ref iterator);
            }

            return found && right > left && top > bottom;
        }

        private static bool TryGetBoardOutlinePolygonBounds(
            IPCB_BoardOutline boardOutline,
            out int left,
            out int bottom,
            out int right,
            out int top)
        {
            left = 0;
            bottom = 0;
            right = 0;
            top = 0;
            try
            {
                IPCB_GeometricPolygon polygon =
                    boardOutline.Internal_BoardOutline_GeometricPolygon() as IPCB_GeometricPolygon;
                if (polygon == null)
                    return false;

                return TryReadPolygonBounds(polygon, 0, out left, out bottom, out right, out top)
                    || TryReadPolygonBounds(polygon, 1, out left, out bottom, out right, out top);
            }
            catch
            {
                left = 0;
                bottom = 0;
                right = 0;
                top = 0;
                return false;
            }
        }

        private static bool TryReadPolygonBounds(
            IPCB_GeometricPolygon polygon,
            int firstIndex,
            out int left,
            out int bottom,
            out int right,
            out int top)
        {
            left = 0;
            bottom = 0;
            right = 0;
            top = 0;
            try
            {
                int count = polygon.GetState_Count();
                if (count <= 0 || count > 10000)
                    return false;

                bool found = false;
                for (int offset = 0; offset < count; offset++)
                {
                    int index = firstIndex + offset;
                    IPCB_Contour contour = polygon.Internal_GetState_Contour(index) as IPCB_Contour;
                    if (contour == null
                        || !TryReadContourBounds(
                            contour,
                            out int contourLeft,
                            out int contourBottom,
                            out int contourRight,
                            out int contourTop))
                    {
                        continue;
                    }

                    if (!found)
                    {
                        left = contourLeft;
                        bottom = contourBottom;
                        right = contourRight;
                        top = contourTop;
                        found = true;
                    }
                    else
                    {
                        left = Math.Min(left, contourLeft);
                        bottom = Math.Min(bottom, contourBottom);
                        right = Math.Max(right, contourRight);
                        top = Math.Max(top, contourTop);
                    }
                }

                return found && right > left && top > bottom;
            }
            catch
            {
                left = 0;
                bottom = 0;
                right = 0;
                top = 0;
                return false;
            }
        }

        private static bool TryReadContourBounds(
            IPCB_Contour contour,
            out int left,
            out int bottom,
            out int right,
            out int top)
        {
            return TryReadContourBounds(contour, 0, out left, out bottom, out right, out top)
                || TryReadContourBounds(contour, 1, out left, out bottom, out right, out top);
        }

        private static bool TryReadContourBounds(
            IPCB_Contour contour,
            int firstIndex,
            out int left,
            out int bottom,
            out int right,
            out int top)
        {
            left = 0;
            bottom = 0;
            right = 0;
            top = 0;
            try
            {
                int count = contour.GetState_Count();
                if (count <= 0 || count > 100000)
                    return false;

                for (int offset = 0; offset < count; offset++)
                {
                    int index = firstIndex + offset;
                    int x = contour.GetState_PointX(index);
                    int y = contour.GetState_PointY(index);

                    if (offset == 0)
                    {
                        left = x;
                        right = x;
                        bottom = y;
                        top = y;
                    }
                    else
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        bottom = Math.Min(bottom, y);
                        top = Math.Max(top, y);
                    }
                }

                return right > left && top > bottom;
            }
            catch
            {
                left = 0;
                bottom = 0;
                right = 0;
                top = 0;
                return false;
            }
        }

        private static bool TryReadPrimitiveBounds(
            IPCB_Primitive primitive,
            out int left,
            out int bottom,
            out int right,
            out int top)
        {
            left = 0;
            bottom = 0;
            right = 0;
            top = 0;
            try
            {
                ICoordRect rect = primitive?.Internal_BoundingRectangle();
                if (rect == null)
                    return false;

                left = rect.GetLeft();
                bottom = rect.GetBottom();
                right = rect.GetRight();
                top = rect.GetTop();
                return right > left && top > bottom;
            }
            catch
            {
                left = 0;
                bottom = 0;
                right = 0;
                top = 0;
                return false;
            }
        }

        private IEnumerable<TronstolE1Placement> ReadPanelFiducials(
            IPCB_Board board,
            BoardCoordinateOrigin origin,
            BoardCoordinateBounds bounds)
        {
            IPCB_BoardIterator iterator = null;
            try
            {
                iterator = board.Internal_BoardIterator_Create() as IPCB_BoardIterator;
                if (iterator == null)
                    yield break;

                iterator.AddFilter_ObjectSet(CreateObjectSet((int)TObjectId.ePadObject));
                object current = iterator.Internal_FirstPCBObject();
                while (current != null)
                {
                    if (current is IPCB_Pad pad
                        && TryParsePanelFiducialNumber(pad.GetState_Name(), out int number))
                    {
                        yield return ReadPanelFiducial(pad, number, origin, bounds);
                    }

                    current = iterator.Internal_NextPCBObject();
                }
            }
            finally
            {
                if (iterator != null)
                    board.BoardIterator_Destroy(ref iterator);
            }
        }

        private static TronstolE1Placement ReadPanelFiducial(
            IPCB_Pad pad,
            int number,
            BoardCoordinateOrigin origin,
            BoardCoordinateBounds bounds)
        {
            bool isBottom = IsBottomPad(pad);
            return new TronstolE1Placement
            {
                Designator = "Fiducial" + number.ToString(CultureInfo.InvariantCulture),
                OriginalPartNumber = "PanelFiducial",
                PartNumber = "PanelFiducial",
                Manufacturer = string.Empty,
                Description = string.Empty,
                Footprint = FormatPadFootprint(pad, isBottom),
                Carrier = string.Empty,
                ReelPitch = string.Empty,
                CenterXMillimeters = EDP.Utils.CoordToMMs(pad.GetState_XLocation() - origin.X),
                CenterYMillimeters = EDP.Utils.CoordToMMs(pad.GetState_YLocation() - origin.Y),
                IsBottom = isBottom,
                RotationDegrees = 0.0,
                RotationText = "0.0",
                IsPanelFiducial = true,
                PanelFiducialNumber = number,
                HasBottomMirrorAxisY = true,
                BottomMirrorAxisYMillimeters = bounds.MirrorAxisYMillimeters
            };
        }

        private static bool TryParsePanelFiducialNumber(string name, out int number)
        {
            number = 0;
            const string prefix = "PanelFiducial";
            name = TronstolE1Text.Normalize(name);
            if (name == null || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            string suffix = name.Substring(prefix.Length);
            if (suffix.Length == 0)
                return false;

            for (int index = 0; index < suffix.Length; index++)
            {
                if (!char.IsDigit(suffix[index]))
                    return false;
            }

            return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }

        private static bool IsBottomPad(IPCB_Pad pad)
        {
            return TryGetPrimitiveLayerNumber(pad, out int layerNumber)
                && IsLayer(layerNumber, TLayerConstant.eBottomLayer);
        }

        private static string FormatPadFootprint(IPCB_Pad pad, bool isBottom)
        {
            ReadPadShapeAndXSize(pad, isBottom, out int shape, out int xSize);

            return FormatPadShape(shape)
                + " "
                + EDP.Utils.CoordToMMs(xSize).ToString("0.00", CultureInfo.InvariantCulture)
                + "mm";
        }

        private static string FormatPadShape(int shape)
        {
            if (shape == (int)TShape.eRounded)
                return "Round";
            if (shape == (int)TShape.eRectangular)
                return "Rectangular";
            if (shape == (int)TShape.eRoundedRectangular
                || shape == (int)TShape.eRoundRectShape)
            {
                return "RoundedRectangular";
            }
            if (shape == (int)TShape.eCustomShape)
                return "Custom";

            return "Shape" + shape.ToString(CultureInfo.InvariantCulture);
        }

        private static void ReadPadShapeAndXSize(
            IPCB_Pad pad,
            bool preferBottom,
            out int shape,
            out int xSize)
        {
            shape = preferBottom
                ? pad.Internal_GetState_BotShape()
                : pad.Internal_GetState_TopShape();
            xSize = preferBottom
                ? pad.GetState_BotXSize()
                : pad.GetState_TopXSize();
            if (xSize > 0)
                return;

            shape = preferBottom
                ? pad.Internal_GetState_TopShape()
                : pad.Internal_GetState_BotShape();
            xSize = preferBottom
                ? pad.GetState_TopXSize()
                : pad.GetState_BotXSize();
            if (xSize > 0)
                return;

            shape = pad.Internal_GetState_MidShape();
            xSize = pad.GetState_MidXSize();
        }

        private IEnumerable<TronstolE1Placement> ReadPlacements(
            IPCB_Board board,
            BoardCoordinateOrigin origin,
            BoardCoordinateBounds bounds,
            IList<TronstolE1Placement> skippedPlacements)
        {
            Dictionary<string, CompiledComponentData> compiledComponents = ReadCompiledComponents(board);
            IPCB_BoardIterator iterator = null;
            try
            {
                iterator = board.Internal_BoardIterator_Create() as IPCB_BoardIterator;
                if (iterator == null)
                    yield break;

                iterator.AddFilter_ObjectSet(CreateObjectSet((int)TObjectId.eComponentObject));
                object current = iterator.Internal_FirstPCBObject();
                while (current != null)
                {
                    if (current is IPCB_Component component)
                    {
                        string designator = ReadText(
                            component.Internal_GetState_Name(),
                            component.GetState_SourceDesignator());
                        compiledComponents.TryGetValue(designator, out CompiledComponentData compiledComponent);
                        string footprint = component.GetState_Pattern() ?? string.Empty;
                        string pcbComment = ReadPcbComponentComment(component);
                        TronstolE1Placement placement = ReadPlacement(
                            component,
                            designator,
                            compiledComponent,
                            pcbComment,
                            settings,
                            origin,
                            bounds);
                        bool skip = settings.ShouldSkipFootprintName(footprint)
                            || settings.ShouldSkipComment(pcbComment)
                            || settings.ShouldSkipAssemblyMarkerPartNumber(placement.OriginalPartNumber)
                            || settings.ShouldSkipAssemblyMarkerPartNumber(placement.PartNumber)
                            || (compiledComponent != null
                                && (compiledComponent.IsNoBom
                                    || settings.ShouldSkipComment(compiledComponent.Comment)
                                    || settings.ShouldSkipPartNumber(compiledComponent.PartNumber)
                                    || settings.ShouldSkipSolderingType(compiledComponent.SolderingType)));
                        if (skip)
                        {
                            skippedPlacements?.Add(placement);
                        }
                        else
                        {
                            yield return placement;
                        }
                    }
                    current = iterator.Internal_NextPCBObject();
                }
            }
            finally
            {
                if (iterator != null)
                    board.BoardIterator_Destroy(ref iterator);
            }
        }

        private static TronstolE1Placement ReadPlacement(
            IPCB_Component component,
            string designator,
            CompiledComponentData compiledComponent,
            string pcbComment,
            TronstolE1Settings settings,
            BoardCoordinateOrigin origin,
            BoardCoordinateBounds bounds)
        {
            string footprint = component.GetState_Pattern() ?? string.Empty;
            string partNumber = compiledComponent?.PartNumber ?? string.Empty;
            string componentDescription = FirstNonEmpty(
                compiledComponent?.Description,
                ReadPcbComponentDescription(component));

            return new TronstolE1Placement
            {
                Designator = TronstolE1Text.Normalize(designator),
                OriginalPartNumber = partNumber,
                PartNumber = settings.FormatPartNumber(partNumber, footprint),
                Manufacturer = compiledComponent?.Manufacturer,
                Description = ResolveDescription(
                    partNumber,
                    FirstNonEmpty(pcbComment, compiledComponent?.Comment),
                    componentDescription),
                Footprint = settings.FormatFootprintName(footprint),
                Carrier = compiledComponent?.Carrier,
                ReelPitch = compiledComponent?.ReelPitch,
                CenterXMillimeters = EDP.Utils.CoordToMMs(component.GetState_XLocation() - origin.X),
                CenterYMillimeters = EDP.Utils.CoordToMMs(component.GetState_YLocation() - origin.Y),
                IsBottom = IsBottomComponent(component),
                RotationDegrees = component.GetState_Rotation(),
                HasBottomMirrorAxisY = true,
                BottomMirrorAxisYMillimeters = bounds.MirrorAxisYMillimeters
            };
        }

        private static bool IsBottomComponent(IPCB_Component component)
        {
            if (component == null)
                return false;

            try
            {
                if (component.GetState_FlippedOnLayer())
                    return true;
            }
            catch
            {
            }

            int topCount = 0;
            int bottomCount = 0;
            foreach (IPCB_Primitive primitive in EnumerateComponentPrimitives(component))
            {
                if (!TryGetPrimitiveLayerNumber(primitive, out int layerNumber))
                    continue;

                if (IsLayer(layerNumber, TLayerConstant.eBottomLayer))
                    bottomCount++;
                else if (IsLayer(layerNumber, TLayerConstant.eTopLayer))
                    topCount++;
            }

            return bottomCount > topCount;
        }

        private static IEnumerable<IPCB_Primitive> EnumerateComponentPrimitives(IPCB_Component component)
        {
            if (!(component is IPCB_Group group))
                yield break;

            IPCB_GroupIterator iterator = null;
            try
            {
                iterator = group.Internal_GroupIterator_Create() as IPCB_GroupIterator;
                if (iterator == null)
                    yield break;

                iterator.SetState_FilterAll();
                object current = iterator.Internal_FirstPCBObject();
                while (current != null)
                {
                    if (current is IPCB_Primitive primitive)
                        yield return primitive;

                    current = iterator.Internal_NextPCBObject();
                }
            }
            finally
            {
                if (iterator != null)
                    group.GroupIterator_Destroy(ref iterator);
            }
        }

        private static bool TryGetPrimitiveLayerNumber(IPCB_Primitive primitive, out int layerNumber)
        {
            layerNumber = 0;
            if (primitive == null)
                return false;

            try
            {
                IV7_Layer v7Layer = primitive.Internal_GetState_V7Layer();
                if (v7Layer != null)
                {
                    layerNumber = new V7_Layer(v7Layer).Number();
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                layerNumber = primitive.Internal_GetState_Layer();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLayer(int layerNumber, TLayerConstant layer)
        {
            if (layerNumber == (int)layer)
                return true;

            try
            {
                return layerNumber == new V7_Layer(layer).Number();
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, CompiledComponentData> ReadCompiledComponents(IPCB_Board board)
        {
            var result = new Dictionary<string, CompiledComponentData>(StringComparer.OrdinalIgnoreCase);
            if (!(board is IPCB_BoardEx boardEx))
                return result;

            var fullComponents = boardEx.Internal_GetState_FullComponents() as IPCB_FullComponents;
            var components = fullComponents?.Internal_GetComponentsForCurrentVariant() as IPCB_FullComponentList
                ?? fullComponents?.Internal_GetComponentsForAllVariants() as IPCB_FullComponentList;
            if (components == null)
                return result;

            for (int index = 0; index < components.GetCount(); index++)
            {
                var component = components.Internal_GetItem(index) as IPCB_FullComponent;
                if (component == null || string.IsNullOrWhiteSpace(component.GetDesignator()))
                    continue;

                IPCB_ParameterList parameters = component.Internal_GetParameters() as IPCB_ParameterList;
                string partNumber = ReadParameterValue(
                    parameters,
                    "PartNumber");
                string comment = FirstNonEmpty(
                    ReadParameterValue(parameters, "Comment"),
                    component.GetComment());
                string manufacturer = ReadParameterValue(
                    parameters,
                    "Manufacturer");
                string carrier = ReadParameterValue(
                    parameters,
                    "Carrier");
                string reelPitch = ReadParameterValue(
                    parameters,
                    "ReelPitch");
                string componentDescription = ReadComponentDescription(
                    component,
                    parameters);
                string solderingType = ReadParameterValue(
                    parameters,
                    "SolderingType");
                int componentKind = component.Internal_GetKind();
                result[TronstolE1Text.Normalize(component.GetDesignator())] = new CompiledComponentData
                {
                    PartNumber = partNumber,
                    Comment = comment,
                    Manufacturer = manufacturer,
                    Description = ResolveDescription(partNumber, comment, componentDescription),
                    Carrier = carrier,
                    ReelPitch = reelPitch,
                    SolderingType = solderingType,
                    IsNoBom = componentKind == (int)EDP.TComponentKind.eComponentKind_NetTie_NoBOM
                        || componentKind == (int)EDP.TComponentKind.eComponentKind_Standard_NoBOM
                };
            }

            return result;
        }

        private static string ReadParameterValue(IPCB_ParameterList parameters, string name)
        {
            if (parameters == null || string.IsNullOrWhiteSpace(name))
                return string.Empty;

            for (int index = 0; index < parameters.GetCount(); index++)
            {
                var parameter = parameters.Internal_GetByIndex(index) as IPCB_Parameter;
                if (parameter != null
                    && string.Equals(parameter.GetName(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return TronstolE1Text.Normalize(parameter.GetValue());
                }
            }

            return string.Empty;
        }

        private static string ReadComponentDescription(
            IPCB_FullComponent component,
            IPCB_ParameterList parameters)
        {
            string parameterDescription = ReadParameterValue(parameters, "Description");
            if (!string.IsNullOrEmpty(parameterDescription))
                return parameterDescription;

            return TryGetStringMember(
                component,
                "GetDescription",
                "GetState_ComponentDescription",
                "GetState_Description",
                "Description");
        }

        private static string ReadPcbComponentDescription(IPCB_Component component)
        {
            return FirstNonEmpty(
                TryGetStringMember(component, "GetState_SourceDescription"),
                TryGetStringMember(component, "GetState_FootprintDescription"));
        }

        private static string ReadPcbComponentComment(IPCB_Component component)
        {
            if (component == null)
                return string.Empty;

            try
            {
                return ReadText(component.Internal_GetState_Comment(), string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolveDescription(
            string partNumber,
            string comment,
            string componentDescription)
        {
            return TronstolE1Description.Resolve(partNumber, comment, componentDescription);
        }

        private static string TryGetStringMember(object target, params string[] memberNames)
        {
            if (target == null || memberNames == null)
                return string.Empty;

            foreach (string memberName in memberNames)
            {
                object rawValue = TryInvokeResult(target, memberName);
                string value = TronstolE1Text.Normalize(Convert.ToString(rawValue, CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(value))
                    return value;

                rawValue = TryGetPropertyValue(target, memberName);
                value = TronstolE1Text.Normalize(Convert.ToString(rawValue, CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(value))
                    return value;
            }

            return string.Empty;
        }

        private sealed class CompiledComponentData
        {
            public string PartNumber { get; set; }
            public string Comment { get; set; }
            public string Manufacturer { get; set; }
            public string Description { get; set; }
            public string Carrier { get; set; }
            public string ReelPitch { get; set; }
            public string SolderingType { get; set; }
            public bool IsNoBom { get; set; }
        }

        private static string ReadText(object textObject, string fallback)
        {
            if (textObject is IPCB_Text text)
            {
                string value = FirstNonEmpty(
                    text.GetState_ConvertedString(),
                    text.GetState_Text(),
                    text.GetState_UnderlyingString());
                if (!string.IsNullOrWhiteSpace(value)
                    && !string.Equals(value, ".Designator", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(value, ".Comment", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            return TronstolE1Text.Normalize(fallback);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                string normalized = TronstolE1Text.Normalize(value);
                if (!string.IsNullOrWhiteSpace(normalized))
                    return normalized;
            }
            return string.Empty;
        }

        private static ITransportSet CreateObjectSet(params int[] objectIds)
        {
            var set = new GenericSet();
            int[] mask = set.Mask;
            foreach (int objectId in objectIds)
            {
                int index = objectId / 32;
                if (index >= 0 && index < mask.Length)
                    mask[index] |= unchecked((int)(1u << (objectId % 32)));
            }
            return new TransportSet(set);
        }

        private static bool TryInvoke(object target, string methodName, params object[] args)
        {
            if (target == null)
                return false;

            if (target is IPCB_BoardIterator iterator)
            {
                try
                {
                    switch (methodName)
                    {
                        case "AddFilter_IPCB_LayerSet" when args.Length == 1:
                            iterator.AddFilter_IPCB_LayerSet(args[0]);
                            return true;
                        case "AddFilter_LayerSet" when args.Length == 1 && args[0] is ITransportSet layerSet:
                            iterator.AddFilter_LayerSet(layerSet);
                            return true;
                    }
                }
                catch
                {
                    return false;
                }
            }

            foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name != methodName || method.GetParameters().Length != args.Length)
                    continue;

                try
                {
                    method.Invoke(target, args);
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static object TryInvokeResult(object target, string methodName, params object[] args)
        {
            if (target == null)
                return null;

            if (target is IPCB_ServerInterface pcbServer)
            {
                try
                {
                    switch (methodName)
                    {
                        case "LayerSet" when args.Length == 0:
                            return pcbServer.LayerSet();
                        case "Internal_LayerSet" when args.Length == 0:
                            return pcbServer.Internal_LayerSet();
                    }
                }
                catch
                {
                    return null;
                }
            }

            if (target is IPCB_LayerSetUtils layerSetUtils)
            {
                try
                {
                    switch (methodName)
                    {
                        case "Factory" when args.Length == 1 && args[0] is V7_LayerBase layer:
                            return layerSetUtils.Factory(layer);
                        case "Internal_Factory" when args.Length == 1 && args[0] is IV7_Layer internalLayer:
                            return layerSetUtils.Internal_Factory(internalLayer);
                    }
                }
                catch
                {
                    return null;
                }
            }

            if (target is IPCB_Component component)
            {
                try
                {
                    switch (methodName)
                    {
                        case "GetState_SourceDescription" when args.Length == 0:
                            return component.GetState_SourceDescription();
                        case "GetState_FootprintDescription" when args.Length == 0:
                            return component.GetState_FootprintDescription();
                    }
                }
                catch
                {
                    return null;
                }
            }

            foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name != methodName || method.GetParameters().Length != args.Length)
                    continue;

                try
                {
                    return method.Invoke(target, args);
                }
                catch
                {
                }
            }

            return null;
        }
    }
}
