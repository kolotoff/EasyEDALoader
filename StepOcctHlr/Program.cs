using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace StepOcctHlr
{
    internal static class Program
    {
        private const string Usage = "Usage: StepOcctHlr <input.step|-> <output.json|-> [--rot-x deg] [--rot-y deg] [--rot-z deg] [--rotation2d deg] [--views x_plus,y_plus,z_plus] [--vector-views x_plus,y_plus,z_plus] [--vector-svg-dir dir]";

        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            string outputPath = args[1];
            string tempInputPath = null;
            try
            {
                string inputPath = ResolveInputPath(args[0], out tempInputPath);
                outputPath = ResolveOutputPath(outputPath);
                OcctConfiguration.Configure();
                ParsedOptions options = ParseOptions(args);
                if (!File.Exists(inputPath))
                {
                    WriteResult(outputPath, new ProjectionResultDto { Success = false, Error = "STEP file not found: " + inputPath });
                    return 2;
                }

                if (options.VectorViewNames.Count > 0)
                {
                    VectorProjectionResultDto vectorResult = ExtractVectorBatch(inputPath, options.VectorViewNames);
                    if (!string.IsNullOrWhiteSpace(options.VectorSvgDirectory))
                        OcctVectorHiddenLineExtractor.WriteSvgFiles(vectorResult, options.VectorSvgDirectory);
                    WriteResult(outputPath, vectorResult);
                    return vectorResult.Success ? 0 : 1;
                }
                else
                {
                    ProjectionResultDto result = options.ViewNames.Count > 0
                        ? ExtractBatch(inputPath, options.ViewNames)
                        : OcctHiddenLineExtractor.Extract(inputPath, options.SingleViewOptions);
                    WriteResult(outputPath, result);
                    return result.Success ? 0 : 1;
                }
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(Usage);
                TryWriteResult(outputPath, new ProjectionResultDto { Success = false, Error = ex.Message });
                return 2;
            }
            catch (Exception ex)
            {
                TryWriteResult(outputPath, new ProjectionResultDto { Success = false, Error = ex.ToString() });
                return 1;
            }
            finally
            {
                TryDeleteFile(tempInputPath);
            }
        }

        private static string ResolveInputPath(string inputArgument, out string tempInputPath)
        {
            tempInputPath = null;
            if (inputArgument != "-")
                return Path.GetFullPath(inputArgument);

            tempInputPath = Path.Combine(Path.GetTempPath(), "EasyEDALoaderHlr_" + Guid.NewGuid().ToString("N") + ".step");
            using (Stream input = Console.OpenStandardInput())
            using (FileStream output = File.Create(tempInputPath))
            {
                input.CopyTo(output);
            }

            return tempInputPath;
        }

        private static string ResolveOutputPath(string outputArgument)
        {
            return outputArgument == "-" ? "-" : Path.GetFullPath(outputArgument);
        }

        private static ProjectionResultDto ExtractBatch(string inputPath, List<string> viewNames)
        {
            var requests = new List<ProjectionViewRequest>();
            foreach (string viewName in viewNames)
            {
                requests.Add(new ProjectionViewRequest
                {
                    Name = viewName,
                    Options = CreateViewOptions(viewName)
                });
            }

            return OcctHiddenLineExtractor.ExtractBatch(inputPath, requests);
        }

        private static VectorProjectionResultDto ExtractVectorBatch(string inputPath, List<string> viewNames)
        {
            var requests = new List<ProjectionViewRequest>();
            foreach (string viewName in viewNames)
            {
                requests.Add(new ProjectionViewRequest
                {
                    Name = viewName,
                    Options = CreateViewOptions(viewName)
                });
            }

            return OcctVectorHiddenLineExtractor.ExtractBatch(inputPath, requests);
        }

        private static ProjectionOptions CreateViewOptions(string viewName)
        {
            var options = new ProjectionOptions();
            if (string.Equals(viewName, "x_plus", StringComparison.OrdinalIgnoreCase))
            {
                options.RotY = -90.0;
                options.Rotation2D = 270.0;
                options.MirrorX = true;
            }
            else if (string.Equals(viewName, "x_minus", StringComparison.OrdinalIgnoreCase))
            {
                options.RotY = 90.0;
                options.Rotation2D = 90.0;
                options.MirrorX = true;
            }
            else if (string.Equals(viewName, "y_plus", StringComparison.OrdinalIgnoreCase))
            {
                options.RotX = 90.0;
                options.MirrorX = true;
            }
            else if (string.Equals(viewName, "y_minus", StringComparison.OrdinalIgnoreCase))
            {
                options.RotX = -90.0;
                options.Rotation2D = 180.0;
                options.MirrorX = true;
            }
            else if (string.Equals(viewName, "z_minus", StringComparison.OrdinalIgnoreCase))
            {
                options.RotX = 180.0;
                options.Rotation2D = 180.0;
                options.MirrorX = true;
            }
            else if (string.Equals(viewName, "z_plus", StringComparison.OrdinalIgnoreCase))
            {
                options.Rotation2D = 180.0;
                options.MirrorX = true;
            }
            else
            {
                throw new ArgumentException("Unknown view name: " + viewName);
            }

            return options;
        }

        private static ParsedOptions ParseOptions(string[] args)
        {
            var options = new ParsedOptions();
            for (int index = 2; index < args.Length; index++)
            {
                string option = args[index];
                if (index + 1 >= args.Length)
                    throw new ArgumentException("Missing value for " + option);

                string valueText = args[++index];
                if (option == "--views")
                {
                    foreach (string viewName in valueText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        options.ViewNames.Add(viewName.Trim());
                    continue;
                }

                if (option == "--vector-views")
                {
                    foreach (string viewName in valueText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        options.VectorViewNames.Add(viewName.Trim());
                    continue;
                }

                if (option == "--vector-svg-dir")
                {
                    options.VectorSvgDirectory = valueText;
                    continue;
                }

                if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    throw new ArgumentException("Invalid numeric value for " + option + ": " + valueText);

                if (option == "--rot-x") options.SingleViewOptions.RotX = value;
                else if (option == "--rot-y") options.SingleViewOptions.RotY = value;
                else if (option == "--rot-z") options.SingleViewOptions.RotZ = value;
                else if (option == "--rotation2d") options.SingleViewOptions.Rotation2D = value;
                else throw new ArgumentException("Unknown option: " + option);
            }

            if (options.ViewNames.Count > 0 && options.VectorViewNames.Count > 0)
                throw new ArgumentException("--views and --vector-views cannot be used together.");

            return options;
        }

        private sealed class ParsedOptions
        {
            public ProjectionOptions SingleViewOptions { get; } = new ProjectionOptions();
            public List<string> ViewNames { get; } = new List<string>();
            public List<string> VectorViewNames { get; } = new List<string>();
            public string VectorSvgDirectory { get; set; }
        }

        private static void WriteResult<T>(string outputPath, T result)
        {
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(result, jsonOptions);
            if (outputPath == "-")
            {
                Console.Out.Write(json);
                return;
            }

            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(outputPath, json);
        }

        private static void TryWriteResult(string outputPath, ProjectionResultDto result)
        {
            try
            {
                WriteResult(outputPath, result);
            }
            catch (Exception writeException)
            {
                Console.Error.WriteLine("Failed to write result JSON: " + writeException.Message);
            }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
