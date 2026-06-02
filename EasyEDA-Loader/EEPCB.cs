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

        public static void AddToPCB(IPCB_LibComponent c, object obj)
        {
            if (c == null || obj == null)
                return;

            c.GetState_Board().AddPCBObject(obj);
            c.AddPCBObject(obj);
        }

        public static TLayerConstant EELayerToAltium(string layer)
        {
            switch (layer)
            {
                case "TopLayer": return TLayerConstant.eTopLayer;
                case "BottomLayer": return TLayerConstant.eBottomLayer;
                case "TopSilkLayer": return TLayerConstant.eTopOverlay;
                case "BottomSilkLayer": return TLayerConstant.eBottomOverlay;
                case "TopPasteMaskLayer": return TLayerConstant.eTopPaste;
                case "BottomPasteMaskLayer": return TLayerConstant.eBottomPaste;
                case "TopSolderMaskLayer": return TLayerConstant.eTopSolder;
                case "BottomSolderMaskLayer": return TLayerConstant.eBottomSolder;
                case "BoardOutline": return TLayerConstant.eMechanical3;
                case "Multi-Layer": return TLayerConstant.eMultiLayer;
                case "TopAssembly": return TLayerConstant.eMechanical2;
                case "Mechanical": return TLayerConstant.eMechanical2;
                case "3DModel": return TLayerConstant.eMechanical1;
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

        public static IPCB_Track CreateLine(IPCB_LibComponent c, TLayerConstant layer, double x1, double y1, double x2, double y2, double width)
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

        public static IPCB_Arc CreateArc(IPCB_LibComponent c, TLayerConstant layer, double x, double y, double rad, double width, double startAngle, double endAngle)
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

        public static IPCB_Pad4 CreatePTH(IPCB_LibComponent c, TLayerConstant layer, TExtendedHoleType holeType, TShape padShape, double x, double y, double height, double width, double holeSize, string name, bool plated, double rotation)
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

        public static IPCB_Via CreateVia(IPCB_LibComponent c, TLayerConstant layerStart, TLayerConstant layerEnd, double x, double y, double size, double holeSize)
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

        public static IPCB_Text3 CreateText(IPCB_LibComponent c, TLayerConstant layer, string text, double x, double y, double width, double size, double rotation)
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

        public static void AddRectangle(IPCB_LibComponent c, TLayerConstant layer, double x1, double y1, double x2, double y2, double width)
        {
            AddToPCB(c, CreateLine(c, layer, x1, y1, x2, y1, width));
            AddToPCB(c, CreateLine(c, layer, x2, y1, x2, y2, width));
            AddToPCB(c, CreateLine(c, layer, x2, y2, x1, y2, width));
            AddToPCB(c, CreateLine(c, layer, x1, y2, x1, y1, width));
        }

        public static int Add3dBodyProjection(IPCB_LibComponent c, IReadOnlyList<StepSilhouettePrimitive> primitives)
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

        public static void AddCourtyard(IPCB_LibComponent c, double width, double height)
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

        public static void AddAssemblyTexts(IPCB_LibComponent c, bool hasDesignator, bool hasComment, double bodyHeight, IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives = null)
        {
            ProjectionTextLocations locations = ChooseProjectionTextLocations(projectionPrimitives);
            if (!hasDesignator)
                AddToPCB(c, CreateText(c, TLayerConstant.eMechanical2, ".Designator", locations.DesignatorX, locations.DesignatorY, MechanicalLineWidthMm, AssemblyTextSizeMm, 0));
            if (!hasComment)
                AddToPCB(c, CreateText(c, TLayerConstant.eMechanical2, ".Comment", locations.CommentX, locations.CommentY, MechanicalLineWidthMm, AssemblyTextSizeMm, 0));
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

        public static IPCB_ComponentBody CreateComponentBody(IPCB_LibComponent c, string fileName, double rx, double ry, double rz, double x, double y, double z, string identifier = null, double overallHeightMm = 0)
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
                stepModel.MoveByXY(AltiumApi.MmToCoord(x) + c.GetState_Board().GetState_XOrigin(), AltiumApi.MmToCoord(y) + c.GetState_Board().GetState_YOrigin());
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

            object iterator = null;
            try
            {
                iterator = TryInvokeResult(component, "GroupIterator_Create")
                    ?? TryInvokeResult(component, "Internal_GroupIterator_Create");
                if (iterator == null)
                    return;

                object primitive = TryInvokeResult(iterator, "FirstPCBObject")
                    ?? TryInvokeResult(iterator, "Internal_FirstPCBObject");
                while (primitive != null)
                {
                    if (primitive is IPCB_ComponentBody body)
                        SetComponentBodyIdentifier(body, identifier);

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

        public static void SetComponentBodyHeights(IPCB_ComponentBody body, double standoffHeightMm, double overallHeightMm)
        {
            if (body == null)
                return;

            if (standoffHeightMm > 0)
                body.SetStandoffHeight(AltiumApi.MmToCoord(standoffHeightMm));

            if (overallHeightMm > 0)
                body.SetOverallHeight(AltiumApi.MmToCoord(overallHeightMm));
        }

        public static bool CenterComponentBodyMm(IPCB_LibComponent component, IPCB_ComponentBody body, double targetCenterX, double targetCenterY)
        {
            if (!TryGetComponentBodyBoundsMm(component, body, out StepSilhouetteBounds currentBounds))
                return false;

            FootprintModelMove move = FootprintModelPlacement.CalculateCenteringMoveMm(currentBounds, targetCenterX, targetCenterY);
            if (Math.Abs(move.XMm) <= 0.000001 && Math.Abs(move.YMm) <= 0.000001)
                return true;

            body.MoveByXY(AltiumApi.MmToCoord(move.XMm), AltiumApi.MmToCoord(move.YMm));
            body.GraphicallyInvalidate();
            return true;
        }

        public static StepSilhouetteBounds GetComponentBodyBoundsMm(IPCB_LibComponent component, IPCB_ComponentBody body, double fallbackCenterX, double fallbackCenterY, double fallbackWidth, double fallbackHeight)
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

        private static bool TryGetComponentBodyBoundsMm(IPCB_LibComponent component, IPCB_ComponentBody body, out StepSilhouetteBounds bounds)
        {
            bounds = null;
            if (component == null || body == null)
                return false;

            object rect = TryInvokeResult(body, "Internal_BoundingRectangle");
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
                IPCB_Board board = component.GetState_Board();
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

        private static bool TryGetRectCoord(object rect, string methodName, out int value)
        {
            value = 0;
            object raw = TryInvokeResult(rect, methodName);
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
    }
}
