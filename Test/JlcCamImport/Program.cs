using EasyEDA_Loader;
using System;
using System.IO;
using System.IO.Compression;

internal static class Program
{
    private static int Main()
    {
        try { ParserAcceptsInlineCommandsAndInches(); ParserPreservesDepthAndArcs(); ParserPreservesModalState(); ParserPreservesRegionPosition(); ParserSkipsClearGeometry(); ParserHandlesSingleQuadrantArcs(); NestedZipEntryLimit(); ArchiveStreamLimit(); ReportUsesMillimetres(); if (Environment.GetCommandLineArgs().Length > 1) AnalyzeFolder(Environment.GetCommandLineArgs()[1]); Console.WriteLine("JlcCamImport tests passed."); return 0; }
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
    private static void ParserPreservesModalState()
    {
        var file = JlcCamGerberParser.Parse("%FSLAX24Y24*%%MOMM*%%ADD10C,1*%D10*X010000Y020000D02*D03*D02*X030000Y040000*D03*M02*", "modal.gbr");
        Check(file.Flashes.Count == 2, "coordinate-free flashes");
        Check(Near(file.Flashes[0].Center.X, 1) && Near(file.Flashes[1].Center.Y, 4), "modal current point");
        Check(file.Segments.Count == 0, "moves must not draw");
    }
    private static void ParserPreservesRegionPosition()
    {
        var file = JlcCamGerberParser.Parse("%FSLAX24Y24*%%MOMM*%%ADD10C,1*%D10*G36*X010000Y020000D02*X030000Y020000D01*X010000Y020000D01*G37*Y040000D03*M02*", "region.gbr");
        Check(file.Flashes.Count == 1 && Near(file.Flashes[0].Center.X, 1), "position survives skipped region");
        Check(file.Segments.Count == 0, "region boundaries not imported as rails");
    }
    private static void ParserSkipsClearGeometry()
    {
        var file = JlcCamGerberParser.Parse("%FSLAX24Y24*%%MOMM*%%LPC*%X000000Y000000D02*X010000Y000000D01*%LPD*%X020000Y000000D01*M02*", "polarity.gbr");
        Check(file.Segments.Count == 1 && Near(file.Segments[0].Start.X, 1), "clear geometry is not a positive rail");
    }
    private static void ParserHandlesSingleQuadrantArcs()
    {
        // Single-quadrant offsets are unsigned; the centre is left of the start.
        var file = JlcCamGerberParser.Parse("%FSLAX24Y24*%%MOMM*%G74*X110000Y060000D02*G03X070000Y100000I040000J0D01*M02*", "single-quadrant.gbr");
        Check(file.Segments.Count == 1 && Near(file.Segments[0].Center.X, 7) && Near(file.Segments[0].Center.Y, 6), "single quadrant centre");
    }
    private static void ReportUsesMillimetres()
    {
        var s = new JlcCamAnalysisSession { SourcePath = "fixture", PackageRoot = "fixture" }; s.Holes.Add(new JlcCamHole { Number = 1, Center = new JlcCamPoint(1.25, 2.5), CamDiameterMm = 2.05, NominalDiameterMm = 2, Verified = true, Status = "Verified" });
        string report = JlcCamReportBuilder.Build(s); Check(report.Contains("Units: mm") && report.Contains("2.05"), "mm report");
    }
    private static void NestedZipEntryLimit()
    {
        string root = Path.Combine(Path.GetTempPath(), "EasyEDA-Loader", "JLCCAM", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "ok"));
        Directory.CreateDirectory(Path.Combine(root, "YG"));
        using var session = new JlcCamAnalysisSession { PackageRoot = root, TemporaryRoot = root };
        using (var zip = ZipFile.Open(Path.Combine(root, "YG", "original.zip"), ZipArchiveMode.Create))
        {
            zip.CreateEntry("board.GKO");
            for (int i = 0; i < JlcCamSource.MaxEntries; i++) zip.CreateEntry("entry-" + i);
        }
        try { JlcCamSource.FindOriginalOutline(session); throw new Exception("Oversized nested ZIP was accepted."); }
        catch (InvalidDataException ex) { Check(ex.Message.Contains("entries"), "nested ZIP entry limit diagnostic"); }
        Check(!File.Exists(Path.Combine(root, "YG-original", "board.GKO")), "nested ZIP checked before extraction");
    }
    private static void ArchiveStreamLimit()
    {
        using var input = new MemoryStream(new byte[] { 1, 2 });
        using var output = new MemoryStream();
        long total = JlcCamSource.MaxTotalBytes - 1;
        try { JlcCamSource.CopyEntryWithLimits(input, output, ref total); throw new Exception("Actual archive byte limit ignored."); }
        catch (InvalidDataException) { }
        Check(output.Length == 0, "oversized chunk is not written");
        input.Position = 0;
        total = 0;
        JlcCamSource.CopyEntryWithLimits(input, output, ref total);
        Check(total == 2 && output.Length == 2, "ordinary archive contents copied");
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
