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

                Directory.CreateDirectory(cleanDirectory);

                var originalFiles = GetStepFiles(originalDirectory);
                var validatedFiles = GetStepFiles(validatedDirectory);
                var validatedByName = validatedFiles.ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
                var generatedCleanByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var failures = new List<string>();

                if (originalFiles.Count == 0)
                    failures.Add("No STEP files were found in Original.");

                if (validatedFiles.Count == 0)
                    failures.Add("No STEP files were found in Validated.");

                foreach (string originalFile in originalFiles)
                {
                    string fileName = Path.GetFileName(originalFile);
                    string outputFile = Path.Combine(cleanDirectory, fileName);
                    byte[] cleanedStep = StepWatermarkCleaner.Clean(File.ReadAllBytes(originalFile));
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
