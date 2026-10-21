using EasyEDA_Loader.TronstolE1Pnp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

internal static class Program
{
    private static int Main()
    {
        CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            CompiledParameterTests.Run();
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");
            string csv = TronstolE1Csv.Render(new List<TronstolE1Placement>
            {
                new TronstolE1Placement
                {
                    Designator = "R1",
                    PartNumber = "10k\t  Res\n",
                    Manufacturer = "Yageo\tCorp",
                    Description = "Line1\nLine2  tab\tend",
                    Footprint = "R0402",
                    Carrier = "Tape\nReel",
                    ReelPitch = "8mm",
                    CenterXMillimeters = 12.5,
                    CenterYMillimeters = 3.25,
                    IsBottom = false,
                    RotationDegrees = 90
                },
                new TronstolE1Placement
                {
                    Designator = "U\"2",
                    PartNumber = "MPN,2",
                    Manufacturer = "Maker",
                    Description = "Quoted \"description\"",
                    Footprint = "QFN-16",
                    Carrier = "Tray",
                    CenterXMillimeters = 7.125,
                    CenterYMillimeters = -2.5,
                    IsBottom = true,
                    RotationDegrees = 270.5,
                    HasBottomMirrorAxisY = true,
                    BottomMirrorAxisYMillimeters = 10.0
                }
            });

            string[] lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            AssertEqual(
                "\"Designator\",\"PartNumber\",\"Footprint\",\"Manufacturer\",\"Description\",\"Mid X\",\"Mid Y\",\"Layer\",\"Rotation\",\"Carrier\",\"ReelPitch\"",
                lines[0],
                "header");
            AssertEqual(
                "\"R1\",\"10k Res\",\"R0402\",\"Yageo Corp\",\"Line1 Line2 tab end\",\"12.5000\",\"3.2500\",\"Top\",\"90\",\"Tape Reel\",\"8mm\"",
                lines[1],
                "top row");
            AssertEqual(
                "\"U\"\"2\",\"MPN,2\",\"QFN-16\",\"Maker\",\"Quoted \"\"description\"\"\",\"7.1250\",\"22.5000\",\"Bottom\",\"89.5\",\"Tray\",\"\"",
                lines[2],
                "bottom row");

            var sortedPlacements = new List<TronstolE1Placement>
            {
                new TronstolE1Placement { Designator = "R11", OriginalPartNumber = "A-PART R0402", PartNumber = "A-PART" },
                new TronstolE1Placement { Designator = "R10", OriginalPartNumber = "A-PART R0402", PartNumber = "A-PART" },
                new TronstolE1Placement { Designator = "R2", OriginalPartNumber = "B-PART R0402", PartNumber = "B-PART" },
                new TronstolE1Placement { Designator = "C1", OriginalPartNumber = "C-PART C0402", PartNumber = "C-PART" },
                new TronstolE1Placement { Designator = "C10", OriginalPartNumber = "C-PART C0603", PartNumber = "C-PART" },
                new TronstolE1Placement
                {
                    Designator = "Fiducial10",
                    PartNumber = "PanelFiducial",
                    Footprint = "Round 2.00mm",
                    RotationText = "0.0",
                    IsPanelFiducial = true,
                    PanelFiducialNumber = 10
                },
                new TronstolE1Placement
                {
                    Designator = "Fiducial2",
                    PartNumber = "PanelFiducial",
                    Footprint = "Rectangular 1.00mm",
                    RotationText = "0.0",
                    IsPanelFiducial = true,
                    PanelFiducialNumber = 2
                },
                new TronstolE1Placement
                {
                    Designator = "PCB_BTLC1",
                    PartNumber = "Board bottom left corner",
                    Footprint = "PCB_BTLC",
                    CenterXMillimeters = -1.25,
                    CenterYMillimeters = 2.5,
                    RotationText = "0.0",
                    IsBoardInfo = true,
                    BoardInfoOrder = 2
                },
                new TronstolE1Placement
                {
                    Designator = "PCB_Size1",
                    PartNumber = "Board dimensions",
                    Footprint = "PCB_Size",
                    CenterXMillimeters = 120.0,
                    CenterYMillimeters = 55.25,
                    RotationText = "1.6",
                    IsBoardInfo = true,
                    BoardInfoOrder = 1
                },
                new TronstolE1Placement
                {
                    Designator = "PCB_BTLC2",
                    PartNumber = "Board bottom left corner",
                    Footprint = "PCB_BTLC",
                    CenterXMillimeters = -1.25,
                    CenterYMillimeters = 2.5,
                    IsBottom = true,
                    RotationText = "0.0",
                    IsBoardInfo = true,
                    BoardInfoOrder = 4,
                    DisableBottomTransform = true
                },
                new TronstolE1Placement
                {
                    Designator = "PCB_Size2",
                    PartNumber = "Board dimensions",
                    Footprint = "PCB_Size",
                    CenterXMillimeters = 120.0,
                    CenterYMillimeters = 55.25,
                    IsBottom = true,
                    RotationText = "1.6",
                    IsBoardInfo = true,
                    BoardInfoOrder = 3,
                    DisableBottomTransform = true
                }
            };
            sortedPlacements.AddRange(TronstolE1BoardInfo.CreateEdgeRails(
                topRailMillimeters: 4.0,
                bottomRailMillimeters: 3.0,
                leftRailMillimeters: 1.0,
                rightRailMillimeters: 2.0));
            string sortedCsv = TronstolE1Csv.Render(sortedPlacements);
            string[] sortedLines = sortedCsv.Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries);
            AssertEqual(
                "\"Fiducial2\",\"PanelFiducial\",\"Rectangular 1.00mm\",\"\",\"\",\"0.0000\",\"0.0000\",\"Top\",\"0.0\",\"\",\"\"",
                sortedLines[1],
                "first panel fiducial row");
            AssertEqual(
                "\"Fiducial10\",\"PanelFiducial\",\"Round 2.00mm\",\"\",\"\",\"0.0000\",\"0.0000\",\"Top\",\"0.0\",\"\",\"\"",
                sortedLines[2],
                "second panel fiducial row");
            AssertEqual(
                "\"PCB_Size1\",\"Board dimensions\",\"PCB_Size\",\"\",\"\",\"120.0000\",\"55.2500\",\"Top\",\"1.6\",\"\",\"\"",
                sortedLines[3],
                "board size row");
            AssertEqual(
                "\"PCB_BTLC1\",\"Board bottom left corner\",\"PCB_BTLC\",\"\",\"\",\"-1.2500\",\"2.5000\",\"Top\",\"0.0\",\"\",\"\"",
                sortedLines[4],
                "board bottom-left row");
            AssertEqual(
                "\"PCB_Size2\",\"Board dimensions\",\"PCB_Size\",\"\",\"\",\"120.0000\",\"55.2500\",\"Bottom\",\"1.6\",\"\",\"\"",
                sortedLines[5],
                "bottom board size row");
            AssertEqual(
                "\"PCB_BTLC2\",\"Board bottom left corner\",\"PCB_BTLC\",\"\",\"\",\"-1.2500\",\"2.5000\",\"Bottom\",\"0.0\",\"\",\"\"",
                sortedLines[6],
                "bottom board bottom-left row");
            AssertEqual(
                "\"EdgeRailTopBottom\",\"Top and bottom edge rail widths\",\"\",\"\",\"\",\"4.0000\",\"3.0000\",\"Top\",\"0.0\",\"\",\"\"",
                sortedLines[7],
                "top and bottom rail widths");
            AssertEqual(
                "\"EdgeRailLeftRight\",\"Left and right edge rail widths\",\"\",\"\",\"\",\"1.0000\",\"2.0000\",\"Top\",\"0.0\",\"\",\"\"",
                sortedLines[8],
                "left and right rail widths");
            AssertEqual("14", sortedLines.Length.ToString(), "metadata replaces legacy rows");
            AssertStartsWith("\"C1\",\"C-PART\",\"\",\"\",\"\",", sortedLines[9], "first group by first designator");
            AssertStartsWith("\"C10\",\"C-PART\",\"\",\"\",\"\",", sortedLines[10], "same cleaned PartNumber remains separate original group");
            AssertStartsWith("\"R2\",\"B-PART\",\"\",\"\",\"\",", sortedLines[11], "second group by first designator");
            AssertStartsWith("\"R10\",\"A-PART\",\"\",\"\",\"\",", sortedLines[12], "third group by first designator");
            AssertStartsWith("\"R11\",\"A-PART\",\"\",\"\",\"\",", sortedLines[13], "designator order within group");

            // Exercise the file writer used by the output generator with four distinct widths.
            string exportPath = Path.GetTempFileName();
            try
            {
                using (var writer = new StreamWriter(exportPath))
                    TronstolE1Csv.Write(writer, sortedPlacements);
                AssertEqual(sortedCsv, File.ReadAllText(exportPath), "four-width CSV file export");
            }
            finally
            {
                File.Delete(exportPath);
            }
            VerifyEdgeRailWidths();
            VerifyArcBounds();
            VerifyMainRKEdgeRails();

            var settings = new TronstolE1Settings();
            AssertEqual("BGA144", settings.FormatFootprintName("BGA144_BGA"), "default suffix removal");
            AssertEqual("BGA144", settings.FormatFootprintName("BGA144_bga"), "case-insensitive suffix removal");
            AssertEqual("BGA144", settings.FormatFootprintName("BGA144 BGA"), "default space suffix removal");
            AssertEqual("BGA144_BGA_TOP", settings.FormatFootprintName("BGA144_BGA_TOP"), "non-suffix preservation");
            AssertEqual(
                "RemoveBgaSuffix=True|RemoveSpaceBgaSuffix=True|SkipNfComponents=True|SkipDnpComponents=True|SkipManualSolderingComponents=True|SkipWaveSolderingComponents=True|SkipTestPoints=True|SkipSolderBridge=True|ExportPanelFiducials=True|ExportBoardDimensions=True|ExportEdgeRailsSize=True|RemoveFootprintFromPartNumber=True",
                settings.ExportToParameters(),
                "default parameters");

            settings.ImportFromParameters("RemoveBgaSuffix=False|RemoveSpaceBgaSuffix=False|CollapsePartNumberSpaces=False");
            AssertEqual("BGA144_BGA", settings.FormatFootprintName("BGA144_BGA"), "disabled suffix removal");
            AssertEqual("BGA144 BGA", settings.FormatFootprintName("BGA144 BGA"), "disabled space suffix removal");
            AssertEqual(
                "RemoveBgaSuffix=False|RemoveSpaceBgaSuffix=False|SkipNfComponents=True|SkipDnpComponents=True|SkipManualSolderingComponents=True|SkipWaveSolderingComponents=True|SkipTestPoints=True|SkipSolderBridge=True|ExportPanelFiducials=True|ExportBoardDimensions=True|ExportEdgeRailsSize=True|RemoveFootprintFromPartNumber=True",
                settings.ExportToParameters(),
                "backward-compatible parameters ignore obsolete whitespace setting");

            settings.ResetToDefault();
            AssertEqual("BGA144", settings.FormatFootprintName("BGA144_BGA"), "reset default");
            AssertEqual("BGA144", settings.FormatFootprintName("BGA144 BGA"), "reset space default");
            AssertEqual("True", settings.ShouldSkipComment("NF").ToString(), "skip NF");
            AssertEqual("True", settings.ShouldSkipComment("DNP ").ToString(), "skip DNP with trailing space");
            AssertEqual("False", settings.ShouldSkipComment("DNPX").ToString(), "preserve non-DNP comment");
            AssertEqual("True", settings.ShouldSkipPartNumber("NF").ToString(), "skip exact NF PartNumber");
            AssertEqual("True", settings.ShouldSkipPartNumber(" DNP ").ToString(), "skip exact DNP PartNumber");
            AssertEqual("False", settings.ShouldSkipPartNumber("NF ±10% X5R").ToString(), "preserve NF PartNumber prefix");
            AssertEqual("False", settings.ShouldSkipPartNumber("DNP 10k").ToString(), "preserve DNP PartNumber prefix");
            AssertEqual("False", settings.ShouldSkipPartNumber("CNF100").ToString(), "preserve non-token NF text");
            AssertEqual("False", settings.ShouldSkipPartNumber("NFA18SL227V1A45").ToString(), "preserve NF prefix inside part code");
            AssertEqual("False", settings.ShouldSkipPartNumber("DNP12345").ToString(), "preserve DNP prefix inside part code");
            AssertEqual("True", settings.ShouldSkipAssemblyMarkerPartNumber("NF ±10% X5R").ToString(), "skip NF assembly marker PartNumber");
            AssertEqual("True", settings.ShouldSkipAssemblyMarkerPartNumber("DNP 10k").ToString(), "skip DNP assembly marker PartNumber");
            AssertEqual("False", settings.ShouldSkipAssemblyMarkerPartNumber("NFA18SL227V1A45").ToString(), "preserve NF prefix inside part code marker");
            AssertEqual("False", settings.ShouldSkipAssemblyMarkerPartNumber("DNP12345").ToString(), "preserve DNP prefix inside part code marker");
            AssertEqual("True", settings.ShouldSkipSolderingType("Manual").ToString(), "skip manual soldering");
            AssertEqual("True", settings.ShouldSkipSolderingType(" Wave ").ToString(), "skip Wave soldering");
            AssertEqual("False", settings.ShouldSkipSolderingType("Reflow").ToString(), "preserve reflow soldering");
            AssertEqual("True", settings.ShouldSkipFootprintName("TestPointSMD1MM").ToString(), "skip testpoint footprint");
            AssertEqual("True", settings.ShouldSkipFootprintName("SMD Test Point 1mm").ToString(), "skip test point footprint");
            AssertEqual("True", settings.ShouldSkipFootprintName("SolderBridge_2P").ToString(), "skip solderbridge footprint");
            AssertEqual("True", settings.ShouldSkipFootprintName("Solder Bridge 2P").ToString(), "skip solder bridge footprint");
            AssertEqual("False", settings.ShouldSkipFootprintName("R0603").ToString(), "preserve normal footprint");

            AssertEqual(
                "100nF 50V ±10% X7R",
                settings.FormatPartNumber("100nF 50V  ±10% X7R C0603", "C0603"),
                "remove trailing footprint and collapse spaces");
            AssertEqual(
                "C0603",
                settings.FormatPartNumber("C0603", "C0603"),
                "keep PartNumber equal to footprint");
            AssertEqual(
                "ABC_C0603",
                settings.FormatPartNumber("ABC_C0603", "C0603"),
                "require whitespace before footprint suffix");
            AssertEqual(
                "Controller",
                settings.FormatPartNumber("Controller BGA144", "BGA144_BGA"),
                "remove normalized footprint suffix");

            settings.RemoveFootprintFromPartNumber = false;
            AssertEqual(
                "100nF 50V ±10% X7R C0603",
                settings.FormatPartNumber("100nF 50V  ±10% X7R C0603", "C0603"),
                "disabled footprint removal");

            settings.RemoveFootprintFromPartNumber = true;
            AssertEqual(
                "100nF 50V",
                settings.FormatPartNumber("100nF  50V C0603", "C0603"),
                "mandatory text whitespace cleanup");

            settings.SkipNfComponents = false;
            settings.SkipDnpComponents = false;
            settings.SkipManualSolderingComponents = false;
            settings.SkipWaveSolderingComponents = false;
            settings.SkipTestPoints = false;
            settings.SkipSolderBridge = false;
            settings.ExportPanelFiducials = false;
            settings.ExportBoardDimensions = false;
            settings.ExportEdgeRailsSize = false;
            AssertEqual("False", settings.ShouldSkipComment("NF").ToString(), "disabled NF skip");
            AssertEqual("False", settings.ShouldSkipComment("DNP ").ToString(), "disabled DNP skip");
            AssertEqual("False", settings.ShouldSkipPartNumber("NF").ToString(), "disabled NF PartNumber skip");
            AssertEqual("False", settings.ShouldSkipPartNumber("DNP").ToString(), "disabled DNP PartNumber skip");
            AssertEqual("False", settings.ShouldSkipAssemblyMarkerPartNumber("NF ±10% X5R").ToString(), "disabled NF assembly marker PartNumber skip");
            AssertEqual("False", settings.ShouldSkipAssemblyMarkerPartNumber("DNP 10k").ToString(), "disabled DNP assembly marker PartNumber skip");
            AssertEqual("False", settings.ShouldSkipSolderingType("Manual").ToString(), "disabled manual skip");
            AssertEqual("False", settings.ShouldSkipSolderingType("Wave").ToString(), "disabled Wave skip");
            AssertEqual("False", settings.ShouldSkipFootprintName("TestPointSMD1MM").ToString(), "disabled test point skip");
            AssertEqual("False", settings.ShouldSkipFootprintName("SolderBridge_2P").ToString(), "disabled solder bridge skip");
            AssertEqual("False", settings.ExportPanelFiducials.ToString(), "disabled panel fiducial export");
            AssertEqual("False", settings.ExportBoardDimensions.ToString(), "disabled board dimensions export");
            AssertEqual("False", settings.ExportEdgeRailsSize.ToString(), "disabled edge rails size export");
            var restoredSettings = new TronstolE1Settings();
            restoredSettings.ImportFromParameters(settings.ExportToParameters());
            AssertEqual("False", restoredSettings.ExportEdgeRailsSize.ToString(), "saved rail export toggle");
            restoredSettings.ResetToDefault();
            AssertEqual("True", restoredSettings.ExportEdgeRailsSize.ToString(), "reset rail export toggle");

            string partNumber = TronstolE1PartNumber.FromParameters(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Comment"] = "wrong-comment-value",
                    ["PartNumber"] = "REAL-PART-123"
                });
            AssertEqual("REAL-PART-123", partNumber, "schematic PartNumber parameter");
            AssertEqual(
                "REAL-PART-123 with tab",
                TronstolE1PartNumber.FromParameters(
                    new Dictionary<string, string> { ["PartNumber"] = "REAL-PART-123\twith\n tab" }),
                "schematic PartNumber text normalization");
            AssertEqual(
                "",
                TronstolE1PartNumber.FromParameters(
                    new Dictionary<string, string> { ["Comment"] = "comment-only" }),
                "no Comment fallback");

            AssertEqual(
                "A B C",
                TronstolE1Text.Normalize(" A\tB\r\nC  "),
                "generic text normalization");
            AssertEqual(
                "10K; Resistor",
                TronstolE1Description.Resolve("RC0603FR-0711KL", "10K", "Resistor"),
                "prepend comment before description when PartNumber and Description do not contain it");
            AssertEqual(
                "Resistor",
                TronstolE1Description.Resolve("10K ±1% R0603", "10K", "Resistor"),
                "do not prepend comment when PartNumber contains it");
            AssertEqual(
                "Ceramic capacitor 100nF 50V X7R",
                TronstolE1Description.Resolve(
                    "CL10B104KB8NNNC",
                    "100nF 50V ±10% X7R",
                    "Ceramic capacitor 100nF 50V X7R"),
                "do not prepend comment when description contains most comment parts");
            AssertEqual(
                "NF",
                TronstolE1Description.Resolve("", "NF", ""),
                "use comment when description is empty");

            Console.WriteLine("Tronstol E1 CSV tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
        }
    }

    private static void VerifyEdgeRailWidths()
    {
        string zeroCsv = TronstolE1Csv.Render(TronstolE1BoardInfo.CreateEdgeRails(0, 0, 0, 0));
        AssertEqual(
            TronstolE1Csv.Header + Environment.NewLine
                + "\"EdgeRailTopBottom\",\"Top and bottom edge rail widths\",\"\",\"\",\"\",\"0.0000\",\"0.0000\",\"Top\",\"0.0\",\"\",\"\"" + Environment.NewLine
                + "\"EdgeRailLeftRight\",\"Left and right edge rail widths\",\"\",\"\",\"\",\"0.0000\",\"0.0000\",\"Top\",\"0.0\",\"\",\"\"" + Environment.NewLine,
            zeroCsv,
            "emit both zero-width rows");

        string[] mixedLines = TronstolE1Csv.Render(TronstolE1BoardInfo.CreateEdgeRails(0, 0.125, 1.25, 0))
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        AssertEqual(
            "\"EdgeRailTopBottom\",\"Top and bottom edge rail widths\",\"\",\"\",\"\",\"0.0000\",\"0.1250\",\"Top\",\"0.0\",\"\",\"\"",
            mixedLines[1], "zero top and fractional bottom under ru-RU culture");
        AssertEqual(
            "\"EdgeRailLeftRight\",\"Left and right edge rail widths\",\"\",\"\",\"\",\"1.2500\",\"0.0000\",\"Top\",\"0.0\",\"\",\"\"",
            mixedLines[2], "fractional left and zero right under ru-RU culture");

        string[] parameterNames = { "topRailMillimeters", "bottomRailMillimeters", "leftRailMillimeters", "rightRailMillimeters" };
        foreach (double invalid in new[] { -0.001, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            for (int side = 0; side < parameterNames.Length; side++)
            {
                double[] widths = { 4, 3, 1, 2 };
                widths[side] = invalid;
                try
                {
                    TronstolE1Csv.Render(TronstolE1BoardInfo.CreateEdgeRails(widths[0], widths[1], widths[2], widths[3]));
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    AssertEqual(parameterNames[side], ex.ParamName, "invalid rail width parameter");
                    continue;
                }
                throw new InvalidOperationException("Invalid width accepted for " + parameterNames[side]);
            }
        }
    }

    private static void VerifyArcBounds()
    {
        AssertArcBounds(0, 90, 1000, 2000, 1100, 2100, "quarter circle");
        AssertArcBounds(350, 10, 1098, 1982, 1100, 2018, "sweep across zero");
        AssertArcBounds(-10, 10, 1098, 1982, 1100, 2018, "negative start angle");
        AssertArcBounds(0, 360, 900, 1900, 1100, 2100, "full circle");
        AssertArcBounds(45, 45, 900, 1900, 1100, 2100, "equal angles are a full circle");
        foreach (double angle in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            AssertEqual("False", TronstolE1ArcBounds.TryGetBounds(0, 0, 100, angle, 90,
                out _, out _, out _, out _).ToString(), "invalid start angle");
            AssertEqual("False", TronstolE1ArcBounds.TryGetBounds(0, 0, 100, 0, angle,
                out _, out _, out _, out _).ToString(), "invalid end angle");
        }
    }

    private static void AssertArcBounds(double start, double end, int left, int bottom, int right, int top, string label)
    {
        AssertEqual("True", TronstolE1ArcBounds.TryGetBounds(1000, 2000, 100, start, end,
            out int actualLeft, out int actualBottom, out int actualRight, out int actualTop).ToString(), label);
        // Allow one internal coordinate of outward rounding at trigonometric cardinal points.
        if (Math.Abs(left - actualLeft) > 1 || Math.Abs(bottom - actualBottom) > 1
            || Math.Abs(right - actualRight) > 1 || Math.Abs(top - actualTop) > 1)
            throw new InvalidOperationException("Incorrect arc bounds: " + label);
    }

    private static void VerifyMainRKEdgeRails()
    {
        // Read-only capture of MainRK_v1.6's live outline rectangle and Keep-Out primitives.
        // Its rectangle differs slightly from the polygon bounds used by the generator.
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("MainRKKeepout.txt");
        using var reader = new StreamReader(stream);
        string[] board = reader.ReadLine().Split('|');
        int boardLeft = int.Parse(board[1]), boardBottom = int.Parse(board[2]);
        int boardRight = int.Parse(board[3]), boardTop = int.Parse(board[4]);
        int left = int.MaxValue, bottom = int.MaxValue, right = int.MinValue, top = int.MinValue;
        int oldLeft = int.MaxValue, oldRight = int.MinValue;
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            string[] fields = line.Split('|');
            int x = int.Parse(fields[2]), y = int.Parse(fields[3]);
            int l, b, r, t;
            if (fields[0] == "ARC")
            {
                int radius = int.Parse(fields[4]);
                double start = double.Parse(fields[5], CultureInfo.GetCultureInfo("ru-RU"));
                double end = double.Parse(fields[6], CultureInfo.GetCultureInfo("ru-RU"));
                AssertEqual("True", TronstolE1ArcBounds.TryGetBounds(x, y, radius, start, end,
                    out l, out b, out r, out t).ToString(), "MainRK arc");
                oldLeft = Math.Min(oldLeft, x - radius);
                oldRight = Math.Max(oldRight, x + radius);
            }
            else
            {
                int x2 = int.Parse(fields[4]), y2 = int.Parse(fields[5]);
                l = Math.Min(x, x2); b = Math.Min(y, y2);
                r = Math.Max(x, x2); t = Math.Max(y, y2);
                oldLeft = Math.Min(oldLeft, l); oldRight = Math.Max(oldRight, r);
            }
            left = Math.Min(left, l); bottom = Math.Min(bottom, b);
            right = Math.Max(right, r); top = Math.Max(top, t);
        }
        AssertEqual("3.1023", ((boardLeft - oldLeft) * 0.00000254).ToString("0.0000", CultureInfo.InvariantCulture),
            "fixture reproduces phantom left rail");
        AssertEqual("4.6019", ((oldRight - boardRight) * 0.00000254).ToString("0.0000", CultureInfo.InvariantCulture),
            "fixture reproduces phantom right rail");
        double Width(int delta) => delta * 0.00000254 <= 0.05 ? 0 : delta * 0.00000254;
        string[] rows = TronstolE1Csv.Render(TronstolE1BoardInfo.CreateEdgeRails(
            Width(top - boardTop), Width(boardBottom - bottom), Width(boardLeft - left), Width(right - boardRight)))
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        AssertEqual(
            "\"EdgeRailTopBottom\",\"Top and bottom edge rail widths\",\"\",\"\",\"\",\"9.9999\",\"10.0000\",\"Top\",\"0.0\",\"\",\"\"",
            rows[1], "MainRK horizontal rail CSV");
        AssertEqual(
            "\"EdgeRailLeftRight\",\"Left and right edge rail widths\",\"\",\"\",\"\",\"0.0000\",\"0.0000\",\"Top\",\"0.0\",\"\",\"\"",
            rows[2], "MainRK has no vertical rails");
    }

    private static void AssertEqual(string expected, string actual, string label)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException(label + " mismatch. Expected: " + expected + " Actual: " + actual);
    }

    private static void AssertStartsWith(string expected, string actual, string label)
    {
        if (actual == null || !actual.StartsWith(expected, StringComparison.Ordinal))
            throw new InvalidOperationException(label + " mismatch. Expected prefix: " + expected + " Actual: " + actual);
    }
}
