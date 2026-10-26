using PCB;
using System;
using System.Collections.Generic;

namespace EasyEDA_Loader
{
    internal static class RegionPolygonConverter
    {
        private sealed class Conversion
        {
            public IPCB_Primitive Source;
            public IV7_Layer Layer;
            public object Net;
            public readonly List<PolySegment> Segments = new List<PolySegment>();
            public IPCB_Polygon Polygon;
        }

        public static int ConvertSelected(IPCB_Board board)
        {
            if (board == null) throw new ArgumentNullException(nameof(board));
            var conversions = ReadSelectedRegions(board);
            if (conversions.Count == 0)
                throw new InvalidOperationException("Select a copper region on the PCB before converting it to a polygon.");

            var added = new List<object>();
            var removed = new List<object>();
            var server = AltiumApi.GlobalVars.PCBServer;
            server.PreProcess();
            try
            {
                foreach (var conversion in conversions)
                {
                    var polygon = server.PCBObjectFactory(TObjectId.ePolyObject, TDimensionKind.eNoDimension,
                        TObjectCreationMode.eCreate_Default) as IPCB_Polygon;
                    if (polygon == null) throw new InvalidOperationException("Could not create a PCB polygon.");
                    conversion.Polygon = polygon;
                    var primitive = (IPCB_Primitive)polygon;
                    primitive.SetState_V7Layer(conversion.Layer);
                    primitive.SetState_Net(conversion.Net);
                    polygon.SetState_PolygonType((int)TPolygonType.eSignalLayerPolygon);
                    polygon.SetState_PolyHatchStyle((int)TPolyHatchStyle.ePolySolid);
                    polygon.SetState_RemoveDead(false);
                    polygon.SetState_RemoveIslandsByArea(false);
                    polygon.SetState_PointCount(conversion.Segments.Count);
                    for (int i = 0; i < conversion.Segments.Count; i++)
                        polygon.SetState_Segments(i, conversion.Segments[i]);
                    board.AddPCBObject(polygon);
                    added.Add(polygon);

                }

                // Remove sources only after all replacement outlines exist.
                foreach (var conversion in conversions)
                {
                    board.RemovePCBObject(conversion.Source);
                    removed.Add(conversion.Source);
                }
                foreach (var conversion in conversions)
                {
                    conversion.Polygon.Rebuild();
                    // Rebuilding a pour clears selection in Altium.
                    ((IPCB_Primitive)conversion.Polygon).SetState_Selected(true);
                }
            }
            catch
            {
                for (int i = added.Count - 1; i >= 0; i--) board.RemovePCBObject(added[i]);
                foreach (object source in removed) board.AddPCBObject(source);
                throw;
            }
            finally { server.PostProcess(); }
            board.ConnectivelyValidateNets();
            board.ViewManager_FullUpdate();
            EasyEDALoaderModule.Trace("Converted selected regions to polygons: " + conversions.Count);
            return conversions.Count;
        }

        private static List<Conversion> ReadSelectedRegions(IPCB_Board board)
        {
            var result = new List<Conversion>();
            var iterator = board.Internal_BoardIterator_Create() as IPCB_BoardIterator;
            if (iterator == null) throw new InvalidOperationException("Could not read PCB selection.");
            try
            {
                iterator.SetState_FilterAll();
                for (object item = iterator.Internal_FirstPCBObject(); item != null; item = iterator.Internal_NextPCBObject())
                {
                    // COM casts alone do not identify an Altium primitive type.
                    var primitive = item as IPCB_Primitive;
                    if (primitive == null || primitive.Internal_GetState_ObjectID() != (int)TObjectId.eRegionObject
                        || !primitive.GetState_Selected()) continue;
                    var region = (IPCB_Region)item;
                    var layer = primitive.Internal_GetState_V7Layer();
                    if (region.Internal_GetState_Kind() != (int)TRegionKind.eRegionKind_Copper
                        || primitive.GetState_IsKeepout() || !new V7_Layer(layer).IsSignalLayer())
                        throw new InvalidOperationException("Only copper regions on signal layers can be converted to polygon pours.");
                    if (primitive.GetState_InComponent() || primitive.GetState_InPolygon())
                        throw new InvalidOperationException("Select standalone copper regions. Component and existing polygon regions cannot be converted.");
                    // A layer-wide pour cutout would also remove copper from
                    // unrelated polygons. Reject the entire selection before
                    // making replacements until polygon-local holes are supported.
                    if (region.GetHoleCount() != 0)
                        throw new InvalidOperationException("Regions with holes cannot be converted without affecting other polygon pours. No regions were converted.");
                    var conversion = new Conversion { Source = primitive, Layer = layer, Net = primitive.Internal_GetState_Net() };
                    ReadOutline(region, conversion.Segments);
                    result.Add(conversion);
                }
            }
            finally { board.BoardIterator_Destroy(ref iterator); }
            return result;
        }

        private static void ReadOutline(IPCB_Region region, List<PolySegment> segments)
        {
            var shape = (IPCB_RegionShape)region;
            int count = shape.GetState_ShapeSegmentCount();
            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var source = shape.Internal_GetState_ShapeSegment(i);
                    segments.Add(new PolySegment
                    {
                        Kind = source.GetKind(), Vx = source.GetVx(), Vy = source.GetVy(),
                        Cx = source.GetCx(), Cy = source.GetCy(), Radius = source.GetRadius(),
                        Angle1 = source.GetAngle1(), Angle2 = source.GetAngle2()
                    });
                }
            }
            else
            {
                var contour = region.Internal_GetMainContour() as IPCB_Contour;
                if (contour == null) throw new InvalidOperationException("Could not read the selected region outline.");
                for (int i = 0; i < contour.GetState_Count(); i++)
                    segments.Add(new PolySegment { Kind = TPolySegmentType.ePolySegmentLine,
                        Vx = contour.GetState_PointX(i), Vy = contour.GetState_PointY(i) });
            }
            if (segments.Count < 2 || (segments.Count < 3 && !segments.Exists(s => s.Kind == TPolySegmentType.ePolySegmentArc)))
                throw new InvalidOperationException("A selected region has no closed outline to convert.");
        }
    }
}
