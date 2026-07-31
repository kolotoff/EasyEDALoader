using EasyEDA_Loader;
using System;
using System.IO;

internal static class Program
{
    private static int Main()
    {
        try { ParserAcceptsInlineCommandsAndInches(); ParserPreservesDepthAndArcs(); ReportUsesMillimetres(); if (Environment.GetCommandLineArgs().Length > 1) AnalyzeFolder(Environment.GetCommandLineArgs()[1]); Console.WriteLine("JlcCamImport tests passed."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void ParserAcceptsInlineCommandsAndInches()
    {
        JlcCamGerberFile file = JlcCamGerberParser.Parse("%FSLAX24Y24*%%MOIN*%%ADD10C,0.040*%D10*X010000Y020000D03*M02*", "fixture.gbr");
        Check(file.Flashes.Count == 1, "flash count"); Check(Near(file.Flashes[0].Center.X, 25.4), "inch X"); Check(Near(file.Flashes[0].Aperture.XSize, 1.016), "inch aperture");
    }
    private static void ParserPreservesDepthAndArcs()
    {
        JlcCamGerberFile file = JlcCamGerberParser.Parse("%FSLAX24Y24*%%MOMM*%G04 DEPTH 1*G01*X000000Y000000D02*X010000Y000000D01*G03X010000Y010000I000000J005000D01*M02*", "outline.ko");
        Check(file.Segments.Count == 2, "segment count"); Check(file.Segments[0].Depth == 1 && file.Segments[1].Kind == JlcCamSegmentKind.Arc, "depth/arc");
    }
    private static void ReportUsesMillimetres()
    {
        var s = new JlcCamAnalysisSession { SourcePath = "fixture", PackageRoot = "fixture" }; s.Holes.Add(new JlcCamHole { Number = 1, Center = new JlcCamPoint(1.25, 2.5), CamDiameterMm = 2.05, NominalDiameterMm = 2, Verified = true, Status = "Verified" });
        string report = JlcCamReportBuilder.Build(s); Check(report.Contains("Units: mm") && report.Contains("2.05"), "mm report");
    }
    private static void AnalyzeFolder(string folder)
    {
        using (JlcCamAnalysisSession session = string.Equals(Path.GetExtension(folder), ".rar", StringComparison.OrdinalIgnoreCase) ? JlcCamSource.OpenArchive(folder) : JlcCamSource.OpenFolder(folder))
        {
            JlcCamAnalyzer.Analyze(session);
            Console.WriteLine("Sample analysis: holes=" + session.Holes.Count + ", fiducials=" + session.Fiducials.Count + ", rails=" + session.RailSegments.Count);
        }
    }
    private static bool Near(double a, double b) { return Math.Abs(a - b) < 0.00001; }
    private static void Check(bool value, string name) { if (!value) throw new InvalidDataException("Test failed: " + name); }
}

namespace EasyEDA_Loader { internal static class EasyEDALoaderModule { internal static void Trace(string message) { Console.Error.WriteLine(message); } } }
