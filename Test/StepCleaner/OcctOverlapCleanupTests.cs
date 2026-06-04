using EasyEDA_Loader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace StepCleaner.Tests
{
    internal static class OcctOverlapCleanupTests
    {
        public static int Run()
        {
            var failures = new List<string>();

            AssertTouchingCollinearLinesAreMerged(failures);
            AssertShortTouchingLinesAreMergedBeforeCutoff(failures);
            AssertTinyProjectedGapIsNotMerged(failures);
            AssertSlightlyAngledTouchingLinesAreNotMerged(failures);
            AssertOffsetParallelLinesAreNotMerged(failures);
            AssertCircularContactChordLinesAreConvertedToArcs(failures);
            AssertMixedLineArcFixtureIsStable(failures);
            AssertGappedCollinearLinesAreKeptSeparate(failures);
            AssertCoveredLineIsRemoved(failures);
            AssertMostlyVisibleLineIsKept(failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("OCCT overlap cleanup tests failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("OCCT overlap cleanup tests passed.");
            return 0;
        }

        private static void AssertTouchingCollinearLinesAreMerged(List<string> failures)
        {
            var primitives = new List<StepSilhouettePrimitive>
            {
                StepSilhouettePrimitive.Line(0.0, 0.0, 5.0, 0.0),
                StepSilhouettePrimitive.Line(5.0, 0.0, 10.0, 0.0)
            };

            List<StepSilhouettePrimitive> cleaned = OptimizeOcctPrimitives(primitives);
            AssertEqual(1, cleaned.Count, "touching collinear OCCT lines should be merged", failures);
            if (cleaned.Count == 1)
                AssertLine(0.0, 0.0, 10.0, 0.0, cleaned[0], "merged touching line", failures);
        }

        private static void AssertGappedCollinearLinesAreKeptSeparate(List<string> failures)
        {
            var primitives = new List<StepSilhouettePrimitive>
            {
                StepSilhouettePrimitive.Line(0.0, 0.0, 5.0, 0.0),
                StepSilhouettePrimitive.Line(5.03, 0.0, 10.0, 0.0)
            };

            List<StepSilhouettePrimitive> cleaned = OptimizeOcctPrimitives(primitives);
            AssertEqual(2, cleaned.Count, "gapped collinear OCCT lines should stay separate", failures);
        }

        private static void AssertTinyProjectedGapIsNotMerged(List<string> failures)
        {
            var primitives = new List<StepSilhouettePrimitive>
            {
                StepSilhouettePrimitive.Line(0.0, 0.0, 5.0, 0.0),
                StepSilhouettePrimitive.Line(5.005, 0.0, 10.0, 0.0)
            };

            List<StepSilhouettePrimitive> cleaned = OptimizeOcctPrimitives(primitives);
            AssertEqual(2, cleaned.Count, "0.005mm projected gap should not merge with 0.001mm interval tolerance", failures);
        }

        private static void AssertSlightlyAngledTouchingLinesAreNotMerged(List<string> failures)
        {
            var primitives = new List<StepSilhouettePrimitive>
            {
                StepSilhouettePrimitive.Line(0.0, 0.0, 5.0, 0.0),
                StepSilhouettePrimitive.Line(5.0, 0.0, 10.0, 0.002)
            };

            List<StepSilhouettePrimitive> cleaned = OptimizeOcctPrimitives(primitives);
            AssertEqual(2, cleaned.Count, "touching OCCT lines with different direction should stay separate", failures);
        }

        private static void AssertOffsetParallelLinesAreNotMerged(List<string> failures)
        {
            var primitives = new List<StepSilhouettePrimitive>
            {
                StepSilhouettePrimitive.Line(0.0, 0.0, 5.0, 0.0),
                StepSilhouettePrimitive.Line(0.0, 0.004, 5.0, 0.004)
            };

            List<StepSilhouettePrimitive> merged = MergeTouchingOcctLinePrimitives(primitives);
            AssertEqual(2, merged.Count, "parallel OCCT lines on different centerlines should not merge", failures);
        }

        private static void AssertCircularContactChordLinesAreConvertedToArcs(List<string> failures)
        {
            var primitives = new List<StepSilhouettePrimitive>
            {
                StepSilhouettePrimitive.Arc(0.0, 0.0, 0.22, 0.0, 90.0),
                StepSilhouettePrimitive.Arc(0.0, 0.0, 0.33, 90.0, 180.0),
                StepSilhouettePrimitive.Arc(0.0, 0.0, 0.49, 180.0, 270.0),
                StepSilhouettePrimitive.Arc(0.0, 0.0, 0.49, 270.0, 360.0),
                StepSilhouettePrimitive.Line(0.13, 0.93, 0.93, 0.13),
                StepSilhouettePrimitive.Line(-0.13, 0.93, -0.93, 0.13),
                StepSilhouettePrimitive.Line(-0.13, -0.93, -0.93, -0.13),
                StepSilhouettePrimitive.Line(0.13, -0.93, 0.93, -0.13),
                StepSilhouettePrimitive.Line(-0.14, 0.0, 0.14, 0.0),
                StepSilhouettePrimitive.Line(0.0, -0.14, 0.0, 0.14)
            };

            List<StepSilhouettePrimitive> optimized = OptimizeOcctPrimitives(primitives);
            AssertEqual(8, CountKind(optimized, StepSilhouettePrimitiveKind.Arc), "circular contact chord lines should become arcs", failures);
            AssertEqual(2, CountKind(optimized, StepSilhouettePrimitiveKind.Line), "contact center detail lines should remain", failures);
            AssertEqual(4, CountArcsNearRadius(optimized, 0.939), "converted contact arcs should keep the chord radius", failures);
        }

        private static void AssertMixedLineArcFixtureIsStable(List<string> failures)
        {
            var primitives = new List<StepSilhouettePrimitive>
            {
                StepSilhouettePrimitive.Arc(2.0, 2.0, 0.5, 0.0, 180.0),
                StepSilhouettePrimitive.Line(0.0, 0.0, 2.5, 0.0),
                StepSilhouettePrimitive.Line(2.5, 0.0, 5.0, 0.0),
                StepSilhouettePrimitive.Line(0.0, 1.0, 1.0, 1.0)
            };

            List<StepSilhouettePrimitive> optimized = OptimizeOcctPrimitives(primitives);
            AssertEqual(3, optimized.Count, "mixed OCCT line/arc fixture should keep stable primitive count", failures);
            AssertEqual(1, CountKind(optimized, StepSilhouettePrimitiveKind.Arc), "mixed fixture should keep its arc", failures);
            AssertEqual(2, CountKind(optimized, StepSilhouettePrimitiveKind.Line), "mixed fixture should keep merged and separate lines", failures);
            AssertContainsLine(optimized, 0.0, 0.0, 5.0, 0.0, "mixed fixture merged line", failures);
            AssertContainsLine(optimized, 0.0, 1.0, 1.0, 1.0, "mixed fixture separate line", failures);
            AssertContainsArc(optimized, 2.0, 2.0, 0.5, 0.0, 180.0, "mixed fixture arc", failures);
        }

        private static void AssertShortTouchingLinesAreMergedBeforeCutoff(List<string> failures)
        {
            var primitives = new List<StepSilhouettePrimitive>
            {
                StepSilhouettePrimitive.Line(0.0, 0.0, 0.02, 0.0),
                StepSilhouettePrimitive.Line(0.02, 0.0, 0.04, 0.0)
            };

            List<StepSilhouettePrimitive> optimized = OptimizeOcctPrimitives(primitives);
            AssertEqual(1, optimized.Count, "short touching OCCT lines should merge before cutoff", failures);
            if (optimized.Count == 1)
                AssertLine(0.0, 0.0, 0.04, 0.0, optimized[0], "merged line should survive cutoff", failures);
        }

        private static void AssertCoveredLineIsRemoved(List<string> failures)
        {
            var primitives = new List<StepSilhouettePrimitive>
            {
                StepSilhouettePrimitive.Line(0.0, 0.005, 9.6, 0.005),
                StepSilhouettePrimitive.Line(0.0, 0.0, 10.0, 0.0)
            };

            List<StepSilhouettePrimitive> cleaned = RemoveFullyOverlappedOcctPrimitives(primitives);
            AssertEqual(1, cleaned.Count, "90% stroke-covered candidate should be removed", failures);
            if (cleaned.Count == 1)
                AssertLineEnd(9.6, cleaned[0].X2, "covering line should remain", failures);
        }

        private static void AssertMostlyVisibleLineIsKept(List<string> failures)
        {
            var primitives = new List<StepSilhouettePrimitive>
            {
                StepSilhouettePrimitive.Line(0.0, 0.005, 9.4, 0.005),
                StepSilhouettePrimitive.Line(0.0, 0.0, 10.0, 0.0)
            };

            List<StepSilhouettePrimitive> cleaned = RemoveFullyOverlappedOcctPrimitives(primitives);
            AssertEqual(1, cleaned.Count, "short covered line should be removed while the below-threshold covered line remains", failures);
            if (cleaned.Count == 1)
                AssertLineEnd(10.0, cleaned[0].X2, "below-threshold covered line should remain", failures);
        }

        private static List<StepSilhouettePrimitive> RemoveFullyOverlappedOcctPrimitives(List<StepSilhouettePrimitive> primitives)
        {
            MethodInfo method = typeof(StepSilhouetteProjection).GetMethod(
                "RemoveFullyOverlappedOcctPrimitives",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
                throw new InvalidOperationException("RemoveFullyOverlappedOcctPrimitives was not found.");

            return (List<StepSilhouettePrimitive>)method.Invoke(null, new object[] { primitives });
        }

        private static List<StepSilhouettePrimitive> OptimizeOcctPrimitives(List<StepSilhouettePrimitive> primitives)
        {
            MethodInfo method = typeof(StepSilhouetteProjection).GetMethod(
                "OptimizeOcctPrimitives",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
                throw new InvalidOperationException("OptimizeOcctPrimitives was not found.");

            return (List<StepSilhouettePrimitive>)method.Invoke(null, new object[] { primitives });
        }

        private static List<StepSilhouettePrimitive> MergeTouchingOcctLinePrimitives(List<StepSilhouettePrimitive> primitives)
        {
            MethodInfo method = typeof(StepSilhouetteProjection).GetMethod(
                "MergeTouchingOcctLinePrimitives",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
                throw new InvalidOperationException("MergeTouchingOcctLinePrimitives was not found.");

            return (List<StepSilhouettePrimitive>)method.Invoke(null, new object[] { primitives });
        }

        private static void AssertEqual(int expected, int actual, string message, List<string> failures)
        {
            if (expected != actual)
                failures.Add(message + ": expected " + expected.ToString(CultureInfo.InvariantCulture) + ", got " + actual.ToString(CultureInfo.InvariantCulture));
        }

        private static int CountKind(List<StepSilhouettePrimitive> primitives, StepSilhouettePrimitiveKind kind)
        {
            int count = 0;
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind == kind)
                    count++;
            }

            return count;
        }

        private static int CountArcsNearRadius(List<StepSilhouettePrimitive> primitives, double radius)
        {
            int count = 0;
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind == StepSilhouettePrimitiveKind.Arc &&
                    Math.Abs(primitive.Radius - radius) <= 0.01)
                {
                    count++;
                }
            }

            return count;
        }

        private static void AssertLineEnd(double expected, double actual, string message, List<string> failures)
        {
            if (Math.Abs(expected - actual) > 0.000001)
                failures.Add(message + ": expected X2 " + expected.ToString(CultureInfo.InvariantCulture) + ", got " + actual.ToString(CultureInfo.InvariantCulture));
        }

        private static void AssertLine(
            double expectedX1,
            double expectedY1,
            double expectedX2,
            double expectedY2,
            StepSilhouettePrimitive primitive,
            string message,
            List<string> failures)
        {
            if (primitive.Kind != StepSilhouettePrimitiveKind.Line ||
                Math.Abs(expectedX1 - primitive.X1) > 0.000001 ||
                Math.Abs(expectedY1 - primitive.Y1) > 0.000001 ||
                Math.Abs(expectedX2 - primitive.X2) > 0.000001 ||
                Math.Abs(expectedY2 - primitive.Y2) > 0.000001)
            {
                failures.Add(message + ": expected line " +
                    expectedX1.ToString(CultureInfo.InvariantCulture) + "," +
                    expectedY1.ToString(CultureInfo.InvariantCulture) + " -> " +
                    expectedX2.ToString(CultureInfo.InvariantCulture) + "," +
                    expectedY2.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void AssertContainsLine(
            List<StepSilhouettePrimitive> primitives,
            double expectedX1,
            double expectedY1,
            double expectedX2,
            double expectedY2,
            string message,
            List<string> failures)
        {
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind == StepSilhouettePrimitiveKind.Line &&
                    Math.Abs(expectedX1 - primitive.X1) <= 0.000001 &&
                    Math.Abs(expectedY1 - primitive.Y1) <= 0.000001 &&
                    Math.Abs(expectedX2 - primitive.X2) <= 0.000001 &&
                    Math.Abs(expectedY2 - primitive.Y2) <= 0.000001)
                {
                    return;
                }
            }

            failures.Add(message + ": expected line " +
                expectedX1.ToString(CultureInfo.InvariantCulture) + "," +
                expectedY1.ToString(CultureInfo.InvariantCulture) + " -> " +
                expectedX2.ToString(CultureInfo.InvariantCulture) + "," +
                expectedY2.ToString(CultureInfo.InvariantCulture));
        }

        private static void AssertContainsArc(
            List<StepSilhouettePrimitive> primitives,
            double expectedCenterX,
            double expectedCenterY,
            double expectedRadius,
            double expectedStartAngle,
            double expectedEndAngle,
            string message,
            List<string> failures)
        {
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind == StepSilhouettePrimitiveKind.Arc &&
                    Math.Abs(expectedCenterX - primitive.CenterX) <= 0.000001 &&
                    Math.Abs(expectedCenterY - primitive.CenterY) <= 0.000001 &&
                    Math.Abs(expectedRadius - primitive.Radius) <= 0.000001 &&
                    Math.Abs(expectedStartAngle - primitive.StartAngle) <= 0.000001 &&
                    Math.Abs(expectedEndAngle - primitive.EndAngle) <= 0.000001)
                {
                    return;
                }
            }

            failures.Add(message + ": expected arc center " +
                expectedCenterX.ToString(CultureInfo.InvariantCulture) + "," +
                expectedCenterY.ToString(CultureInfo.InvariantCulture) +
                " radius " +
                expectedRadius.ToString(CultureInfo.InvariantCulture) +
                " angles " +
                expectedStartAngle.ToString(CultureInfo.InvariantCulture) +
                " -> " +
                expectedEndAngle.ToString(CultureInfo.InvariantCulture));
        }
    }
}
