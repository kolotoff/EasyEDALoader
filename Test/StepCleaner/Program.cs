using EasyEDA_Loader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace StepCleaner.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                string dataRoot = FindDataRoot();
                string originalDirectory = Path.Combine(dataRoot, "Original");
                string cleanDirectory = Path.Combine(dataRoot, "Clean");
                string validatedDirectory = Path.Combine(dataRoot, "Validated");
                string markedDirectory = Path.Combine(dataRoot, "Marked");
                string projectionDirectory = Path.Combine(dataRoot, "Projection");
                string detectionDirectory = Path.Combine(cleanDirectory, "Detection");

                Directory.CreateDirectory(cleanDirectory);

                var originalFiles = GetStepFiles(originalDirectory);
                var validatedFiles = GetStepFiles(validatedDirectory);
                var validatedByName = validatedFiles.ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
                var originalBaseNames = new HashSet<string>(
                    originalFiles.Select(file => Path.GetFileNameWithoutExtension(file)),
                    StringComparer.OrdinalIgnoreCase);
                var generatedCleanByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var failures = new List<string>();

                if (originalFiles.Count == 0)
                    failures.Add("No STEP files were found in Original.");

                if (validatedFiles.Count == 0)
                    failures.Add("No STEP files were found in Validated.");

                VerifyDetectionDebugImages(
                    originalFiles,
                    originalBaseNames,
                    projectionDirectory,
                    markedDirectory,
                    detectionDirectory,
                    failures);

                foreach (string originalFile in originalFiles)
                {
                    string fileName = Path.GetFileName(originalFile);
                    string outputFile = Path.Combine(cleanDirectory, fileName);
                    byte[] cleanedStep = StepWatermarkCleaner.Clean(File.ReadAllBytes(originalFile), new StepWatermarkCleanerOptions());
                    File.WriteAllBytes(outputFile, cleanedStep);
                    generatedCleanByName[fileName] = outputFile;

                    Console.WriteLine("Cleaned " + fileName);
                    if (!validatedByName.TryGetValue(fileName, out string validatedFile))
                    {
                        failures.Add(
                            "Clean output is missing from Validated, so it is treated as not fully cleaned. " +
                            "Please view the generated clean model before accepting it: " +
                            outputFile);
                        continue;
                    }

                    byte[] expected = File.ReadAllBytes(validatedFile);
                    byte[] actual = File.ReadAllBytes(outputFile);

                    if (!expected.SequenceEqual(actual))
                        failures.Add(FormatDifference(fileName, expected, actual));
                }

                foreach (string note in GetCleanupNotes())
                    Console.WriteLine("Cleanup note: " + note);

                foreach (string validatedFile in validatedFiles)
                {
                    string fileName = Path.GetFileName(validatedFile);
                    if (!generatedCleanByName.ContainsKey(fileName))
                        failures.Add("Validated file has no matching Original model or generated Clean output: " + fileName);
                }

                if (failures.Count > 0)
                {
                    Console.Error.WriteLine("STEP cleaner regression test failed.");
                    foreach (string failure in failures)
                        Console.Error.WriteLine("  " + failure);

                    return 1;
                }

                Console.WriteLine(
                    "STEP cleaner regression test passed. Cleaned " +
                    originalFiles.Count.ToString(CultureInfo.InvariantCulture) +
                    " original file(s), compared " +
                    validatedFiles.Count.ToString(CultureInfo.InvariantCulture) +
                    " validated file(s).");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("STEP cleaner regression test failed: " + ex.Message);
                return 1;
            }
        }

        private static void VerifyDetectionDebugImages(
            List<string> originalFiles,
            HashSet<string> originalBaseNames,
            string projectionDirectory,
            string markedDirectory,
            string detectionDirectory,
            List<string> failures)
        {
            if (!Directory.Exists(markedDirectory))
            {
                failures.Add("Marked directory was not found: " + markedDirectory);
                return;
            }

            if (!Directory.Exists(projectionDirectory))
            {
                failures.Add("Projection directory was not found: " + projectionDirectory);
                return;
            }

            Directory.CreateDirectory(detectionDirectory);

            foreach (string staleImage in Directory.GetFiles(detectionDirectory, "*.png"))
                File.Delete(staleImage);

            foreach (string originalFile in originalFiles)
            {
                var detectionReport = StepWatermarkCleaner.Detect(File.ReadAllBytes(originalFile), new StepWatermarkCleanerOptions());
                var markedRegions = StepWatermarkCleaner.LoadMarkedRegionsForStepFile(
                    originalFile,
                    projectionDirectory,
                    markedDirectory);

                StepProjectionRenderer.ProjectDetectionFile(
                    originalFile,
                    detectionDirectory,
                    detectionReport,
                    new StepProjectionOptions
                    {
                        WriteMetadata = false
                    },
                    markedRegions);
            }

            var expectedNames = GetMarkedDetectionImageNames(markedDirectory, originalBaseNames);
            var actualNames = Directory.GetFiles(detectionDirectory, "*.png")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine(
                "Detection debug images: marked=" +
                expectedNames.Count.ToString(CultureInfo.InvariantCulture) +
                ", generated=" +
                actualNames.Count.ToString(CultureInfo.InvariantCulture));

            if (actualNames.Count != expectedNames.Count)
            {
                failures.Add(
                    "Detection debug image count differs from Marked sidecars: marked=" +
                    expectedNames.Count.ToString(CultureInfo.InvariantCulture) +
                    ", generated=" +
                    actualNames.Count.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }

            var expectedSet = new HashSet<string>(expectedNames, StringComparer.OrdinalIgnoreCase);
            var actualSet = new HashSet<string>(actualNames, StringComparer.OrdinalIgnoreCase);

            foreach (string expectedName in expectedNames)
            {
                if (!actualSet.Contains(expectedName))
                    failures.Add("Detection debug image is missing for marked side: " + expectedName);
            }

            foreach (string actualName in actualNames)
            {
                if (!expectedSet.Contains(actualName))
                    failures.Add("Detection debug image has no matching marked side: " + actualName);
            }
        }

        private static List<string> GetMarkedDetectionImageNames(string markedDirectory, HashSet<string> originalBaseNames)
        {
            var result = new List<string>();
            foreach (string modelName in originalBaseNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                string stepFileName = modelName + ".step";
                var markedRegions = StepWatermarkCleaner.LoadMarkedRegionsForStepFile(
                    stepFileName,
                    Path.Combine(Path.GetDirectoryName(markedDirectory) ?? string.Empty, "Projection"),
                    markedDirectory);

                foreach (var region in markedRegions)
                {
                    string markerPath = region.SourceMarkerPath;
                    if (string.IsNullOrEmpty(markerPath))
                        continue;

                    result.Add(Path.GetFileNameWithoutExtension(markerPath) + ".png");
                }
            }

            result = result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static IReadOnlyList<string> GetCleanupNotes()
        {
            return new[]
            {
                "LED-SMD_XL-3838UV2SA06G3.step cleaned output should be reviewed as cleaned.",
                "USB-A-TH_FUS264-FDSW3K.step cleaned output should be reviewed as cleaned.",
                "SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50.step is not fully cleaned."
            };
        }

        private static string FindDataRoot()
        {
            var roots = new List<string>
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory
            };

            foreach (string root in roots)
            {
                string current = Path.GetFullPath(root);
                for (int i = 0; i < 12; i++)
                {
                    string directData = Path.Combine(current, "Data");
                    if (IsDataRoot(directData))
                        return directData;

                    string repoData = Path.Combine(current, "Test", "StepCleaner", "Data");
                    if (IsDataRoot(repoData))
                        return repoData;

                    string parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                        break;

                    current = parent;
                }
            }

            throw new DirectoryNotFoundException("Could not find Test\\StepCleaner\\Data.");
        }

        private static bool IsDataRoot(string path)
        {
            return Directory.Exists(Path.Combine(path, "Original"))
                && Directory.Exists(Path.Combine(path, "Validated"));
        }

        private static List<string> GetStepFiles(string directory)
        {
            if (!Directory.Exists(directory))
                return new List<string>();

            var result = new List<string>();
            foreach (string file in Directory.GetFiles(directory))
            {
                string extension = Path.GetExtension(file);
                if (string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase))
                    result.Add(file);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static string FormatDifference(string fileName, byte[] expected, byte[] actual)
        {
            long offset = FindFirstDifference(expected, actual);
            return
                fileName +
                " differs from Validated at byte " +
                offset.ToString(CultureInfo.InvariantCulture) +
                " (validated length " +
                expected.Length.ToString(CultureInfo.InvariantCulture) +
                ", clean length " +
                actual.Length.ToString(CultureInfo.InvariantCulture) +
                ").";
        }

        private static long FindFirstDifference(byte[] expected, byte[] actual)
        {
            int length = Math.Min(expected.Length, actual.Length);
            for (int i = 0; i < length; i++)
            {
                if (expected[i] != actual[i])
                    return i;
            }

            return length;
        }
    }
}
