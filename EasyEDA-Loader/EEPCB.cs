using PCB;

using System;
using System.IO;
using System.Reflection;

namespace EasyEDA_Loader
{
    internal class EEPCB
    {
        public const double CourtyardMarginMm = 0.25;
        private const double MechanicalLineWidthMm = 0.1;
        private const double OverlayLineWidthMm = 0.2;
        private const double AssemblyTextSizeMm = 1.0;

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
            width = NormalizeLineWidth(layer, width);
            textObject.SetState_V7Layer(new V7_Layer(layer));
            textObject.SetState_XLocation(AltiumApi.MmToCoord(x) + c.GetState_XLocation());
            textObject.SetState_YLocation(AltiumApi.MmToCoord(y) + c.GetState_YLocation());
            textObject.SetState_Text(text);
            textObject.SetState_Size(AltiumApi.MmToCoord(size));
            textObject.SetState_Width(AltiumApi.MmToCoord(width));
            textObject.SetState_Rotation(rotation);
            return textObject;
        }

        public static void AddRectangle(IPCB_LibComponent c, TLayerConstant layer, double x1, double y1, double x2, double y2, double width)
        {
            AddToPCB(c, CreateLine(c, layer, x1, y1, x2, y1, width));
            AddToPCB(c, CreateLine(c, layer, x2, y1, x2, y2, width));
            AddToPCB(c, CreateLine(c, layer, x2, y2, x1, y2, width));
            AddToPCB(c, CreateLine(c, layer, x1, y2, x1, y1, width));
        }

        public static void Add3dBodyProjection(IPCB_LibComponent c, double centerX, double centerY, double width, double height)
        {
            if (width <= 0 || height <= 0)
                return;

            double halfWidth = width / 2.0;
            double halfHeight = height / 2.0;
            AddRectangle(
                c,
                TLayerConstant.eMechanical2,
                centerX - halfWidth,
                centerY - halfHeight,
                centerX + halfWidth,
                centerY + halfHeight,
                MechanicalLineWidthMm);
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

        public static void AddAssemblyTexts(IPCB_LibComponent c, bool hasDesignator, bool hasComment, double bodyHeight)
        {
            double offset = Math.Max(AssemblyTextSizeMm, Math.Min(2.0, bodyHeight / 4.0));
            if (!hasDesignator)
                AddToPCB(c, CreateText(c, TLayerConstant.eMechanical2, ".Designator", 0, offset, MechanicalLineWidthMm, AssemblyTextSizeMm, 0));
            if (!hasComment)
                AddToPCB(c, CreateText(c, TLayerConstant.eMechanical2, ".Comment", 0, -offset, MechanicalLineWidthMm, AssemblyTextSizeMm, 0));
        }

        public static IPCB_ComponentBody CreateComponentBody(IPCB_LibComponent c, string fileName, double rx, double ry, double rz, double x, double y, double z, string identifier = null, double overallHeightMm = 0)
        {
            var stepModel = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eComponentBodyObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_ComponentBody;
            if (stepModel == null) return null;
            var model = stepModel.ModelFactory_FromFilename(fileName, false);
            if (model == null) return null;
            model.SetState(rx, ry, rz, AltiumApi.MmToCoord(z));
            stepModel.SetModel(model);
            TrySetLayer(stepModel, TLayerConstant.eMechanical1);
            string modelIdentifier = !string.IsNullOrWhiteSpace(identifier)
                ? identifier
                : Path.GetFileNameWithoutExtension(fileName);
            TrySetIdentifier(stepModel, modelIdentifier);
            TrySetIdentifier(model, modelIdentifier);
            TrySetHeight(stepModel, z, overallHeightMm);
            // Model is created at the bottom-left origin of the board, so we need to offset it
            stepModel.MoveByXY(AltiumApi.MmToCoord(x) + c.GetState_Board().GetState_XOrigin(), AltiumApi.MmToCoord(y) + c.GetState_Board().GetState_YOrigin());
            return stepModel;
        }

        private static void TrySetLayer(object target, TLayerConstant layer)
        {
            TryInvoke(target, "SetState_V7Layer", new V7_Layer(layer));
            TryInvoke(target, "SetState_Layer", new V7_Layer(layer));
            TryInvoke(target, "SetState_Layer", layer);
        }

        private static void TrySetIdentifier(object target, string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
                return;

            TryInvoke(target, "SetState_Identifier", identifier);
            TryInvoke(target, "SetState_ModelIdentifier", identifier);
            TryInvoke(target, "SetState_Name", identifier);
        }

        private static void TrySetHeight(object target, double standoffHeightMm, double overallHeightMm)
        {
            if (target == null)
                return;

            if (standoffHeightMm > 0)
                TryInvoke(target, "SetStandoffHeight", AltiumApi.MmToCoord(standoffHeightMm));

            if (overallHeightMm > 0)
                TryInvoke(target, "SetOverallHeight", AltiumApi.MmToCoord(overallHeightMm));
        }

        private static void TryInvoke(object target, string methodName, params object[] args)
        {
            if (target == null)
                return;

            foreach (var method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name != methodName || method.GetParameters().Length != args.Length)
                    continue;

                try
                {
                    method.Invoke(target, args);
                    return;
                }
                catch
                {
                }
            }
        }
    }
}
