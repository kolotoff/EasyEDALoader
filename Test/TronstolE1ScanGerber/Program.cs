using EasyEDA_Loader.TronstolE1ScanGerber;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

internal static class Program
{
    private const string Gerber = "G04 Test*\n%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.8*%\n%LPD*%\nD10*\nX-1000000Y2000000D03*\nM02*\n";
    private const string Drill = "M48\nMETRIC,TZ\nT01C0.8\n%\nG90\nT01\nX-1000000Y2000000\nX3000000Y4000000G85X5000000Y4000000\nM30\n";
    private static int checks;

    private static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ScanGerberTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string gerbers = Path.Combine(root, "gerber"), drills = Path.Combine(root, "drill");
            Directory.CreateDirectory(gerbers); Directory.CreateDirectory(drills);
            foreach (string ext in new[] { "gtl", "gbl", "gts", "gbs", "gto", "gbo" })
                Write(Path.Combine(gerbers, "pcb." + ext), ext == "gbo" ? ScanGerberArchive.EmptyGerber : Gerber);
            Write(Path.Combine(root, "outline.gko"), Gerber.Replace("X-1000000Y2000000D03*", "X0Y0D02*\nX1000000Y0D01*\nX1000000Y1000000D01*\nX0Y0D01*"));
            Write(Path.Combine(drills, "pcb.TXT"), Drill);
            Write(Path.Combine(drills, "pcb.DRR"), "Ignored report");
            Write(Path.Combine(gerbers, "unrelated.gtp"), "Ignored paste");
            string zipPath = Path.Combine(root, "out", "scan.zip");
            ScanGerberArchive.Create(root, zipPath);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                Check(zip.Entries.Select(e => e.FullName).OrderBy(s => s).SequenceEqual(
                    new[] { "board.drl", "board.gbl", "board.gbo", "board.gbs", "board.gko", "board.gtl", "board.gto", "board.gts" }), "ZIP contains exactly both sides, outline and drill at root");
                using (var reader = new StreamReader(zip.GetEntry("board.drl").Open()))
                {
                    string drill = reader.ReadToEnd();
                    Check(drill.Contains(";FILE_FORMAT=4:6") && drill.Contains("G85") && drill.Contains("X-1000000Y2000000"), "Drill format, slots and signed coordinates retained");
                }
                using (var reader = new StreamReader(zip.GetEntry("board.gbl").Open()))
                    Check(reader.ReadToEnd() == Gerber, "Bottom geometry is not mirrored");
                Check(zip.GetEntry("board.gbo").Length > 0, "Empty legend retained");
            }
            byte[] originalZip = File.ReadAllBytes(zipPath);
            File.Delete(Path.Combine(gerbers, "pcb.gtl"));
            Throws(() => ScanGerberArchive.Create(root, zipPath), "Missing copper rejected");
            Check(originalZip.SequenceEqual(File.ReadAllBytes(zipPath)), "Failed export preserves previous ZIP");
            Write(Path.Combine(gerbers, "pcb.gtl"), Gerber);
            Write(Path.Combine(gerbers, "duplicate.gtl"), Gerber);
            Throws(() => ScanGerberArchive.Create(root, zipPath), "Duplicate layers rejected");
            File.Delete(Path.Combine(gerbers, "duplicate.gtl"));

            Write(Path.Combine(drills, "pcb.TXT"), Drill.Replace("X3000000Y4000000G85X5000000Y4000000\n", ""));
            Write(Path.Combine(drills, "pcb-SlotHoles.TXT"), Drill.Replace("X-1000000Y2000000\n", ""));
            ScanGerberArchive.Create(root, zipPath);
            using (var zip = ZipFile.OpenRead(zipPath))
                Check(zip.GetEntry("board.drl") == null && zip.GetEntry("board-RoundHoles.drl") != null && zip.GetEntry("board-SlotHoles.drl") != null, "Complementary round/slot programs have no combined duplicate");
            Write(Path.Combine(drills, "extra.TXT"), Drill);
            Throws(() => ScanGerberArchive.Create(root, zipPath), "Ambiguous multiple drill outputs rejected");
            File.Delete(Path.Combine(drills, "extra.TXT"));
            File.Delete(Path.Combine(drills, "pcb.TXT"));
            File.Delete(Path.Combine(drills, "pcb-SlotHoles.TXT"));
            Write(Path.Combine(drills, "empty.drl"), ScanGerberArchive.EmptyGerber);
            ScanGerberArchive.Create(root, zipPath);
            using (var zip = ZipFile.OpenRead(zipPath))
            using (var reader = new StreamReader(zip.GetEntry("board.drl").Open()))
                Check(reader.ReadToEnd() == ScanGerberArchive.EmptyGerber, "Hole-free board keeps a shared empty Gerber drill layer");

            string identities = "%ASAXBY*%\n%OFA0B0*%\n%SFA1.0B1.0*%\n%MIA0B0*%\n%IR0*%\n%IPPOS*%\n%SR*%\n";
            Check(ScanGerberArchive.NormalizeGerber(identities + Gerber, ".gtl") == Gerber, "Legacy identity transforms removed without touching geometry");
            Check(ScanGerberArchive.NormalizeGerber(ScanGerberArchive.EmptyGerber.Replace("%ADD10C,0.01*%\n", ""), ".gbo").Contains("%ADD10C,0.01*%"), "Native empty legends receive an aperture table");
            Check(ScanGerberArchive.NormalizeDrill(Drill.Replace("T01C0.8", "T01F00S00C0.8")).Contains("T01F00S00C0.8"), "Native drill speed/feed tool definitions accepted");
            string nativeDrill = "M48\n;FILE_FORMAT=4:6\nMETRIC,LZ\n;TYPE=PLATED\nT01F00S00C0.200000\n%\nT01\nX0074887001Y0001\nM30\n";
            Check(ScanGerberArchive.NormalizeDrill(nativeDrill) == nativeDrill, "Altium LZ header and trailing-suppressed coordinates remain unchanged");
            Check(ScanGerberArchive.NormalizeDrill("M48\nMETRIC,TZ\n%\nM30\n") == ScanGerberArchive.EmptyGerber, "Tool-free empty NC program becomes empty Gerber");
            Throws(() => ScanGerberArchive.NormalizeDrill(Drill.Replace("T01C0.8\n", "")), "Hole geometry without tool diameters rejected");
            foreach (string command in new[] { "%MIA1B0*%", "%SFA2B1*%", "%IR90*%", "%SRX2Y2I10J10*%", "%IFother.gbr*%", "%TF.FileFunction,Copper,L2,Bot*%", "%TF.ProjectId,Other,1*%" })
                Throws(() => ScanGerberArchive.NormalizeGerber(command + Gerber, ".gtl"), "Unsafe command rejected: " + command);
            Throws(() => ScanGerberArchive.NormalizeGerber(Gerber.Replace("X46Y46", "X46Y45"), ".gtl"), "Unequal coordinate precision rejected");
            Throws(() => ScanGerberArchive.NormalizeGerber(Gerber.Replace("%MOMM*%", ""), ".gtl"), "Missing units rejected");
            Throws(() => ScanGerberArchive.NormalizeGerber(Gerber.Replace("M02*", ""), ".gtl"), "Incomplete geometry rejected");
            Throws(() => ScanGerberArchive.NormalizeDrill(Drill.Replace("METRIC,TZ", "INCH,TZ")), "Unexpected drill units rejected");
            Throws(() => ScanGerberArchive.NormalizeDrill(Drill.Replace("M48\n", "M48\n;FILE_FORMAT=2:4\n")), "Conflicting drill format rejected");
            Throws(() => ScanGerberArchive.NormalizeDrill(Drill.Replace("G90", "G91")), "Incremental drilling rejected");

            Write(Path.Combine(gerbers, "pcb.gtl"), Gerber + "\u00e9");
            Throws(() => ScanGerberArchive.Create(root, zipPath), "Non-ASCII export rejected");
            using (var huge = new FileStream(Path.Combine(gerbers, "pcb.gtl"), FileMode.Create)) huge.SetLength(ScanGerberArchive.MaxFileBytes + 1);
            Throws(() => ScanGerberArchive.Create(root, zipPath), "Oversized file rejected before reading");
            string job = NativeFabricationExport.BuildJob(@"D:\board\PCB.PcbDoc");
            Check(job.Contains("OutputType1=Gerber") && job.Contains("OutputType2=NC Drill") && !job.Contains("OriginPosition=Absolute") && !job.Contains("Panelize=True"), "Native job uses common relative origin and no repeats");
            Check(!NativeFabricationExport.GerberParameters.Contains("16842751~0") && NativeFabricationExport.GerberParameters.Contains("16973835~1"), "Both sides selected");
            Throws(() => NativeFabricationExport.BuildJob("board\nOutputType3=Other"), "OutJob path injection rejected");
            var overrides = NativeFabricationExport.CreateParameterOverrides();
            Check(overrides.Length == 0 && overrides.Capacity >= 65536, "Native output settings use a reserved mutable buffer");
            overrides.Append(new string('X', 32000));
            Check(overrides.Length == 32000, "Native exporter can write settings into its buffer");
            Check(typeof(RT_Workspace.IOutputer).GetMethod("DM_Generate_OutputFilesTo").GetParameters()[1].ParameterType == typeof(StringBuilder), "Typed Altium interface uses writable settings instead of SDK string");
            Console.WriteLine("Tronstol E1 Scan Gerber tests passed (" + checks + " checks).");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { checks++; return; }
        catch (ArgumentException) { checks++; return; }
        throw new Exception(message);
    }

    private static void Write(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));
}
