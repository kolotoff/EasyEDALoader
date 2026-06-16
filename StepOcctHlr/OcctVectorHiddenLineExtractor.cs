using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Occt;

namespace StepOcctHlr
{
    internal static class OcctVectorHiddenLineExtractor
    {
        private const string EngineName = "occt-hlr-vector-managed";
        private const double ArcSampleStepDegrees = 3.0;

        public static VectorProjectionResultDto ExtractBatch(
            string inputPath,
            IReadOnlyList<ProjectionViewRequest> views)
        {
            var stopwatch = Stopwatch.StartNew();
            ProjectionResultDto readFailure = OcctHiddenLineExtractor.TryReadRootShape(inputPath, stopwatch, out TopoDS_Shape shape);
            if (readFailure != null)
            {
                return new VectorProjectionResultDto
                {
                    Success = false,
                    Error = readFailure.Error,
                    Engine = EngineName
                };
            }

            var result = new VectorProjectionResultDto
            {
                Success = true,
                Engine = EngineName,
                Views = new List<VectorProjectionViewDto>()
            };

            foreach (ProjectionViewRequest view in views)
            {
                VectorProjectionViewDto viewResult = ExtractFromShape(shape, view.Name, view.Options, stopwatch);
                result.Views.Add(viewResult);
                if (!viewResult.Success)
                    result.Success = false;
            }

            if (!result.Success)
                result.Error = "One or more requested vector views failed.";

            return result;
        }

        public static void WriteSvgFiles(VectorProjectionResultDto result, string outputDirectory)
        {
            if (result == null || result.Views == null || string.IsNullOrWhiteSpace(outputDirectory))
                return;

            Directory.CreateDirectory(outputDirectory);
            foreach (VectorProjectionViewDto view in result.Views)
            {
                if (view == null || string.IsNullOrWhiteSpace(view.Name))
                    continue;

                string outputPath = Path.Combine(outputDirectory, view.Name + ".svg");
                File.WriteAllText(outputPath, CreateSvg(view), Encoding.UTF8);
            }
        }

        private static VectorProjectionViewDto ExtractFromShape(
            TopoDS_Shape rootShape,
            string viewName,
            ProjectionOptions options,
            Stopwatch stopwatch)
        {
            try
            {
                if (options == null)
                    options = new ProjectionOptions();

                TopoDS_Shape shape = OcctHiddenLineExtractor.ApplyModelRotation(rootShape, options);
                var primitives = new List<VectorProjectionPrimitiveDto>();
                int sourceIndex = 0;

                using (var algo = new HlrBRepAlgo(new[] { shape }))
                {
                    algo.SetProjection(new gp_Ax3(new gp_Pnt(0, 0, 0), new gp_Dir(0, 0, 1), new gp_Dir(1, 0, 0)));
                    algo.Update();

                    AddCategoryPrimitives(primitives, algo.GetResult(HlrEdgeTypes.VisibleSharp, shape), options, stopwatch, "sharp", ref sourceIndex, 0);
                    AddCategoryPrimitives(primitives, algo.GetResult(HlrEdgeTypes.VisibleSmooth, shape), options, stopwatch, "smooth", ref sourceIndex, 1);
                    AddCategoryPrimitives(primitives, algo.GetResult(HlrEdgeTypes.VisibleSewn, shape), options, stopwatch, "sewn", ref sourceIndex, 2);
                    AddCategoryPrimitives(primitives, algo.GetResult(HlrEdgeTypes.VisibleOutline, shape), options, stopwatch, "outline", ref sourceIndex, 3);
                }

                VectorProjectionBoundsDto bounds = MeasureBounds(primitives);
                return new VectorProjectionViewDto
                {
                    Name = viewName,
                    Success = primitives.Count > 0,
                    Error = primitives.Count > 0 ? null : "OCCT HLR produced no visible vector primitives.",
                    Bounds = bounds,
                    Primitives = primitives
                };
            }
            catch (Exception ex)
            {
                return new VectorProjectionViewDto
                {
                    Name = viewName,
                    Success = false,
                    Error = ex.ToString(),
                    Bounds = CreateDefaultBounds()
                };
            }
        }

        private static void AddCategoryPrimitives(
            List<VectorProjectionPrimitiveDto> target,
            TopoDS_Shape compound,
            ProjectionOptions options,
            Stopwatch stopwatch,
            string category,
            ref int sourceIndex,
            int compoundIndex)
        {
            List<ProjectionPrimitiveDto> primitives =
                OcctHiddenLineExtractor.ExtractPrimitivesFromCompound(compound, options, stopwatch, compoundIndex);
            foreach (ProjectionPrimitiveDto primitive in primitives)
            {
                VectorProjectionPrimitiveDto vectorPrimitive = ConvertPrimitive(primitive, category, sourceIndex++);
                if (vectorPrimitive != null)
                    target.Add(vectorPrimitive);
            }
        }

        private static VectorProjectionPrimitiveDto ConvertPrimitive(
            ProjectionPrimitiveDto primitive,
            string category,
            int sourceIndex)
        {
            if (primitive == null)
                return null;

            if (string.Equals(primitive.Kind, "Line", StringComparison.OrdinalIgnoreCase))
            {
                return new VectorProjectionPrimitiveDto
                {
                    Kind = "line",
                    Visibility = "visible",
                    Category = category,
                    SourceIndex = sourceIndex,
                    Points = new[] { primitive.X1, primitive.Y1, primitive.X2, primitive.Y2 }
                };
            }

            if (string.Equals(primitive.Kind, "Arc", StringComparison.OrdinalIgnoreCase))
            {
                return new VectorProjectionPrimitiveDto
                {
                    Kind = "arc",
                    Visibility = "visible",
                    Category = category,
                    SourceIndex = sourceIndex,
                    Points = new[] { primitive.CenterX, primitive.CenterY },
                    CenterX = primitive.CenterX,
                    CenterY = primitive.CenterY,
                    Radius = primitive.Radius,
                    StartAngle = primitive.StartAngle,
                    EndAngle = primitive.EndAngle
                };
            }

            if (string.Equals(primitive.Kind, "Polyline", StringComparison.OrdinalIgnoreCase) &&
                primitive.Points != null &&
                primitive.Points.Length >= 4)
            {
                return new VectorProjectionPrimitiveDto
                {
                    Kind = "polyline",
                    Visibility = "visible",
                    Category = category,
                    SourceIndex = sourceIndex,
                    Points = primitive.Points,
                    OriginalKind = primitive.OriginalKind,
                    Tolerance = primitive.Tolerance
                };
            }

            return null;
        }

        private static VectorProjectionBoundsDto MeasureBounds(IReadOnlyList<VectorProjectionPrimitiveDto> primitives)
        {
            var bounds = new MutableBounds();
            foreach (VectorProjectionPrimitiveDto primitive in primitives)
            {
                if (primitive == null)
                    continue;

                if (primitive.Kind == "line" && primitive.Points != null && primitive.Points.Length >= 4)
                {
                    bounds.Include(primitive.Points[0], primitive.Points[1]);
                    bounds.Include(primitive.Points[2], primitive.Points[3]);
                }
                else if (primitive.Kind == "arc")
                {
                    foreach (Point2 point in SampleArc(primitive))
                        bounds.Include(point.X, point.Y);
                }
                else if (primitive.Kind == "polyline" && primitive.Points != null)
                {
                    for (int index = 0; index + 1 < primitive.Points.Length; index += 2)
                        bounds.Include(primitive.Points[index], primitive.Points[index + 1]);
                }
            }

            return bounds.ToDto();
        }

        private static VectorProjectionBoundsDto CreateDefaultBounds()
        {
            return new VectorProjectionBoundsDto
            {
                Left = -5.0,
                Bottom = -5.0,
                Right = 5.0,
                Top = 5.0,
                Width = 10.0,
                Height = 10.0
            };
        }

        private static IEnumerable<Point2> SampleArc(VectorProjectionPrimitiveDto primitive)
        {
            double sweep = primitive.EndAngle - primitive.StartAngle;
            while (sweep < 0.0)
                sweep += 360.0;
            while (sweep > 360.0)
                sweep -= 360.0;
            if (Math.Abs(sweep) < 1e-9 && Math.Abs(primitive.EndAngle - primitive.StartAngle) > 1e-9)
                sweep = 360.0;

            int steps = Math.Max(8, (int)Math.Ceiling(sweep / ArcSampleStepDegrees));
            for (int index = 0; index <= steps; index++)
            {
                double angle = (primitive.StartAngle + sweep * index / steps) * Math.PI / 180.0;
                yield return new Point2(
                    primitive.CenterX + Math.Cos(angle) * primitive.Radius,
                    primitive.CenterY + Math.Sin(angle) * primitive.Radius);
            }
        }

        private static string CreateSvg(VectorProjectionViewDto view)
        {
            VectorProjectionBoundsDto bounds = view.Bounds ?? CreateDefaultBounds();
            if (bounds.Width <= 0.0 || bounds.Height <= 0.0)
                bounds = CreateDefaultBounds();

            double margin = Math.Max(bounds.Width, bounds.Height) * 0.04;
            if (margin <= 0.0)
                margin = 1.0;

            double left = bounds.Left - margin;
            double top = -bounds.Top - margin;
            double width = bounds.Width + margin * 2.0;
            double height = bounds.Height + margin * 2.0;

            var builder = new StringBuilder();
            builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            builder.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"")
                .Append(Invariant(left)).Append(' ')
                .Append(Invariant(top)).Append(' ')
                .Append(Invariant(width)).Append(' ')
                .Append(Invariant(height)).AppendLine("\">");
            builder.AppendLine("  <g fill=\"none\" stroke=\"black\" stroke-width=\"0.08\" stroke-linecap=\"round\" stroke-linejoin=\"round\">");

            foreach (VectorProjectionPrimitiveDto primitive in view.Primitives ?? new List<VectorProjectionPrimitiveDto>())
                AppendSvgPrimitive(builder, primitive);

            builder.AppendLine("  </g>");
            builder.AppendLine("</svg>");
            return builder.ToString();
        }

        private static void AppendSvgPrimitive(StringBuilder builder, VectorProjectionPrimitiveDto primitive)
        {
            if (primitive == null)
                return;

            if (primitive.Kind == "line" && primitive.Points != null && primitive.Points.Length >= 4)
            {
                builder.Append("    <line data-category=\"")
                    .Append(EscapeXml(primitive.Category))
                    .Append("\" x1=\"")
                    .Append(Invariant(primitive.Points[0]))
                    .Append("\" y1=\"")
                    .Append(Invariant(-primitive.Points[1]))
                    .Append("\" x2=\"")
                    .Append(Invariant(primitive.Points[2]))
                    .Append("\" y2=\"")
                    .Append(Invariant(-primitive.Points[3]))
                    .AppendLine("\" />");
                return;
            }

            if (primitive.Kind == "arc")
            {
                Point2 start = ArcPoint(primitive, primitive.StartAngle);
                Point2 end = ArcPoint(primitive, primitive.EndAngle);
                double sweep = NormalizedSweep(primitive);
                int largeArc = sweep > 180.0 ? 1 : 0;
                int sweepFlag = 1;

                builder.Append("    <path data-category=\"")
                    .Append(EscapeXml(primitive.Category))
                    .Append("\" d=\"M ")
                    .Append(Invariant(start.X))
                    .Append(' ')
                    .Append(Invariant(-start.Y));
                if (Math.Abs(sweep - 360.0) < 1e-9)
                {
                    Point2 middle = ArcPoint(primitive, primitive.StartAngle + 180.0);
                    AppendSvgArcSegment(builder, primitive.Radius, 0, sweepFlag, middle);
                    AppendSvgArcSegment(builder, primitive.Radius, 0, sweepFlag, start);
                }
                else
                {
                    AppendSvgArcSegment(builder, primitive.Radius, largeArc, sweepFlag, end);
                }

                builder.AppendLine("\" />");
                return;
            }

            if (primitive.Kind == "polyline" && primitive.Points != null && primitive.Points.Length >= 4)
            {
                builder.Append("    <polyline data-category=\"")
                    .Append(EscapeXml(primitive.Category))
                    .Append("\" data-original-kind=\"")
                    .Append(EscapeXml(primitive.OriginalKind))
                    .Append("\" points=\"");
                for (int index = 0; index + 1 < primitive.Points.Length; index += 2)
                {
                    if (index > 0)
                        builder.Append(' ');
                    builder.Append(Invariant(primitive.Points[index]))
                        .Append(',')
                        .Append(Invariant(-primitive.Points[index + 1]));
                }

                builder.AppendLine("\" />");
            }
        }

        private static void AppendSvgArcSegment(
            StringBuilder builder,
            double radius,
            int largeArc,
            int sweepFlag,
            Point2 end)
        {
            builder.Append(" A ")
                .Append(Invariant(radius))
                .Append(' ')
                .Append(Invariant(radius))
                .Append(" 0 ")
                .Append(largeArc.ToString(CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(sweepFlag.ToString(CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(Invariant(end.X))
                .Append(' ')
                .Append(Invariant(-end.Y));
        }

        private static Point2 ArcPoint(VectorProjectionPrimitiveDto primitive, double angleDegrees)
        {
            double angle = angleDegrees * Math.PI / 180.0;
            return new Point2(
                primitive.CenterX + Math.Cos(angle) * primitive.Radius,
                primitive.CenterY + Math.Sin(angle) * primitive.Radius);
        }

        private static double NormalizedSweep(VectorProjectionPrimitiveDto primitive)
        {
            double sweep = primitive.EndAngle - primitive.StartAngle;
            while (sweep < 0.0)
                sweep += 360.0;
            while (sweep > 360.0)
                sweep -= 360.0;
            if (Math.Abs(sweep) < 1e-9 && Math.Abs(primitive.EndAngle - primitive.StartAngle) > 1e-9)
                sweep = 360.0;
            return sweep;
        }

        private static string Invariant(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string EscapeXml(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("\"", "&quot;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private readonly struct Point2
        {
            public Point2(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }
        }

        private sealed class MutableBounds
        {
            private bool _hasPoint;
            private double _left;
            private double _bottom;
            private double _right;
            private double _top;

            public void Include(double x, double y)
            {
                if (!_hasPoint)
                {
                    _left = x;
                    _right = x;
                    _bottom = y;
                    _top = y;
                    _hasPoint = true;
                    return;
                }

                if (x < _left)
                    _left = x;
                if (x > _right)
                    _right = x;
                if (y < _bottom)
                    _bottom = y;
                if (y > _top)
                    _top = y;
            }

            public VectorProjectionBoundsDto ToDto()
            {
                if (!_hasPoint)
                    return CreateDefaultBounds();

                double width = _right - _left;
                double height = _top - _bottom;
                return new VectorProjectionBoundsDto
                {
                    Left = _left,
                    Bottom = _bottom,
                    Right = _right,
                    Top = _top,
                    Width = width,
                    Height = height
                };
            }
        }
    }
}
