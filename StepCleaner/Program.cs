using EasyEDA_Loader;
using System;
using System.Collections.Generic;
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
            string outputPath = args.Length == 2 ? args[1] : GetDefaultOutputPath(inputPath);

            if (Directory.Exists(inputPath))
                return CleanDirectory(inputPath, outputPath);

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("Input STEP file was not found: " + inputPath);
                return 2;
            }

            try
            {
                var report = CleanFile(inputPath, outputPath);

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

        private static int CleanDirectory(string inputDirectory, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);

            var inputFiles = GetStepFiles(inputDirectory);
            if (inputFiles.Count == 0)
            {
                Console.Error.WriteLine("No STEP files were found in: " + inputDirectory);
                return 2;
            }

            Console.WriteLine("STEP watermark batch cleanup");
            Console.WriteLine("Input directory:  " + Path.GetFullPath(inputDirectory));
            Console.WriteLine("Output directory: " + Path.GetFullPath(outputDirectory));
            Console.WriteLine("Files: " + inputFiles.Count.ToString(CultureInfo.InvariantCulture));

            int totalRemovedSolids = 0;
            int totalFlattenedFaces = 0;
            int totalFlattenedPoints = 0;
            int totalRecoloredFaces = 0;

            try
            {
                foreach (string inputFile in inputFiles)
                {
                    string outputFile = Path.Combine(outputDirectory, Path.GetFileName(inputFile));
                    var report = CleanFile(inputFile, outputFile);

                    totalRemovedSolids += report.RemovedSolidCount;
                    totalFlattenedFaces += report.FlattenedFaceCount;
                    totalFlattenedPoints += report.FlattenedPointCount;
                    totalRecoloredFaces += report.RecoloredFaceCount;

                    Console.WriteLine(
                        Path.GetFileName(inputFile) +
                        ": removedSolids=" + report.RemovedSolidCount.ToString(CultureInfo.InvariantCulture) +
                        ", flattenedFaces=" + report.FlattenedFaceCount.ToString(CultureInfo.InvariantCulture) +
                        ", flattenedPoints=" + report.FlattenedPointCount.ToString(CultureInfo.InvariantCulture) +
                        ", recoloredFaces=" + report.RecoloredFaceCount.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("STEP watermark batch cleanup failed: " + ex.Message);
                return 3;
            }

            Console.WriteLine("Batch cleanup complete");
            Console.WriteLine("Total removed solids: " + totalRemovedSolids.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("Total flattened faces: " + totalFlattenedFaces.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("Total flattened points: " + totalFlattenedPoints.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("Total recolored faces: " + totalRecoloredFaces.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static StepWatermarkCleanerReport CleanFile(string inputPath, string outputPath)
        {
            byte[] stepBytes = File.ReadAllBytes(inputPath);
            string stepText = System.Text.Encoding.Latin1.GetString(stepBytes);
            var report = StepWatermarkCleaner.CleanWithReport(stepText);
            File.WriteAllBytes(outputPath, System.Text.Encoding.Latin1.GetBytes(report.CleanedStep));
            return report;
        }

        private static string GetDefaultOutputPath(string inputPath)
        {
            if (Directory.Exists(inputPath))
            {
                string fullInput = Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string parent = Path.GetDirectoryName(fullInput) ?? fullInput;
                string name = Path.GetFileName(fullInput);
                return string.Equals(name, "Original", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(parent, "Clean")
                    : Path.Combine(fullInput, "Clean");
            }

            return Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty,
                Path.GetFileNameWithoutExtension(inputPath) + ".clean" + Path.GetExtension(inputPath));
        }

        private static List<string> GetStepFiles(string inputDirectory)
        {
            var result = new List<string>();
            foreach (string file in Directory.GetFiles(inputDirectory))
            {
                string extension = Path.GetExtension(file);
                if (string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase))
                    result.Add(file);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
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
            Console.WriteLine("  StepCleaner <input-directory> [output-directory]");
            Console.WriteLine();
            Console.WriteLine("When output.step is omitted, the cleaner writes <input>.clean.step next to the input file.");
            Console.WriteLine("When input-directory is named Original and output-directory is omitted, the cleaner writes to sibling Clean.");
        }
    }
}
