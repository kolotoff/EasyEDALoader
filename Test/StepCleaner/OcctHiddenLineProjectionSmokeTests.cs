using EasyEDA_Loader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace StepCleaner.Tests
{
    internal static class OcctHiddenLineProjectionSmokeTests
    {
        private const string ReferenceFileName = "CONN-SMD_DF56_40S_0.3V_51.step";
        private const int MinimumExpectedLines = 290;
        private const int MinimumExpectedArcs = 20;

        public static int Run()
        {
            var failures = new List<string>();
            string inputPath = Path.GetFullPath(Path.Combine(
                "Test",
                "StepCleaner",
                "Data",
                "Validated",
                ReferenceFileName));
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("OCCT HLR smoke test failed: STEP file does not exist: " + inputPath);
                return 2;
            }

            IReadOnlyList<StepSilhouettePrimitive> primitives = StepSilhouetteProjection.Generate(
                File.ReadAllBytes(inputPath),
                CreateDefaultPlacement());
            int lineCount = primitives.Count(primitive => primitive.Kind == StepSilhouettePrimitiveKind.Line);
            int arcCount = primitives.Count(primitive => primitive.Kind == StepSilhouettePrimitiveKind.Arc);

            if (lineCount < MinimumExpectedLines)
            {
                failures.Add(
                    "expected at least " +
                    MinimumExpectedLines.ToString(CultureInfo.InvariantCulture) +
                    " visible line primitives, got " +
                    lineCount.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }

            if (arcCount < MinimumExpectedArcs)
            {
                failures.Add(
                    "expected at least " +
                    MinimumExpectedArcs.ToString(CultureInfo.InvariantCulture) +
                    " visible arc primitives from OCCT HLR, got " +
                    arcCount.ToString(CultureInfo.InvariantCulture) +
                    ". This usually means the legacy silhouette fallback or destructive post-processing was used.");
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("OCCT HLR smoke test failed for " + ReferenceFileName);
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                Console.Error.WriteLine("  Primitive totals: " + FormatTotals(lineCount, arcCount, primitives.Count));
                return 1;
            }

            Console.WriteLine("OCCT HLR smoke test passed for " + ReferenceFileName);
            Console.WriteLine("Primitive totals: " + FormatTotals(lineCount, arcCount, primitives.Count));
            return 0;
        }

        private static StepSilhouettePlacement CreateDefaultPlacement()
        {
            return new StepSilhouettePlacement
            {
                TargetBounds = new StepSilhouetteBounds
                {
                    Left = -0.5,
                    Bottom = -0.5,
                    Right = 0.5,
                    Top = 0.5
                },
                RotX = 0.0,
                RotY = 0.0,
                RotZ = 0.0,
                Rotation2D = 0.0
            };
        }

        private static string FormatTotals(int lineCount, int arcCount, int totalCount)
        {
            return
                lineCount.ToString(CultureInfo.InvariantCulture) +
                " line(s), " +
                arcCount.ToString(CultureInfo.InvariantCulture) +
                " arc(s), " +
                totalCount.ToString(CultureInfo.InvariantCulture) +
                " total.";
        }
    }
}
