using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EasyEDA_Loader
{
    internal static class JlcCamPcbImporter
    {
        public static JlcCamImportResult Import(JlcCamAnalysisSession session, JlcCamImportOptions options, IPCB_Board capturedBoard)
        {
            if (session == null || !session.CanImport) throw new InvalidOperationException("JLCCAM analysis is not safe to import.");
            if (options == null || (!options.ImportRails && !options.ImportHoles && !options.ImportFiducials)) throw new InvalidOperationException("Select at least one JLCCAM import category.");
            var adapter = new JlcCamPcbAdapter(); IPCB_Board board = adapter.GetCurrentBoard();
            if (board == null || !ReferenceEquals(board, capturedBoard)) throw new InvalidOperationException("The active PCB changed while the JLCCAM review dialog was open.");
            if (options.ImportHoles && session.Holes.Any(h => !h.Verified)) throw new InvalidOperationException("One or more selected edge holes have unverified dimensions.");
            if (options.ImportFiducials && session.Fiducials.Any(f => !f.Verified)) throw new InvalidOperationException("One or more selected fiducials have unverified dimensions.");
            var added = new List<object>(); var result = new JlcCamImportResult(); int holeIndex = 1, fiducialIndex = 1;
            adapter.Begin();
            try
            {
                if (options.ImportRails) foreach (JlcCamSegment rail in session.RailSegments) { object item = CreateRail(adapter, rail); adapter.Add(board, item); added.Add(item); result.RailsImported++; }
                if (options.ImportHoles) foreach (JlcCamHole hole in session.Holes) { object item = CreateHole(adapter, hole, "PanelHole" + holeIndex++); adapter.Add(board, item); added.Add(item); result.HolesImported++; }
                if (options.ImportFiducials) foreach (JlcCamFiducial f in session.Fiducials) { object item = CreateFiducial(adapter, f, "PanelFiducial" + fiducialIndex++); adapter.Add(board, item); added.Add(item); result.FiducialsImported++; }
            }
            catch
            {
                foreach (object item in added.AsEnumerable().Reverse()) adapter.Remove(board, item);
                throw;
            }
            finally { adapter.End(); }
            adapter.Redraw(board); return result;
        }

        private static object CreateRail(JlcCamPcbAdapter adapter, JlcCamSegment rail)
        {
            if (rail.Kind == JlcCamSegmentKind.Arc && rail.Center != null)
            {
                IPCB_Arc arc = adapter.Create(TObjectId.eArcObject) as IPCB_Arc; if (arc == null) throw new InvalidOperationException("Could not create PCB arc.");
                arc.SetState_CenterX(AltiumApi.MmToCoord(rail.Center.X)); arc.SetState_CenterY(AltiumApi.MmToCoord(rail.Center.Y)); arc.SetState_Radius(AltiumApi.MmToCoord(rail.Center.DistanceTo(rail.Start))); arc.SetState_LineWidth(AltiumApi.MmToCoord(0.1));
                // Altium sweeps an arc in its native positive direction. JLC G02
                // clockwise arcs therefore require exchanged endpoints; G03 arcs use
                // their Gerber endpoint order unchanged.
                arc.SetState_StartAngle(Angle(rail.Center, rail.Clockwise ? rail.End : rail.Start)); arc.SetState_EndAngle(Angle(rail.Center, rail.Clockwise ? rail.Start : rail.End));
                ((IPCB_Primitive)arc).SetState_V7Layer(new V7_Layer(TLayerConstant.eKeepOutLayer)); ((IPCB_Primitive)arc).SetState_IsKeepout(true); return arc;
            }
            IPCB_Track track = adapter.Create(TObjectId.eTrackObject) as IPCB_Track; if (track == null) throw new InvalidOperationException("Could not create PCB track.");
            track.SetState_X1(AltiumApi.MmToCoord(rail.Start.X)); track.SetState_Y1(AltiumApi.MmToCoord(rail.Start.Y)); track.SetState_X2(AltiumApi.MmToCoord(rail.End.X)); track.SetState_Y2(AltiumApi.MmToCoord(rail.End.Y)); track.SetState_Width(AltiumApi.MmToCoord(0.1)); ((IPCB_Primitive)track).SetState_V7Layer(new V7_Layer(TLayerConstant.eKeepOutLayer)); ((IPCB_Primitive)track).SetState_IsKeepout(true); return track;
        }
        private static object CreateHole(JlcCamPcbAdapter adapter, JlcCamHole hole, string name)
        {
            IPCB_Pad4 pad = adapter.Create(TObjectId.ePadObject) as IPCB_Pad4; if (pad == null) throw new InvalidOperationException("Could not create PCB pad."); int size = AltiumApi.MmToCoord(hole.NominalDiameterMm);
            pad.SetState_Mode(TPadMode.ePadMode_Simple); pad.SetState_Name(name); pad.SetState_HoleType(TExtendedHoleType.eRoundHole); pad.SetState_HoleSize(size); pad.SetState_Plated(false); pad.SetState_V7Layer(new V7_Layer(TLayerConstant.eMultiLayer));
            // A panel tooling hole is NPTH only: all copper pad-stack sizes are zero.
            pad.SetState_TopXSize(0); pad.SetState_TopYSize(0); pad.SetState_MidXSize(0); pad.SetState_MidYSize(0); pad.SetState_BotXSize(0); pad.SetState_BotYSize(0); pad.SetState_XLocation(AltiumApi.MmToCoord(hole.Center.X)); pad.SetState_YLocation(AltiumApi.MmToCoord(hole.Center.Y));
            SetMasks(pad, hole.NominalDiameterMm, hole.TopMaskOpeningMm, hole.BottomMaskOpeningMm); DisablePaste(pad); return pad;
        }
        private static object CreateFiducial(JlcCamPcbAdapter adapter, JlcCamFiducial f, string name)
        {
            IPCB_Pad4 pad = adapter.Create(TObjectId.ePadObject) as IPCB_Pad4; if (pad == null) throw new InvalidOperationException("Could not create PCB pad."); int size = AltiumApi.MmToCoord(f.NominalDiameterMm);
            pad.SetState_Mode(TPadMode.ePadMode_Simple); pad.SetState_Name(name); pad.SetState_HoleSize(0); pad.SetState_V7Layer(new V7_Layer(f.Side == JlcCamSide.Top ? TLayerConstant.eTopLayer : TLayerConstant.eBottomLayer));
            // Simple-mode pads use the current-layer stack; configure both stack
            // endpoints after assigning the side so a bottom pad cannot retain the
            // 1.524 mm factory default.
            pad.SetState_TopShape(TShape.eRounded); pad.SetState_TopXSize(size); pad.SetState_TopYSize(size); pad.SetState_BotShape(TShape.eRounded); pad.SetState_BotXSize(size); pad.SetState_BotYSize(size); pad.SetState_XLocation(AltiumApi.MmToCoord(f.Center.X)); pad.SetState_YLocation(AltiumApi.MmToCoord(f.Center.Y));
            SetMasks(pad, f.NominalDiameterMm, f.Side == JlcCamSide.Top ? (double?)f.MaskOpeningMm : null, f.Side == JlcCamSide.Bottom ? (double?)f.MaskOpeningMm : null); DisablePaste(pad); return pad;
        }
        private static void SetMasks(IPCB_Pad4 pad, double baseSize, double? top, double? bottom)
        {
            // These setters are invoked reflectively because Altium renamed mask enums between SDK releases.
            if (top.HasValue) JlcCamPcbAdapter.Set(pad, "TopSolderMaskExpansion", AltiumApi.MmToCoord((top.Value - baseSize) / 2));
            if (bottom.HasValue) JlcCamPcbAdapter.Set(pad, "BottomSolderMaskExpansion", AltiumApi.MmToCoord((bottom.Value - baseSize) / 2));
            JlcCamPcbAdapter.Set(pad, "SolderMaskExpansionFromHoleEdge", true);
        }
        private static void DisablePaste(IPCB_Pad4 pad) { pad.SetState_IsTopPasteEnabled(false); pad.SetState_IsBottomPasteEnabled(false); }
        private static double Angle(JlcCamPoint c, JlcCamPoint p) { return Math.Atan2(p.Y - c.Y, p.X - c.X) * 180.0 / Math.PI; }
    }
}
