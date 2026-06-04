using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using StepF3DRenderLib;

namespace StepF3DRender
{
    internal static class Program
    {
        private const int Success = 0;
        private const int UsageError = 2;
        private const int RuntimeError = 1;

        private static int Main(string[] args)
        {
            try
            {
                RenderRequest request = ParseArguments(args);
                if (request == null)
                    return UsageError;

                var stopwatch = Stopwatch.StartNew();
                IReadOnlyList<F3DRenderedFile> files = F3DProjectionRenderer.RenderPngFilesFromFile(
                    request.InputPath,
                    request.OutputDirectory,
                    request.SizePixels,
                    request.ViewNames);
                stopwatch.Stop();

                foreach (F3DRenderedFile file in files)
                    Console.WriteLine("view=" + file.ViewName + " output=" + file.OutputPath);
                Console.WriteLine("six_side_f3d_library_ms=" + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                return Success;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return RuntimeError;
            }
        }

        private static RenderRequest ParseArguments(string[] args)
        {
            if (args == null || args.Length < 3 || !IsOption(args[0], "--six-sides"))
            {
                WriteUsage();
                return null;
            }

            string inputPath = args[1];
            string outputDirectory = args[2];
            int sizePixels = 1600;
            List<string> viewNames = null;

            for (int i = 3; i < args.Length; i++)
            {
                if (IsOption(args[i], "--size") && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out sizePixels))
                        throw new ArgumentException("Invalid --size value.");
                    continue;
                }

                if (IsOption(args[i], "--views"))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--views must include at least one view name.");
                    viewNames = ParseViewNames(args[++i]);
                    continue;
                }

                throw new ArgumentException("Unknown argument: " + args[i]);
            }

            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                throw new FileNotFoundException("Input STEP file was not found.", inputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Output directory is required.");
            if (sizePixels <= 0)
                throw new ArgumentException("--size must be greater than zero.");

            return new RenderRequest
            {
                InputPath = Path.GetFullPath(inputPath),
                OutputDirectory = Path.GetFullPath(outputDirectory),
                SizePixels = sizePixels,
                ViewNames = viewNames
            };
        }

        private static List<string> ParseViewNames(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("--views must include at least one view name.");

            var viewNames = new List<string>();
            foreach (string rawName in value.Split(','))
            {
                string name = rawName.Trim();
                if (name.Length == 0)
                    continue;
                viewNames.Add(name);
            }

            if (viewNames.Count == 0)
                throw new ArgumentException("--views must include at least one view name.");

            F3DProjectionRenderer.NormalizeViewNames(viewNames);
            return viewNames;
        }

        private static void WriteUsage()
        {
            Console.Error.WriteLine("Usage: StepF3DRender --six-sides <input.step> <output-directory> [--size pixels] [--views x_plus,y_plus,z_plus]");
        }

        private static bool IsOption(string actual, string expected)
        {
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class RenderRequest
        {
            public string InputPath { get; set; }
            public string OutputDirectory { get; set; }
            public int SizePixels { get; set; }
            public IReadOnlyList<string> ViewNames { get; set; }
        }

    }
}
