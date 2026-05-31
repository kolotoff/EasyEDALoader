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
            var arguments = new List<string>(args);
            bool writeDetectionDebug = RemoveDetectionDebugFlag(arguments);

            if (arguments.Count > 0 && IsProjectionCommand(arguments[0]))
                return Project(arguments.ToArray());

            if (arguments.Count > 0 && IsDetectionCommand(arguments[0]))
                return Detect(arguments.ToArray(), writeDetectionDebug);

            if (arguments.Count < 1 || arguments.Count > 2 || IsHelp(arguments[0]))
            {
                PrintUsage();
                return arguments.Count == 0 ? 1 : 0;
            }

            string inputPath = arguments[0];
            string outputPath = arguments.Count == 2 ? arguments[1] : GetDefaultOutputPath(inputPath);

            if (Directory.Exists(inputPath))
                return CleanDirectory(inputPath, outputPath, writeDetectionDebug);

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("Input STEP file was not found: " + inputPath);
                return 2;
            }

            try
            {
                var report = CleanFile(inputPath, outputPath);
                if (writeDetectionDebug)
                    WriteDetectionDebug(inputPath, GetDefaultDetectionDebugOutputPath(inputPath, outputPath), report.DetectionReport);

                Console.WriteLine("STEP watermark cleanup complete");
                Console.WriteLine("Input:  " + Path.GetFullPath(inputPath));
                Console.WriteLine("Output: " + Path.GetFullPath(outputPath));
                if (writeDetectionDebug)
                    Console.WriteLine("Detection debug: " + Path.GetFullPath(GetDefaultDetectionDebugOutputPath(inputPath, outputPath)));
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

        private static int Detect(string[] args, bool writeDetectionDebug)
        {
            if (args.Length != 2 || IsHelp(args[1]))
            {
                PrintUsage();
                return args.Length < 2 ? 1 : 0;
            }

            string inputPath = args[1];
            if (!Directory.Exists(inputPath) && !File.Exists(inputPath))
            {
                Console.Error.WriteLine("Input STEP file or directory was not found: " + inputPath);
                return 2;
            }

            try
            {
                if (Directory.Exists(inputPath))
                {
                    var inputFiles = GetStepFiles(inputPath);
                    if (inputFiles.Count == 0)
                    {
                        Console.Error.WriteLine("No STEP files were found in: " + inputPath);
                        return 2;
                    }

                    Console.WriteLine("STEP watermark detection");
                    Console.WriteLine("Input directory: " + Path.GetFullPath(inputPath));
                    Console.WriteLine("Files: " + inputFiles.Count.ToString(CultureInfo.InvariantCulture));
                    string debugDirectory = GetDefaultDetectionDebugOutputPath(inputPath, GetDefaultOutputPath(inputPath));
                    if (writeDetectionDebug)
                        Console.WriteLine("Detection debug: " + Path.GetFullPath(debugDirectory));

                    foreach (string inputFile in inputFiles)
                    {
                        var report = StepWatermarkCleaner.Detect(File.ReadAllBytes(inputFile));
                        PrintDetection(Path.GetFileName(inputFile), report);
                        if (writeDetectionDebug)
                            WriteDetectionDebug(inputFile, debugDirectory, report);
                    }

                    return 0;
                }

                Console.WriteLine("STEP watermark detection");
                Console.WriteLine("Input: " + Path.GetFullPath(inputPath));
                string debugDirectoryForFile = GetDefaultDetectionDebugOutputPath(inputPath, GetDefaultOutputPath(inputPath));
                if (writeDetectionDebug)
                    Console.WriteLine("Detection debug: " + Path.GetFullPath(debugDirectoryForFile));

                var singleReport = StepWatermarkCleaner.Detect(File.ReadAllBytes(inputPath));
                PrintDetection(Path.GetFileName(inputPath), singleReport);
                if (writeDetectionDebug)
                    WriteDetectionDebug(inputPath, debugDirectoryForFile, singleReport);

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("STEP watermark detection failed: " + ex.Message);
                return 3;
            }
        }

        private static int Project(string[] args)
        {
            if (args.Length < 2 || args.Length > 3 || IsHelp(args[1]))
            {
                PrintUsage();
                return args.Length < 2 ? 1 : 0;
            }

            string inputPath = args[1];
            string outputDirectory = args.Length == 3 ? args[2] : GetDefaultProjectionOutputPath(inputPath);

            if (!Directory.Exists(inputPath) && !File.Exists(inputPath))
            {
                Console.Error.WriteLine("Input STEP file or directory was not found: " + inputPath);
                return 2;
            }

            try
            {
                if (Directory.Exists(inputPath))
                {
                    var reports = StepProjectionRenderer.ProjectDirectory(inputPath, outputDirectory);
                    if (reports.Count == 0)
                    {
                        Console.Error.WriteLine("No STEP files were found in: " + inputPath);
                        return 2;
                    }

                    Console.WriteLine("STEP six-side projection complete");
                    Console.WriteLine("Input directory:      " + Path.GetFullPath(inputPath));
                    Console.WriteLine("Projection directory: " + Path.GetFullPath(outputDirectory));
                    Console.WriteLine("Files: " + reports.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (var report in reports)
                    {
                        Console.WriteLine(
                            Path.GetFileName(report.InputPath) +
                            ": faces=" + report.FaceCount.ToString(CultureInfo.InvariantCulture) +
                            ", edges=" + report.EdgeCount.ToString(CultureInfo.InvariantCulture) +
                            ", outputs=" + report.OutputFiles.Count.ToString(CultureInfo.InvariantCulture));
                    }

                    return 0;
                }

                var singleReport = StepProjectionRenderer.ProjectFile(inputPath, outputDirectory);
                Console.WriteLine("STEP six-side projection complete");
                Console.WriteLine("Input:                " + Path.GetFullPath(inputPath));
                Console.WriteLine("Projection directory: " + Path.GetFullPath(outputDirectory));
                Console.WriteLine("Faces: " + singleReport.FaceCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Edges: " + singleReport.EdgeCount.ToString(CultureInfo.InvariantCulture));
                foreach (string outputFile in singleReport.OutputFiles)
                    Console.WriteLine("Output: " + Path.GetFullPath(outputFile));

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("STEP six-side projection failed: " + ex.Message);
                return 3;
            }
        }

        private static int CleanDirectory(string inputDirectory, string outputDirectory, bool writeDetectionDebug)
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
            string debugDirectory = Path.Combine(outputDirectory, "Detection");
            if (writeDetectionDebug)
                Console.WriteLine("Detection debug: " + Path.GetFullPath(debugDirectory));
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
                    if (writeDetectionDebug)
                        WriteDetectionDebug(inputFile, debugDirectory, report.DetectionReport);

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

            var report = StepWatermarkCleaner.CleanWithReport(stepText, new StepWatermarkCleanerOptions());
            File.WriteAllBytes(outputPath, System.Text.Encoding.Latin1.GetBytes(report.CleanedStep));
            return report;
        }

        private static void WriteDetectionDebug(
            string inputPath,
            string outputDirectory,
            StepWatermarkDetectionReport detectionReport)
        {
            var markedRegions = StepWatermarkCleaner.LoadMarkedRegionsForStepFile(
                inputPath,
                GetDefaultProjectionOutputPath(inputPath),
                GetDefaultMarkedDirectory(inputPath));

            StepProjectionRenderer.ProjectDetectionFile(
                inputPath,
                outputDirectory,
                detectionReport,
                new StepProjectionOptions
                {
                    WriteMetadata = false
                },
                markedRegions);
        }

        private static void PrintDetection(string label, StepWatermarkDetectionReport report)
        {
            Console.WriteLine(
                label +
                ": solids=" + report.SolidCount.ToString(CultureInfo.InvariantCulture) +
                ", styledFaces=" + report.StyledFaceCount.ToString(CultureInfo.InvariantCulture) +
                ", removableSolids=" + report.RemovableSolidCount.ToString(CultureInfo.InvariantCulture) +
                ", embeddedFaces=" + report.EmbeddedFaceCount.ToString(CultureInfo.InvariantCulture) +
                ", coplanarFaces=" + report.CoplanarFaceCount.ToString(CultureInfo.InvariantCulture) +
                ", hostLoopCandidates=" + report.HostLoopCandidateCount.ToString(CultureInfo.InvariantCulture) +
                ", hostLoops=" + report.HostLoopCount.ToString(CultureInfo.InvariantCulture));
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

        private static string GetDefaultDetectionDebugOutputPath(string inputPath, string outputPath)
        {
            if (Directory.Exists(inputPath))
                return Path.Combine(outputPath, "Detection");

            string fullInputDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty;
            string inputParent = Path.GetDirectoryName(fullInputDirectory) ?? fullInputDirectory;
            if (string.Equals(Path.GetFileName(fullInputDirectory), "Original", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(inputParent, "Clean", "Detection");

            return Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? string.Empty,
                "Detection");
        }

        private static string GetDefaultProjectionOutputPath(string inputPath)
        {
            if (Directory.Exists(inputPath))
            {
                string fullInput = Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string inputParent = Path.GetDirectoryName(fullInput) ?? fullInput;
                string name = Path.GetFileName(fullInput);
                return string.Equals(name, "Original", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(inputParent, "Projection")
                    : Path.Combine(fullInput, "Projection");
            }

            string fullInputDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty;
            string fileParent = Path.GetDirectoryName(fullInputDirectory) ?? fullInputDirectory;
            return string.Equals(Path.GetFileName(fullInputDirectory), "Original", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(fileParent, "Projection")
                : Path.Combine(fullInputDirectory, "Projection");
        }

        private static string GetDefaultMarkedDirectory(string inputPath)
        {
            if (Directory.Exists(inputPath))
            {
                string fullInput = Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string parent = Path.GetDirectoryName(fullInput) ?? fullInput;
                string name = Path.GetFileName(fullInput);
                return string.Equals(name, "Original", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(parent, "Marked")
                    : Path.Combine(fullInput, "Marked");
            }

            string fullInputDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty;
            string directoryParent = Path.GetDirectoryName(fullInputDirectory) ?? fullInputDirectory;
            return string.Equals(Path.GetFileName(fullInputDirectory), "Original", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(directoryParent, "Marked")
                : Path.Combine(fullInputDirectory, "Marked");
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

        private static bool IsProjectionCommand(string arg)
        {
            return string.Equals(arg, "project", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "projection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "projections", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDetectionCommand(string arg)
        {
            return string.Equals(arg, "detect", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "detection", StringComparison.OrdinalIgnoreCase);
        }

        private static bool RemoveDetectionDebugFlag(List<string> args)
        {
            bool found = false;
            for (int i = args.Count - 1; i >= 0; i--)
            {
                if (!IsDetectionDebugFlag(args[i]))
                    continue;

                args.RemoveAt(i);
                found = true;
            }

            return found;
        }

        private static bool IsDetectionDebugFlag(string arg)
        {
            return string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-d", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--debug-detection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--detection-debug", StringComparison.OrdinalIgnoreCase);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  StepCleaner <input.step> [output.step] [--debug]");
            Console.WriteLine("  StepCleaner <input-directory> [output-directory] [--debug]");
            Console.WriteLine("  StepCleaner detect <input.step|input-directory> [--debug]");
            Console.WriteLine("  StepCleaner project <input.step|input-directory> [projection-directory]");
            Console.WriteLine();
            Console.WriteLine("When output.step is omitted, the cleaner writes <input>.clean.step next to the input file.");
            Console.WriteLine("When input-directory is named Original and output-directory is omitted, the cleaner writes to sibling Clean.");
            Console.WriteLine("The detect command runs automatic stage 1 detection only; marked JSON is not loaded.");
            Console.WriteLine("The --debug option writes detected watermark region projection PNG files to Clean\\Detection.");
            Console.WriteLine("The project command writes six PNG side projections and JSON mapping files; when the input directory is named Original, the projection directory defaults to sibling Projection.");
        }
    }
}
