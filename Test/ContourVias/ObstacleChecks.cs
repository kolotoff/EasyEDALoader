using EasyEDA_Loader;
using PCB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

// Exercise the production planner through SDK interface doubles. Exact copper
// geometry and rule evaluation belong to Altium and require native verification.
internal static class ObstacleChecks
{
    public static void Run()
    {
        var fixture = new Fixture();
        var options = new ContourViaOptions { NetName = "GND" };
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 4, "skip touching and clearance-violating candidates");
        Check(fixture.Destroyed == 1 && fixture.Added == 0, "preview is destroyed without board mutation");
        Check(fixture.ProbeBoard == fixture.Board && fixture.ProbeNet == fixture.Ground, "preview carries board and target-net context");
        Check(fixture.ProbeSize == AltiumApi.MmToCoord(0.4) && fixture.ProbeHole == AltiumApi.MmToCoord(0.2), "preview uses requested via dimensions");

        options.IgnoreObstacles = true;
        fixture.FailChecks = true;
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 6 && fixture.Destroyed == 1, "ignore obstacles preserves every original sample and bypasses rule checks");
        options.IgnoreObstacles = false;
        ExpectFailure(() => ContourViaPcbWriter.Plan(fixture.Board, options));
        Check(fixture.Destroyed == 2 && fixture.Added == 0, "failed native checks release the preview and leave the board untouched");

        fixture = new Fixture { Distance = 0, Violates = false };
        options = new ContourViaOptions { NetName = "OTHER" };
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 6, "same-net copper may touch");
        options.NetName = "";
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 0, "no-net vias cannot touch assigned copper");
        fixture.ObstacleNet = null;
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 0, "two unassigned objects are still obstacles");
        options.NetName = "GND";
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 0, "assigned vias avoid unassigned copper");

        fixture = new Fixture { HasRule = false, Distance = 0 };
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 0, "touching is forbidden without a clearance rule");
        fixture.Distance = 1;
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 6, "positive distance is allowed when no rule applies");
        fixture.HasRule = true;
        fixture.Violates = false;
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 6, "exact rule clearance is accepted when native check passes");

        foreach (var id in new[] { TObjectId.eTrackObject, TObjectId.eArcObject, TObjectId.ePadObject, TObjectId.eViaObject,
            TObjectId.eFillObject, TObjectId.eRegionObject, TObjectId.eTextObject, TObjectId.eSplitPlaneObject })
        {
            fixture = new Fixture { ObstacleId = id, Distance = 0 };
            Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 0, "check copper kind " + id);
        }
        fixture = new Fixture { Electrical = false, Distance = 0 };
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 6, "non-copper graphics do not block vias");
        fixture = new Fixture { Keepout = true, Distance = 0 };
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 6, "keepout geometry is not another net's copper");
        fixture = new Fixture { ObstacleId = TObjectId.ePolyObject, Distance = 0 };
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 6, "polygon container is not treated as solid poured copper");
        foreach (var kind in new[] { TRegionKind.eRegionKind_BoardCutout, TRegionKind.eRegionKind_Cutout,
            TRegionKind.eRegionKind_NamedRegion, TRegionKind.eRegionKind_Cavity })
        {
            fixture = new Fixture { ObstacleId = TObjectId.eRegionObject, RegionKind = kind, Distance = 0 };
            Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 6, "non-copper region must not block vias: " + kind);
        }
        fixture = new Fixture { ObstacleId = TObjectId.eRegionObject, ObstacleLayer = TLayerConstant.eTopOverlay, Distance = 0 };
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 6, "non-signal region must not block vias");
        fixture = new Fixture { ObstacleId = TObjectId.eRegionObject, ObstacleNet = null, Distance = 0 };
        fixture.ParentPolygon = Polygon(fixture.Ground);
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 6, "same-net poured regions inherit their polygon net when the child net is unset");
        fixture.ParentPolygon = Polygon(Net("OTHER"));
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 0, "other-net poured regions remain obstacles when the child net is unset");

        fixture = new Fixture { ObstacleBottom = 1.4, BaseGap = 0.1, MatrixGap = 1.0, Distance = 1, Violates = true };
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 0, "advanced matrix clearance exceeds base gap and survives bounding-box rejection");
        fixture.ObstacleBottom = 20;
        fixture.FailChecks = true;
        Check(ContourViaPcbWriter.Plan(fixture.Board, options).Count == 6, "distant objects bypass native pair checks");

        fixture = new Fixture { Distance = 0 };
        EEPCB.Board = fixture.Board;
        ExpectFailure(() => ContourViaPcbWriter.Add(fixture.Board, options));
        Check(fixture.Added == 0 && fixture.PreProcesses == 0, "all-blocked add exits before starting mutation");
        EEPCB.Board = null;
        ExpectFailure(() => ContourViaPcbWriter.Add(fixture.Board, options));
        Check(fixture.Added == 0, "active-board guard still rejects stale dialogs");

        fixture = new Fixture();
        EEPCB.Board = fixture.Board;
        Check(ContourViaPcbWriter.Add(fixture.Board, options) == 4 && fixture.Added == 4, "add registers only accepted positions");
        Check(fixture.Created.Skip(1).Select(v => (int)v["XLocation"]).SequenceEqual(new[] { 4.0, 6.0, 8.0, 10.0 }.Select(AltiumApi.MmToCoord)), "add uses the filtered coordinates");
        Check(fixture.Created.Skip(1).All(v => (bool)v["IsTenting_Top"] && (bool)v["IsTenting_Bottom"]
            && ReferenceEquals(v["Net"], fixture.Ground)), "accepted vias retain the selected net and full tenting");
        fixture.Distance = 0;
        ExpectFailure(() => ContourViaPcbWriter.Add(fixture.Board, options));
        Check(fixture.Added == 4, "add replans against obstacles changed after a prior preview");
        options.IgnoreObstacles = true;
        Check(ContourViaPcbWriter.Add(fixture.Board, options) == 6 && fixture.Added == 10, "bypass also preserves unrestricted placement during add");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected placement to fail.");
    }

    private sealed class Fixture
    {
        public readonly IPCB_Board Board;
        public readonly IPCB_Net Ground = Net("GND");
        private readonly IPCB_Net other = Net("OTHER");
        public IPCB_Net ObstacleNet;
        public IPCB_Primitive ParentPolygon;
        public object ProbeBoard, ProbeNet;
        public int Destroyed, Added, ProbeSize, ProbeHole, PreProcesses;
        public readonly List<Dictionary<string, object>> Created = new List<Dictionary<string, object>>();
        private int probeX;
        public int? Distance;
        public bool? Violates;
        public bool FailChecks, HasRule = true, Electrical = true, Keepout;
        public double ObstacleBottom = 0.5, BaseGap = 0.2, MatrixGap = 0.2;
        public TObjectId ObstacleId = TObjectId.eTrackObject;
        public TRegionKind RegionKind = TRegionKind.eRegionKind_Copper;
        public TLayerConstant ObstacleLayer = TLayerConstant.eTopLayer;

        public Fixture()
        {
            ObstacleNet = other;
            var contour = Proxy<IPCB_Track>((name, args) => name switch
            {
                "Internal_GetState_ObjectID" => (int)TObjectId.eTrackObject,
                "GetState_X2" => AltiumApi.MmToCoord(10),
                "GetState_X1" or "GetState_Y1" or "GetState_Y2" => 0,
                "Internal_GetState_V7Layer" => new V7_Layer(TLayerConstant.eKeepOutLayer),
                "Internal_BoundingRectangle" => Bounds(0, 0, 10, 10),
                "GetState_IsElectricalPrim" => false,
                _ => null
            });
            var obstacle = Proxy<IPCB_Region>((name, args) => name switch
            {
                "Internal_GetState_ObjectID" => (int)ObstacleId,
                "Internal_GetState_V7Layer" => new V7_Layer(ObstacleLayer),
                "Internal_GetState_Kind" => (int)RegionKind,
                "Internal_BoundingRectangle" => Bounds(-1, ObstacleBottom, 11, ObstacleBottom + 0.1),
                "GetState_IsElectricalPrim" => Electrical,
                "GetState_IsKeepout" => Keepout,
                "Internal_GetState_Net" => ObstacleNet,
                "GetState_InPolygon" => ParentPolygon != null,
                "Internal_GetState_Polygon" => ParentPolygon,
                _ => null
            });
            var rule = Proxy<IPCB_ClearanceConstraint>((name, args) => name switch
            {
                "Internal_GetState_ObjectID" => (int)TObjectId.eRuleObject,
                "Internal_GetState_RuleKind" => (int)TRuleKind.eRule_Clearance,
                "GetState_Gap" => AltiumApi.MmToCoord(BaseGap),
                "GetClearance_1" => AltiumApi.MmToCoord(MatrixGap),
                "PrimitivesViolate" => Violates ?? probeX == AltiumApi.MmToCoord(2),
                _ => null
            });
            var objects = new object[] { contour, obstacle, rule, Ground, other };
            Board = Proxy<IPCB_Board>((name, args) =>
            {
                switch (name)
                {
                    case "Internal_BoardIterator_Create":
                        int index = 0;
                        return Proxy<IPCB_BoardIterator>((method, values) =>
                        {
                            if (method == "Internal_FirstPCBObject") { index = 0; return objects[index]; }
                            if (method == "Internal_NextPCBObject") return ++index < objects.Length ? objects[index] : null;
                            if (method == "AddFilter_Method") Check((int)values[0] == (int)TIterationMethod.eProcessAll, "include component/polygon children");
                            return null;
                        });
                    case "LayerName": return ((IV7_Layer)args[0]).GetID() == new V7_Layer(TLayerConstant.eKeepOutLayer).GetID() ? "Keep-Out Layer" : "Top Layer";
                    case "Internal_FindDominantRuleForObjectPair":
                        if (FailChecks) throw new InvalidOperationException("Native rule lookup failed.");
                        return HasRule ? rule : null;
                    case "PrimPrimDistance": return Distance ?? (probeX == 0 ? 0 : AltiumApi.MmToCoord(0.1));
                    case "AddPCBObject": Added++; return null;
                    default: return null;
                }
            });
            AltiumApi.GlobalVars.PCBServer = Proxy<IPCB_ServerInterface>((name, args) =>
            {
                switch (name)
                {
                    case "Internal_PCBObjectFactory":
                        var state = new Dictionary<string, object>();
                        Created.Add(state);
                        return Proxy<ITestVia>((method, values) =>
                        {
                            if (method.StartsWith("SetState_")) state[method.Substring("SetState_".Length)] = values[0];
                            if (method.StartsWith("GetState_") && state.TryGetValue(method.Substring("GetState_".Length), out object value)) return value;
                            switch (method)
                            {
                                case "SetState_Board": ProbeBoard = values[0]; break;
                                case "SetState_Net": ProbeNet = values[0]; break;
                                case "SetState_XLocation": probeX = (int)values[0]; break;
                                case "SetState_Size": ProbeSize = (int)values[0]; break;
                                case "SetState_HoleSize": ProbeHole = (int)values[0]; break;
                            }
                            return null;
                        });
                    case "DestroyPCBObject": Destroyed++; return null;
                    case "PreProcess": PreProcesses++; return null;
                    default: return null;
                }
            });
        }
    }

    private static ICoordRect Bounds(double left, double bottom, double right, double top)
        => Proxy<ICoordRect>((name, args) => name switch
        {
            "GetLeft" => AltiumApi.MmToCoord(left), "GetBottom" => AltiumApi.MmToCoord(bottom),
            "GetRight" => AltiumApi.MmToCoord(right), "GetTop" => AltiumApi.MmToCoord(top), _ => null
        });

    private static IPCB_Net Net(string netName) => Proxy<IPCB_Net>((name, args) => name switch
    {
        "Internal_GetState_ObjectID" => (int)TObjectId.eNetObject, "GetState_Name" => netName, _ => null
    });

    private static IPCB_Primitive Polygon(IPCB_Net net) => Proxy<IPCB_Primitive>((name, args) => name switch
    {
        "Internal_GetState_ObjectID" => (int)TObjectId.ePolyObject, "Internal_GetState_Net" => net, _ => null
    });

    private static T Proxy<T>(Func<string, object[], object> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, SdkProxy>();
        ((SdkProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class SdkProxy : DispatchProxy
    {
        public Func<string, object[], object> Handler;
        protected override object Invoke(MethodInfo method, object[] args)
        {
            object result = Handler(method.Name, args);
            return result ?? (method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
        }
    }

    public interface ITestVia : IPCB_Via, IPCB_Primitive2 { }
}

namespace EasyEDA_Loader
{
    internal static class AltiumApi
    {
        public static int MmToCoord(double value) => (int)Math.Round(value * 10000000 / 25.4);
        public static double CoordToMm(int value) => value * 25.4 / 10000000;
        internal static class GlobalVars { public static IPCB_ServerInterface PCBServer; }
    }
    internal static class EEPCB
    {
        public static IPCB_Board Board;
        public static IPCB_Board GetCurrentPcbBoard() => Board;
    }
    internal static class EasyEDALoaderModule { public static void Trace(string message) { } }
}
