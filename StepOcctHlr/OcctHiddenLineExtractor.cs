using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Occt;

namespace StepOcctHlr
{
    internal sealed class ProjectionOptions
    {
        public double RotX { get; set; }
        public double RotY { get; set; }
        public double RotZ { get; set; }
        public double Rotation2D { get; set; }
    }

    internal sealed class ProjectionViewRequest
    {
        public string Name { get; set; }
        public ProjectionOptions Options { get; set; }
    }

    internal static class OcctHiddenLineExtractor
    {
        private static readonly Regex ShapeReferenceRegex = new Regex(@"[+-](\d+)\s+0", RegexOptions.Compiled);
        private const double GeometryTolerance = 1e-6;
        private const double CircleRadiusTolerance = 1e-5;
        private const double FullCircleRadiansTolerance = 1e-5;

        public static ProjectionResultDto Extract(string inputPath, ProjectionOptions options)
        {
            if (options == null)
                options = new ProjectionOptions();

            var stopwatch = Stopwatch.StartNew();
            ProjectionResultDto readFailure = TryReadRootShape(inputPath, stopwatch, out TopoDS_Shape shape);
            if (readFailure != null)
                return readFailure;

            return ExtractFromShape(shape, options, stopwatch);
        }

        public static ProjectionResultDto ExtractBatch(
            string inputPath,
            IReadOnlyList<ProjectionViewRequest> views)
        {
            var stopwatch = Stopwatch.StartNew();
            ProjectionResultDto readFailure = TryReadRootShape(inputPath, stopwatch, out TopoDS_Shape shape);
            if (readFailure != null)
                return readFailure;

            var result = new ProjectionResultDto
            {
                Success = true,
                Views = new List<ProjectionViewResultDto>()
            };

            foreach (ProjectionViewRequest view in views)
            {
                ProjectionResultDto viewResult = ExtractFromShape(shape, view.Options, stopwatch);
                result.Views.Add(new ProjectionViewResultDto
                {
                    Name = view.Name,
                    Success = viewResult.Success,
                    Error = viewResult.Error,
                    Primitives = viewResult.Primitives
                });
                if (!viewResult.Success)
                    result.Success = false;
            }

            if (!result.Success)
                result.Error = "One or more requested views failed.";

            return result;
        }

        private static ProjectionResultDto TryReadRootShape(
            string inputPath,
            Stopwatch stopwatch,
            out TopoDS_Shape shape)
        {
            shape = null;
            Trace(stopwatch, "StepReader read begin");
            var stepReader = new StepReader();
            if (!stepReader.ReadFromFile(inputPath))
                return new ProjectionResultDto { Success = false, Error = "StepReader.ReadFromFile failed." };
            Trace(stopwatch, "StepReader read done");
            Trace(stopwatch, "StepReader GetRootShape begin");
            shape = stepReader.GetRootShape();
            Trace(stopwatch, "StepReader GetRootShape done");
            Trace(stopwatch, "Shape null check begin");
            if (shape == null)
                return new ProjectionResultDto { Success = false, Error = "STEP reader returned null shape." };
            Trace(stopwatch, "Shape IsNull begin");
            if (shape.IsNull)
                return new ProjectionResultDto { Success = false, Error = "STEPControl_Reader.OneShape returned null shape." };
            Trace(stopwatch, "Shape IsNull done");

            return null;
        }

        private static ProjectionResultDto ExtractFromShape(
            TopoDS_Shape rootShape,
            ProjectionOptions options,
            Stopwatch stopwatch)
        {
            if (options == null)
                options = new ProjectionOptions();

            TopoDS_Shape shape = rootShape;
            shape = ApplyModelRotation(shape, options);

            var primitives = new List<ProjectionPrimitiveDto>();
            Trace(stopwatch, "High-level HLR ctor begin");
            using (var algo = new HlrBRepAlgo(new[] { shape }))
            {
                Trace(stopwatch, "High-level HLR ctor done");
                algo.SetProjection(new gp_Ax3(new gp_Pnt(0, 0, 0), new gp_Dir(0, 0, 1), new gp_Dir(1, 0, 0)));
                Trace(stopwatch, "High-level HLR Update begin");
                algo.Update();
                Trace(stopwatch, "High-level HLR Update done");

                var compounds = new[]
                {
                    algo.GetResult(HlrEdgeTypes.VisibleSharp, shape),
                    algo.GetResult(HlrEdgeTypes.VisibleSmooth, shape),
                    algo.GetResult(HlrEdgeTypes.VisibleSewn, shape),
                    algo.GetResult(HlrEdgeTypes.VisibleOutline, shape)
                };

                for (int index = 0; index < compounds.Length; index++)
                    AddPrimitivesFromBrep(primitives, compounds[index], options, stopwatch, index);
            }

            primitives = DedupePrimitives(primitives);
            Trace(stopwatch, "BREP edge conversion done: " + primitives.Count);

            return new ProjectionResultDto
            {
                Success = primitives.Count > 0,
                Error = primitives.Count > 0 ? null : "OCCT HLR produced no visible primitives.",
                Primitives = primitives
            };
        }

        private static void AddPrimitivesFromBrep(
            List<ProjectionPrimitiveDto> primitives,
            TopoDS_Shape compound,
            ProjectionOptions options,
            Stopwatch stopwatch,
            int compoundIndex)
        {
            if (compound == null)
                return;

            Trace(stopwatch, "WriteASCII begin " + compoundIndex.ToString(CultureInfo.InvariantCulture));
            byte[] brepBytes = compound.WriteASCII(false);
            Trace(stopwatch, "WriteASCII done " + compoundIndex.ToString(CultureInfo.InvariantCulture) + ": " + brepBytes.Length.ToString(CultureInfo.InvariantCulture));
            AddPrimitivesFromBrepText(primitives, Encoding.ASCII.GetString(brepBytes), options);
        }

        private static TopoDS_Shape ApplyModelRotation(TopoDS_Shape shape, ProjectionOptions options)
        {
            if (IsIdentityModelRotation(options))
                return shape;

            double rx = DegreesToRadians(options.RotX);
            double ry = DegreesToRadians(options.RotY);
            double cx = Math.Cos(rx);
            double sx = Math.Sin(rx);
            double cy = Math.Cos(ry);
            double sy = Math.Sin(ry);

            var transform = new gp_Trsf();
            transform.SetValues(
                cy, sy * sx, sy * cx, 0.0,
                0.0, cx, -sx, 0.0,
                -sy, cy * sx, cy * cx, 0.0);

            var builder = new BRepBuilderAPI_Transform(shape, transform, true);
            return builder.Shape;
        }

        private static bool IsIdentityModelRotation(ProjectionOptions options)
        {
            return IsZeroRotation(options.RotX) && IsZeroRotation(options.RotY);
        }

        private static bool IsZeroRotation(double degrees)
        {
            double normalized = NormalizeDegrees(degrees);
            return Math.Abs(normalized) < 1e-9 || Math.Abs(normalized - 360.0) < 1e-9;
        }

        private static void AddPrimitivesFromBrepText(List<ProjectionPrimitiveDto> primitives, string brepText, ProjectionOptions options)
        {
            if (string.IsNullOrWhiteSpace(brepText))
                return;

            string[] lines = brepText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int tshapesLine = Array.FindIndex(lines, line => line.TrimStart().StartsWith("TShapes ", StringComparison.Ordinal));
            if (tshapesLine < 0)
                return;

            Dictionary<int, Curve2dRecord> curves = ReadCurve2ds(lines, tshapesLine);

            string[] headerParts = SplitFields(lines[tshapesLine]);
            if (headerParts.Length < 2 || !int.TryParse(headerParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tshapeCount))
                return;

            var vertices = new Dictionary<int, Point2d>();
            int lineIndex = tshapesLine + 1;
            for (int shapeIndex = tshapeCount; shapeIndex >= 1 && lineIndex < lines.Length; shapeIndex--)
            {
                string kind = NextNonEmpty(lines, ref lineIndex);
                if (kind == null)
                    break;

                if (kind == "Ve")
                {
                    ReadVertex(lines, ref lineIndex, shapeIndex, vertices, options);
                }
                else if (kind == "Ed")
                {
                    ReadEdge(lines, ref lineIndex, vertices, curves, primitives, options);
                }
                else
                {
                    SkipShape(lines, ref lineIndex);
                }
            }
        }

        private static Dictionary<int, Curve2dRecord> ReadCurve2ds(string[] lines, int tshapesLine)
        {
            var curves = new Dictionary<int, Curve2dRecord>();
            int curve2dsLine = Array.FindIndex(lines, 0, tshapesLine, line => line.TrimStart().StartsWith("Curve2ds ", StringComparison.Ordinal));
            if (curve2dsLine < 0)
                return curves;

            string[] headerParts = SplitFields(lines[curve2dsLine]);
            if (headerParts.Length < 2 || !int.TryParse(headerParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int curveCount))
                return curves;

            int curveIndex = 1;
            for (int lineIndex = curve2dsLine + 1; lineIndex < tshapesLine && curveIndex <= curveCount; lineIndex++)
            {
                string line = lines[lineIndex].Trim();
                if (line.Length == 0)
                    continue;

                if (!TryGetCurve2dType(line, out int type))
                    continue;

                Curve2dRecord record = ParseCurve2dRecord(line, type);
                curves[curveIndex++] = record;
            }

            return curves;
        }

        private static bool TryGetCurve2dType(string line, out int type)
        {
            type = 0;
            string[] fields = SplitFields(line);
            return fields.Length > 0 &&
                int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out type) &&
                type > 0;
        }

        private static Curve2dRecord ParseCurve2dRecord(string line, int type)
        {
            double[] values = ParseDoubles(line);
            if (type == 1 && values.Length >= 5)
            {
                return Curve2dRecord.Line(values[1], values[2], values[3], values[4]);
            }

            if (type == 2 && values.Length >= 8)
            {
                return Curve2dRecord.Conic(
                    values[1],
                    values[2],
                    values[3],
                    values[4],
                    values[5],
                    values[6],
                    values[7],
                    values[7]);
            }

            if (type == 3 && values.Length >= 9)
            {
                return Curve2dRecord.Conic(
                    values[1],
                    values[2],
                    values[3],
                    values[4],
                    values[5],
                    values[6],
                    values[7],
                    values[8]);
            }

            if (type == 7)
                return Curve2dRecord.Fallback;

            return Curve2dRecord.Unsupported;
        }

        private static void ReadVertex(
            string[] lines,
            ref int lineIndex,
            int shapeIndex,
            Dictionary<int, Point2d> vertices,
            ProjectionOptions options)
        {
            if (lineIndex >= lines.Length)
                return;

            lineIndex++; // tolerance
            if (lineIndex >= lines.Length)
                return;

            double[] coords = ParseDoubles(lines[lineIndex++]);
            if (coords.Length >= 2)
            {
                var point = new Point2d(coords[0], -coords[1]);
                point = Rotate2D(point, EffectiveRotation2D(options));
                vertices[shapeIndex] = point;
            }

            SkipShape(lines, ref lineIndex);
        }

        private static void ReadEdge(
            string[] lines,
            ref int lineIndex,
            Dictionary<int, Point2d> vertices,
            Dictionary<int, Curve2dRecord> curves,
            List<ProjectionPrimitiveDto> primitives,
            ProjectionOptions options)
        {
            lineIndex++; // tolerance and flags
            EdgeCurveReference curveReference = EdgeCurveReference.None;
            while (lineIndex < lines.Length && lines[lineIndex].Trim() != "0")
            {
                if (!curveReference.IsValid)
                    curveReference = ParseEdgeCurveReference(lines[lineIndex]);
                lineIndex++;
            }

            if (lineIndex < lines.Length)
                lineIndex++;

            while (lineIndex < lines.Length && string.IsNullOrWhiteSpace(lines[lineIndex]))
                lineIndex++;
            if (lineIndex < lines.Length)
                lineIndex++; // orientation flags

            while (lineIndex < lines.Length && string.IsNullOrWhiteSpace(lines[lineIndex]))
                lineIndex++;
            if (lineIndex >= lines.Length)
                return;

            MatchCollection matches = ShapeReferenceRegex.Matches(lines[lineIndex++]);
            if (matches.Count < 2)
                return;

            if (!int.TryParse(matches[0].Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int firstVertex) ||
                !int.TryParse(matches[1].Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int secondVertex))
                return;

            if (!vertices.TryGetValue(firstVertex, out Point2d a) ||
                !vertices.TryGetValue(secondVertex, out Point2d b))
                return;

            if (curveReference.IsValid &&
                curves.TryGetValue(curveReference.CurveIndex, out Curve2dRecord curve) &&
                AddCurvePrimitive(primitives, curve, curveReference.FirstParameter, curveReference.LastParameter, a, b, options))
            {
                return;
            }

            AddEndpointLine(primitives, a, b);
        }

        private static EdgeCurveReference ParseEdgeCurveReference(string line)
        {
            double[] values = ParseDoubles(line);
            if (values.Length < 6)
                return EdgeCurveReference.None;

            int representationType = (int)Math.Round(values[0]);
            int curveIndex = (int)Math.Round(values[1]);
            if (representationType <= 0 || curveIndex <= 0)
                return EdgeCurveReference.None;

            return new EdgeCurveReference(curveIndex, values[values.Length - 2], values[values.Length - 1]);
        }

        private static bool AddCurvePrimitive(
            List<ProjectionPrimitiveDto> primitives,
            Curve2dRecord curve,
            double firstParameter,
            double lastParameter,
            Point2d firstVertex,
            Point2d secondVertex,
            ProjectionOptions options)
        {
            if (curve.Kind == Curve2dKind.Line)
            {
                Point2d a = TransformBrepPoint(
                    curve.OriginX + curve.XDirection * firstParameter,
                    curve.OriginY + curve.YDirection * firstParameter,
                    options);
                Point2d b = TransformBrepPoint(
                    curve.OriginX + curve.XDirection * lastParameter,
                    curve.OriginY + curve.YDirection * lastParameter,
                    options);

                if (Distance(a, b) < GeometryTolerance)
                    return false;

                primitives.Add(Line(a, b));
                return true;
            }

            if (curve.Kind == Curve2dKind.Conic)
            {
                if (Math.Abs(curve.MajorRadius - curve.MinorRadius) > CircleRadiusTolerance)
                    return false;

                double radius = (Math.Abs(curve.MajorRadius) + Math.Abs(curve.MinorRadius)) / 2.0;
                if (radius < GeometryTolerance)
                    return false;

                Point2d center = TransformBrepPoint(curve.OriginX, curve.OriginY, options);
                Vector2d xAxis = TransformBrepVector(curve.XDirectionX, curve.XDirectionY, options);
                Vector2d yAxis = TransformBrepVector(curve.YDirectionX, curve.YDirectionY, options);
                Point2d startPoint = TransformBrepPoint(
                    curve.OriginX + radius * (Math.Cos(firstParameter) * curve.XDirectionX + Math.Sin(firstParameter) * curve.YDirectionX),
                    curve.OriginY + radius * (Math.Cos(firstParameter) * curve.XDirectionY + Math.Sin(firstParameter) * curve.YDirectionY),
                    options);
                Point2d endPoint = TransformBrepPoint(
                    curve.OriginX + radius * (Math.Cos(lastParameter) * curve.XDirectionX + Math.Sin(lastParameter) * curve.YDirectionX),
                    curve.OriginY + radius * (Math.Cos(lastParameter) * curve.XDirectionY + Math.Sin(lastParameter) * curve.YDirectionY),
                    options);

                double parameterSweep = lastParameter - firstParameter;
                if (Math.Abs(parameterSweep) >= (2.0 * Math.PI) - FullCircleRadiansTolerance ||
                    (Distance(firstVertex, secondVertex) < GeometryTolerance && Distance(startPoint, endPoint) < GeometryTolerance && Math.Abs(parameterSweep) > FullCircleRadiansTolerance))
                {
                    primitives.Add(Arc(center, radius, 0.0, 360.0));
                    return true;
                }

                if (Distance(startPoint, endPoint) < GeometryTolerance)
                    return false;

                double startAngle = AngleDegrees(center, startPoint);
                double endAngle = AngleDegrees(center, endPoint);
                if (Cross(xAxis, yAxis) < 0.0)
                {
                    double temp = startAngle;
                    startAngle = endAngle;
                    endAngle = temp;
                }

                endAngle = EndAngleAfterStart(startAngle, endAngle);
                if (endAngle - startAngle < GeometryTolerance)
                    return false;

                primitives.Add(Arc(center, radius, startAngle, endAngle));
                return true;
            }

            return false;
        }

        private static void AddEndpointLine(List<ProjectionPrimitiveDto> primitives, Point2d a, Point2d b)
        {
            if (Distance(a, b) < GeometryTolerance)
                return;

            primitives.Add(Line(a, b));
        }

        private static ProjectionPrimitiveDto Line(Point2d a, Point2d b)
        {
            return new ProjectionPrimitiveDto
            {
                Kind = "Line",
                X1 = a.X,
                Y1 = a.Y,
                X2 = b.X,
                Y2 = b.Y
            };
        }

        private static ProjectionPrimitiveDto Arc(Point2d center, double radius, double startAngle, double endAngle)
        {
            return new ProjectionPrimitiveDto
            {
                Kind = "Arc",
                CenterX = center.X,
                CenterY = center.Y,
                Radius = radius,
                StartAngle = startAngle,
                EndAngle = endAngle
            };
        }

        private static List<ProjectionPrimitiveDto> DedupePrimitives(List<ProjectionPrimitiveDto> primitives)
        {
            var result = new List<ProjectionPrimitiveDto>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProjectionPrimitiveDto primitive in primitives)
            {
                string key = PrimitiveKey(primitive);
                if (key == null || keys.Add(key))
                    result.Add(primitive);
            }

            return result;
        }

        private static string PrimitiveKey(ProjectionPrimitiveDto primitive)
        {
            if (primitive.Kind == "Line")
            {
                double ax = Math.Round(primitive.X1, 6);
                double ay = Math.Round(primitive.Y1, 6);
                double bx = Math.Round(primitive.X2, 6);
                double by = Math.Round(primitive.Y2, 6);
                string forward = CoordKey(ax, ay) + "|" + CoordKey(bx, by);
                string reverse = CoordKey(bx, by) + "|" + CoordKey(ax, ay);
                return "L|" + (string.CompareOrdinal(forward, reverse) <= 0 ? forward : reverse);
            }

            if (primitive.Kind == "Arc")
            {
                return "A|" +
                    ScalarKey(primitive.CenterX) + "," +
                    ScalarKey(primitive.CenterY) + "," +
                    ScalarKey(primitive.Radius) + "," +
                    ScalarKey(NormalizeDegrees(primitive.StartAngle)) + "," +
                    ScalarKey(NormalizeDegrees(primitive.EndAngle));
            }

            return null;
        }

        private static string CoordKey(double x, double y)
        {
            return ScalarKey(x) + "," + ScalarKey(y);
        }

        private static string ScalarKey(double value)
        {
            return Math.Round(value, 6).ToString("R", CultureInfo.InvariantCulture);
        }

        private static void SkipShape(string[] lines, ref int lineIndex)
        {
            while (lineIndex < lines.Length)
            {
                if (lines[lineIndex++].Contains("*"))
                    break;
            }
        }

        private static string NextNonEmpty(string[] lines, ref int lineIndex)
        {
            while (lineIndex < lines.Length)
            {
                string line = lines[lineIndex++].Trim();
                if (line.Length > 0)
                    return line;
            }

            return null;
        }

        private static string[] SplitFields(string line)
        {
            return line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        private static double[] ParseDoubles(string line)
        {
            string[] fields = SplitFields(line);
            var values = new List<double>(fields.Length);
            foreach (string field in fields)
            {
                if (double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    values.Add(value);
            }

            return values.ToArray();
        }

        private static Point2d TransformBrepPoint(double x, double y, ProjectionOptions options)
        {
            return Rotate2D(new Point2d(x, -y), EffectiveRotation2D(options));
        }

        private static Vector2d TransformBrepVector(double x, double y, ProjectionOptions options)
        {
            Point2d point = Rotate2D(new Point2d(x, -y), EffectiveRotation2D(options));
            return new Vector2d(point.X, point.Y);
        }

        private static Point2d Rotate2D(Point2d point, double degrees)
        {
            if (Math.Abs(degrees) < 0.000000001)
                return point;

            double radians = degrees * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            return new Point2d(
                point.X * cos - point.Y * sin,
                point.X * sin + point.Y * cos);
        }

        private static double EffectiveRotation2D(ProjectionOptions options)
        {
            double baseline = 180.0;
            double normalizedRotX = NormalizeDegrees(options.RotX);
            if (Math.Abs(normalizedRotX) < 1e-9 ||
                Math.Abs(normalizedRotX - 90.0) < 1e-9 ||
                Math.Abs(normalizedRotX - 270.0) < 1e-9)
            {
                baseline = 0.0;
            }

            return NormalizeDegrees(baseline - options.RotZ + options.Rotation2D);
        }

        private static double NormalizeDegrees(double degrees)
        {
            double normalized = degrees % 360.0;
            if (normalized < 0.0)
                normalized += 360.0;
            return normalized;
        }

        private static double EndAngleAfterStart(double startAngle, double endAngle)
        {
            while (endAngle < startAngle)
                endAngle += 360.0;
            return endAngle;
        }

        private static double AngleDegrees(Point2d center, Point2d point)
        {
            return NormalizeDegrees(Math.Atan2(point.Y - center.Y, point.X - center.X) * 180.0 / Math.PI);
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static double Distance(Point2d a, Point2d b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double Cross(Vector2d a, Vector2d b)
        {
            return a.X * b.Y - a.Y * b.X;
        }

        private static void Trace(Stopwatch stopwatch, string message)
        {
            if (Environment.GetEnvironmentVariable("STEP_OCCT_HLR_TRACE") == "1")
                Console.Error.WriteLine(
                    stopwatch == null ? "[?.???s] {0}" : "[{1:n3}s] {0}",
                    message,
                    stopwatch == null ? 0.0 : stopwatch.Elapsed.TotalSeconds);
        }

        private struct Point2d
        {
            public Point2d(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }
        }

        private struct Vector2d
        {
            public Vector2d(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }
        }

        private enum Curve2dKind
        {
            Unsupported,
            Line,
            Conic,
            Fallback
        }

        private struct Curve2dRecord
        {
            public static readonly Curve2dRecord Unsupported = new Curve2dRecord(Curve2dKind.Unsupported);
            public static readonly Curve2dRecord Fallback = new Curve2dRecord(Curve2dKind.Fallback);

            private Curve2dRecord(Curve2dKind kind)
            {
                Kind = kind;
                OriginX = 0.0;
                OriginY = 0.0;
                XDirection = 0.0;
                YDirection = 0.0;
                XDirectionX = 0.0;
                XDirectionY = 0.0;
                YDirectionX = 0.0;
                YDirectionY = 0.0;
                MajorRadius = 0.0;
                MinorRadius = 0.0;
            }

            public Curve2dKind Kind { get; private set; }
            public double OriginX { get; private set; }
            public double OriginY { get; private set; }
            public double XDirection { get; private set; }
            public double YDirection { get; private set; }
            public double XDirectionX { get; private set; }
            public double XDirectionY { get; private set; }
            public double YDirectionX { get; private set; }
            public double YDirectionY { get; private set; }
            public double MajorRadius { get; private set; }
            public double MinorRadius { get; private set; }

            public static Curve2dRecord Line(double originX, double originY, double xDirection, double yDirection)
            {
                return new Curve2dRecord(Curve2dKind.Line)
                {
                    OriginX = originX,
                    OriginY = originY,
                    XDirection = xDirection,
                    YDirection = yDirection
                };
            }

            public static Curve2dRecord Conic(
                double originX,
                double originY,
                double xDirectionX,
                double xDirectionY,
                double yDirectionX,
                double yDirectionY,
                double majorRadius,
                double minorRadius)
            {
                return new Curve2dRecord(Curve2dKind.Conic)
                {
                    OriginX = originX,
                    OriginY = originY,
                    XDirectionX = xDirectionX,
                    XDirectionY = xDirectionY,
                    YDirectionX = yDirectionX,
                    YDirectionY = yDirectionY,
                    MajorRadius = majorRadius,
                    MinorRadius = minorRadius
                };
            }
        }

        private struct EdgeCurveReference
        {
            public static readonly EdgeCurveReference None = new EdgeCurveReference(0, 0.0, 0.0);

            public EdgeCurveReference(int curveIndex, double firstParameter, double lastParameter)
            {
                CurveIndex = curveIndex;
                FirstParameter = firstParameter;
                LastParameter = lastParameter;
            }

            public int CurveIndex { get; }
            public double FirstParameter { get; }
            public double LastParameter { get; }
            public bool IsValid => CurveIndex > 0;
        }
    }
}
