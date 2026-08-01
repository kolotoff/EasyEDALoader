using EasyEDA_Loader.TronstolE1Pnp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

internal static class Program
{
    private static int Main()
    {
        CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");
            string csv = TronstolE1Csv.Render(new List<TronstolE1Placement>
            {
                new TronstolE1Placement
                {
                    Designator = "R1",
                    PartNumber = "10k",
                    Footprint = "R0402",
                    CenterXMillimeters = 12.5,
                    CenterYMillimeters = 3.25,
                    IsBottom = false,
                    RotationDegrees = 90
                },
                new TronstolE1Placement
                {
                    Designator = "U\"2",
                    PartNumber = "MPN,2",
                    Footprint = "QFN-16",
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
                "\"Designator\",\"PartNumber\",\"Footprint\",\"Mid X\",\"Mid Y\",\"Layer\",\"Rotation\"",
                lines[0],
                "header");
            AssertEqual(
                "\"R1\",\"10k\",\"R0402\",\"12.5000\",\"3.2500\",\"Top\",\"90\"",
                lines[1],
                "top row");
            AssertEqual(
                "\"U\"\"2\",\"MPN,2\",\"QFN-16\",\"7.1250\",\"22.5000\",\"Bottom\",\"89.5\"",
                lines[2],
                "bottom row");

            string sortedCsv = TronstolE1Csv.Render(new List<TronstolE1Placement>
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
                    RotationText = "0.0",
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
                    RotationText = "0.0",
                    IsBoardInfo = true,
                    BoardInfoOrder = 3,
                    DisableBottomTransform = true
                }
            });
            string[] sortedLines = sortedCsv.Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries);
            AssertEqual(
                "\"Fiducial2\",\"PanelFiducial\",\"Rectangular 1.00mm\",\"0.0000\",\"0.0000\",\"Top\",\"0.0\"",
                sortedLines[1],
                "first panel fiducial row");
            AssertEqual(
                "\"Fiducial10\",\"PanelFiducial\",\"Round 2.00mm\",\"0.0000\",\"0.0000\",\"Top\",\"0.0\"",
                sortedLines[2],
                "second panel fiducial row");
            AssertEqual(
                "\"PCB_Size1\",\"Board dimensions\",\"PCB_Size\",\"120.0000\",\"55.2500\",\"Top\",\"0.0\"",
                sortedLines[3],
                "board size row");
            AssertEqual(
                "\"PCB_BTLC1\",\"Board bottom left corner\",\"PCB_BTLC\",\"-1.2500\",\"2.5000\",\"Top\",\"0.0\"",
                sortedLines[4],
                "board bottom-left row");
            AssertEqual(
                "\"PCB_Size2\",\"Board dimensions\",\"PCB_Size\",\"120.0000\",\"55.2500\",\"Bottom\",\"0.0\"",
                sortedLines[5],
                "bottom board size row");
            AssertEqual(
                "\"PCB_BTLC2\",\"Board bottom left corner\",\"PCB_BTLC\",\"-1.2500\",\"2.5000\",\"Bottom\",\"0.0\"",
                sortedLines[6],
                "bottom board bottom-left row");
            AssertStartsWith("\"C1\",\"C-PART\",", sortedLines[7], "first group by first designator");
            AssertStartsWith("\"C10\",\"C-PART\",", sortedLines[8], "same cleaned PartNumber remains separate original group");
            AssertStartsWith("\"R2\",\"B-PART\",", sortedLines[9], "second group by first designator");
            AssertStartsWith("\"R10\",\"A-PART\",", sortedLines[10], "third group by first designator");
            AssertStartsWith("\"R11\",\"A-PART\",", sortedLines[11], "designator order within group");

            var settings = new TronstolE1Settings();
            AssertEqual("BGA144", settings.FormatFootprintName("BGA144_BGA"), "default suffix removal");
            AssertEqual("BGA144", settings.FormatFootprintName("BGA144_bga"), "case-insensitive suffix removal");
            AssertEqual("BGA144", settings.FormatFootprintName("BGA144 BGA"), "default space suffix removal");
            AssertEqual("BGA144_BGA_TOP", settings.FormatFootprintName("BGA144_BGA_TOP"), "non-suffix preservation");
            AssertEqual(
                "RemoveBgaSuffix=True|RemoveSpaceBgaSuffix=True|SkipNfComponents=True|SkipDnpComponents=True|SkipManualSolderingComponents=True|SkipWaveSolderingComponents=True|ExportPanelFiducials=True|ExportBoardDimensions=True|RemoveFootprintFromPartNumber=True|CollapsePartNumberSpaces=True",
                settings.ExportToParameters(),
                "default parameters");

            settings.ImportFromParameters("RemoveBgaSuffix=False|RemoveSpaceBgaSuffix=False");
            AssertEqual("BGA144_BGA", settings.FormatFootprintName("BGA144_BGA"), "disabled suffix removal");
            AssertEqual("BGA144 BGA", settings.FormatFootprintName("BGA144 BGA"), "disabled space suffix removal");
            AssertEqual(
                "RemoveBgaSuffix=False|RemoveSpaceBgaSuffix=False|SkipNfComponents=True|SkipDnpComponents=True|SkipManualSolderingComponents=True|SkipWaveSolderingComponents=True|ExportPanelFiducials=True|ExportBoardDimensions=True|RemoveFootprintFromPartNumber=True|CollapsePartNumberSpaces=True",
                settings.ExportToParameters(),
                "backward-compatible parameters");

            settings.ResetToDefault();
            AssertEqual("BGA144", settings.FormatFootprintName("BGA144_BGA"), "reset default");
            AssertEqual("BGA144", settings.FormatFootprintName("BGA144 BGA"), "reset space default");
            AssertEqual("True", settings.ShouldSkipComment("NF").ToString(), "skip NF");
            AssertEqual("True", settings.ShouldSkipComment("DNP ").ToString(), "skip DNP with trailing space");
            AssertEqual("False", settings.ShouldSkipComment("DNPX").ToString(), "preserve non-DNP comment");
            AssertEqual("True", settings.ShouldSkipSolderingType("Manual").ToString(), "skip manual soldering");
            AssertEqual("True", settings.ShouldSkipSolderingType(" Wave ").ToString(), "skip Wave soldering");
            AssertEqual("False", settings.ShouldSkipSolderingType("Reflow").ToString(), "preserve reflow soldering");

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
            settings.CollapsePartNumberSpaces = false;
            AssertEqual(
                "100nF  50V",
                settings.FormatPartNumber("100nF  50V C0603", "C0603"),
                "disabled space collapse");

            settings.SkipNfComponents = false;
            settings.SkipDnpComponents = false;
            settings.SkipManualSolderingComponents = false;
            settings.SkipWaveSolderingComponents = false;
            settings.ExportPanelFiducials = false;
            settings.ExportBoardDimensions = false;
            AssertEqual("False", settings.ShouldSkipComment("NF").ToString(), "disabled NF skip");
            AssertEqual("False", settings.ShouldSkipComment("DNP ").ToString(), "disabled DNP skip");
            AssertEqual("False", settings.ShouldSkipSolderingType("Manual").ToString(), "disabled manual skip");
            AssertEqual("False", settings.ShouldSkipSolderingType("Wave").ToString(), "disabled Wave skip");
            AssertEqual("False", settings.ExportPanelFiducials.ToString(), "disabled panel fiducial export");
            AssertEqual("False", settings.ExportBoardDimensions.ToString(), "disabled board dimensions export");

            string partNumber = TronstolE1PartNumber.FromParameters(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Comment"] = "wrong-comment-value",
                    ["PartNumber"] = "REAL-PART-123"
                });
            AssertEqual("REAL-PART-123", partNumber, "schematic PartNumber parameter");
            AssertEqual(
                "",
                TronstolE1PartNumber.FromParameters(
                    new Dictionary<string, string> { ["Comment"] = "comment-only" }),
                "no Comment fallback");

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
