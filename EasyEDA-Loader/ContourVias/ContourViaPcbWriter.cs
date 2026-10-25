using PCB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyEDA_Loader
{
    internal static class ContourViaPcbWriter
    {
        public static List<string> ReadNetNames(IPCB_Board board)
            => ReadNets(board).Select(n => n.GetState_Name()).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        private static List<IPCB_Net> ReadNets(IPCB_Board board)
        {
            var nets = new List<IPCB_Net>();
            // Altium's COM wrappers can cast to unrelated PCB interfaces. Check
            // the native object ID before calling an interface-specific method.
            Visit(board, item =>
            {
                if (item is IPCB_Primitive primitive && primitive.Internal_GetState_ObjectID() == (int)TObjectId.eNetObject)
                    nets.Add((IPCB_Net)item);
            });
            return nets;
        }

        private static void Visit(IPCB_Board board, Action<object> action)
        {
            var iterator = board.Internal_BoardIterator_Create() as IPCB_BoardIterator;
            if (iterator == null) throw new InvalidOperationException("Could not read PCB objects.");
            try
            {
                iterator.SetState_FilterAll();
                for (object item = iterator.Internal_FirstPCBObject(); item != null; item = iterator.Internal_NextPCBObject())
                    action(item);
            }
            finally { board.BoardIterator_Destroy(ref iterator); }
        }

        public static List<ContourViaPoint> Plan(IPCB_Board board, ContourViaOptions options)
        {
            var segments = new List<ContourViaSegment>();
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            Visit(board, item =>
            {
                if (!(item is IPCB_Primitive primitive)) return;
                int objectId = primitive.Internal_GetState_ObjectID();
                if (objectId != (int)TObjectId.eTrackObject && objectId != (int)TObjectId.eArcObject) return;
                // The bundled SDK's Keep-Out enum differs from Agile's native
                // value. Match the native layer name, as altium-mcp does.
                string layerName = board.LayerName(primitive.Internal_GetState_V7Layer());
                if (!string.Equals(layerName?.Replace("-", " "), "Keep Out Layer", StringComparison.OrdinalIgnoreCase)) return;
                ContourViaSegment segment;
                if (objectId == (int)TObjectId.eTrackObject)
                {
                    var track = (IPCB_Track)item;
                    segment = new ContourViaSegment { X1 = Mm(track.GetState_X1()), Y1 = Mm(track.GetState_Y1()), X2 = Mm(track.GetState_X2()), Y2 = Mm(track.GetState_Y2()) };
                }
                else
                {
                    var arc = (IPCB_Arc)item;
                    segment = new ContourViaSegment { IsArc = true, X1 = Mm(arc.GetState_CenterX()), Y1 = Mm(arc.GetState_CenterY()), Radius = Mm(arc.GetState_Radius()), StartAngle = arc.GetState_StartAngle(), EndAngle = arc.GetState_EndAngle() };
                }
                var bounds = primitive.Internal_BoundingRectangle();
                minX = Math.Min(minX, Mm(bounds.GetLeft())); minY = Math.Min(minY, Mm(bounds.GetBottom()));
                maxX = Math.Max(maxX, Mm(bounds.GetRight())); maxY = Math.Max(maxY, Mm(bounds.GetTop()));
                segments.Add(segment);
            });
            if (segments.Count == 0) throw new InvalidOperationException("No Keep-Out Layer tracks or arcs found for the PCB contour.");
            var points = ContourViaGeometry.Generate(segments, (minX + maxX) / 2, (minY + maxY) / 2, options);
            if (points.Count == 0) throw new InvalidOperationException("The keepout contour has no usable tracks or arcs.");
            return points;
        }

        public static int Add(IPCB_Board capturedBoard, ContourViaOptions options)
        {
            options.Validate();
            IPCB_Board board = EEPCB.GetCurrentPcbBoard();
            if (board == null || !ReferenceEquals(board, capturedBoard))
                throw new InvalidOperationException("The active PCB changed while the contour vias dialog was open.");
            var points = Plan(board, options);
            IPCB_Net net = null;
            if (!string.IsNullOrEmpty(options.NetName))
                net = ReadNets(board).FirstOrDefault(n => string.Equals(n.GetState_Name(), options.NetName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Net not found: " + options.NetName);
            var added = new List<object>();
            var server = AltiumApi.GlobalVars.PCBServer;
            server.PreProcess();
            try
            {
                foreach (var point in points)
                {
                    var via = server.PCBObjectFactory(TObjectId.eViaObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Via;
                    if (via == null) throw new InvalidOperationException("Could not create PCB via.");
                    via.SetState_XLocation(AltiumApi.MmToCoord(point.X));
                    via.SetState_YLocation(AltiumApi.MmToCoord(point.Y));
                    via.SetState_Size(AltiumApi.MmToCoord(options.DiameterMm));
                    via.SetState_HoleSize(AltiumApi.MmToCoord(options.HoleMm));
                    via.SetState_LowLayer(new V7_Layer(TLayerConstant.eTopLayer));
                    via.SetState_HighLayer(new V7_Layer(TLayerConstant.eBottomLayer));
                    var primitive = (IPCB_Primitive)via;
                    primitive.SetState_Net(net);
                    primitive.SetState_Selected(true);
                    board.AddPCBObject(via);
                    added.Add(via);
                    // Rule-driven mask expansion otherwise discards the tenting
                    // flags in Agile. Set the override on the registered via.
                    var mask = (IPCB_Primitive2)via;
                    mask.SetState_SolderMaskExpansionMode((int)TMaskExpansionMode.eMaskExpansionMode_Manual);
                    mask.SetState_SolderMaskExpansion(-AltiumApi.MmToCoord(options.DiameterMm) / 2);
                    primitive.SetState_IsTenting(true);
                    primitive.SetState_IsTenting_Top(true);
                    primitive.SetState_IsTenting_Bottom(true);
                    if (!primitive.GetState_IsTenting_Top() || !primitive.GetState_IsTenting_Bottom())
                        throw new InvalidOperationException("Altium did not apply tenting on both sides of the via.");
                }
            }
            catch
            {
                foreach (var item in added) board.RemovePCBObject(item);
                throw;
            }
            finally { server.PostProcess(); }
            board.ConnectivelyValidateNets();
            board.ViewManager_FullUpdate();
            EasyEDALoaderModule.Trace("Contour vias added: " + added.Count + "; net=" + options.NetName
                + "; diameter=" + options.DiameterMm + "; hole=" + options.HoleMm + "; clearance=" + options.ClearanceMm + "; pitch=" + options.PitchMm);
            return added.Count;
        }

        private static double Mm(int coordinate) => AltiumApi.CoordToMm(coordinate);
    }
}
