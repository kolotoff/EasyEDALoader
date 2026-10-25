using EasyEDA_Loader;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;

internal static class Pr9ImportTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void MaskOverrides()
    {
        const string pad = "PAD~RECT~10~20~4~6~1~~1~0~~0~id~0~~1~0";
        var legacy = EeFootprintPad.FromString(pad);
        var empty = EeFootprintPad.FromString(pad + "~~");
        Check(!legacy.PasteMaskExpansion.HasValue && !legacy.SolderMaskExpansion.HasValue &&
            !empty.PasteMaskExpansion.HasValue && !empty.SolderMaskExpansion.HasValue,
            "Unspecified overrides must keep Altium design rules.");
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var explicitValues = EeFootprintPad.FromString(pad + "~-0.5~0");
            Check(Math.Abs(explicitValues.PasteMaskExpansion.Value + 0.127) < 1e-9 && explicitValues.SolderMaskExpansion == 0,
                "Explicit negative/zero overrides were not converted correctly.");
        }
        finally { CultureInfo.CurrentCulture = previous; }
        try { EeFootprintPad.FromString(pad + "~NaN~"); throw new InvalidOperationException("NaN override accepted."); }
        catch (FormatException) { }
    }

    internal static void SolidRegions()
    {
        var region = JsonConvert.DeserializeObject<EeFootprintShape>("\"SOLIDREGION~1~~m0,0 h20 v20 h-20 z~solid~outer~0\"") as EeFootprintSolidRegion;
        Check(region != null, "SOLIDREGION was discarded by the footprint converter.");
        var cutout = EeFootprintSolidRegion.FromString("SOLIDREGION~1~~M5,5 L15,5 15,15 5,15z~cutout~hole~0");
        var unrelated = EeFootprintSolidRegion.FromString("SOLIDREGION~2~~M0,0H20V20H0z~cutout~other-layer~0");
        var geometry = region.BuildGeometry(new[] { region, cutout, unrelated });
        Check(geometry.FillContains(new Point(2, 2)) && !geometry.FillContains(new Point(10, 10)), "Cutout was filled or solid area disappeared.");
        var contours = ShapePathGeometry.Flatten(geometry, true);
        Check(contours.Count == 2 && contours.Count(contour => contour.IsHole) == 1, "Altium region hole topology was lost.");
        var curved = ShapePathGeometry.Flatten(ShapePathGeometry.Parse("M0 0 C0 10 10 10 10 0 L0 0Z"), true);
        Check(curved.Count == 1 && curved[0].Points.Count > 6, "Curved region was not flattened.");
        var separated = ShapePathGeometry.Flatten(ShapePathGeometry.Parse("M0,0H2V2H0Z M5,0H7V2H5Z"), true);
        Check(separated.Count == 2 && separated.All(contour => !contour.IsHole), "Disconnected solids were joined or marked as holes.");
        var islands = ShapePathGeometry.SplitRegions(ShapePathGeometry.Flatten(ShapePathGeometry.Parse("M0,0H20V20H0Z M5,5H15V15H5Z M30,0H35V5H30Z"), true)).ToList();
        Check(islands.Count == 2 && islands.Count(island => island.Count == 2) == 1 && islands.All(island => !island[0].IsHole),
            "Separate region islands did not retain their own holes.");
    }

    internal static void MultipartSymbols()
    {
        string gate(string id) => "{\"head\":{\"uuid\":\"" + id + "\"},\"shape\":[\"PL~0 0 10 0~#000000~1~0~none~g~0\"]}";
        var component = JsonConvert.DeserializeObject<ComponentInfo>("{\"dataStr\":{\"shape\":[]},\"subparts\":[{\"dataStr\":" + gate("A") + "},null,{\"dataStr\":" + gate("B") + "}]} ");
        var parts = component.GetSymbolParts();
        Check(parts.Count == 2 && parts[0].Head.Uuid == "A" && parts[1].Head.Uuid == "B" && component.GetPrimarySymbol() == parts[0],
            "Multipart symbol data was omitted or reordered.");
        var single = JsonConvert.DeserializeObject<ComponentInfo>("{\"dataStr\":" + gate("single") + "}");
        Check(single.GetSymbolParts().Count == 1 && single.GetPrimarySymbol().Head.Uuid == "single", "Single-part import regressed.");
    }

    internal static void Graphics()
    {
        var shapes = new List<EeSymbolShape>
        {
            new EeSymbolPolyline { Points = "0,0 10,0 10,10", FillColor = "none" },
            new EeSymbolPolygon { Points = "2,2 8,2 5,8", FillColor = "#000000" },
            new EeSymbolPath { Paths = "M0,5 C2,0 8,10 10,5", FillColor = "none" }
        };
        var target = new AltiumSymbolRectangle { X1 = 0, Y1 = 0, X2 = 100, Y2 = 60 };
        var graphics = SymbolGraphics.Build(shapes, target);
        Check(graphics.Count == 3 && !graphics[0].IsClosed && graphics[1].IsFilled && graphics[2].Points.Count > 4,
            "Graphic type, fill or curved path was lost.");
        Check(graphics.SelectMany(graphic => graphic.Points).All(point => double.IsFinite(point.X) && double.IsFinite(point.Y) && point.X >= 0 && point.X <= 100 && point.Y >= 0 && point.Y <= 60),
            "Graphics escaped the generated symbol bounds.");
        var horizontal = SymbolGraphics.Build(new[] { new EeSymbolPolyline { Points = "1 3 5 3", FillColor = "none" } }, target);
        Check(horizontal[0].Points.All(point => Math.Abs(point.Y - 30) < 1e-9), "Degenerate graphic axis did not stay centered.");
    }

    internal static void GraphicLabelClearance()
    {
        var target = new AltiumSymbolRectangle { X1 = 0, Y1 = 0, X2 = 900, Y2 = 400 };
        var graphics = SymbolGraphics.Build(new[] { new EeSymbolPolygon { Points = "400 290 420 290 410 300", FillColor = "#000000" } }, target);
        Check(graphics.SelectMany(graphic => graphic.Points).All(point => point.X >= 270 && point.X <= 630),
            "Filled graphic overlaps the left/right pin-label areas of the generated GOST symbol.");
    }

    internal static void RegionInterop()
    {
        var holeFlags = new List<bool>();
        int outlinePoints = 0;
        object polygon = PcbTestProxy.Create("PCB.IPCB_GeometricPolygon", (method, args) =>
        {
            if (method.Name == "Internal_AddContourIsHole") holeFlags.Add((bool)args[1]);
            return PcbTestProxy.Default(method.ReturnType);
        });
        object region = PcbTestProxy.Create("PCB.IPCB_Region", (method, args) => PcbTestProxy.Default(method.ReturnType));
        object component = PcbTestProxy.Create("PCB.IPCB_LibComponent", (method, args) => PcbTestProxy.Default(method.ReturnType));
        object server = PcbTestProxy.Create("PCB.IPCB_ServerInterface", (method, args) =>
        {
            if (method.Name.Contains("GeometricPolygonFactory")) return polygon;
            if (method.Name.Contains("ContourFactory")) return PcbTestProxy.Create("PCB.IPCB_Contour", (member, values) =>
            {
                if (member.Name == "AddPoint") outlinePoints++;
                return PcbTestProxy.Default(member.ReturnType);
            });
            if (method.Name.Contains("PCBObjectFactory")) return region;
            return PcbTestProxy.Default(method.ReturnType);
        });
        var field = typeof(AltiumApi).GetNestedType("GlobalVars", BindingFlags.NonPublic).GetField("pcbServer", BindingFlags.NonPublic | BindingFlags.Static);
        object previous = field.GetValue(null);
        try
        {
            field.SetValue(null, server);
            var contours = ShapePathGeometry.Flatten(ShapePathGeometry.Parse("M0,0H20V20H0Z M5,5H15V15H5Z"), true);
            var create = typeof(EEPCB).GetMethod("CreateSolidRegion", BindingFlags.Static | BindingFlags.NonPublic);
            object layer = Enum.Parse(create.GetParameters()[1].ParameterType, "eTopLayer");
            create.Invoke(null, new object[] { component, layer, contours });
            Check(holeFlags.Count == 2 && holeFlags.Count(value => value) == 1 && outlinePoints >= 8, "Region interop dropped hole flags or points.");
        }
        finally { field.SetValue(null, previous); }
    }

    internal static void MultipartInterop()
    {
        int currentPart = 1, declaredParts = 0, libraryAdds = 0;
        bool currentComponentSet = false;
        var ownerParts = new List<int>();
        var vertexIndices = new List<int>();
        object library = PcbTestProxy.Create("SCH.ISch_Lib", (method, args) =>
        {
            if (method.Name == "SetState_Current_SchComponent") currentComponentSet = true;
            if (method.Name == "SetState_CurrentSchComponentPartId") currentPart = Convert.ToInt32(args[0]);
            if (method.Name == "GetState_CurrentSchComponentPartId") return currentPart;
            if (method.Name == "AddSchComponent") libraryAdds++;
            return PcbTestProxy.Default(method.ReturnType);
        });
        object component = PcbTestProxy.Create("SCH.ISch_Component", (method, args) =>
        {
            if (method.Name == "SetState_PartCountNoPart0") declaredParts = Convert.ToInt32(args[0]);
            return PcbTestProxy.Default(method.ReturnType);
        });
        object server = PcbTestProxy.Create("SCH.ISch_ServerInterface", (method, args) =>
        {
            if (!method.Name.Contains("SchObjectFactory")) return PcbTestProxy.Default(method.ReturnType);
            Check(currentComponentSet, "Part ownership was assigned before selecting the current component.");
            string kind = Enum.GetName(Type.GetType("SCH.TObjectId, Altium.SDK.Interfaces", true), args[0]);
            string type = kind switch { "eRectangle" => "SCH.ISch_Rectangle", "ePolygon" => "SCH.ISch_Polygon", "eLine" => "SCH.ISch_Line", _ => throw new InvalidOperationException("Unexpected graphic type " + kind) };
            return PcbTestProxy.Create(type, (member, values) =>
            {
                if (member.Name == "SetState_OwnerPartId") ownerParts.Add(Convert.ToInt32(values[0]));
                if (member.Name == "SetState_Vertex") vertexIndices.Add(Convert.ToInt32(values[0]));
                return PcbTestProxy.Default(member.ReturnType);
            });
        });
        var field = typeof(AltiumApi).GetNestedType("GlobalVars", BindingFlags.NonPublic).GetField("schServer", BindingFlags.NonPublic | BindingFlags.Static);
        object previous = field.GetValue(null);
        try
        {
            field.SetValue(null, server);
            var symbol = new SymbolData { Shapes = new List<EeSymbolShape> { new EeSymbolPolygon { Points = "0 0 10 0 5 10", FillColor = "#000000" } } };
            typeof(SymbolDrawing).GetMethod("CreateMultiPartComponent").Invoke(null, new object[] { library, component, "", "", new[] { symbol, symbol }, false });
            Check(declaredParts == 2 && libraryAdds == 1 && currentPart == 1 && ownerParts.Contains(1) && ownerParts.Contains(2), "Multipart declaration, ownership, reset or library insertion failed.");
            Check(vertexIndices.Count >= 6 && vertexIndices.Min() == 1, "Schematic polygon vertices must use 1-based indices.");
        }
        finally { field.SetValue(null, previous); }
    }
}
