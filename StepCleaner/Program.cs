using EasyEDA_Loader;
using System;
using System.Globalization;
using System.IO;

namespace StepCleaner
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1 || args.Length > 2 || IsHelp(args[0]))
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            string inputPath = args[0];
            string outputPath = args.Length == 2
                ? args[1]
                : Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(inputPath) + ".clean" + Path.GetExtension(inputPath));

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("Input STEP file was not found: " + inputPath);
                return 2;
            }

            try
            {
                byte[] stepBytes = File.ReadAllBytes(inputPath);
                string stepText = System.Text.Encoding.Latin1.GetString(stepBytes);
                var report = StepWatermarkCleaner.CleanWithReport(stepText);
                File.WriteAllBytes(outputPath, System.Text.Encoding.Latin1.GetBytes(report.CleanedStep));

                Console.WriteLine("STEP watermark cleanup complete");
                Console.WriteLine("Input:  " + Path.GetFullPath(inputPath));
                Console.WriteLine("Output: " + Path.GetFullPath(outputPath));
                Console.WriteLine("Solids: " + report.SolidCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Styled faces: " + report.StyledFaceCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Candidates: " + report.CandidateFaceCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Removed solids: " + report.RemovedSolidCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Flattened faces: " + report.FlattenedFaceCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Flattened points: " + report.FlattenedPointCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Recolored faces: " + report.RecoloredFaceCount.ToString(CultureInfo.InvariantCulture));

                foreach (string diagnostic in report.Diagnostics)
                    Console.WriteLine(diagnostic);

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("STEP watermark cleanup failed: " + ex.Message);
                return 3;
            }
        }

        private static bool IsHelp(string arg)
        {
            return string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  StepCleaner <input.step> [output.step]");
            Console.WriteLine();
            Console.WriteLine("When output.step is omitted, the cleaner writes <input>.clean.step next to the input file.");
        }
    }
}
