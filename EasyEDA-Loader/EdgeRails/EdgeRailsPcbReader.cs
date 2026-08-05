using PCB;

namespace EasyEDA_Loader
{
    internal static class EdgeRailsPcbReader
    {
        public static bool TryRead(IPCB_Board board, out EdgeRailBounds bounds, out EdgeRailContour contour, out double cornerRMm)
        {
            bounds = new EdgeRailBounds();
            contour = new EdgeRailContour();
            cornerRMm = 0;
            if (board == null) return false;
            object outline;
            try { outline = board.Internal_GetState_BoardOutline(); }
            catch { return TryReadFromPrimitives(board, bounds, contour, out cornerRMm); }

            if (outline is IPCB_BoardOutline boardOutline && TryReadFromPolygon(boardOutline, bounds, contour))
            {
                cornerRMm = EdgeRailContourAnalyzer.DetectCornerRadius(contour);
                return true;
            }
            return TryReadFromPrimitives(board, bounds, contour, out cornerRMm);
        }

        private static bool TryReadFromPolygon(IPCB_BoardOutline boardOutline, EdgeRailBounds bounds, EdgeRailContour contour)
        {
            IPCB_GeometricPolygon polygon = boardOutline.Internal_BoardOutline_GeometricPolygon() as IPCB_GeometricPolygon;
            if (polygon == null || polygon.GetState_Count() == 0) return false;
            IPCB_Contour outer = polygon.Internal_GetState_Contour(0) as IPCB_Contour;
            if (outer == null || outer.GetState_Count() < 2) return false;
            for (int i = 0; i < outer.GetState_Count(); i++)
            {
                EdgeRailPoint p = new EdgeRailPoint(AltiumApi.CoordToMm(outer.GetState_PointX(i)), AltiumApi.CoordToMm(outer.GetState_PointY(i)));
                contour.Points.Add(p); bounds.Add(p);
            }
            // Ensure the contour is closed (first point repeated at the end) for the analyzer.
            if (contour.Points.Count > 0) contour.Points.Add(new EdgeRailPoint(contour.Points[0].X, contour.Points[0].Y));
            contour.Bounds.MinX = bounds.MinX; contour.Bounds.MinY = bounds.MinY;
            contour.Bounds.MaxX = bounds.MaxX; contour.Bounds.MaxY = bounds.MaxY;
            return !bounds.IsEmpty;
        }

        private static bool TryReadFromPrimitives(IPCB_Board board, EdgeRailBounds bounds, EdgeRailContour contour, out double cornerRMm)
        {
            cornerRMm = 0;
            IPCB_BoardIterator iterator = board.Internal_BoardIterator_Create() as IPCB_BoardIterator;
            if (iterator == null) return false;
            try
            {
                iterator.SetState_FilterAll();
                object current = iterator.Internal_FirstPCBObject();
                bool found = false;
                while (current != null)
                {
                    if (current is IPCB_Primitive prim)
                    {
                        ICoordRect rect = prim.Internal_BoundingRectangle();
                        if (rect != null && rect.GetRight() > rect.GetLeft() && rect.GetTop() > rect.GetBottom())
                        {
                            bounds.Add(new EdgeRailPoint(AltiumApi.CoordToMm(rect.GetLeft()), AltiumApi.CoordToMm(rect.GetBottom())));
                            bounds.Add(new EdgeRailPoint(AltiumApi.CoordToMm(rect.GetRight()), AltiumApi.CoordToMm(rect.GetTop())));
                            found = true;
                        }
                    }
                    current = iterator.Internal_NextPCBObject();
                }
                contour.Bounds.MinX = bounds.MinX; contour.Bounds.MinY = bounds.MinY;
                contour.Bounds.MaxX = bounds.MaxX; contour.Bounds.MaxY = bounds.MaxY;
                return found;
            }
            finally { board.BoardIterator_Destroy(ref iterator); }
        }
    }
}
