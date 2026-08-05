using PCB;
using System;
using System.Collections.Generic;

namespace EasyEDA_Loader
{
    internal sealed class EdgeRailsResult { public int Rails; public int Holes; public int Fiducials; }

    internal static class EdgeRailsPcbWriter
    {
        public static EdgeRailsResult Import(EdgeRailPlan plan, IPCB_Board capturedBoard)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var adapter = new JlcCamPcbAdapter();
            IPCB_Board board = adapter.GetCurrentBoard();
            if (board == null || !ReferenceEquals(board, capturedBoard))
                throw new InvalidOperationException("The active PCB changed while the Add Edge Rails dialog was open.");
            var added = new List<object>();
            var result = new EdgeRailsResult();
            int holeIndex = 1, fiducialIndex = 1;
            adapter.Begin();
            try
            {
                foreach (EdgeRailSegment rail in plan.RailSegments) { object item = CreateRail(adapter, rail); adapter.Add(board, item); added.Add(item); result.Rails++; }
                foreach (EdgeRailHole hole in plan.Holes) { object item = CreateHole(adapter, hole, "PanelHole" + holeIndex++); adapter.Add(board, item); added.Add(item); result.Holes++; }
                foreach (EdgeRailFiducial f in plan.Fiducials) { object item = CreateFiducial(adapter, f, "PanelFiducial" + fiducialIndex++); adapter.Add(board, item); added.Add(item); result.Fiducials++; }
            }
            catch { foreach (object item in added) { try { adapter.Remove(board, item); } catch { } } throw; }
            finally { adapter.End(); }
            adapter.Redraw(board);
            return result;
        }

        private static object CreateRail(JlcCamPcbAdapter adapter, EdgeRailSegment rail)
        {
            if (rail.Kind == EdgeRailSegmentKind.Arc && rail.Center != null)
            {
                IPCB_Arc arc = adapter.Create(TObjectId.eArcObject) as IPCB_Arc;
                if (arc == null) throw new InvalidOperationException("Could not create PCB arc.");
                // The generator sets Start/End to the two corner tangents and Center to the true centre.
                double radius = rail.Center.DistanceTo(rail.Start);
                arc.SetState_CenterX(AltiumApi.MmToCoord(rail.Center.X));
                arc.SetState_CenterY(AltiumApi.MmToCoord(rail.Center.Y));
                arc.SetState_Radius(AltiumApi.MmToCoord(radius));
                arc.SetState_LineWidth(AltiumApi.MmToCoord(0.1));
                // Altium sweeps arcs in its native positive direction; mirror the JLCCAM convention.
                arc.SetState_StartAngle(Angle(rail.Center, rail.Clockwise ? rail.End : rail.Start));
                arc.SetState_EndAngle(Angle(rail.Center, rail.Clockwise ? rail.Start : rail.End));
                ((IPCB_Primitive)arc).SetState_V7Layer(new V7_Layer(TLayerConstant.eKeepOutLayer));
                ((IPCB_Primitive)arc).SetState_IsKeepout(true);
                return arc;
            }
            IPCB_Track track = adapter.Create(TObjectId.eTrackObject) as IPCB_Track;
            if (track == null) throw new InvalidOperationException("Could not create PCB track.");
            track.SetState_X1(AltiumApi.MmToCoord(rail.Start.X)); track.SetState_Y1(AltiumApi.MmToCoord(rail.Start.Y));
            track.SetState_X2(AltiumApi.MmToCoord(rail.End.X)); track.SetState_Y2(AltiumApi.MmToCoord(rail.End.Y));
            track.SetState_Width(AltiumApi.MmToCoord(0.1));
            ((IPCB_Primitive)track).SetState_V7Layer(new V7_Layer(TLayerConstant.eKeepOutLayer));
            ((IPCB_Primitive)track).SetState_IsKeepout(true);
            return track;
        }

        private static object CreateHole(JlcCamPcbAdapter adapter, EdgeRailHole hole, string name)
        {
            IPCB_Pad4 pad = adapter.Create(TObjectId.ePadObject) as IPCB_Pad4;
            if (pad == null) throw new InvalidOperationException("Could not create PCB pad.");
            int size = AltiumApi.MmToCoord(hole.DiameterMm);
            pad.SetState_Mode(TPadMode.ePadMode_Simple);
            pad.SetState_Name(name);
            pad.SetState_HoleType(TExtendedHoleType.eRoundHole);
            pad.SetState_HoleSize(size);
            pad.SetState_Plated(false);
            pad.SetState_V7Layer(new V7_Layer(TLayerConstant.eMultiLayer));
            pad.SetState_TopXSize(0); pad.SetState_TopYSize(0); pad.SetState_MidXSize(0); pad.SetState_MidYSize(0); pad.SetState_BotXSize(0); pad.SetState_BotYSize(0);
            pad.SetState_XLocation(AltiumApi.MmToCoord(hole.Center.X));
            pad.SetState_YLocation(AltiumApi.MmToCoord(hole.Center.Y));
            JlcCamPcbAdapter.Set(pad, "TopSolderMaskExpansion", 0);    // mask opening = hole diameter (no expansion)
            JlcCamPcbAdapter.Set(pad, "BottomSolderMaskExpansion", 0);
            JlcCamPcbAdapter.Set(pad, "SolderMaskExpansionFromHoleEdge", true);
            pad.SetState_IsTopPasteEnabled(false); pad.SetState_IsBottomPasteEnabled(false);
            return pad;
        }

        private static object CreateFiducial(JlcCamPcbAdapter adapter, EdgeRailFiducial f, string name)
        {
            IPCB_Pad4 pad = adapter.Create(TObjectId.ePadObject) as IPCB_Pad4;
            if (pad == null) throw new InvalidOperationException("Could not create PCB pad.");
            int size = AltiumApi.MmToCoord(f.CopperDiameterMm);
            pad.SetState_Mode(TPadMode.ePadMode_Simple);
            pad.SetState_Name(name);
            pad.SetState_HoleSize(0);
            pad.SetState_V7Layer(new V7_Layer(f.Side == EdgeRailSide.Top ? TLayerConstant.eTopLayer : TLayerConstant.eBottomLayer));
            pad.SetState_TopShape(TShape.eRounded); pad.SetState_TopXSize(size); pad.SetState_TopYSize(size);
            pad.SetState_BotShape(TShape.eRounded); pad.SetState_BotXSize(size); pad.SetState_BotYSize(size);
            pad.SetState_XLocation(AltiumApi.MmToCoord(f.Center.X));
            pad.SetState_YLocation(AltiumApi.MmToCoord(f.Center.Y));
            int expansion = AltiumApi.MmToCoord((f.MaskOpeningMm - f.CopperDiameterMm) / 2.0);   // NOTE: int (matches JlcCamPcbImporter.cs:72-73)
            JlcCamPcbAdapter.Set(pad, "TopSolderMaskExpansion", expansion);
            JlcCamPcbAdapter.Set(pad, "BottomSolderMaskExpansion", expansion);
            JlcCamPcbAdapter.Set(pad, "SolderMaskExpansionFromHoleEdge", true);
            pad.SetState_IsTopPasteEnabled(false); pad.SetState_IsBottomPasteEnabled(false);
            return pad;
        }

        private static double Angle(EdgeRailPoint c, EdgeRailPoint p) { return System.Math.Atan2(p.Y - c.Y, p.X - c.X) * 180.0 / System.Math.PI; }
    }
}
