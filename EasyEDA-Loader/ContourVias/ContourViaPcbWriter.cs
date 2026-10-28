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
                iterator.AddFilter_Method((int)TIterationMethod.eProcessAll);
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
            return options.IgnoreObstacles ? points : FilterObstacles(board, options, points);
        }

        private static IPCB_Net FindNet(IPCB_Board board, ContourViaOptions options)
            => string.IsNullOrEmpty(options.NetName) ? null :
                ReadNets(board).FirstOrDefault(n => string.Equals(n.GetState_Name(), options.NetName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Net not found: " + options.NetName);

        private static IPCB_Via CreateVia(ContourViaOptions options, IPCB_Net net)
        {
            var via = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eViaObject,
                TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Via;
            if (via == null) throw new InvalidOperationException("Could not create PCB via.");
            try
            {
                via.SetState_Size(AltiumApi.MmToCoord(options.DiameterMm));
                via.SetState_HoleSize(AltiumApi.MmToCoord(options.HoleMm));
                via.SetState_LowLayer(new V7_Layer(TLayerConstant.eTopLayer));
                via.SetState_HighLayer(new V7_Layer(TLayerConstant.eBottomLayer));
                ((IPCB_Primitive)via).SetState_Net(net);
                return via;
            }
            catch
            {
                object failedVia = via;
                AltiumApi.GlobalVars.PCBServer.DestroyPCBObject(ref failedVia);
                throw;
            }
        }

        private static List<ContourViaPoint> FilterObstacles(IPCB_Board board, ContourViaOptions options, List<ContourViaPoint> points)
        {
            var net = FindNet(board, options);
            var obstacles = new List<(IPCB_Primitive Primitive, int Left, int Bottom, int Right, int Top)>();
            int maxClearance = 0;
            Visit(board, item =>
            {
                if (!(item is IPCB_Primitive primitive)) return;
                int id = primitive.Internal_GetState_ObjectID();
                if (id == (int)TObjectId.eRuleObject)
                {
                    if (((IPCB_Rule)item).Internal_GetState_RuleKind() != (int)TRuleKind.eRule_Clearance) return;
                    // Include the advanced clearance matrix, including hole clearances,
                    // so the bounding-box rejection cannot miss a higher scoped rule.
                    var rule = (IPCB_ClearanceConstraint)item;
                    maxClearance = Math.Max(maxClearance, rule.GetState_Gap());
                    foreach (TObjectClearanceId type in Enum.GetValues(typeof(TObjectClearanceId)))
                        foreach (var viaType in new[] { TObjectClearanceId.eObjectClearanceID_Via, TObjectClearanceId.eObjectClearanceID_Hole })
                        {
                            maxClearance = Math.Max(maxClearance, rule.GetClearance_1((int)viaType, (int)type));
                            maxClearance = Math.Max(maxClearance, rule.GetClearance_1((int)type, (int)viaType));
                        }
                    return;
                }
                // Polygon containers are not copper: their poured tracks/arcs/regions
                // are included by eProcessAll, as are component pads and copper text.
                if (id != (int)TObjectId.eTrackObject && id != (int)TObjectId.eArcObject
                    && id != (int)TObjectId.ePadObject && id != (int)TObjectId.eViaObject
                    && id != (int)TObjectId.eFillObject && id != (int)TObjectId.eRegionObject
                    && id != (int)TObjectId.eTextObject && id != (int)TObjectId.eSplitPlaneObject) return;
                if (!primitive.GetState_IsElectricalPrim() || primitive.GetState_IsKeepout()) return;
                // Altium also marks structural board regions and polygon cutouts
                // as electrical primitives. Only signal-layer copper is an obstacle.
                if (id == (int)TObjectId.eRegionObject
                    && (((IPCB_Region)item).Internal_GetState_Kind() != (int)TRegionKind.eRegionKind_Copper
                        || !new V7_Layer(primitive.Internal_GetState_V7Layer()).IsSignalLayer())) return;
                var otherNet = primitive.Internal_GetState_Net() as IPCB_Net;
                // Rebuilt pour children can leave Net unset even though their
                // parent polygon is assigned. Preserve intentional same-net contact.
                if (otherNet == null && primitive.GetState_InPolygon()
                    && primitive.Internal_GetState_Polygon() is IPCB_Primitive polygon
                    && polygon.Internal_GetState_ObjectID() == (int)TObjectId.ePolyObject)
                    otherNet = polygon.Internal_GetState_Net() as IPCB_Net;
                // No-net copper is an obstacle even for no-net vias: it does not
                // establish an intentional same-net electrical connection.
                if (net != null && otherNet != null && string.Equals(net.GetState_Name(), otherNet.GetState_Name(), StringComparison.OrdinalIgnoreCase)) return;
                var bounds = primitive.Internal_BoundingRectangle();
                obstacles.Add((primitive, bounds.GetLeft(), bounds.GetBottom(), bounds.GetRight(), bounds.GetTop()));
            });
            if (obstacles.Count == 0) return points;
            var via = CreateVia(options, net);
            object probe = via;
            try
            {
                // Supply board context for scoped rules without adding a preview
                // primitive to the board or changing its selection/undo history.
                ((IPCB_Primitive)via).SetState_Board(board);
                long reach = (long)AltiumApi.MmToCoord(options.DiameterMm) / 2 + maxClearance + 1;
                var result = new List<ContourViaPoint>();
                foreach (var point in points)
                {
                    int x = AltiumApi.MmToCoord(point.X), y = AltiumApi.MmToCoord(point.Y);
                    via.SetState_XLocation(x);
                    via.SetState_YLocation(y);
                    bool blocked = false;
                    foreach (var obstacle in obstacles)
                    {
                        if (obstacle.Left > x + reach || obstacle.Right < x - reach
                            || obstacle.Bottom > y + reach || obstacle.Top < y - reach) continue;
                        var rule = board.Internal_FindDominantRuleForObjectPair(via, obstacle.Primitive, (int)TRuleKind.eRule_Clearance) as IPCB_ClearanceConstraint;
                        // Native geometry handles pad shapes, arcs, region holes and
                        // poured copper. Touching is forbidden even with zero/no rule.
                        if (board.PrimPrimDistance(via, obstacle.Primitive) <= 0
                            || (rule != null && rule.PrimitivesViolate(via, obstacle.Primitive)))
                        { blocked = true; break; }
                    }
                    if (!blocked) result.Add(point);
                }
                return result;
            }
            finally { AltiumApi.GlobalVars.PCBServer.DestroyPCBObject(ref probe); }
        }

        public static int Add(IPCB_Board capturedBoard, ContourViaOptions options)
        {
            options.Validate();
            IPCB_Board board = EEPCB.GetCurrentPcbBoard();
            if (board == null || !ReferenceEquals(board, capturedBoard))
                throw new InvalidOperationException("The active PCB changed while the contour vias dialog was open.");
            var points = Plan(board, options);
            if (points.Count == 0) throw new InvalidOperationException("No via positions are clear of other nets. Change the settings or enable Ignore obstacles.");
            IPCB_Net net = FindNet(board, options);
            var added = new List<object>();
            var server = AltiumApi.GlobalVars.PCBServer;
            server.PreProcess();
            try
            {
                foreach (var point in points)
                {
                    var via = CreateVia(options, net);
                    via.SetState_XLocation(AltiumApi.MmToCoord(point.X));
                    via.SetState_YLocation(AltiumApi.MmToCoord(point.Y));
                    var primitive = (IPCB_Primitive)via;
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
                + "; diameter=" + options.DiameterMm + "; hole=" + options.HoleMm + "; clearance=" + options.ClearanceMm + "; pitch=" + options.PitchMm
                + "; ignoreObstacles=" + options.IgnoreObstacles);
            return added.Count;
        }

        private static double Mm(int coordinate) => AltiumApi.CoordToMm(coordinate);
    }
}
