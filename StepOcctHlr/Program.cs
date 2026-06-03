using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace StepOcctHlr
{
    internal static class Program
    {
        private const string Usage = "Usage: StepOcctHlr <input.step> <output.json> [--rot-x deg] [--rot-y deg] [--rot-z deg] [--rotation2d deg]";

        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            string outputPath = args[1];
            try
            {
                string inputPath = Path.GetFullPath(args[0]);
                outputPath = Path.GetFullPath(outputPath);
                OcctConfiguration.Configure();
                ProjectionOptions options = ParseOptions(args);
                if (!File.Exists(inputPath))
                {
                    WriteResult(outputPath, new ProjectionResultDto { Success = false, Error = "STEP file not found: " + inputPath });
                    return 2;
                }

                ProjectionResultDto result = OcctHiddenLineExtractor.Extract(inputPath, options);
                WriteResult(outputPath, result);
                return result.Success ? 0 : 1;
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
        }

        private static ProjectionOptions ParseOptions(string[] args)
        {
            var options = new ProjectionOptions();
            for (int index = 2; index < args.Length; index++)
            {
                string option = args[index];
                if (index + 1 >= args.Length)
                    throw new ArgumentException("Missing value for " + option);

                if (!double.TryParse(args[++index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    throw new ArgumentException("Invalid numeric value for " + option + ": " + args[index]);

                if (option == "--rot-x") options.RotX = value;
                else if (option == "--rot-y") options.RotY = value;
                else if (option == "--rot-z") options.RotZ = value;
                else if (option == "--rotation2d") options.Rotation2D = value;
                else throw new ArgumentException("Unknown option: " + option);
            }

            return options;
        }

        private static void WriteResult(string outputPath, ProjectionResultDto result)
        {
            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(outputPath, JsonSerializer.Serialize(result, jsonOptions));
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
    }
}
