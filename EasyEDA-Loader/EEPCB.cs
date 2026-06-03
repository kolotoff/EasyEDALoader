using PCB;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace EasyEDA_Loader
{
    internal class EEPCB
    {
        public const double CourtyardMarginMm = 0.25;
        private const double MechanicalLineWidthMm = 0.1;
        private const double OverlayLineWidthMm = 0.2;
        private const double AssemblyTextSizeMm = 1.5;
        private const string AssemblyTextFontName = "ARIAL";
        private const double AssemblyTextAverageCharWidthFactor = 0.45;
        private const double AssemblyTextVisibleHeightFactor = 0.675;
        private const double AssemblyTextGapMm = 0.2;
        private const double AssemblyTextKeepoutMm = 0.05;
        private const double AssemblyTextSearchStepMm = 0.025;

        public class LayerMapException : Exception
        {
            public LayerMapException(string message) : base(message)
            {

            }
        }
        public static IPCB_LibComponent CreateFootprintInLib(string name, string description, double heightMm = 0)
        {
            var pcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
            if (pcbLib == null) return null;
            var footprint = pcbLib.CreateNewComponent();
            pcbLib.SetState_CurrentComponent(footprint);
            var uid = pcbLib.GetUniqueCompName(name);
            footprint.SetState_Pattern(uid);
            SetFootprintMetadata(footprint, description, heightMm);
            AltiumApi.GlobalVars.PCBServer.PostProcess();
            return footprint;
        }

        public static void SetFootprintMetadata(IPCB_LibComponent footprint, string description, double heightMm = 0)
        {
            if (footprint == null)
                return;

            if (!string.IsNullOrWhiteSpace(description))
                footprint.SetState_Description(description);

            if (heightMm > 0)
                footprint.SetState_Height(AltiumApi.MmToCoord(heightMm));
        }

        public static string GetComponentPattern(object footprint)
        {
            if (footprint == null)
                return "";

            return TryInvokeResult(footprint, "GetState_Pattern") as string
                ?? TryInvokeResult(footprint, "GetState_Name") as string
                ?? "";
        }

        public static IPCB_Group GetCurrentPcbLibComponent()
        {
            var pcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
            if (pcbLib == null)
                return null;

            IPCB_Group currentComponent = CoercePcbLibComponent(
                pcbLib,
                TryInvokeResult(pcbLib, "GetState_CurrentComponent")
                    ?? TryInvokeResult(pcbLib, "Internal_GetState_CurrentComponent"));
            if (currentComponent != null)
                return currentComponent;

            return FindCurrentPcbLibComponentFallback(pcbLib);
        }

        private static IPCB_Group FindCurrentPcbLibComponentFallback(IPCB_Library pcbLib)
        {
            if (pcbLib == null)
                return null;

            IPCB_Board activeBoard = TryInvokeResult(pcbLib, "GetState_Board") as IPCB_Board
                ?? TryInvokeResult(pcbLib, "Internal_GetState_Board") as IPCB_Board;
            IPCB_Group firstComponent = null;
            IPCB_Group onlyComponent = null;
            int componentCount = 0;

            foreach (IPCB_Group component in EnumeratePcbLibComponents(pcbLib))
            {
                componentCount++;
                if (firstComponent == null)
                    firstComponent = component;
                onlyComponent = component;

                if (ComponentUsesBoard(component, activeBoard))
                {
                    SetCurrentPcbLibComponent(pcbLib, component);
                    return component;
                }
            }

            if (componentCount == 1)
            {
                SetCurrentPcbLibComponent(pcbLib, onlyComponent);
                return onlyComponent;
            }

            if (TryConvertToInt(TryInvokeResult(pcbLib, "ComponentCount"), out int count) && count == 1 && firstComponent != null)
            {
                SetCurrentPcbLibComponent(pcbLib, firstComponent);
                return firstComponent;
            }

            return null;
        }

        private static IPCB_Group CoercePcbLibComponent(IPCB_Library pcbLib, object component)
        {
            if (component == null)
                return null;

            if (component is IPCB_Group group)
                return group;

            string pattern = TryInvokeResult(component, "GetState_Pattern") as string;
            if (string.IsNullOrWhiteSpace(pattern))
                pattern = TryInvokeResult(component, "GetState_Name") as string;

            if (string.IsNullOrWhiteSpace(pattern))
                return null;

            return TryInvokeResult(pcbLib, "GetComponentByName", pattern) as IPCB_Group
                ?? TryInvokeResult(pcbLib, "Internal_GetComponentByName", pattern) as IPCB_Group;
        }

        private static void SetCurrentPcbLibComponent(IPCB_Library pcbLib, IPCB_Group component)
        {
            if (pcbLib == null || component == null)
                return;

            TryInvoke(pcbLib, "SetState_CurrentComponent", component);
        }

        private static IEnumerable<IPCB_Group> EnumeratePcbLibComponents(IPCB_Library pcbLib)
        {
            object iterator = null;
            try
            {
                iterator = TryInvokeResult(pcbLib, "LibraryIterator_Create")
                    ?? TryInvokeResult(pcbLib, "Internal_LibraryIterator_Create");
                if (iterator == null)
                    yield break;

                TryInvoke(iterator, "SetState_FilterAll");
                object component = TryInvokeResult(iterator, "FirstPCBObject")
                    ?? TryInvokeResult(iterator, "Internal_FirstPCBObject");
                while (component != null)
                {
                    IPCB_Group libComponent = CoercePcbLibComponent(pcbLib, component);
                    if (libComponent != null)
                        yield return libComponent;

                    component = TryInvokeResult(iterator, "NextPCBObject")
                        ?? TryInvokeResult(iterator, "Internal_NextPCBObject");
                }
            }
            finally
            {
                if (iterator != null)
                    TryInvoke(pcbLib, "LibraryIterator_Destroy", iterator);
            }
        }

        private static bool ComponentUsesBoard(IPCB_Group component, IPCB_Board activeBoard)
        {
            if (component == null || activeBoard == null)
                return false;

            IPCB_Board componentBoard = GetComponentBoard(component);
            if (componentBoard == null)
                return false;

            if (ReferenceEquals(componentBoard, activeBoard))
                return true;

            return TryGetBoardId(componentBoard, out int componentBoardId)
                && TryGetBoardId(activeBoard, out int activeBoardId)
                && componentBoardId == activeBoardId;
        }

        private static IPCB_Board GetComponentBoard(object component)
        {
            if (component == null)
                return null;

            try
            {
                if (component is IPCB_LibComponent libComponent)
                    return libComponent.GetState_Board();
            }
            catch
            {
            }

            return TryInvokeResult(component, "GetState_Board") as IPCB_Board
                ?? TryInvokeResult(component, "Internal_GetState_Board") as IPCB_Board;
        }

        public static IPCB_Board GetPcbGroupBoard(IPCB_Group component)
        {
            return GetComponentBoard(component);
        }

        private static bool TryGetBoardId(IPCB_Board board, out int boardId)
        {
            boardId = 0;
            if (board == null)
                return false;

            return TryConvertToInt(TryInvokeResult(board, "GetState_BoardID"), out boardId)
                && boardId != 0;
        }

        public static void AddToPCB(IPCB_Group c, object obj)
        {
            if (c == null || obj == null)
                return;

            GetComponentBoard(c)?.AddPCBObject(obj);
            c.AddPCBObject(obj);
        }

        public static TLayerConstant EELayerToAltium(string layer)
        {
            return EELayerToAltium(layer, true);
        }

        public static TLayerConstant EELayerToAltium(string layer, bool importLcscMechanicalLayers)
        {
            if (TryEELayerToAltium(layer, importLcscMechanicalLayers, out TLayerConstant altiumLayer))
                return altiumLayer;

            throw new LayerMapException($"Skipped layer {layer}");
        }

        public static bool TryEELayerToAltium(string layer, bool importLcscMechanicalLayers, out TLayerConstant altiumLayer)
        {
            layer = FootprintLayerMap.NormalizeLayerName(layer, importLcscMechanicalLayers);
            if (layer == null)
            {
                altiumLayer = default(TLayerConstant);
                return false;
            }

            switch (layer)
            {
                case "TopLayer": altiumLayer = TLayerConstant.eTopLayer; return true;
                case "BottomLayer": altiumLayer = TLayerConstant.eBottomLayer; return true;
                case "TopSilkLayer": altiumLayer = TLayerConstant.eTopOverlay; return true;
                case "BottomSilkLayer": altiumLayer = TLayerConstant.eBottomOverlay; return true;
                case "TopPasteMaskLayer": altiumLayer = TLayerConstant.eTopPaste; return true;
                case "BottomPasteMaskLayer": altiumLayer = TLayerConstant.eBottomPaste; return true;
                case "TopSolderMaskLayer": altiumLayer = TLayerConstant.eTopSolder; return true;
                case "BottomSolderMaskLayer": altiumLayer = TLayerConstant.eBottomSolder; return true;
                case "BoardOutline": altiumLayer = TLayerConstant.eMechanical3; return true;
                case "Multi-Layer": altiumLayer = TLayerConstant.eMultiLayer; return true;
                case "TopAssembly": altiumLayer = TLayerConstant.eMechanical2; return true;
                case "Mechanical": altiumLayer = TLayerConstant.eMechanical2; return true;
                case "3DModel": altiumLayer = TLayerConstant.eMechanical1; return true;
                default: throw new LayerMapException($"Invalid layer {layer}");
            }
        }

        public static double NormalizeLineWidth(TLayerConstant layer, double width)
        {
            if (layer == TLayerConstant.eTopOverlay || layer == TLayerConstant.eBottomOverlay)
                return Math.Max(OverlayLineWidthMm, NormalizeImperialWidth(width));

            if (layer == TLayerConstant.eMechanical2 || layer == TLayerConstant.eMechanical3)
                return Math.Max(MechanicalLineWidthMm, NormalizeImperialWidth(width));

            return width;
        }

        private static double NormalizeImperialWidth(double width)
        {
            if (width <= 0)
                return width;

            double imperialMultiple = width / 0.254;
            double roundedMultiple = Math.Round(imperialMultiple);
            if (roundedMultiple >= 1 && Math.Abs(imperialMultiple - roundedMultiple) < 0.05)
                return 0.2 * roundedMultiple;

            return width;
        }

        public static IPCB_Track CreateLine(IPCB_Group c, TLayerConstant layer, double x1, double y1, double x2, double y2, double width)
        {
            var track = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eTrackObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Track;
            if (track == null) return null;
            width = NormalizeLineWidth(layer, width);
            track.SetState_Width(AltiumApi.MmToCoord(width));
            track.SetState_V7Layer(new V7_Layer(layer));
            track.SetState_X1(AltiumApi.MmToCoord(x1) + c.GetState_XLocation());
            track.SetState_X2(AltiumApi.MmToCoord(x2) + c.GetState_XLocation());
            track.SetState_Y1(AltiumApi.MmToCoord(y1) + c.GetState_YLocation());
            track.SetState_Y2(AltiumApi.MmToCoord(y2) + c.GetState_YLocation());
            return track;
        }

        public static IPCB_Arc CreateArc(IPCB_Group c, TLayerConstant layer, double x, double y, double rad, double width, double startAngle, double endAngle)
        {
            var circle = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eArcObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Arc;
            if (circle == null) return null;
            width = NormalizeLineWidth(layer, width);
            circle.SetState_CenterX(AltiumApi.MmToCoord(x) + c.GetState_XLocation());
            circle.SetState_CenterY(AltiumApi.MmToCoord(y) + c.GetState_YLocation());
            circle.SetState_Radius(AltiumApi.MmToCoord(rad));
            circle.SetState_LineWidth(AltiumApi.MmToCoord(width));
            circle.SetState_StartAngle(startAngle);
            circle.SetState_EndAngle(endAngle);
            circle.SetState_V7Layer(new V7_Layer(layer));
            return circle;
        }

        public static IPCB_Pad4 CreatePTH(IPCB_Group c, TLayerConstant layer, TExtendedHoleType holeType, TShape padShape, double x, double y, double height, double width, double holeSize, string name, bool plated, double rotation)
        {
            var pth = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.ePadObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Pad4;
            if (pth == null) return null;
            pth.SetState_Mode(TPadMode.ePadMode_Simple);
            pth.SetState_Name(name);
            pth.SetState_HoleType(holeType);
            pth.SetState_HoleSize(AltiumApi.MmToCoord(holeSize));
            pth.SetState_Rotation(rotation);
            pth.SetState_Plated(plated);
            pth.SetState_TopShape(padShape);
            pth.SetState_TopXSize(AltiumApi.MmToCoord(width));
            pth.SetState_TopYSize(AltiumApi.MmToCoord(height));
            pth.SetState_V7Layer(new V7_Layer(layer));
            pth.SetState_XLocation(AltiumApi.MmToCoord(x) + c.GetState_XLocation());
            pth.SetState_YLocation(AltiumApi.MmToCoord(y) + c.GetState_YLocation());
            return pth;
        }

        public static IPCB_Via CreateVia(IPCB_Group c, TLayerConstant layerStart, TLayerConstant layerEnd, double x, double y, double size, double holeSize)
        {
            var via = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eViaObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Via;
            if (via == null) return null;
            via.SetState_HighLayer(new V7_Layer(layerStart));
            via.SetState_LowLayer(new V7_Layer(layerEnd));
            via.SetState_XLocation(AltiumApi.MmToCoord(x) + c.GetState_XLocation());
            via.SetState_YLocation(AltiumApi.MmToCoord(y) + c.GetState_YLocation());
            via.SetState_HoleSize(AltiumApi.MmToCoord(holeSize));
            via.SetState_Size(AltiumApi.MmToCoord(size));
            return via;
        }

        public static IPCB_Text3 CreateText(IPCB_Group c, TLayerConstant layer, string text, double x, double y, double width, double size, double rotation)
        {
            var textObject = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eTextObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Text3;
            if (textObject == null) return null;
            bool isAssemblyText = IsAssemblyText(layer, text);
            if (isAssemblyText)
            {
                size = AssemblyTextSizeMm;
                width = MechanicalLineWidthMm;
            }

            width = NormalizeLineWidth(layer, width);
            textObject.SetState_V7Layer(new V7_Layer(layer));
            textObject.SetState_XLocation(AltiumApi.MmToCoord(x) + c.GetState_XLocation());
            textObject.SetState_YLocation(AltiumApi.MmToCoord(y) + c.GetState_YLocation());
            textObject.SetState_Text(text);
            textObject.SetState_Size(AltiumApi.MmToCoord(size));
            textObject.SetState_Width(AltiumApi.MmToCoord(width));
            textObject.SetState_Rotation(rotation);
            if (isAssemblyText)
                ApplyAssemblyTextStyle(textObject);

            return textObject;
        }

        private static bool IsAssemblyText(TLayerConstant layer, string text)
        {
            if (layer != TLayerConstant.eMechanical2)
                return false;

            string value = (text ?? string.Empty).Trim();
            return string.Equals(value, ".Designator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, ".Comment", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyAssemblyTextStyle(IPCB_Text3 textObject)
        {
            textObject.SetState_UseTTFonts(true);
            textObject.SetState_FontName(AssemblyTextFontName);
            textObject.SetState_Bold(false);
            textObject.SetState_Italic(false);
        }

        public static void AddRectangle(IPCB_Group c, TLayerConstant layer, double x1, double y1, double x2, double y2, double width)
        {
            AddToPCB(c, CreateLine(c, layer, x1, y1, x2, y1, width));
            AddToPCB(c, CreateLine(c, layer, x2, y1, x2, y2, width));
            AddToPCB(c, CreateLine(c, layer, x2, y2, x1, y2, width));
            AddToPCB(c, CreateLine(c, layer, x1, y2, x1, y1, width));
        }

        public static int Add3dBodyProjection(IPCB_Group c, IReadOnlyList<StepSilhouettePrimitive> primitives)
        {
            if (c == null || primitives == null)
                return 0;

            int count = 0;
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                object pcbPrimitive = null;
                if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                {
                    pcbPrimitive = CreateLine(c, TLayerConstant.eMechanical2, primitive.X1, primitive.Y1, primitive.X2, primitive.Y2, MechanicalLineWidthMm);
                }
                else if (primitive.Kind == StepSilhouettePrimitiveKind.Arc)
                {
                    pcbPrimitive = CreateArc(c, TLayerConstant.eMechanical2, primitive.CenterX, primitive.CenterY, primitive.Radius, MechanicalLineWidthMm, primitive.StartAngle, primitive.EndAngle);
                }

                if (pcbPrimitive == null)
                    continue;

                AddToPCB(c, pcbPrimitive);
                count++;
            }

            return count;
        }

        public static void AddCourtyard(IPCB_Group c, double width, double height)
        {
            if (width <= 0 || height <= 0)
                return;

            double halfWidth = width / 2.0 + CourtyardMarginMm;
            double halfHeight = height / 2.0 + CourtyardMarginMm;
            AddRectangle(c, TLayerConstant.eMechanical3, -halfWidth, -halfHeight, halfWidth, halfHeight, MechanicalLineWidthMm);

            double centerMark = Math.Min(1.0, Math.Max(0.5, Math.Min(halfWidth, halfHeight) / 3.0));
            AddToPCB(c, CreateLine(c, TLayerConstant.eMechanical3, -centerMark, 0, centerMark, 0, MechanicalLineWidthMm));
            AddToPCB(c, CreateLine(c, TLayerConstant.eMechanical3, 0, -centerMark, 0, centerMark, MechanicalLineWidthMm));
        }

        public static void AddAssemblyTexts(IPCB_Group c, bool hasDesignator, bool hasComment, double bodyHeight, IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives = null)
        {
            ProjectionTextLocations locations = ChooseProjectionTextLocations(projectionPrimitives);
            if (!hasDesignator)
                AddToPCB(c, CreateText(c, TLayerConstant.eMechanical2, ".Designator", locations.DesignatorX, locations.DesignatorY, MechanicalLineWidthMm, AssemblyTextSizeMm, 0));
            if (!hasComment)
                AddToPCB(c, CreateText(c, TLayerConstant.eMechanical2, ".Comment", locations.CommentX, locations.CommentY, MechanicalLineWidthMm, AssemblyTextSizeMm, 0));
        }

        public static int ClearMechanical2Projection(IPCB_Group component)
        {
            if (component == null)
                return 0;

            var primitivesToRemove = new List<object>();
            var seenPrimitives = new HashSet<object>();
            foreach (object primitive in EnumerateComponentPrimitives(component))
            {
                if (IsPrimitiveOnLayer(primitive, TLayerConstant.eMechanical2) && seenPrimitives.Add(primitive))
                    primitivesToRemove.Add(primitive);
            }

            IPCB_Board board = GetComponentBoard(component);
            foreach (object primitive in EnumerateBoardPrimitives(board))
            {
                if (IsPrimitiveOnLayer(primitive, TLayerConstant.eMechanical2) && seenPrimitives.Add(primitive))
                    primitivesToRemove.Add(primitive);
            }

            foreach (object primitive in primitivesToRemove)
            {
                TryInvoke(component, "RemovePCBObject", primitive);
                TryInvoke(board, "RemovePCBObject", primitive);
            }

            return primitivesToRemove.Count;
        }

        public static int ReprojectComponentBodySilhouette(IPCB_Group component)
        {
            if (component == null)
                return 0;

            int projectionCount = 0;
            var allProjectionPrimitives = new List<StepSilhouettePrimitive>();
            foreach (IPCB_ComponentBody body in EnumerateComponentBodies(component))
            {
                if (!TryGetComponentBodyBoundsMm(component, body, out StepSilhouetteBounds bodyBounds))
                    continue;

                byte[] stepData = TryExportComponentBodyStep(body);
                if (stepData == null || stepData.Length == 0)
                    continue;

                TryGetComponentBodyModelState(body, out double rotX, out double rotY, out double rotZ);
                IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives = StepSilhouetteProjection.Generate(
                    stepData,
                    new StepSilhouettePlacement
                    {
                        TargetBounds = bodyBounds,
                        RotX = rotX,
                        RotY = rotY,
                        RotZ = rotZ
                    });

                projectionCount += Add3dBodyProjection(component, projectionPrimitives);
                allProjectionPrimitives.AddRange(projectionPrimitives);
            }

            if (projectionCount == 0)
                throw new InvalidOperationException("The active footprint does not contain an exportable 3D body to reproject.");

            AddAssemblyTexts(component, false, false, 0, allProjectionPrimitives);
            return projectionCount;
        }

        public static int AlignComponentBodiesToPads(IPCB_Group component)
        {
            if (component == null)
                return 0;

            StepSilhouetteBounds padBounds = MeasurePadBounds(component);
            if (padBounds == null)
                throw new InvalidOperationException("The active footprint does not contain pads to align against.");

            int alignedCount = 0;
            foreach (IPCB_ComponentBody body in EnumerateComponentBodies(component))
            {
                if (CenterComponentBodyMm(component, body, padBounds.CenterX, padBounds.CenterY))
                    alignedCount++;
            }

            return alignedCount;
        }

        private static ProjectionTextLocations ChooseProjectionTextLocations(IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives)
        {
            double designatorHeight = AssemblyTextSizeMm;
            double designatorAnchorWidth = ProjectionTextWidth(".De");
            double commentWidth = ProjectionTextWidth(".Comment");
            StepSilhouetteBounds bounds = MeasureProjectionPrimitives(projectionPrimitives);

            if (bounds == null)
            {
                double centerX = 0;
                double centerY = 0;
                return new ProjectionTextLocations
                {
                    DesignatorX = centerX - designatorAnchorWidth / 2.0,
                    DesignatorY = centerY - designatorHeight / 2.0,
                    CommentX = centerX - commentWidth / 2.0,
                    CommentY = centerY - designatorHeight - AssemblyTextGapMm - ProjectionTrueTypeVisibleHeight()
                };
            }

            double projectionCenterX = bounds.CenterX;
            double projectionCenterY = bounds.CenterY;
            if (TryChooseProjectionDesignatorCenter(projectionPrimitives, projectionCenterX, projectionCenterY, bounds.Bottom, bounds.Top, designatorHeight, out double designatorY))
            {
                return new ProjectionTextLocations
                {
                    DesignatorX = projectionCenterX - designatorAnchorWidth / 2.0,
                    DesignatorY = designatorY,
                    CommentX = projectionCenterX - commentWidth / 2.0,
                    CommentY = bounds.Bottom - AssemblyTextGapMm - ProjectionTrueTypeVisibleHeight()
                };
            }

            return new ProjectionTextLocations
            {
                DesignatorX = projectionCenterX - designatorAnchorWidth / 2.0,
                DesignatorY = bounds.Top + AssemblyTextGapMm,
                CommentX = projectionCenterX - commentWidth / 2.0,
                CommentY = bounds.Bottom - AssemblyTextGapMm - ProjectionTrueTypeVisibleHeight()
            };
        }

        private static bool TryChooseProjectionDesignatorCenter(
            IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives,
            double centerX,
            double centerY,
            double bottom,
            double top,
            double textHeight,
            out double designatorY)
        {
            designatorY = centerY - textHeight / 2.0;
            double minCenterY = bottom + textHeight / 2.0;
            double maxCenterY = top - textHeight / 2.0;
            if (maxCenterY < minCenterY)
                return false;

            double maxOffset = Math.Max(Math.Abs(centerY - minCenterY), Math.Abs(maxCenterY - centerY));
            double offset = 0;
            while (offset <= maxOffset + 0.0000001)
            {
                double candidateCenterY = centerY - offset;
                if (candidateCenterY >= minCenterY && candidateCenterY <= maxCenterY &&
                    ProjectionDesignatorAnchorIsClear(projectionPrimitives, centerX, candidateCenterY, textHeight))
                {
                    designatorY = candidateCenterY - textHeight / 2.0;
                    return true;
                }

                if (offset > 0)
                {
                    candidateCenterY = centerY + offset;
                    if (candidateCenterY >= minCenterY && candidateCenterY <= maxCenterY &&
                        ProjectionDesignatorAnchorIsClear(projectionPrimitives, centerX, candidateCenterY, textHeight))
                    {
                        designatorY = candidateCenterY - textHeight / 2.0;
                        return true;
                    }
                }

                offset += AssemblyTextSearchStepMm;
            }

            return false;
        }

        private static bool ProjectionDesignatorAnchorIsClear(
            IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives,
            double centerX,
            double centerY,
            double textHeight)
        {
            double anchorWidth = ProjectionTextWidth(".De");
            double left = centerX - anchorWidth / 2.0;
            double right = centerX + anchorWidth / 2.0;
            double textY = centerY - textHeight / 2.0;
            double bottom = textY;
            double top = textY + ProjectionTrueTypeVisibleHeight();
            return !ProjectionPrimitivesIntersectRect(projectionPrimitives, left, bottom, right, top);
        }

        private static bool ProjectionPrimitivesIntersectRect(
            IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives,
            double left,
            double bottom,
            double right,
            double top)
        {
            if (projectionPrimitives == null)
                return false;

            left -= AssemblyTextKeepoutMm;
            bottom -= AssemblyTextKeepoutMm;
            right += AssemblyTextKeepoutMm;
            top += AssemblyTextKeepoutMm;

            foreach (StepSilhouettePrimitive primitive in projectionPrimitives)
            {
                if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                {
                    if (SegmentIntersectsRect(primitive.X1, primitive.Y1, primitive.X2, primitive.Y2, left, bottom, right, top))
                        return true;
                }
                else
                {
                    StepSilhouetteBounds arcBounds = BoundsForArc(primitive.CenterX, primitive.CenterY, primitive.Radius, primitive.StartAngle, primitive.EndAngle);
                    if (!(arcBounds.Right < left || arcBounds.Left > right || arcBounds.Top < bottom || arcBounds.Bottom > top))
                        return true;
                }
            }

            return false;
        }

        private static StepSilhouetteBounds MeasureProjectionPrimitives(IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives)
        {
            if (projectionPrimitives == null || projectionPrimitives.Count == 0)
                return null;

            StepSilhouetteBounds bounds = null;
            foreach (StepSilhouettePrimitive primitive in projectionPrimitives)
            {
                StepSilhouetteBounds primitiveBounds = BoundsForPrimitive(primitive);
                if (primitiveBounds == null)
                    continue;

                if (bounds == null)
                {
                    bounds = primitiveBounds;
                    continue;
                }

                bounds.Left = Math.Min(bounds.Left, primitiveBounds.Left);
                bounds.Bottom = Math.Min(bounds.Bottom, primitiveBounds.Bottom);
                bounds.Right = Math.Max(bounds.Right, primitiveBounds.Right);
                bounds.Top = Math.Max(bounds.Top, primitiveBounds.Top);
            }

            return bounds;
        }

        private static StepSilhouetteBounds BoundsForPrimitive(StepSilhouettePrimitive primitive)
        {
            if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
            {
                return new StepSilhouetteBounds
                {
                    Left = Math.Min(primitive.X1, primitive.X2),
                    Bottom = Math.Min(primitive.Y1, primitive.Y2),
                    Right = Math.Max(primitive.X1, primitive.X2),
                    Top = Math.Max(primitive.Y1, primitive.Y2)
                };
            }

            return BoundsForArc(primitive.CenterX, primitive.CenterY, primitive.Radius, primitive.StartAngle, primitive.EndAngle);
        }

        private static StepSilhouetteBounds BoundsForArc(double centerX, double centerY, double radius, double startAngle, double endAngle)
        {
            var bounds = new StepSilhouetteBounds
            {
                Left = double.PositiveInfinity,
                Bottom = double.PositiveInfinity,
                Right = double.NegativeInfinity,
                Top = double.NegativeInfinity
            };

            AddArcBoundsPoint(bounds, centerX, centerY, radius, startAngle);
            AddArcBoundsPoint(bounds, centerX, centerY, radius, endAngle);
            foreach (double cardinalAngle in new[] { 0.0, 90.0, 180.0, 270.0, 360.0, 450.0, 540.0, 630.0, 720.0 })
            {
                if (AngleInArcSweep(cardinalAngle, startAngle, endAngle))
                    AddArcBoundsPoint(bounds, centerX, centerY, radius, cardinalAngle);
            }

            return bounds;
        }

        private static void AddArcBoundsPoint(StepSilhouetteBounds bounds, double centerX, double centerY, double radius, double angle)
        {
            double radians = angle * Math.PI / 180.0;
            double x = centerX + radius * Math.Cos(radians);
            double y = centerY + radius * Math.Sin(radians);
            bounds.Left = Math.Min(bounds.Left, x);
            bounds.Bottom = Math.Min(bounds.Bottom, y);
            bounds.Right = Math.Max(bounds.Right, x);
            bounds.Top = Math.Max(bounds.Top, y);
        }

        private static bool AngleInArcSweep(double testAngle, double startAngle, double endAngle)
        {
            while (testAngle < startAngle)
                testAngle += 360.0;
            return testAngle >= startAngle && testAngle <= endAngle;
        }

        private static bool SegmentIntersectsRect(double x1, double y1, double x2, double y2, double left, double bottom, double right, double top)
        {
            if (Math.Max(x1, x2) < left || Math.Min(x1, x2) > right || Math.Max(y1, y2) < bottom || Math.Min(y1, y2) > top)
                return false;

            if (PointInsideRect(x1, y1, left, bottom, right, top) || PointInsideRect(x2, y2, left, bottom, right, top))
                return true;

            return SegmentsIntersect(x1, y1, x2, y2, left, bottom, right, bottom)
                || SegmentsIntersect(x1, y1, x2, y2, right, bottom, right, top)
                || SegmentsIntersect(x1, y1, x2, y2, right, top, left, top)
                || SegmentsIntersect(x1, y1, x2, y2, left, top, left, bottom);
        }

        private static bool PointInsideRect(double x, double y, double left, double bottom, double right, double top)
        {
            return x >= left && x <= right && y >= bottom && y <= top;
        }

        private static bool SegmentsIntersect(double a1X, double a1Y, double a2X, double a2Y, double b1X, double b1Y, double b2X, double b2Y)
        {
            double d1 = SegmentDirection(b1X, b1Y, b2X, b2Y, a1X, a1Y);
            double d2 = SegmentDirection(b1X, b1Y, b2X, b2Y, a2X, a2Y);
            double d3 = SegmentDirection(a1X, a1Y, a2X, a2Y, b1X, b1Y);
            double d4 = SegmentDirection(a1X, a1Y, a2X, a2Y, b2X, b2Y);

            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
                return true;

            return Math.Abs(d1) < 0.000001 && PointOnSegment(b1X, b1Y, b2X, b2Y, a1X, a1Y)
                || Math.Abs(d2) < 0.000001 && PointOnSegment(b1X, b1Y, b2X, b2Y, a2X, a2Y)
                || Math.Abs(d3) < 0.000001 && PointOnSegment(a1X, a1Y, a2X, a2Y, b1X, b1Y)
                || Math.Abs(d4) < 0.000001 && PointOnSegment(a1X, a1Y, a2X, a2Y, b2X, b2Y);
        }

        private static double SegmentDirection(double aX, double aY, double bX, double bY, double cX, double cY)
        {
            return (cX - aX) * (bY - aY) - (bX - aX) * (cY - aY);
        }

        private static bool PointOnSegment(double aX, double aY, double bX, double bY, double cX, double cY)
        {
            return Math.Abs(SegmentDirection(aX, aY, bX, bY, cX, cY)) < 0.000001
                && cX >= Math.Min(aX, bX)
                && cX <= Math.Max(aX, bX)
                && cY >= Math.Min(aY, bY)
                && cY <= Math.Max(aY, bY);
        }

        private static double ProjectionTextWidth(string text)
        {
            return (text ?? string.Empty).Length * AssemblyTextSizeMm * AssemblyTextAverageCharWidthFactor;
        }

        private static double ProjectionTrueTypeVisibleHeight()
        {
            return AssemblyTextSizeMm * AssemblyTextVisibleHeightFactor;
        }

        private sealed class ProjectionTextLocations
        {
            public double DesignatorX { get; set; }
            public double DesignatorY { get; set; }
            public double CommentX { get; set; }
            public double CommentY { get; set; }
        }

        public static IPCB_ComponentBody CreateComponentBody(IPCB_Group c, string fileName, double rx, double ry, double rz, double x, double y, double z, string identifier = null, double overallHeightMm = 0)
        {
            var stepModel = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eComponentBodyObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_ComponentBody;
            if (stepModel == null) return null;
            var model = stepModel.ModelFactory_FromFilename(fileName, false);
            if (model == null) return null;
            model.SetState(rx, ry, rz, AltiumApi.MmToCoord(z));
            stepModel.SetModel(model);
            stepModel.SetState_FromModel();
            model.SetState(rx, ry, rz, AltiumApi.MmToCoord(z));
            stepModel.SetModel(model);
            TrySetLayer(stepModel, TLayerConstant.eMechanical1);
            string modelIdentifier = !string.IsNullOrWhiteSpace(identifier)
                ? identifier
                : Path.GetFileNameWithoutExtension(fileName);
            SetComponentBodyIdentifier(stepModel, modelIdentifier);
            SetComponentBodyHeights(stepModel, z, overallHeightMm);
            if (TryGetComponentBodyBoundsMm(c, stepModel, out StepSilhouetteBounds currentBounds))
            {
                FootprintModelMove move = FootprintModelPlacement.CalculateCenteringMoveMm(currentBounds, x, y);
                stepModel.MoveByXY(AltiumApi.MmToCoord(move.XMm), AltiumApi.MmToCoord(move.YMm));
            }
            else
            {
                // Some Altium builds expose body bounds only after insertion; keep the legacy origin move as a fallback.
                IPCB_Board board = GetComponentBoard(c);
                int originX = board != null ? board.GetState_XOrigin() : 0;
                int originY = board != null ? board.GetState_YOrigin() : 0;
                stepModel.MoveByXY(AltiumApi.MmToCoord(x) + originX, AltiumApi.MmToCoord(y) + originY);
            }
            return stepModel;
        }

        public static void SetComponentBodyIdentifier(IPCB_ComponentBody body, string identifier)
        {
            if (body == null || string.IsNullOrWhiteSpace(identifier))
                return;

            body.SetState_Identifier(identifier);
            body.GraphicallyInvalidate();
        }

        public static void SetComponentBodyIdentifiers(IPCB_LibComponent component, string identifier)
        {
            if (component == null || string.IsNullOrWhiteSpace(identifier))
                return;

            foreach (IPCB_ComponentBody body in EnumerateComponentBodies(component))
                SetComponentBodyIdentifier(body, identifier);
        }

        private static IEnumerable<object> EnumerateComponentPrimitives(IPCB_Group component)
        {
            if (component == null)
                yield break;

            object iterator = null;
            try
            {
                iterator = TryInvokeResult(component, "GroupIterator_Create")
                    ?? TryInvokeResult(component, "Internal_GroupIterator_Create");
                if (iterator == null)
                    yield break;

                object primitive = TryInvokeResult(iterator, "FirstPCBObject")
                    ?? TryInvokeResult(iterator, "Internal_FirstPCBObject");
                while (primitive != null)
                {
                    yield return primitive;

                    primitive = TryInvokeResult(iterator, "NextPCBObject")
                        ?? TryInvokeResult(iterator, "Internal_NextPCBObject");
                }
            }
            finally
            {
                if (iterator != null)
                    TryInvoke(component, "GroupIterator_Destroy", iterator);
            }
        }

        private static IEnumerable<object> EnumerateBoardPrimitives(IPCB_Board board)
        {
            if (board == null)
                yield break;

            object iterator = null;
            try
            {
                iterator = TryInvokeResult(board, "BoardIterator_Create")
                    ?? TryInvokeResult(board, "Internal_BoardIterator_Create");
                if (iterator == null)
                    yield break;

                TryInvoke(iterator, "SetState_FilterAll");
                object primitive = TryInvokeResult(iterator, "FirstPCBObject")
                    ?? TryInvokeResult(iterator, "Internal_FirstPCBObject");
                while (primitive != null)
                {
                    yield return primitive;

                    primitive = TryInvokeResult(iterator, "NextPCBObject")
                        ?? TryInvokeResult(iterator, "Internal_NextPCBObject");
                }
            }
            finally
            {
                if (iterator != null)
                    TryInvoke(board, "BoardIterator_Destroy", iterator);
            }
        }

        private static IEnumerable<IPCB_ComponentBody> EnumerateComponentBodies(IPCB_Group component)
        {
            foreach (object primitive in EnumerateComponentPrimitives(component))
            {
                if (primitive is IPCB_ComponentBody body)
                    yield return body;
            }
        }

        private static byte[] TryExportComponentBodyStep(IPCB_ComponentBody body)
        {
            if (body == null)
                return null;

            string tempPath = Path.Combine(Path.GetTempPath(), "EasyEDA-Reproject-" + Guid.NewGuid().ToString("N") + ".step");
            try
            {
                if (body.SaveModelToFile(tempPath) && File.Exists(tempPath))
                    return File.ReadAllBytes(tempPath);

                object modelObject = TryInvokeResult(body, "Internal_GetModel");
                if (modelObject is IPCB_Model model)
                {
                    try
                    {
                        model.ExportToStep(tempPath);
                        if (File.Exists(tempPath))
                            return File.ReadAllBytes(tempPath);
                    }
                    catch
                    {
                    }

                    string modelPath = model.GetFileName();
                    if (!string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath))
                        return File.ReadAllBytes(modelPath);
                }
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("Failed to export 3D body model for projection: " + ex);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
            }

            return null;
        }

        private static void TryGetComponentBodyModelState(IPCB_ComponentBody body, out double rotX, out double rotY, out double rotZ)
        {
            rotX = 0;
            rotY = 0;
            rotZ = 0;
            if (body == null)
                return;

            object modelObject = TryInvokeResult(body, "Internal_GetModel");
            if (!(modelObject is IPCB_Model model))
                return;

            int dz = 0;
            try
            {
                model.GetState(out rotX, out rotY, out rotZ, out dz);
            }
            catch
            {
                rotX = 0;
                rotY = 0;
                rotZ = model.GetRotation();
            }
        }

        private static StepSilhouetteBounds MeasurePadBounds(IPCB_Group component)
        {
            StepSilhouetteBounds bounds = null;
            foreach (object primitive in EnumerateComponentPrimitives(component))
            {
                if (!(primitive is IPCB_Pad))
                    continue;

                if (!TryGetPrimitiveBoundsMm(component, primitive, out StepSilhouetteBounds padBounds))
                    continue;

                if (bounds == null)
                {
                    bounds = padBounds;
                    continue;
                }

                bounds.Left = Math.Min(bounds.Left, padBounds.Left);
                bounds.Bottom = Math.Min(bounds.Bottom, padBounds.Bottom);
                bounds.Right = Math.Max(bounds.Right, padBounds.Right);
                bounds.Top = Math.Max(bounds.Top, padBounds.Top);
            }

            return bounds;
        }

        public static void SetComponentBodyHeights(IPCB_ComponentBody body, double standoffHeightMm, double overallHeightMm)
        {
            if (body == null)
                return;

            if (standoffHeightMm > 0)
                body.SetStandoffHeight(AltiumApi.MmToCoord(standoffHeightMm));

            if (overallHeightMm > 0)
                body.SetOverallHeight(AltiumApi.MmToCoord(overallHeightMm));
        }

        public static bool CenterComponentBodyMm(IPCB_Group component, IPCB_ComponentBody body, double targetCenterX, double targetCenterY)
        {
            if (!TryGetComponentBodyBoundsMm(component, body, out StepSilhouetteBounds currentBounds))
                return false;

            FootprintModelMove move = FootprintModelPlacement.CalculateCenteringMoveMm(currentBounds, targetCenterX, targetCenterY);
            if (Math.Abs(move.XMm) <= 0.000001 && Math.Abs(move.YMm) <= 0.000001)
                return true;

            if (!TranslateComponentBodyModelOriginMm(body, move.XMm, move.YMm))
                return false;

            body.GraphicallyInvalidate();
            return true;
        }

        private static bool TranslateComponentBodyModelOriginMm(IPCB_ComponentBody body, double xMm, double yMm)
        {
            if (body == null)
                return false;

            object modelObject = TryInvokeResult(body, "Internal_GetModel");
            if (!(modelObject is IPCB_Model model))
                return false;

            int originX = 0;
            int originY = 0;
            object origin = TryInvokeResult(model, "Internal_GetOrigin");
            if (origin != null)
            {
                TryGetCoordPointValue(origin, "GetX", "X", out originX);
                TryGetCoordPointValue(origin, "GetY", "Y", out originY);
            }

            var newOrigin = new CoordPoint
            {
                X = originX + AltiumApi.MmToCoord(xMm),
                Y = originY + AltiumApi.MmToCoord(yMm)
            };

            model.SetOrigin(newOrigin);
            body.SetModel(model);
            body.SetState_FromModel();
            return true;
        }

        private static bool TryGetCoordPointValue(object point, string methodName, string propertyName, out int value)
        {
            value = 0;
            object raw = TryInvokeResult(point, methodName);
            if (raw == null)
            {
                PropertyInfo property = point.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                raw = property?.GetValue(point);
            }

            if (raw == null)
                return false;

            try
            {
                value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static StepSilhouetteBounds GetComponentBodyBoundsMm(IPCB_Group component, IPCB_ComponentBody body, double fallbackCenterX, double fallbackCenterY, double fallbackWidth, double fallbackHeight)
        {
            StepSilhouetteBounds fallback = new StepSilhouetteBounds
            {
                Left = fallbackCenterX - fallbackWidth / 2.0,
                Bottom = fallbackCenterY - fallbackHeight / 2.0,
                Right = fallbackCenterX + fallbackWidth / 2.0,
                Top = fallbackCenterY + fallbackHeight / 2.0
            };

            if (!TryGetComponentBodyBoundsMm(component, body, out StepSilhouetteBounds bounds))
                return fallback;

            return bounds;
        }

        private static bool TryGetComponentBodyBoundsMm(IPCB_Group component, IPCB_ComponentBody body, out StepSilhouetteBounds bounds)
        {
            return TryGetPrimitiveBoundsMm(component, body, out bounds);
        }

        private static bool TryGetPrimitiveBoundsMm(IPCB_Group component, object primitive, out StepSilhouetteBounds bounds)
        {
            bounds = null;
            if (component == null || primitive == null)
                return false;

            object rect = TryInvokeResult(primitive, "Internal_BoundingRectangle");
            if (rect == null)
                return false;

            if (!TryGetRectCoord(rect, "GetLeft", out int left)
                || !TryGetRectCoord(rect, "GetRight", out int right)
                || !TryGetRectCoord(rect, "GetBottom", out int bottom)
                || !TryGetRectCoord(rect, "GetTop", out int top))
                return false;

            int originX = 0;
            int originY = 0;
            try
            {
                IPCB_Board board = GetComponentBoard(component);
                if (board != null)
                {
                    originX = board.GetState_XOrigin();
                    originY = board.GetState_YOrigin();
                }
            }
            catch
            {
                originX = 0;
                originY = 0;
            }

            bounds = new StepSilhouetteBounds
            {
                Left = AltiumApi.CoordToMm(left - originX),
                Bottom = AltiumApi.CoordToMm(bottom - originY),
                Right = AltiumApi.CoordToMm(right - originX),
                Top = AltiumApi.CoordToMm(top - originY)
            };

            if (bounds.Right <= bounds.Left || bounds.Top <= bounds.Bottom)
                return false;

            return true;
        }

        private static bool IsPrimitiveOnLayer(object primitive, TLayerConstant layer)
        {
            if (!TryGetPrimitiveLayerNumber(primitive, out int primitiveLayer))
                return false;

            int expectedLayer = (int)layer;
            if (primitiveLayer == expectedLayer)
                return true;

            try
            {
                return primitiveLayer == new V7_Layer(layer).Number();
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetPrimitiveLayerNumber(object primitive, out int layer)
        {
            layer = 0;
            if (primitive == null)
                return false;

            object raw = TryInvokeResult(primitive, "GetState_Layer")
                ?? TryInvokeResult(primitive, "Internal_GetState_Layer");
            if (TryConvertToInt(raw, out layer))
                return true;

            object v7Layer = TryInvokeResult(primitive, "Internal_GetState_V7Layer");
            if (v7Layer is IV7_Layer layerObject)
            {
                try
                {
                    layer = new V7_Layer(layerObject).Number();
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TryGetRectCoord(object rect, string methodName, out int value)
        {
            value = 0;
            object raw = TryInvokeResult(rect, methodName);
            if (raw == null)
                return false;

            return TryConvertToInt(raw, out value);
        }

        private static bool TryConvertToInt(object raw, out int value)
        {
            value = 0;
            if (raw == null)
                return false;

            try
            {
                value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TrySetLayer(object target, TLayerConstant layer)
        {
            TryInvoke(target, "SetState_V7Layer", new V7_Layer(layer));
            TryInvoke(target, "SetState_Layer", new V7_Layer(layer));
            TryInvoke(target, "SetState_Layer", layer);
        }

        private static void TryInvoke(object target, string methodName, params object[] args)
        {
            TryInvokeResult(target, methodName, args);
        }

        private static object TryInvokeResult(object target, string methodName, params object[] args)
        {
            if (target == null)
                return null;

            object directResult = TryInvokePcbLibrary(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbBoard(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbGroup(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbComponent(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbComponentBody(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbModel(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbPrimitive(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbCoordObject(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbAbstractIterator(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            foreach (var method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
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

        private static object TryInvokePcbLibrary(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_Library pcbLib))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_GetState_CurrentComponent" when args.Length == 0:
                        return pcbLib.Internal_GetState_CurrentComponent();
                    case "Internal_GetState_Board" when args.Length == 0:
                        return pcbLib.Internal_GetState_Board();
                    case "Internal_GetComponentByName" when args.Length == 1:
                        return pcbLib.Internal_GetComponentByName(Convert.ToString(args[0], CultureInfo.InvariantCulture));
                    case "Internal_LibraryIterator_Create" when args.Length == 0:
                        return pcbLib.Internal_LibraryIterator_Create();
                    case "ComponentCount" when args.Length == 0:
                        return pcbLib.ComponentCount();
                    case "SetState_CurrentComponent" when args.Length == 1:
                        pcbLib.SetState_CurrentComponent(args[0]);
                        return null;
                    case "LibraryIterator_Destroy" when args.Length == 1:
                        object iterator = args[0];
                        pcbLib.LibraryIterator_Destroy(ref iterator);
                        return null;
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbBoard(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_Board board))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_BoardIterator_Create" when args.Length == 0:
                        return board.Internal_BoardIterator_Create();
                    case "BoardIterator_Destroy" when args.Length == 1:
                        object iterator = args[0];
                        board.BoardIterator_Destroy(ref iterator);
                        return null;
                    case "RemovePCBObject" when args.Length == 1:
                        board.RemovePCBObject(args[0]);
                        return null;
                    case "AddPCBObject" when args.Length == 1:
                        board.AddPCBObject(args[0]);
                        return null;
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbComponent(object target, string methodName, object[] args)
        {
            try
            {
                if (target is IPCB_LibComponent libComponent)
                {
                    switch (methodName)
                    {
                        case "GetState_Pattern" when args.Length == 0:
                            return libComponent.GetState_Pattern();
                        case "SetState_Pattern" when args.Length == 1:
                            libComponent.SetState_Pattern(Convert.ToString(args[0], CultureInfo.InvariantCulture));
                            return null;
                    }
                }

                if (target is IPCB_Component component)
                {
                    switch (methodName)
                    {
                        case "GetState_Pattern" when args.Length == 0:
                            return component.GetState_Pattern();
                        case "Internal_GetState_Name" when args.Length == 0:
                            return component.Internal_GetState_Name();
                        case "SetState_Pattern" when args.Length == 1:
                            component.SetState_Pattern(Convert.ToString(args[0], CultureInfo.InvariantCulture));
                            return null;
                    }
                }
            }
            catch
            {
                return null;
            }

            return Missing.Value;
        }

        private static object TryInvokePcbComponentBody(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_ComponentBody body))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_GetModel" when args.Length == 0:
                        return body.Internal_GetModel();
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbModel(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_Model model))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_GetOrigin" when args.Length == 0:
                        return model.Internal_GetOrigin();
                    case "GetFileName" when args.Length == 0:
                        return model.GetFileName();
                    case "ExportToStep" when args.Length == 1:
                        model.ExportToStep(Convert.ToString(args[0], CultureInfo.InvariantCulture));
                        return null;
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbGroup(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_Group group))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_GroupIterator_Create" when args.Length == 0:
                        return group.Internal_GroupIterator_Create();
                    case "GroupIterator_Destroy" when args.Length == 1:
                        object iterator = args[0];
                        group.GroupIterator_Destroy(ref iterator);
                        return null;
                    case "RemovePCBObject" when args.Length == 1:
                        group.RemovePCBObject(args[0]);
                        return null;
                    case "AddPCBObject" when args.Length == 1:
                        group.AddPCBObject(args[0]);
                        return null;
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbPrimitive(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_Primitive primitive))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_BoundingRectangle" when args.Length == 0:
                        return primitive.Internal_BoundingRectangle();
                    case "Internal_GetState_Layer" when args.Length == 0:
                        return primitive.Internal_GetState_Layer();
                    case "Internal_GetState_V7Layer" when args.Length == 0:
                        return primitive.Internal_GetState_V7Layer();
                    case "SetState_V7Layer" when args.Length == 1 && args[0] is IV7_Layer v7Layer:
                        primitive.SetState_V7Layer(v7Layer);
                        return null;
                    case "SetState_Layer" when args.Length == 1 && TryConvertToInt(args[0], out int layer):
                        primitive.SetState_Layer(layer);
                        return null;
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbCoordObject(object target, string methodName, object[] args)
        {
            try
            {
                if (target is ICoordRect rect)
                {
                    switch (methodName)
                    {
                        case "GetLeft" when args.Length == 0:
                            return rect.GetLeft();
                        case "GetRight" when args.Length == 0:
                            return rect.GetRight();
                        case "GetBottom" when args.Length == 0:
                            return rect.GetBottom();
                        case "GetTop" when args.Length == 0:
                            return rect.GetTop();
                    }
                }

                if (target is ICoordPoint point)
                {
                    switch (methodName)
                    {
                        case "GetX" when args.Length == 0:
                            return point.GetX();
                        case "GetY" when args.Length == 0:
                            return point.GetY();
                    }
                }
            }
            catch
            {
                return null;
            }

            return Missing.Value;
        }

        private static object TryInvokePcbAbstractIterator(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_AbstractIterator iterator))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "SetState_FilterAll" when args.Length == 0:
                        iterator.SetState_FilterAll();
                        return null;
                    case "Internal_FirstPCBObject" when args.Length == 0:
                        return iterator.Internal_FirstPCBObject();
                    case "Internal_NextPCBObject" when args.Length == 0:
                        return iterator.Internal_NextPCBObject();
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
