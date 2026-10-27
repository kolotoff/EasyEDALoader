using EasyEDA_Loader;
using System;
using System.Collections.Generic;

internal static class Program
{
    private static void Main()
    {
        var options = new ContourViaOptions();
        Check(!options.IgnoreObstacles, "obstacle checking is enabled by default");
        var bottom = new ContourViaSegment { X1 = 0, Y1 = 0, X2 = 10, Y2 = 0 };
        var points = Generate(bottom, 5, 5, options);
        Check(points.Count == 6 && Near(points[0].X, 0) && Near(points[0].Y, 0.5)
            && Near(points[5].X, 10), "line pitch and clearance include via radius");
        var reverse = new ContourViaSegment { X1 = 10, Y1 = 0, X2 = 0, Y2 = 0 };
        Check(Near(Generate(reverse, 5, 5, options)[0].Y, 0.5), "reversed track offsets inward");
        var left = new ContourViaSegment { X1 = 0, Y1 = 0, X2 = 0, Y2 = 10 };
        Check(Near(Generate(left, 5, 5, options)[0].X, 0.5), "vertical track offsets inward");
        var diagonal = new ContourViaSegment { X1 = 0, Y1 = 0, X2 = 6, Y2 = 8 };
        points = Generate(diagonal, -4, 3, options);
        Check(Near(points[0].X, -0.4) && Near(points[0].Y, 0.3)
            && Near(Distance(points[1], points[0]), 2), "diagonal pitch and normal");
        var arc = new ContourViaSegment { IsArc = true, X1 = 0, Y1 = 0, Radius = 5, StartAngle = 0, EndAngle = 90 };
        points = Generate(arc, 0, 0, options);
        Check(points.Count == 4 && Near(points[0].X, 4.5) && Near(points[0].Y, 0), "arc count and inner radius");
        Check(Near(points[1].X, 4.5 * Math.Cos(0.4)) && Near(points[1].Y, 4.5 * Math.Sin(0.4)), "arc samples follow source contour pitch");
        arc.StartAngle = 350; arc.EndAngle = 10;
        Check(Generate(arc, 0, 0, options).Count == 1, "arc wraps zero degrees");
        arc.StartAngle = 0; arc.EndAngle = 0;
        Check(Generate(arc, 0, 0, options).Count == 16, "equal angles mean full circle");
        var duplicate = ContourViaGeometry.Generate(new[] { bottom, bottom }, 5, 5, options);
        Check(duplicate.Count == 6, "deduplicate coincident contour samples");
        ViaCountLimitIncludesEverySample();
        Check(Generate(new ContourViaSegment(), 0, 0, options).Count == 0, "ignore zero length track");
        ExpectInvalid(new ContourViaOptions { HoleMm = 0.4 });
        ExpectInvalid(new ContourViaOptions { DiameterMm = double.NaN });
        ExpectInvalid(new ContourViaOptions { HoleMm = -1 });
        ExpectInvalid(new ContourViaOptions { ClearanceMm = -1 });
        ExpectInvalid(new ContourViaOptions { PitchMm = 0 });
        ExpectInvalid(new ContourViaOptions { PitchMm = 0.2 });
        ExpectInvalid(new ContourViaOptions { PitchMm = double.PositiveInfinity });
        ExpectThrows(() => Generate(new ContourViaSegment { X2 = 1e7 }, 5, 5, options));
        ExpectThrows(() => Generate(new ContourViaSegment { IsArc = true, Radius = 0.1 }, 0, 0, options));
        ObstacleChecks.Run();
        Console.WriteLine("Contour via checks passed (geometry, limits, obstacle filtering, rules, bypass and preview cleanup).");
    }

    private static List<ContourViaPoint> Generate(ContourViaSegment segment, double cx, double cy, ContourViaOptions options)
        => ContourViaGeometry.Generate(new[] { segment }, cx, cy, options);
    private static void ViaCountLimitIncludesEverySample()
    {
        var options = new ContourViaOptions();
        var atLimit = new ContourViaSegment { X2 = 199998 };
        Check(Generate(atLimit, 0, 1, options).Count == 100000, "allow exactly 100,000 vias");
        ExpectThrows(() => Generate(new ContourViaSegment { X2 = 200000 }, 0, 1, options));
        ExpectThrows(() => ContourViaGeometry.Generate(new[]
        {
            new ContourViaSegment { X2 = 199996 },
            new ContourViaSegment { X1 = 200000, X2 = 200002 }
        }, 0, 1, options));
        Check(ContourViaGeometry.Generate(new[] { atLimit, atLimit }, 0, 1, options).Count == 100000,
            "count unique vias when applying the limit");
    }
    private static double Distance(ContourViaPoint a, ContourViaPoint b) => Math.Sqrt((a.X-b.X)*(a.X-b.X) + (a.Y-b.Y)*(a.Y-b.Y));
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static void ExpectInvalid(ContourViaOptions options) => ExpectThrows(options.Validate);
    private static void ExpectThrows(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception("Expected invalid input to be rejected.");
    }
}
