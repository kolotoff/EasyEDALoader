using PCB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EasyEDA_Loader
{
    internal static class LayoutDuplicationApply
    {
        public static LayoutDuplicationResult ApplyLayoutDuplication(
            LayoutDuplicationSession session,
            LayoutComponentSnapshot sourceAnchor,
            LayoutMappingValidationResult mapping,
            IProgress<LayoutDuplicationProgress> progress = null)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));
            if (sourceAnchor == null)
                throw new ArgumentNullException(nameof(sourceAnchor));
            if (mapping == null || !mapping.HasValidGroups)
                throw new InvalidOperationException("No valid layout duplication groups were supplied.");

            IPCB_Board board = session.Board ?? LayoutDuplicationPcbAccess.GetCurrentBoard();
            if (board == null)
                throw new InvalidOperationException("Open a PCB document before applying Duplicate layout.");

            var result = new LayoutDuplicationResult();
            var selectedRouting = LayoutDuplicationCapture.CaptureSelectedRoutingPrimitives(board);
            var sourceByDesignator = session.SourceComponents.ToDictionary(component => component.Designator, StringComparer.OrdinalIgnoreCase);
            var boardByDesignator = session.BoardComponents.ToDictionary(component => component.Designator, StringComparer.OrdinalIgnoreCase);

            AltiumApi.GlobalVars.PCBServer.PreProcess();
            try
            {
                int groupIndex = 0;
                foreach (LayoutValidatedGroup group in mapping.ValidGroups)
                {
                    groupIndex++;
                    progress?.Report(new LayoutDuplicationProgress
                    {
                        Message = "Applying target " + group.TargetAnchorDesignator + "...",
                        Percent = groupIndex * 100.0 / mapping.ValidGroups.Count,
                        IsIndeterminate = false
                    });

                    if (!boardByDesignator.TryGetValue(group.TargetAnchorDesignator, out LayoutComponentSnapshot targetAnchor))
                    {
                        result.Warnings.Add("Target anchor not found: " + group.TargetAnchorDesignator);
                        continue;
                    }

                    var transform = LayoutTransform.FromAnchors(sourceAnchor, targetAnchor);
                    foreach (KeyValuePair<string, string> pair in group.SourceToDestination)
                    {
                        if (!sourceByDesignator.TryGetValue(pair.Key, out LayoutComponentSnapshot source) ||
                            !boardByDesignator.TryGetValue(pair.Value, out LayoutComponentSnapshot destination))
                            continue;

                        ApplyPlacement(source, destination, transform);
                        result.PlacedComponents++;
                    }

                    if (selectedRouting.Count > 0)
                    {
                        progress?.Report(new LayoutDuplicationProgress { Message = "Copying routing for " + group.TargetAnchorDesignator + "...", IsIndeterminate = true });
                        EnsureRoutingPadData(sourceByDesignator, boardByDesignator, group);
                        foreach (object primitive in selectedRouting)
                        {
                            object copy = ReplicateRoutingPrimitive(board, primitive, transform);
                            if (copy == null)
                                continue;

                            TranslatePrimitiveNet(copy, board, sourceByDesignator, boardByDesignator, group);
                            result.CopiedRoutingPrimitives++;
                        }
                    }
                }
            }
            finally
            {
                AltiumApi.GlobalVars.PCBServer.PostProcess();
            }

            LayoutDuplicationPcbAccess.Redraw(board);
            return result;
        }

        private static void EnsureRoutingPadData(
            Dictionary<string, LayoutComponentSnapshot> sourceByDesignator,
            Dictionary<string, LayoutComponentSnapshot> boardByDesignator,
            LayoutValidatedGroup group)
        {
            foreach (KeyValuePair<string, string> pair in group.SourceToDestination)
            {
                if (sourceByDesignator.TryGetValue(pair.Key, out LayoutComponentSnapshot source))
                    LayoutDuplicationCapture.EnsurePadsCaptured(source);
                if (boardByDesignator.TryGetValue(pair.Value, out LayoutComponentSnapshot destination))
                    LayoutDuplicationCapture.EnsurePadsCaptured(destination);
            }
        }

        private static void ApplyPlacement(
            LayoutComponentSnapshot source,
            LayoutComponentSnapshot destination,
            LayoutTransform transform)
        {
            object component = destination.PcbObject;
            if (component == null)
                return;

            int targetX = AltiumApi.MmToCoord(transform.TransformX(source.XMm, source.YMm));
            int targetY = AltiumApi.MmToCoord(transform.TransformY(source.XMm, source.YMm));
            double targetRotation = NormalizeRotation(source.Rotation + transform.RotationDeltaDeg);

            LayoutDuplicationPcbAccess.BeginModify(component);
            try
            {
                LayoutDuplicationPcbAccess.Invoke(component, "SetState_XLocation", targetX + transform.BoardOriginX);
                LayoutDuplicationPcbAccess.Invoke(component, "SetState_YLocation", targetY + transform.BoardOriginY);
                LayoutDuplicationPcbAccess.Invoke(component, "SetState_Rotation", targetRotation);
                LayoutDuplicationPcbAccess.SetLayer(component, LayoutDuplicationPcbAccess.ReadLayer(source.PcbObject));
            }
            finally
            {
                LayoutDuplicationPcbAccess.EndModify(component);
            }
        }

        private static object ReplicateRoutingPrimitive(IPCB_Board board, object primitive, LayoutTransform transform)
        {
            if (primitive == null)
                return null;

            // Supported routing objects: eTrackObject, eArcObject, eViaObject, ePolyObject, eRegionObject, eFillObject.
            object copy = LayoutDuplicationPcbAccess.Invoke(primitive, "Replicate");
            if (copy == null)
            {
                int objectId = LayoutDuplicationPcbAccess.GetObjectId(primitive);
                if (objectId > 0)
                    copy = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory((TObjectId)objectId, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default);
            }

            if (copy == null)
                return null;

            LayoutDuplicationPcbAccess.BeginModify(copy);
            try
            {
                MovePrimitive(copy, transform);
                board.AddPCBObject(copy);
            }
            finally
            {
                LayoutDuplicationPcbAccess.EndModify(copy);
            }

            return copy;
        }

        private static void MovePrimitive(object primitive, LayoutTransform transform)
        {
            int objectId = LayoutDuplicationPcbAccess.GetObjectId(primitive);
            if (objectId == (int)TObjectId.eTrackObject)
            {
                TransformPoint(primitive, transform, "X1", "Y1");
                TransformPoint(primitive, transform, "X2", "Y2");
            }
            else if (objectId == (int)TObjectId.eArcObject)
            {
                TransformPoint(primitive, transform, "CenterX", "CenterY");
                double start = LayoutDuplicationPcbAccess.GetDouble(primitive, "GetState_StartAngle");
                double end = LayoutDuplicationPcbAccess.GetDouble(primitive, "GetState_EndAngle");
                LayoutDuplicationPcbAccess.Invoke(primitive, "SetState_StartAngle", NormalizeRotation(start + transform.RotationDeltaDeg));
                LayoutDuplicationPcbAccess.Invoke(primitive, "SetState_EndAngle", NormalizeRotation(end + transform.RotationDeltaDeg));
            }
            else if (objectId == (int)TObjectId.eViaObject
                || objectId == (int)TObjectId.eFillObject
                || objectId == (int)TObjectId.eRegionObject
                || objectId == (int)TObjectId.ePolyObject)
            {
                if (!TransformPoint(primitive, transform, "XLocation", "YLocation"))
                    LayoutDuplicationPcbAccess.Invoke(primitive, "MoveByXY", transform.DeltaXCoord, transform.DeltaYCoord);
            }
            else
            {
                LayoutDuplicationPcbAccess.Invoke(primitive, "MoveByXY", transform.DeltaXCoord, transform.DeltaYCoord);
            }
        }

        private static bool TransformPoint(object primitive, LayoutTransform transform, string xProperty, string yProperty)
        {
            int x = LayoutDuplicationPcbAccess.GetInt(primitive, "GetState_" + xProperty);
            int y = LayoutDuplicationPcbAccess.GetInt(primitive, "GetState_" + yProperty);
            if (x == 0 && y == 0)
                return false;

            transform.TransformCoordPoint(x, y, out int targetX, out int targetY);
            LayoutDuplicationPcbAccess.Invoke(primitive, "SetState_" + xProperty, targetX);
            LayoutDuplicationPcbAccess.Invoke(primitive, "SetState_" + yProperty, targetY);
            return true;
        }

        private static void TranslatePrimitiveNet(
            object primitive,
            IPCB_Board board,
            Dictionary<string, LayoutComponentSnapshot> sourceByDesignator,
            Dictionary<string, LayoutComponentSnapshot> boardByDesignator,
            LayoutValidatedGroup group)
        {
            string sourceNet = LayoutDuplicationPcbAccess.ReadNetName(primitive);
            if (string.IsNullOrWhiteSpace(sourceNet))
                return;

            foreach (KeyValuePair<string, string> pair in group.SourceToDestination)
            {
                if (!sourceByDesignator.TryGetValue(pair.Key, out LayoutComponentSnapshot source) ||
                    !boardByDesignator.TryGetValue(pair.Value, out LayoutComponentSnapshot destination))
                    continue;

                LayoutPadSnapshot sourcePad = source.Pads.FirstOrDefault(pad => string.Equals(pad.Net, sourceNet, StringComparison.OrdinalIgnoreCase));
                if (sourcePad == null)
                    continue;

                LayoutPadSnapshot destinationPad = destination.Pads.FirstOrDefault(pad => string.Equals(pad.Name, sourcePad.Name, StringComparison.OrdinalIgnoreCase));
                if (destinationPad == null || string.IsNullOrWhiteSpace(destinationPad.Net))
                    continue;

                object net = LayoutDuplicationPcbAccess.FindBoardNetByName(board, destinationPad.Net);
                if (net != null)
                    LayoutDuplicationPcbAccess.Invoke(primitive, "SetState_Net", net);
                return;
            }
        }

        private static double NormalizeRotation(double rotation)
        {
            while (rotation < 0)
                rotation += 360;
            while (rotation >= 360)
                rotation -= 360;
            return rotation;
        }

        private sealed class LayoutTransform
        {
            public double DeltaXMm { get; private set; }
            public double DeltaYMm { get; private set; }
            public double RotationDeltaDeg { get; private set; }
            public int BoardOriginX { get; private set; }
            public int BoardOriginY { get; private set; }
            public double SourceAnchorXMm { get; private set; }
            public double SourceAnchorYMm { get; private set; }
            public double TargetAnchorXMm { get; private set; }
            public double TargetAnchorYMm { get; private set; }
            public int DeltaXCoord => AltiumApi.MmToCoord(DeltaXMm);
            public int DeltaYCoord => AltiumApi.MmToCoord(DeltaYMm);

            public static LayoutTransform FromAnchors(LayoutComponentSnapshot sourceAnchor, LayoutComponentSnapshot targetAnchor)
            {
                IPCB_Board board = LayoutDuplicationPcbAccess.GetCurrentBoard();
                return new LayoutTransform
                {
                    DeltaXMm = targetAnchor.XMm - sourceAnchor.XMm,
                    DeltaYMm = targetAnchor.YMm - sourceAnchor.YMm,
                    RotationDeltaDeg = targetAnchor.Rotation - sourceAnchor.Rotation,
                    BoardOriginX = LayoutDuplicationPcbAccess.GetInt(board, "GetState_XOrigin"),
                    BoardOriginY = LayoutDuplicationPcbAccess.GetInt(board, "GetState_YOrigin"),
                    SourceAnchorXMm = sourceAnchor.XMm,
                    SourceAnchorYMm = sourceAnchor.YMm,
                    TargetAnchorXMm = targetAnchor.XMm,
                    TargetAnchorYMm = targetAnchor.YMm
                };
            }

            public double TransformX(double xMm, double yMm)
            {
                RotateRelative(xMm, yMm, out double rotatedX, out _);
                return TargetAnchorXMm + rotatedX;
            }

            public double TransformY(double xMm, double yMm)
            {
                RotateRelative(xMm, yMm, out _, out double rotatedY);
                return TargetAnchorYMm + rotatedY;
            }

            public void TransformCoordPoint(int xCoord, int yCoord, out int targetXCoord, out int targetYCoord)
            {
                double xMm = AltiumApi.CoordToMm(xCoord - BoardOriginX);
                double yMm = AltiumApi.CoordToMm(yCoord - BoardOriginY);
                targetXCoord = AltiumApi.MmToCoord(TransformX(xMm, yMm)) + BoardOriginX;
                targetYCoord = AltiumApi.MmToCoord(TransformY(xMm, yMm)) + BoardOriginY;
            }

            private void RotateRelative(double xMm, double yMm, out double rotatedX, out double rotatedY)
            {
                double dx = xMm - SourceAnchorXMm;
                double dy = yMm - SourceAnchorYMm;
                double radians = RotationDeltaDeg * Math.PI / 180.0;
                double cos = Math.Cos(radians);
                double sin = Math.Sin(radians);
                rotatedX = (dx * cos) - (dy * sin);
                rotatedY = (dx * sin) + (dy * cos);
            }
        }
    }

    internal static class LayoutDuplicationPcbAccess
    {
        public static IPCB_Board GetCurrentBoard(DXP.IServerDocumentView commandView = null)
        {
            return EEPCB.GetCurrentPcbBoard(commandView);
        }

        public static object GetComponentByRefDes(IPCB_Board board, string designator)
        {
            if (board == null || string.IsNullOrWhiteSpace(designator))
                return null;

            return Invoke(board, "Internal_GetPcbComponentByRefDes", designator);
        }

        public static IEnumerable<object> EnumerateBoardObjects(IPCB_Board board, params int[] objectIds)
        {
            object iterator = Invoke(board, "BoardIterator_Create") ?? Invoke(board, "Internal_BoardIterator_Create");
            if (iterator == null)
                yield break;

            try
            {
                if (objectIds != null && objectIds.Length > 0)
                    Invoke(iterator, "AddFilter_ObjectSet", CreateObjectSet(objectIds));
                else
                    Invoke(iterator, "SetState_FilterAll");
                object primitive = Invoke(iterator, "FirstPCBObject") ?? Invoke(iterator, "Internal_FirstPCBObject");
                while (primitive != null)
                {
                    yield return primitive;
                    primitive = Invoke(iterator, "NextPCBObject") ?? Invoke(iterator, "Internal_NextPCBObject");
                }
            }
            finally
            {
                Invoke(board, "BoardIterator_Destroy", iterator);
            }
        }

        public static List<object> GetSelectedObjects(IPCB_Board board)
        {
            var result = new List<object>();
            int count = 0;
            if (board is IPCB_Board typedBoard)
                count = typedBoard.GetState_SelectecObjectCount();
            if (count <= 0 && board is IPCB_Board selectedCountBoard)
                count = selectedCountBoard.SelectedObjectsCount();
            if (count <= 0)
                count = GetInt(board, "GetState_SelectecObjectCount");
            if (count <= 0)
                count = GetInt(board, "SelectedObjectsCount");

            if (board is IPCB_Board selectedObjectBoard)
            {
                for (int i = 0; i < count; i++)
                    AddDistinct(result, selectedObjectBoard.Internal_GetState_SelectecObject(i));
                for (int i = 1; i <= count; i++)
                    AddDistinct(result, selectedObjectBoard.Internal_GetState_SelectecObject(i));
            }
            else
            {
                for (int i = 0; i < count; i++)
                    AddDistinct(result, Invoke(board, "Internal_GetState_SelectecObject", i));
                for (int i = 1; i <= count; i++)
                    AddDistinct(result, Invoke(board, "Internal_GetState_SelectecObject", i));
            }

            if (result.Count > 0)
                return result;

            return result;
        }

        public static bool IsSelected(object primitive)
        {
            if (primitive is IPCB_Primitive selectedPrimitive)
                return selectedPrimitive.GetState_Selected();

            object selected = Invoke(primitive, "GetState_Selected");
            if (selected == null)
                selected = Invoke(primitive, "Selected");
            return selected is bool value && value;
        }

        public static string GetObjectIdentity(object primitive)
        {
            if (primitive == null)
                return "";

            string handle = Convert.ToString(Invoke(primitive, "GetState_Handle"));
            if (!string.IsNullOrWhiteSpace(handle))
                return "handle:" + handle;

            string uniqueId = Convert.ToString(Invoke(primitive, "GetState_UniqueId"));
            if (!string.IsNullOrWhiteSpace(uniqueId))
                return "uid:" + uniqueId;

            return "";
        }

        public static int GetObjectId(object primitive)
        {
            if (primitive == null)
                return 0;

            object objectId = Invoke(primitive, "Internal_GetState_ObjectID")
                ?? Invoke(primitive, "GetState_ObjectID")
                ?? Invoke(primitive, "GetState_ObjectId")
                ?? Invoke(primitive, "ObjectId");
            if (objectId == null)
                return 0;

            try { return Convert.ToInt32(objectId); }
            catch { return 0; }
        }

        public static bool IsComponentObject(object primitive)
        {
            return primitive is IPCB_Component || GetObjectId(primitive) == (int)TObjectId.eComponentObject;
        }

        public static bool IsPadObject(object primitive)
        {
            return primitive is IPCB_Pad || GetObjectId(primitive) == (int)TObjectId.ePadObject;
        }

        public static bool IsTextObject(object primitive)
        {
            return primitive is IPCB_Text || GetObjectId(primitive) == (int)TObjectId.eTextObject;
        }

        public static bool IsRoutingObject(object primitive)
        {
            int objectId = GetObjectId(primitive);
            return primitive is IPCB_Track
                || primitive is IPCB_Arc
                || primitive is IPCB_Via
                || primitive is IPCB_Polygon
                || primitive is IPCB_Region
                || primitive is IPCB_Fill
                || objectId == (int)TObjectId.eTrackObject
                || objectId == (int)TObjectId.eArcObject
                || objectId == (int)TObjectId.eViaObject
                || objectId == (int)TObjectId.ePolyObject
                || objectId == (int)TObjectId.eRegionObject
                || objectId == (int)TObjectId.eFillObject;
        }

        public static void BeginModify(object primitive)
        {
            Invoke(primitive, "BeginModify");
        }

        public static void EndModify(object primitive)
        {
            Invoke(primitive, "EndModify");
        }

        public static void Redraw(IPCB_Board board)
        {
            Invoke(board, "ViewManager_FullUpdate");
            Invoke(board, "GraphicalView_ZoomRedraw");
            Invoke(board, "Update_PCBGraphicalView", true, true);
            Invoke(board, "Navigate_RedrawChangedObjectsInBoard");
        }

        public static object FindBoardNetByName(IPCB_Board board, string netName)
        {
            if (string.IsNullOrWhiteSpace(netName))
                return null;

            foreach (object primitive in EnumerateBoardObjects(board))
            {
                string name = Convert.ToString(Invoke(primitive, "GetState_Name"));
                if (string.Equals(name, netName, StringComparison.OrdinalIgnoreCase))
                    return primitive;
            }

            return null;
        }

        public static int GetInt(object target, string methodName)
        {
            object value = Invoke(target, methodName);
            if (value == null)
                return 0;

            try { return Convert.ToInt32(value); }
            catch { return 0; }
        }

        public static double GetDouble(object target, string methodName)
        {
            object value = Invoke(target, methodName);
            if (value == null)
                return 0;

            try { return Convert.ToDouble(value); }
            catch { return 0; }
        }

        public static string ReadLayerName(object primitive)
        {
            object layer = ReadLayer(primitive);
            return layer == null ? "" : layer.ToString();
        }

        public static object ReadLayer(object primitive)
        {
            return Invoke(primitive, "Internal_GetState_V7Layer")
                ?? Invoke(primitive, "GetState_V7Layer")
                ?? Invoke(primitive, "GetState_Layer");
        }

        public static void SetLayer(object target, object layer)
        {
            if (layer == null)
                return;

            Invoke(target, "SetState_V7Layer", layer);
            Invoke(target, "SetState_Layer", layer);
        }

        public static string ReadNetName(object primitive)
        {
            object net = Invoke(primitive, "GetState_Net") ?? Invoke(primitive, "Internal_GetState_Net");
            return Convert.ToString(Invoke(net, "GetState_Name")) ?? "";
        }

        public static string ReadPadName(object primitive)
        {
            if (primitive is IPCB_Pad pad)
                return pad.GetState_Name() ?? "";

            return Convert.ToString(Invoke(primitive, "GetState_Name")) ?? "";
        }

        public static object Invoke(object target, string methodName, params object[] args)
        {
            if (target == null)
                return null;

            object typedResult = TryInvokeTyped(target, methodName, args);
            if (typedResult != Missing.Value)
                return typedResult;

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

        private static object TryInvokeTyped(object target, string methodName, object[] args)
        {
            try
            {
                if (target is IPCB_Board board)
                {
                    switch (methodName)
                    {
                        case "Internal_BoardIterator_Create" when args.Length == 0:
                        case "BoardIterator_Create" when args.Length == 0:
                            return board.Internal_BoardIterator_Create();
                        case "BoardIterator_Destroy" when args.Length == 1:
                            object boardIterator = args[0];
                            board.BoardIterator_Destroy(ref boardIterator);
                            return null;
                        case "AddPCBObject" when args.Length == 1:
                            board.AddPCBObject(args[0]);
                            return null;
                        case "GetState_SelectecObjectCount" when args.Length == 0:
                            return board.GetState_SelectecObjectCount();
                        case "SelectedObjectsCount" when args.Length == 0:
                            return board.SelectedObjectsCount();
                        case "Internal_GetState_SelectecObject" when args.Length == 1:
                            return board.Internal_GetState_SelectecObject(Convert.ToInt32(args[0]));
                        case "Internal_GetPcbComponentByRefDes" when args.Length == 1:
                            return board.Internal_GetPcbComponentByRefDes(Convert.ToString(args[0]));
                        case "GetState_XOrigin" when args.Length == 0:
                            return board.GetState_XOrigin();
                        case "GetState_YOrigin" when args.Length == 0:
                            return board.GetState_YOrigin();
                        case "ViewManager_FullUpdate" when args.Length == 0:
                            board.ViewManager_FullUpdate();
                            return null;
                        case "GraphicalView_ZoomRedraw" when args.Length == 0:
                            board.GraphicalView_ZoomRedraw();
                            return null;
                        case "Update_PCBGraphicalView" when args.Length == 2:
                            board.Update_PCBGraphicalView(Convert.ToBoolean(args[0]), Convert.ToBoolean(args[1]));
                            return null;
                        case "Navigate_RedrawChangedObjectsInBoard" when args.Length == 0:
                            board.Navigate_RedrawChangedObjectsInBoard();
                            return null;
                    }
                }

                if (target is IPCB_AbstractIterator iterator)
                {
                    switch (methodName)
                    {
                        case "AddFilter_ObjectSet" when args.Length == 1 && args[0] is DXP.ITransportSet objectSet:
                            iterator.AddFilter_ObjectSet(objectSet);
                            return null;
                        case "SetState_FilterAll" when args.Length == 0:
                            iterator.SetState_FilterAll();
                            return null;
                        case "Internal_FirstPCBObject" when args.Length == 0:
                        case "FirstPCBObject" when args.Length == 0:
                            return iterator.Internal_FirstPCBObject();
                        case "Internal_NextPCBObject" when args.Length == 0:
                        case "NextPCBObject" when args.Length == 0:
                            return iterator.Internal_NextPCBObject();
                    }
                }

                if (target is IPCB_Group group)
                {
                    switch (methodName)
                    {
                        case "Internal_GroupIterator_Create" when args.Length == 0:
                        case "GroupIterator_Create" when args.Length == 0:
                            return group.Internal_GroupIterator_Create();
                        case "GroupIterator_Destroy" when args.Length == 1:
                            object groupIterator = args[0];
                            group.GroupIterator_Destroy(ref groupIterator);
                            return null;
                        case "GetState_XLocation" when args.Length == 0:
                            return group.GetState_XLocation();
                        case "GetState_YLocation" when args.Length == 0:
                            return group.GetState_YLocation();
                        case "SetState_XLocation" when args.Length == 1:
                            group.SetState_XLocation(Convert.ToInt32(args[0]));
                            return null;
                        case "SetState_YLocation" when args.Length == 1:
                            group.SetState_YLocation(Convert.ToInt32(args[0]));
                            return null;
                        case "Internal_GetPrimitiveAt" when args.Length == 2:
                            return group.Internal_GetPrimitiveAt(Convert.ToInt32(args[0]), Convert.ToInt32(args[1]));
                    }
                }

                if (target is IPCB_Component component)
                {
                    switch (methodName)
                    {
                        case "Internal_GetState_Name" when args.Length == 0:
                        case "GetState_Name" when args.Length == 0:
                            return component.Internal_GetState_Name();
                        case "Internal_GetState_Comment" when args.Length == 0:
                            return component.Internal_GetState_Comment();
                        case "GetState_Pattern" when args.Length == 0:
                            return component.GetState_Pattern();
                        case "GetState_FlippedOnLayer" when args.Length == 0:
                            return component.GetState_FlippedOnLayer();
                        case "GetState_FootprintDescription" when args.Length == 0:
                            return component.GetState_FootprintDescription();
                        case "GetState_SourceCompDesignItemID" when args.Length == 0:
                            return component.GetState_SourceCompDesignItemID();
                        case "GetState_SourceDescription" when args.Length == 0:
                            return component.GetState_SourceDescription();
                        case "GetState_SourceDesignator" when args.Length == 0:
                            return component.GetState_SourceDesignator();
                        case "GetState_SourceLibReference" when args.Length == 0:
                            return component.GetState_SourceLibReference();
                        case "GetState_Rotation" when args.Length == 0:
                            return component.GetState_Rotation();
                        case "SetState_Rotation" when args.Length == 1:
                            component.SetState_Rotation(Convert.ToDouble(args[0]));
                            return null;
                    }
                }

                if (target is IPCB_Primitive primitive)
                {
                    switch (methodName)
                    {
                        case "BeginModify" when args.Length == 0:
                            primitive.BeginModify();
                            return null;
                        case "EndModify" when args.Length == 0:
                            primitive.EndModify();
                            return null;
                        case "Internal_GetState_ObjectID" when args.Length == 0:
                        case "GetState_ObjectID" when args.Length == 0:
                        case "GetState_ObjectId" when args.Length == 0:
                        case "ObjectId" when args.Length == 0:
                            return primitive.Internal_GetState_ObjectID();
                        case "GetState_Selected" when args.Length == 0:
                            return primitive.GetState_Selected();
                        case "Internal_GetState_Component" when args.Length == 0:
                        case "GetState_Component" when args.Length == 0:
                            return primitive.Internal_GetState_Component();
                        case "GetState_Handle" when args.Length == 0:
                            return primitive.GetState_Handle();
                        case "GetState_UniqueId" when args.Length == 0:
                            return primitive.GetState_UniqueId();
                        case "Internal_GetState_V7Layer" when args.Length == 0:
                        case "GetState_V7Layer" when args.Length == 0:
                            return primitive.Internal_GetState_V7Layer();
                        case "Internal_GetState_Layer" when args.Length == 0:
                        case "GetState_Layer" when args.Length == 0:
                            return primitive.Internal_GetState_Layer();
                        case "Internal_GetState_Net" when args.Length == 0:
                        case "GetState_Net" when args.Length == 0:
                            return primitive.Internal_GetState_Net();
                        case "SetState_V7Layer" when args.Length == 1 && args[0] is IV7_Layer v7Layer:
                            primitive.SetState_V7Layer(v7Layer);
                            return null;
                        case "SetState_Layer" when args.Length == 1:
                            primitive.SetState_Layer(Convert.ToInt32(args[0]));
                            return null;
                        case "SetState_Net" when args.Length == 1:
                            primitive.SetState_Net(args[0]);
                            return null;
                        case "Replicate" when args.Length == 0:
                        case "Internal_Replicate" when args.Length == 0:
                            return primitive.Internal_Replicate();
                        case "MoveByXY" when args.Length == 2:
                            primitive.MoveByXY(Convert.ToInt32(args[0]), Convert.ToInt32(args[1]));
                            return null;
                    }
                }

                if (target is IPCB_Text text)
                {
                    switch (methodName)
                    {
                        case "GetState_Text" when args.Length == 0:
                            return text.GetState_Text();
                        case "GetState_UnderlyingString" when args.Length == 0:
                            return text.GetState_UnderlyingString();
                    }
                }

                if (target is IPCB_Pad pad)
                {
                    switch (methodName)
                    {
                        case "GetState_Name" when args.Length == 0:
                            return pad.GetState_Name();
                        case "GetState_XLocation" when args.Length == 0:
                            return pad.GetState_XLocation();
                        case "GetState_YLocation" when args.Length == 0:
                            return pad.GetState_YLocation();
                        case "GetState_Rotation" when args.Length == 0:
                            return pad.GetState_Rotation();
                    }
                }
            }
            catch
            {
                return null;
            }

            return Missing.Value;
        }

        private static DXP.ITransportSet CreateObjectSet(params int[] values)
        {
            var genericSet = new DXP.GenericSet();
            int[] mask = genericSet.Mask;
            foreach (int value in values ?? Array.Empty<int>())
            {
                if (value < 0)
                    continue;

                int index = value / 32;
                if (index >= mask.Length)
                    continue;

                mask[index] |= unchecked((int)(1u << (value % 32)));
            }

            return new DXP.TransportSet(genericSet);
        }

        private static void AddDistinct(List<object> objects, object value)
        {
            if (value == null || objects.Any(existing => ReferenceEquals(existing, value)))
                return;

            objects.Add(value);
        }
    }
}
