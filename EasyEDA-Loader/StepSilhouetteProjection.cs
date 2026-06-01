using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyEDA_Loader
{
    internal enum StepSilhouettePrimitiveKind
    {
        Line,
        Arc
    }

    internal sealed class StepSilhouettePrimitive
    {
        public StepSilhouettePrimitiveKind Kind { get; private set; }
        public double X1 { get; private set; }
        public double Y1 { get; private set; }
        public double X2 { get; private set; }
        public double Y2 { get; private set; }
        public double CenterX { get; private set; }
        public double CenterY { get; private set; }
        public double Radius { get; private set; }
        public double StartAngle { get; private set; }
        public double EndAngle { get; private set; }

        public static StepSilhouettePrimitive Line(double x1, double y1, double x2, double y2)
        {
            return new StepSilhouettePrimitive
            {
                Kind = StepSilhouettePrimitiveKind.Line,
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2
            };
        }

        public static StepSilhouettePrimitive Arc(double centerX, double centerY, double radius, double startAngle, double endAngle)
        {
            return new StepSilhouettePrimitive
            {
                Kind = StepSilhouettePrimitiveKind.Arc,
                CenterX = centerX,
                CenterY = centerY,
                Radius = radius,
                StartAngle = startAngle,
                EndAngle = endAngle
            };
        }
    }

    internal sealed class StepSilhouetteBounds
    {
        public double Left { get; set; }
        public double Bottom { get; set; }
        public double Right { get; set; }
        public double Top { get; set; }

        public double CenterX => (Left + Right) / 2.0;
        public double CenterY => (Bottom + Top) / 2.0;
        public double Width => Right - Left;
        public double Height => Top - Bottom;
    }

    internal sealed class StepSilhouettePlacement
    {
        public StepSilhouetteBounds TargetBounds { get; set; }
        public double RotX { get; set; }
        public double RotY { get; set; }
        public double RotZ { get; set; }
        public double Rotation2D { get; set; }
    }

    internal static class StepSilhouetteProjection
    {
        private const double AltiumTopProjectionZBaselineDeg = 180.0;
        private const double AltiumTopProjectionRotationCorrectionDeg = 0.0;

        private static double Hypot(double x, double y)
        {
            return Math.Sqrt(x * x + y * y);
        }
        private const double RotationEpsilonDeg = 1e-6;
        private const double FaceVisibleEpsilon = 1e-6;
        private const double CoplanarFaceDot = 0.9999;
        private const double OcclusionEpsilonMm = 0.01;
        private const double PointEpsilonMm = 1e-5;
        private const int OutputCoordDecimals = 3;
        private const double OutputMinLineLengthMm = 0.03;
        private const double ProjectionLineWidthMm = 0.1;
        private const double OptimizePointGridMm = 0.001;
        private const double CollinearDistanceToleranceMm = 0.01;
        private const double CollinearGapToleranceMm = 0.02;
        private const double ArcRadialToleranceMm = 0.025;
        private const double ArcMinSweepDeg = 3.0;
        private const double ArcMaxSweepDeg = 355.0;
        private const double ArcMergeAngleToleranceDeg = 0.5;
        private const double ArcBboxToleranceMm = 0.05;
        private const double MinVisibleLineLengthFactor = 1.5;
        private const double MinVisibleArcLengthFactor = 1.0;
        private const double MinVisibleArcRadiusFactor = 1.0;
        private const double StrokeCoverageDistanceFactor = 0.5;
        private const double StrokeCoverageMaxLengthFactor = 8.0;
        private const double StrokeCoverageSampleStepFactor = 0.33;
        private const double LineGapBridgeMaxFactor = 2.5;
        private const double ClosedPathEndpointToleranceMm = 0.02;
        private const double ClosedCircleSweepToleranceDeg = 20.0;
        private const double CircularPathRadialToleranceMm = 0.04;
        private const int SegmentedArcMinPointCount = 5;
        private const double ShortDisconnectedLineLengthMm = 0.25;
        private const double PrimitiveConnectionToleranceMm = 0.08;
        private const string NumPattern = @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[Ee][+-]?\d+)?";

        private static readonly Regex NumberRegex = new Regex(NumPattern, RegexOptions.Compiled);
        private static readonly Regex RefRegex = new Regex(@"#(\d+)", RegexOptions.Compiled);
        private static readonly Regex CartesianPointRegex = new Regex(@"#(\d+)\s*=\s*CARTESIAN_POINT\s*\(\s*[^,]*,\s*\((.*?)\)\s*\)\s*;?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DirectionRegex = new Regex(@"#(\d+)\s*=\s*DIRECTION\s*\(\s*[^,]*,\s*\((.*?)\)\s*\)\s*;?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex VertexPointRegex = new Regex(@"#(\d+)\s*=\s*VERTEX_POINT\s*\(\s*[^,]*,\s*#(\d+)\s*\)\s*;?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AxisPlacementRegex = new Regex(@"#(\d+)\s*=\s*AXIS2_PLACEMENT_3D\s*\(\s*[^,]*,\s*#(\d+)\s*,\s*#(\d+)\s*,\s*#(\d+)\s*\)\s*;?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PlaneRegex = new Regex(@"#(\d+)\s*=\s*PLANE\s*\(\s*[^,]*,\s*#(\d+)\s*\)\s*;?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CircleRegex = new Regex(@"#(\d+)\s*=\s*CIRCLE\s*\(\s*[^,]*,\s*#(\d+)\s*,\s*(" + NumPattern + @")\s*\)\s*;?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex EdgeCurveRegex = new Regex(@"#(\d+)\s*=\s*EDGE_CURVE\s*\(\s*(?:'[^']*'|\$|\*)\s*,\s*#(\d+)\s*,\s*#(\d+)\s*,\s*#(\d+)\s*,\s*\.(T|F)\.\s*\)\s*;?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex OrientedEdgeRegex = new Regex(@"#(\d+)\s*=\s*ORIENTED_EDGE\s*\(\s*(?:'[^']*'|\$|\*)\s*,\s*\*\s*,\s*\*\s*,\s*#(\d+)\s*,\s*\.(T|F)\.\s*\)\s*;?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex EdgeLoopRegex = new Regex(@"#(\d+)\s*=\s*EDGE_LOOP\s*\(\s*[^,]*,\s*\((.*?)\)\s*\)\s*;?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex FaceBoundRegex = new Regex(@"#(\d+)\s*=\s*(FACE_OUTER_BOUND|FACE_BOUND)\s*\(\s*[^,]*,\s*#(\d+)\s*,\s*\.(T|F)\.\s*\)\s*;?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AdvancedFaceRegex = new Regex(@"#(\d+)\s*=\s*ADVANCED_FACE\s*\(\s*[^,]*,\s*\((.*?)\)\s*,\s*#(\d+)\s*,\s*\.(T|F)\.\s*\)\s*;?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static IReadOnlyList<StepSilhouettePrimitive> Generate(byte[] stepData, StepSilhouettePlacement placement)
        {
            if (stepData == null || stepData.Length == 0)
                return Array.Empty<StepSilhouettePrimitive>();
            if (placement == null || placement.TargetBounds == null)
                return Array.Empty<StepSilhouettePrimitive>();

            string stepText = Encoding.Latin1.GetString(stepData);
            StepGeometry geometry = ParseStepGeometry(stepText);
            if (geometry.Edges.Count == 0)
                return Array.Empty<StepSilhouettePrimitive>();

            var state = new ModelState
            {
                RotX = placement.RotX,
                RotY = placement.RotY,
                RotZ = placement.RotZ,
                Rotation2D = placement.Rotation2D
            };

            VisibleProjection projection = VisibleEdgeProjection(geometry, state);
            if (projection.Segments.Count == 0 || projection.SourceBounds == null)
                return Array.Empty<StepSilhouettePrimitive>();

            double placementRotation = NormalizeRotationDegrees(
                ProjectionRotationFromModelState(state) + AltiumTopProjectionRotationCorrectionDeg);
            List<Segment2d> placed = PlaceSegmentsWithoutRescale(
                projection.Segments,
                placement.TargetBounds,
                projection.SourceBounds,
                placementRotation);

            return OptimizeSegmentsToPrimitives(placed);
        }

        private static StepGeometry ParseStepGeometry(string text)
        {
            var geometry = new StepGeometry();
            foreach (string rawRecord in SplitStepRecords(text))
            {
                string record = string.Join(" ", rawRecord.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
                Match match = CartesianPointRegex.Match(record);
                if (match.Success)
                {
                    double[] values = ParseTuple(match.Groups[2].Value);
                    if (values.Length == 3)
                        geometry.Points[ParseInt(match.Groups[1].Value)] = new Vec3d(values[0], values[1], values[2]);
                    continue;
                }

                match = DirectionRegex.Match(record);
                if (match.Success)
                {
                    double[] values = ParseTuple(match.Groups[2].Value);
                    if (values.Length == 3)
                        geometry.Directions[ParseInt(match.Groups[1].Value)] = Normalize(new Vec3d(values[0], values[1], values[2]));
                    continue;
                }

                match = VertexPointRegex.Match(record);
                if (match.Success)
                {
                    geometry.Vertices[ParseInt(match.Groups[1].Value)] = ParseInt(match.Groups[2].Value);
                    continue;
                }

                match = AxisPlacementRegex.Match(record);
                if (match.Success)
                {
                    geometry.Placements[ParseInt(match.Groups[1].Value)] = new Placement3d(
                        ParseInt(match.Groups[2].Value),
                        ParseInt(match.Groups[3].Value),
                        ParseInt(match.Groups[4].Value));
                    continue;
                }

                match = PlaneRegex.Match(record);
                if (match.Success)
                {
                    geometry.Planes[ParseInt(match.Groups[1].Value)] = ParseInt(match.Groups[2].Value);
                    continue;
                }

                match = CircleRegex.Match(record);
                if (match.Success)
                {
                    geometry.Circles[ParseInt(match.Groups[1].Value)] = new Circle3d(
                        ParseInt(match.Groups[2].Value),
                        ParseDouble(match.Groups[3].Value));
                    continue;
                }

                match = EdgeCurveRegex.Match(record);
                if (match.Success)
                {
                    geometry.Edges[ParseInt(match.Groups[1].Value)] = new EdgeCurve(
                        ParseInt(match.Groups[2].Value),
                        ParseInt(match.Groups[3].Value),
                        ParseInt(match.Groups[4].Value),
                        string.Equals(match.Groups[5].Value, "T", StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                match = OrientedEdgeRegex.Match(record);
                if (match.Success)
                {
                    geometry.OrientedEdges[ParseInt(match.Groups[1].Value)] = new OrientedEdge(
                        ParseInt(match.Groups[2].Value),
                        string.Equals(match.Groups[3].Value, "T", StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                match = EdgeLoopRegex.Match(record);
                if (match.Success)
                {
                    geometry.EdgeLoops[ParseInt(match.Groups[1].Value)] = ParseRefList(match.Groups[2].Value);
                    continue;
                }

                match = FaceBoundRegex.Match(record);
                if (match.Success)
                {
                    geometry.FaceBounds[ParseInt(match.Groups[1].Value)] = new FaceBound(
                        ParseInt(match.Groups[3].Value),
                        string.Equals(match.Groups[2].Value, "FACE_OUTER_BOUND", StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                match = AdvancedFaceRegex.Match(record);
                if (match.Success)
                {
                    geometry.Faces[ParseInt(match.Groups[1].Value)] = new AdvancedFace(
                        ParseRefList(match.Groups[2].Value),
                        ParseInt(match.Groups[3].Value),
                        string.Equals(match.Groups[4].Value, "T", StringComparison.OrdinalIgnoreCase));
                }
            }

            return geometry;
        }

        private static List<string> SplitStepRecords(string text)
        {
            var records = new List<string>();
            var current = new StringBuilder();
            bool inString = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                current.Append(ch);
                if (ch == '\'')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        current.Append(text[i + 1]);
                        i++;
                    }
                    else
                    {
                        inString = !inString;
                    }
                }
                else if (ch == ';' && !inString)
                {
                    records.Add(current.ToString().Trim());
                    current.Clear();
                }
            }

            return records;
        }

        private static double[] ParseTuple(string text)
        {
            return NumberRegex.Matches(text)
                .Cast<Match>()
                .Select(match => ParseDouble(match.Value))
                .ToArray();
        }

        private static List<int> ParseRefList(string text)
        {
            return RefRegex.Matches(text)
                .Cast<Match>()
                .Select(match => ParseInt(match.Groups[1].Value))
                .ToList();
        }

        private static Vec3d? PointForVertex(StepGeometry geometry, int vertexId)
        {
            if (!geometry.Vertices.TryGetValue(vertexId, out int pointId))
                return null;
            if (!geometry.Points.TryGetValue(pointId, out Vec3d point))
                return null;
            return point;
        }

        private static Vec3d RotateX(Vec3d point, double angleDeg)
        {
            double radians = DegreesToRadians(angleDeg);
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            return new Vec3d(point.X, point.Y * cos - point.Z * sin, point.Y * sin + point.Z * cos);
        }

        private static Vec3d RotateY(Vec3d point, double angleDeg)
        {
            double radians = DegreesToRadians(angleDeg);
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            return new Vec3d(point.X * cos + point.Z * sin, point.Y, -point.X * sin + point.Z * cos);
        }

        private static Point2d RotateViewXY(double u, double v, double angleDeg)
        {
            angleDeg = NormalizeRotationDegrees(angleDeg);
            if (angleDeg == 0.0)
                return new Point2d(u, v);

            double radians = DegreesToRadians(angleDeg);
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            return new Point2d(u * cos - v * sin, u * sin + v * cos);
        }

        private static Vec3d TransformModelPoint(Vec3d point, ModelState state)
        {
            Vec3d rotated = RotateX(point, state != null && state.RotX.HasValue ? state.RotX.Value : 90.0);
            rotated = RotateY(rotated, state != null && state.RotY.HasValue ? state.RotY.Value : 0.0);

            double u = rotated.X;
            double v = -rotated.Y;
            double w = rotated.Z;
            Point2d view = RotateViewXY(u, v, ProjectionRotationFromModelState(state));
            return new Vec3d(view.X, view.Y, w);
        }

        private static Vec3d TransformModelVector(Vec3d vector, ModelState state)
        {
            return TransformModelPoint(vector, state);
        }

        private static List<Vec3d> EdgePolylinePoints(StepGeometry geometry, int edgeId)
        {
            if (!geometry.Edges.TryGetValue(edgeId, out EdgeCurve edge))
                return new List<Vec3d>();

            Vec3d? startPoint = PointForVertex(geometry, edge.StartVertex);
            Vec3d? endPoint = PointForVertex(geometry, edge.EndVertex);
            if (!startPoint.HasValue || !endPoint.HasValue)
                return new List<Vec3d>();

            Vec3d start = startPoint.Value;
            Vec3d end = endPoint.Value;
            if (geometry.Circles.TryGetValue(edge.CurveId, out Circle3d circle) &&
                geometry.Placements.TryGetValue(circle.PlacementId, out Placement3d placement) &&
                geometry.Points.TryGetValue(placement.CenterId, out Vec3d center) &&
                geometry.Directions.TryGetValue(placement.AxisId, out Vec3d axis) &&
                geometry.Directions.TryGetValue(placement.RefId, out Vec3d xDir) &&
                circle.Radius > 0)
            {
                Vec3d yDir = Normalize(Cross(axis, xDir));
                double a1 = CircleAngle(start, center, xDir, yDir);
                double a2 = CircleAngle(end, center, xDir, yDir);
                double delta = PositiveModulo(a2 - a1 + Math.PI, 2.0 * Math.PI) - Math.PI;
                if (Math.Abs(delta) < 1e-9 && Distance(start, end) > 1e-6)
                    delta = 2.0 * Math.PI;

                int steps = Math.Max(2, Math.Min(24, (int)Math.Ceiling(Math.Abs(delta) * circle.Radius / 0.12)));
                var points = new List<Vec3d>(steps + 1);
                for (int index = 0; index <= steps; index++)
                {
                    double theta = a1 + delta * index / steps;
                    double cos = Math.Cos(theta);
                    double sin = Math.Sin(theta);
                    points.Add(new Vec3d(
                        center.X + circle.Radius * (cos * xDir.X + sin * yDir.X),
                        center.Y + circle.Radius * (cos * xDir.Y + sin * yDir.Y),
                        center.Z + circle.Radius * (cos * xDir.Z + sin * yDir.Z)));
                }

                return points;
            }

            return new List<Vec3d> { start, end };
        }

        private static ProjectedSegmentsResult ProjectedSegmentsForEdges(
            StepGeometry geometry,
            ModelState state,
            IEnumerable<int> edgeIds,
            List<FaceInfo> occluderFaces,
            Dictionary<int, HashSet<int>> adjacentFaceIdsByEdge,
            int viewSign)
        {
            var segments = new List<Segment2d>();
            int occludedSegments = 0;
            foreach (int edgeId in edgeIds)
            {
                List<Vec3d> points = EdgePolylinePoints(geometry, edgeId)
                    .Select(point => TransformModelPoint(point, state))
                    .ToList();
                for (int index = 0; index < points.Count - 1; index++)
                {
                    Vec3d p1 = points[index];
                    Vec3d p2 = points[index + 1];
                    if (Hypot(p2.X - p1.X, p2.Y - p1.Y) < 1e-9)
                        continue;

                    HashSet<int> adjacentFaceIds = null;
                    adjacentFaceIdsByEdge?.TryGetValue(edgeId, out adjacentFaceIds);

                    if (occluderFaces != null && SegmentIsOccluded(p1, p2, occluderFaces, adjacentFaceIds, viewSign))
                    {
                        occludedSegments++;
                        continue;
                    }

                    segments.Add(new Segment2d(p1.X, p1.Y, p2.X, p2.Y));
                }
            }

            return new ProjectedSegmentsResult(segments, occludedSegments);
        }

        private static Vec3d? FaceNormal(StepGeometry geometry, AdvancedFace face, ModelState state)
        {
            if (!geometry.Planes.TryGetValue(face.SurfaceId, out int placementId))
                return null;
            if (!geometry.Placements.TryGetValue(placementId, out Placement3d placement))
                return null;
            if (!geometry.Directions.TryGetValue(placement.AxisId, out Vec3d axis))
                return null;

            Vec3d normal = face.SameSense ? axis : Negate(axis);
            return Normalize(TransformModelVector(normal, state));
        }

        private static Vec3d? FacePlanePoint(StepGeometry geometry, AdvancedFace face, ModelState state)
        {
            if (!geometry.Planes.TryGetValue(face.SurfaceId, out int placementId))
                return null;
            if (!geometry.Placements.TryGetValue(placementId, out Placement3d placement))
                return null;
            if (!geometry.Points.TryGetValue(placement.CenterId, out Vec3d point))
                return null;

            return TransformModelPoint(point, state);
        }

        private static List<Vec3d> OrientedLoopPoints(StepGeometry geometry, int loopId, ModelState state)
        {
            var result = new List<Vec3d>();
            if (!geometry.EdgeLoops.TryGetValue(loopId, out List<int> orientedEdgeIds))
                return result;

            foreach (int orientedEdgeId in orientedEdgeIds)
            {
                if (!geometry.OrientedEdges.TryGetValue(orientedEdgeId, out OrientedEdge orientedEdge))
                    continue;

                List<Vec3d> points = EdgePolylinePoints(geometry, orientedEdge.EdgeId);
                if (!orientedEdge.Forward)
                    points.Reverse();

                List<Vec3d> transformed = points
                    .Select(point => TransformModelPoint(point, state))
                    .ToList();
                if (result.Count > 0 && transformed.Count > 0 && Distance(result[result.Count - 1], transformed[0]) <= PointEpsilonMm)
                    transformed.RemoveAt(0);
                result.AddRange(transformed);
            }

            if (result.Count > 2 && Distance(result[0], result[result.Count - 1]) <= PointEpsilonMm)
                result.RemoveAt(result.Count - 1);

            return result;
        }

        private static FacePolygons FacePolygonsFor(StepGeometry geometry, AdvancedFace face, ModelState state)
        {
            var outers = new List<List<Point2d>>();
            var holes = new List<List<Point2d>>();
            foreach (int boundId in face.BoundIds)
            {
                if (!geometry.FaceBounds.TryGetValue(boundId, out FaceBound bound))
                    continue;

                List<Point2d> polygon = OrientedLoopPoints(geometry, bound.LoopId, state)
                    .Select(point => new Point2d(point.X, point.Y))
                    .ToList();
                if (polygon.Count < 3)
                    continue;

                if (bound.IsOuter)
                    outers.Add(polygon);
                else
                    holes.Add(polygon);
            }

            return new FacePolygons(outers, holes);
        }

        private static bool PointNearSegment2d(Point2d point, Point2d start, Point2d end, double epsilon = PointEpsilonMm)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSq = dx * dx + dy * dy;
            if (lengthSq <= epsilon * epsilon)
                return Hypot(point.X - start.X, point.Y - start.Y) <= epsilon;

            double t = Math.Max(0.0, Math.Min(1.0, ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSq));
            double nearestX = start.X + t * dx;
            double nearestY = start.Y + t * dy;
            return Hypot(point.X - nearestX, point.Y - nearestY) <= epsilon;
        }

        private static bool PointInPolygonStrict(Point2d point, List<Point2d> polygon)
        {
            if (polygon.Count < 3)
                return false;

            for (int index = 0; index < polygon.Count; index++)
            {
                Point2d start = polygon[index];
                Point2d end = polygon[(index + 1) % polygon.Count];
                if (PointNearSegment2d(point, start, end))
                    return false;
            }

            bool inside = false;
            Point2d previous = polygon[polygon.Count - 1];
            foreach (Point2d current in polygon)
            {
                if ((current.Y > point.Y) != (previous.Y > point.Y))
                {
                    double intersectionX = (previous.X - current.X) * (point.Y - current.Y) / (previous.Y - current.Y) + current.X;
                    if (point.X < intersectionX)
                        inside = !inside;
                }

                previous = current;
            }

            return inside;
        }

        private static bool FaceContainsPoint(FaceInfo faceInfo, Point2d point)
        {
            if (!faceInfo.Outers.Any(polygon => PointInPolygonStrict(point, polygon)))
                return false;
            return !faceInfo.Holes.Any(polygon => PointInPolygonStrict(point, polygon));
        }

        private static double? FaceHeightAt(FaceInfo faceInfo, double u, double v)
        {
            Vec3d normal = faceInfo.Normal;
            Vec3d planePoint = faceInfo.PlanePoint;
            if (Math.Abs(normal.Z) <= FaceVisibleEpsilon)
                return null;

            return planePoint.Z - (normal.X * (u - planePoint.X) + normal.Y * (v - planePoint.Y)) / normal.Z;
        }

        private static bool SegmentIsOccluded(
            Vec3d p1,
            Vec3d p2,
            List<FaceInfo> faceInfos,
            HashSet<int> adjacentFaceIds,
            int viewSign)
        {
            var midpoint = new Point2d((p1.X + p2.X) / 2.0, (p1.Y + p2.Y) / 2.0);
            double segmentHeight = (p1.Z + p2.Z) / 2.0;

            foreach (FaceInfo faceInfo in faceInfos)
            {
                if (adjacentFaceIds != null && adjacentFaceIds.Contains(faceInfo.FaceId))
                    continue;
                if (!FaceContainsPoint(faceInfo, midpoint))
                    continue;

                double? faceHeight = FaceHeightAt(faceInfo, midpoint.X, midpoint.Y);
                if (!faceHeight.HasValue)
                    continue;

                if (viewSign >= 0)
                {
                    if (faceHeight.Value > segmentHeight + OcclusionEpsilonMm)
                        return true;
                }
                else if (faceHeight.Value < segmentHeight - OcclusionEpsilonMm)
                {
                    return true;
                }
            }

            return false;
        }

        private static VisibleProjection VisibleEdgeProjection(StepGeometry geometry, ModelState state)
        {
            VisibleProjection top = VisibleEdgeProjectionForSide(geometry, state, 1);
            VisibleProjection bottom = VisibleEdgeProjectionForSide(geometry, state, -1);
            if (bottom.VisibleProjectedSegments > top.VisibleProjectedSegments)
                return bottom;
            if (bottom.VisibleProjectedSegments == top.VisibleProjectedSegments && bottom.VisibleEdges > top.VisibleEdges)
                return bottom;
            return top;
        }

        private static VisibleProjection VisibleEdgeProjectionForSide(StepGeometry geometry, ModelState state, int viewSign)
        {
            List<int> allEdgeIds = geometry.Edges.Keys.OrderBy(id => id).ToList();
            ProjectedSegmentsResult allProjection = ProjectedSegmentsForEdges(geometry, state, allEdgeIds, null, null, viewSign);
            if (allProjection.Segments.Count == 0)
                return new VisibleProjection(new List<Segment2d>(), null, 0, 0);

            StepSilhouetteBounds sourceBounds = BoundsForSegments(allProjection.Segments);
            var edgeFaceIds = new Dictionary<int, HashSet<int>>();
            var visibleEdgeFaceIds = new Dictionary<int, HashSet<int>>();
            var faceInfos = new Dictionary<int, FaceInfo>();

            foreach (KeyValuePair<int, AdvancedFace> item in geometry.Faces)
            {
                int faceId = item.Key;
                AdvancedFace face = item.Value;
                Vec3d? normal = FaceNormal(geometry, face, state);
                Vec3d? planePoint = FacePlanePoint(geometry, face, state);
                if (!normal.HasValue || !planePoint.HasValue)
                    continue;

                var faceEdgeIds = new HashSet<int>();
                foreach (int boundId in face.BoundIds)
                {
                    if (!geometry.FaceBounds.TryGetValue(boundId, out FaceBound bound))
                        continue;
                    if (!geometry.EdgeLoops.TryGetValue(bound.LoopId, out List<int> orientedEdgeIds))
                        continue;

                    foreach (int orientedEdgeId in orientedEdgeIds)
                    {
                        if (!geometry.OrientedEdges.TryGetValue(orientedEdgeId, out OrientedEdge orientedEdge))
                            continue;
                        faceEdgeIds.Add(orientedEdge.EdgeId);
                        if (!edgeFaceIds.TryGetValue(orientedEdge.EdgeId, out HashSet<int> facesForEdge))
                        {
                            facesForEdge = new HashSet<int>();
                            edgeFaceIds[orientedEdge.EdgeId] = facesForEdge;
                        }
                        facesForEdge.Add(faceId);
                    }
                }

                if (viewSign * normal.Value.Z <= FaceVisibleEpsilon)
                    continue;

                FacePolygons polygons = FacePolygonsFor(geometry, face, state);
                faceInfos[faceId] = new FaceInfo(faceId, normal.Value, planePoint.Value, polygons.Outers, polygons.Holes);
                foreach (int edgeId in faceEdgeIds)
                {
                    if (!visibleEdgeFaceIds.TryGetValue(edgeId, out HashSet<int> facesForEdge))
                    {
                        facesForEdge = new HashSet<int>();
                        visibleEdgeFaceIds[edgeId] = facesForEdge;
                    }
                    facesForEdge.Add(faceId);
                }
            }

            var visibleEdgeIds = new List<int>();
            foreach (KeyValuePair<int, HashSet<int>> item in visibleEdgeFaceIds)
            {
                int edgeId = item.Key;
                HashSet<int> adjacentVisibleFaces = item.Value;
                bool removeCoplanar = false;
                if (adjacentVisibleFaces.Count >= 2 &&
                    edgeFaceIds.TryGetValue(edgeId, out HashSet<int> allAdjacentFaces) &&
                    allAdjacentFaces.IsSubsetOf(adjacentVisibleFaces))
                {
                    List<Vec3d> normals = adjacentVisibleFaces
                        .Where(faceInfos.ContainsKey)
                        .Select(faceId => faceInfos[faceId].Normal)
                        .ToList();
                    if (normals.Count >= 2)
                    {
                        Vec3d first = normals[0];
                        removeCoplanar = normals.Skip(1).All(normal => Math.Abs(Dot(first, normal)) >= CoplanarFaceDot);
                    }
                }

                if (!removeCoplanar)
                    visibleEdgeIds.Add(edgeId);
            }

            ProjectedSegmentsResult visibleProjection = ProjectedSegmentsForEdges(
                geometry,
                state,
                visibleEdgeIds.OrderBy(id => id),
                faceInfos.Values.ToList(),
                visibleEdgeFaceIds,
                viewSign);

            List<Segment2d> visibleSegments = visibleProjection.Segments.Count > 0
                ? visibleProjection.Segments
                : allProjection.Segments;

            return new VisibleProjection(visibleSegments, sourceBounds, visibleSegments.Count, visibleEdgeIds.Count);
        }

        private static List<Segment2d> PlaceSegmentsWithoutRescale(
            List<Segment2d> segments,
            StepSilhouetteBounds targetBounds,
            StepSilhouetteBounds sourceBounds,
            double rotationDeg)
        {
            double sourceCenterX = sourceBounds.CenterX;
            double sourceCenterY = sourceBounds.CenterY;
            double targetCenterX = targetBounds.CenterX;
            double targetCenterY = targetBounds.CenterY;
            rotationDeg = NormalizeRotationDegrees(rotationDeg);
            double radians = DegreesToRadians(rotationDeg);
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            var placed = new List<Segment2d>();
            var seen = new HashSet<SegmentKey>();
            foreach (Segment2d segment in segments)
            {
                Point2d a = MapPlacedPoint(segment.X1, segment.Y1, sourceCenterX, sourceCenterY, targetCenterX, targetCenterY, cos, sin);
                Point2d b = MapPlacedPoint(segment.X2, segment.Y2, sourceCenterX, sourceCenterY, targetCenterX, targetCenterY, cos, sin);
                if (Distance(a, b) < OutputMinLineLengthMm)
                    continue;

                var rounded = new Segment2d(RoundCoord(a.X), RoundCoord(a.Y), RoundCoord(b.X), RoundCoord(b.Y));
                SegmentKey key = CanonicalSegmentKey(rounded);
                if (!seen.Add(key))
                    continue;
                placed.Add(rounded);
            }

            return placed;
        }

        private static Point2d MapPlacedPoint(
            double x,
            double y,
            double sourceCenterX,
            double sourceCenterY,
            double targetCenterX,
            double targetCenterY,
            double cos,
            double sin)
        {
            double relX = x - sourceCenterX;
            double relY = y - sourceCenterY;
            return new Point2d(
                targetCenterX + relX * cos - relY * sin,
                targetCenterY + relX * sin + relY * cos);
        }

        private static IReadOnlyList<StepSilhouettePrimitive> OptimizeSegmentsToPrimitives(List<Segment2d> segments)
        {
            List<Segment2d> deduped = DedupeSegments(segments);
            List<List<Point2d>> paths = TraceSegmentPaths(deduped);
            var primitives = new List<StepSilhouettePrimitive>();
            foreach (List<Point2d> path in paths)
                primitives.AddRange(PrimitivesFromPath(path));

            primitives = MergeLinePrimitives(primitives);
            primitives = MergeArcPrimitives(primitives);
            primitives = RemoveSmallVisiblePrimitives(primitives);
            primitives = RemoveStrokeCoveredPrimitives(primitives);
            primitives = AddLineGapBridges(primitives);
            primitives = RemoveShortDisconnectedLines(primitives);

            var result = new List<StepSilhouettePrimitive>();
            var lineKeys = new HashSet<SegmentKey>();
            var arcKeys = new HashSet<string>();
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                {
                    SegmentKey key = CanonicalSegmentKey(new Segment2d(primitive.X1, primitive.Y1, primitive.X2, primitive.Y2));
                    if (!lineKeys.Add(key))
                        continue;
                }
                else
                {
                    string key = string.Join("|",
                        (int)Math.Round(primitive.CenterX * 1000.0),
                        (int)Math.Round(primitive.CenterY * 1000.0),
                        (int)Math.Round(primitive.Radius * 1000.0),
                        (int)Math.Round(primitive.StartAngle * 1000.0),
                        (int)Math.Round(primitive.EndAngle * 1000.0));
                    if (!arcKeys.Add(key))
                        continue;
                }

                result.Add(primitive);
            }

            return result;
        }

        private static List<Segment2d> DedupeSegments(List<Segment2d> segments)
        {
            var result = new List<Segment2d>();
            var seen = new HashSet<SegmentKey>();
            foreach (Segment2d segment in segments)
            {
                if (SegmentLength(segment) < OutputMinLineLengthMm)
                    continue;
                SegmentKey key = CanonicalSegmentKey(segment);
                if (!seen.Add(key))
                    continue;
                result.Add(segment);
            }

            return result;
        }

        private static List<List<Point2d>> TraceSegmentPaths(List<Segment2d> segments)
        {
            var edgeKeys = new List<Tuple<PointKey, PointKey>>();
            var graph = new Dictionary<PointKey, List<int>>();
            foreach (Segment2d segment in segments)
            {
                PointKey a = PointKeyFromPoint(new Point2d(segment.X1, segment.Y1));
                PointKey b = PointKeyFromPoint(new Point2d(segment.X2, segment.Y2));
                if (a.Equals(b))
                    continue;

                int edgeIndex = edgeKeys.Count;
                edgeKeys.Add(Tuple.Create(a, b));
                AddGraphEdge(graph, a, edgeIndex);
                AddGraphEdge(graph, b, edgeIndex);
            }

            var unvisited = new HashSet<int>(Enumerable.Range(0, edgeKeys.Count));
            var paths = new List<List<Point2d>>();

            List<Point2d> Follow(int startEdge, PointKey startNode)
            {
                var nodes = new List<PointKey> { startNode };
                PointKey currentNode = startNode;
                int edgeIndex = startEdge;
                while (unvisited.Contains(edgeIndex))
                {
                    unvisited.Remove(edgeIndex);
                    Tuple<PointKey, PointKey> edge = edgeKeys[edgeIndex];
                    PointKey nextNode = currentNode.Equals(edge.Item1) ? edge.Item2 : edge.Item1;
                    nodes.Add(nextNode);

                    List<int> candidates = graph.TryGetValue(nextNode, out List<int> graphEdges)
                        ? graphEdges.Where(candidate => unvisited.Contains(candidate)).ToList()
                        : new List<int>();
                    if (candidates.Count != 1 || !graph.TryGetValue(nextNode, out graphEdges) || graphEdges.Count != 2)
                        break;

                    currentNode = nextNode;
                    edgeIndex = candidates[0];
                }

                return nodes.Select(PointFromKey).Select(RoundedPoint).ToList();
            }

            for (int edgeIndex = 0; edgeIndex < edgeKeys.Count; edgeIndex++)
            {
                if (!unvisited.Contains(edgeIndex))
                    continue;
                PointKey a = edgeKeys[edgeIndex].Item1;
                PointKey b = edgeKeys[edgeIndex].Item2;
                int degreeA = graph.TryGetValue(a, out List<int> edgesA) ? edgesA.Count : 0;
                int degreeB = graph.TryGetValue(b, out List<int> edgesB) ? edgesB.Count : 0;
                if (degreeA != 2)
                    paths.Add(Follow(edgeIndex, a));
                else if (degreeB != 2)
                    paths.Add(Follow(edgeIndex, b));
            }

            while (unvisited.Count > 0)
            {
                int edgeIndex = unvisited.First();
                paths.Add(Follow(edgeIndex, edgeKeys[edgeIndex].Item1));
            }

            return paths.Where(path => path.Count >= 2).ToList();
        }

        private static List<StepSilhouettePrimitive> PrimitivesFromPath(List<Point2d> path)
        {
            if (path.Count < 2)
                return new List<StepSilhouettePrimitive>();

            if (TryFitClosedCircularPath(path, out List<StepSilhouettePrimitive> circularArcs))
                return circularArcs;

            if (PointsAreCollinear(path))
            {
                StepSilhouettePrimitive line = LinePrimitive(path[0], path[path.Count - 1]);
                return line != null ? new List<StepSilhouettePrimitive> { line } : new List<StepSilhouettePrimitive>();
            }

            StepSilhouettePrimitive arc = FitArcToPoints(path);
            if (arc != null)
                return new List<StepSilhouettePrimitive> { arc };

            List<StepSilhouettePrimitive> simplifiedLines = SimplifiedLinePrimitives(path);
            List<StepSilhouettePrimitive> segmented = SegmentedPrimitivesFromPath(path);
            int maxSegmentedPrimitiveCount = Math.Max(
                simplifiedLines.Count + 4,
                (int)Math.Ceiling(simplifiedLines.Count * 1.5));
            if (segmented.Any(primitive => primitive.Kind == StepSilhouettePrimitiveKind.Arc) &&
                segmented.Count <= maxSegmentedPrimitiveCount)
                return segmented;

            return simplifiedLines;
        }

        private static List<StepSilhouettePrimitive> SimplifiedLinePrimitives(List<Point2d> path)
        {
            List<Point2d> simplified = RdpSimplify(path, CollinearDistanceToleranceMm);
            var result = new List<StepSilhouettePrimitive>();
            for (int index = 0; index < simplified.Count - 1; index++)
            {
                StepSilhouettePrimitive line = LinePrimitive(simplified[index], simplified[index + 1]);
                if (line != null)
                    result.Add(line);
            }

            return result;
        }

        private static List<StepSilhouettePrimitive> RemoveShortDisconnectedLines(List<StepSilhouettePrimitive> primitives)
        {
            if (primitives.Count == 0)
                return primitives;

            var result = new List<StepSilhouettePrimitive>(primitives.Count);
            for (int index = 0; index < primitives.Count; index++)
            {
                StepSilhouettePrimitive primitive = primitives[index];
                if (primitive.Kind != StepSilhouettePrimitiveKind.Line ||
                    PrimitiveLength(primitive) >= ShortDisconnectedLineLengthMm)
                {
                    result.Add(primitive);
                    continue;
                }

                var start = new Point2d(primitive.X1, primitive.Y1);
                var end = new Point2d(primitive.X2, primitive.Y2);
                bool startConnected = PrimitiveEndpointTouchesAny(primitives, index, start);
                bool endConnected = PrimitiveEndpointTouchesAny(primitives, index, end);
                if (startConnected && endConnected)
                    result.Add(primitive);
            }

            return result;
        }

        private static bool TryFitClosedCircularPath(List<Point2d> path, out List<StepSilhouettePrimitive> circularArcs)
        {
            circularArcs = null;
            if (path.Count < SegmentedArcMinPointCount + 1)
                return false;
            if (Distance(path[0], path[path.Count - 1]) > ClosedPathEndpointToleranceMm)
                return false;

            List<Point2d> points = CompactPathPoints(path, dropClosingPoint: true);
            if (points.Count < SegmentedArcMinPointCount)
                return false;

            Circle2d? circle = CircleFromClosedPathPoints(points);
            if (!circle.HasValue)
                return false;

            Circle2d value = circle.Value;
            double maxRadialError = points.Max(point => Math.Abs(Distance(point, new Point2d(value.CenterX, value.CenterY)) - value.Radius));
            if (maxRadialError > CircularPathRadialToleranceMm)
                return false;

            List<double> angles = points
                .Select(point => Math.Atan2(point.Y - value.CenterY, point.X - value.CenterX))
                .ToList();
            var deltas = new List<double>();
            for (int index = 0; index < angles.Count; index++)
                deltas.Add(SignedAngleDelta(angles[index], angles[(index + 1) % angles.Count]));

            List<double> nonzeroDeltas = deltas.Where(delta => Math.Abs(RadiansToDegrees(delta)) > 0.05).ToList();
            if (nonzeroDeltas.Count == 0)
                return false;
            bool hasPositive = nonzeroDeltas.Any(delta => delta > 0);
            bool hasNegative = nonzeroDeltas.Any(delta => delta < 0);
            if (hasPositive && hasNegative)
                return false;

            double sweepDeg = Math.Abs(RadiansToDegrees(nonzeroDeltas.Sum()));
            if (Math.Abs(sweepDeg - 360.0) > ClosedCircleSweepToleranceDeg)
                return false;

            double startAngle = NormalizeRotationDegrees(RadiansToDegrees(angles[0]));
            circularArcs = new List<StepSilhouettePrimitive>
            {
                StepSilhouettePrimitive.Arc(
                    RoundCoord(value.CenterX),
                    RoundCoord(value.CenterY),
                    RoundCoord(value.Radius),
                    RoundCoord(startAngle),
                    RoundCoord(startAngle + 180.0)),
                StepSilhouettePrimitive.Arc(
                    RoundCoord(value.CenterX),
                    RoundCoord(value.CenterY),
                    RoundCoord(value.Radius),
                    RoundCoord(startAngle + 180.0),
                    RoundCoord(startAngle + 360.0))
            };
            return true;
        }

        private static List<StepSilhouettePrimitive> SegmentedPrimitivesFromPath(List<Point2d> path)
        {
            var result = new List<StepSilhouettePrimitive>();
            int index = 0;
            int lastIndex = path.Count - 1;
            while (index < lastIndex)
            {
                int arcEnd;
                StepSilhouettePrimitive arc = LongestArcFromPath(path, index, lastIndex, out arcEnd);
                if (arc != null)
                {
                    result.Add(arc);
                    index = arcEnd;
                    continue;
                }

                int lineEnd = LongestCollinearEnd(path, index, lastIndex);
                StepSilhouettePrimitive line = LinePrimitive(path[index], path[lineEnd]);
                if (line != null)
                    result.Add(line);
                index = Math.Max(index + 1, lineEnd);
            }

            return result;
        }

        private static StepSilhouettePrimitive LongestArcFromPath(List<Point2d> path, int startIndex, int lastIndex, out int arcEnd)
        {
            StepSilhouettePrimitive bestArc = null;
            arcEnd = -1;
            int failedAfterBest = 0;
            for (int endIndex = startIndex + SegmentedArcMinPointCount - 1; endIndex <= lastIndex; endIndex++)
            {
                List<Point2d> candidate = path.GetRange(startIndex, endIndex - startIndex + 1);
                StepSilhouettePrimitive arc = FitArcToPoints(candidate);
                if (arc != null)
                {
                    bestArc = arc;
                    arcEnd = endIndex;
                    failedAfterBest = 0;
                    continue;
                }

                if (bestArc != null && ++failedAfterBest >= 3)
                    break;
            }

            return bestArc;
        }

        private static int LongestCollinearEnd(List<Point2d> path, int startIndex, int lastIndex)
        {
            int lineEnd = Math.Min(startIndex + 1, lastIndex);
            for (int endIndex = startIndex + 2; endIndex <= lastIndex; endIndex++)
            {
                List<Point2d> candidate = path.GetRange(startIndex, endIndex - startIndex + 1);
                if (!PointsAreCollinear(candidate))
                    break;
                lineEnd = endIndex;
            }

            return lineEnd;
        }

        private static List<Point2d> CompactPathPoints(List<Point2d> path, bool dropClosingPoint)
        {
            int limit = path.Count;
            if (dropClosingPoint && path.Count > 1)
                limit--;

            var result = new List<Point2d>();
            for (int index = 0; index < limit; index++)
            {
                Point2d point = path[index];
                if (result.Count == 0 || Distance(result[result.Count - 1], point) > PointEpsilonMm)
                    result.Add(point);
            }

            if (result.Count > 1 && Distance(result[0], result[result.Count - 1]) <= ClosedPathEndpointToleranceMm)
                result.RemoveAt(result.Count - 1);

            return result;
        }

        private static Circle2d? CircleFromClosedPathPoints(List<Point2d> points)
        {
            int count = points.Count;
            for (int offset = 0; offset < count; offset++)
            {
                Point2d a = points[offset % count];
                Point2d b = points[(offset + count / 3) % count];
                Point2d c = points[(offset + 2 * count / 3) % count];
                Circle2d? circle = CircleFromPoints(a, b, c);
                if (circle.HasValue)
                    return circle;
            }

            return null;
        }

        private static bool PrimitiveEndpointTouchesAny(List<StepSilhouettePrimitive> primitives, int skipIndex, Point2d point)
        {
            for (int index = 0; index < primitives.Count; index++)
            {
                if (index == skipIndex)
                    continue;
                if (PrimitiveEndpointTouchesPoint(primitives[index], point))
                    return true;
            }

            return false;
        }

        private static bool PrimitiveEndpointTouchesPoint(StepSilhouettePrimitive primitive, Point2d point)
        {
            if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
            {
                return Distance(point, new Point2d(primitive.X1, primitive.Y1)) <= PrimitiveConnectionToleranceMm ||
                    Distance(point, new Point2d(primitive.X2, primitive.Y2)) <= PrimitiveConnectionToleranceMm;
            }

            return Distance(point, ArcEndpoint(primitive, primitive.StartAngle)) <= PrimitiveConnectionToleranceMm ||
                Distance(point, ArcEndpoint(primitive, primitive.EndAngle)) <= PrimitiveConnectionToleranceMm;
        }

        private static StepSilhouettePrimitive LinePrimitive(Point2d start, Point2d end)
        {
            if (Distance(start, end) < OutputMinLineLengthMm)
                return null;
            return StepSilhouettePrimitive.Line(
                RoundCoord(start.X),
                RoundCoord(start.Y),
                RoundCoord(end.X),
                RoundCoord(end.Y));
        }

        private static List<StepSilhouettePrimitive> MergeLinePrimitives(List<StepSilhouettePrimitive> primitives)
        {
            var groups = new Dictionary<string, List<LineInterval>>();
            var passthrough = new List<StepSilhouettePrimitive>();
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind != StepSilhouettePrimitiveKind.Line)
                {
                    passthrough.Add(primitive);
                    continue;
                }

                double dx = primitive.X2 - primitive.X1;
                double dy = primitive.Y2 - primitive.Y1;
                double length = Hypot(dx, dy);
                if (length < OutputMinLineLengthMm)
                    continue;

                double ux = dx / length;
                double uy = dy / length;
                if (ux < -1e-12 || (Math.Abs(ux) <= 1e-12 && uy < 0))
                {
                    ux = -ux;
                    uy = -uy;
                }

                double normalX = -uy;
                double normalY = ux;
                double offset = normalX * primitive.X1 + normalY * primitive.Y1;
                double angle = Math.Atan2(uy, ux);
                string key = ((int)Math.Round(angle / 0.001)).ToString(CultureInfo.InvariantCulture)
                    + "|"
                    + ((int)Math.Round(offset / CollinearDistanceToleranceMm)).ToString(CultureInfo.InvariantCulture);
                double t1 = ux * primitive.X1 + uy * primitive.Y1;
                double t2 = ux * primitive.X2 + uy * primitive.Y2;
                if (!groups.TryGetValue(key, out List<LineInterval> intervals))
                {
                    intervals = new List<LineInterval>();
                    groups[key] = intervals;
                }
                intervals.Add(new LineInterval(Math.Min(t1, t2), Math.Max(t1, t2), ux, uy, normalX, normalY, offset));
            }

            var merged = new List<StepSilhouettePrimitive>();
            foreach (List<LineInterval> intervals in groups.Values)
            {
                intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
                LineInterval current = intervals[0];
                for (int index = 1; index < intervals.Count; index++)
                {
                    LineInterval next = intervals[index];
                    if (next.Start <= current.End + CollinearGapToleranceMm)
                    {
                        current.End = Math.Max(current.End, next.End);
                        continue;
                    }

                    AddMergedLine(merged, current);
                    current = next;
                }

                AddMergedLine(merged, current);
            }

            passthrough.AddRange(merged);
            return passthrough;
        }

        private static void AddMergedLine(List<StepSilhouettePrimitive> merged, LineInterval interval)
        {
            StepSilhouettePrimitive primitive = LinePrimitive(
                new Point2d(interval.Ux * interval.Start + interval.NormalX * interval.Offset, interval.Uy * interval.Start + interval.NormalY * interval.Offset),
                new Point2d(interval.Ux * interval.End + interval.NormalX * interval.Offset, interval.Uy * interval.End + interval.NormalY * interval.Offset));
            if (primitive != null)
                merged.Add(primitive);
        }

        private static List<StepSilhouettePrimitive> MergeArcPrimitives(List<StepSilhouettePrimitive> primitives)
        {
            var groups = new Dictionary<string, List<ArcInterval>>();
            var values = new Dictionary<string, Tuple<double, double, double>>();
            var passthrough = new List<StepSilhouettePrimitive>();
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind != StepSilhouettePrimitiveKind.Arc)
                {
                    passthrough.Add(primitive);
                    continue;
                }

                double start = primitive.StartAngle;
                double end = primitive.EndAngle;
                if (end < start)
                    end += 360.0;
                if (end - start < ArcMinSweepDeg)
                    continue;

                string key = string.Join("|",
                    (int)Math.Round(primitive.CenterX / CollinearDistanceToleranceMm),
                    (int)Math.Round(primitive.CenterY / CollinearDistanceToleranceMm),
                    (int)Math.Round(primitive.Radius / CollinearDistanceToleranceMm));
                if (!groups.TryGetValue(key, out List<ArcInterval> intervals))
                {
                    intervals = new List<ArcInterval>();
                    groups[key] = intervals;
                    values[key] = Tuple.Create(primitive.CenterX, primitive.CenterY, primitive.Radius);
                }
                intervals.Add(new ArcInterval(start, end));
            }

            var merged = new List<StepSilhouettePrimitive>();
            foreach (KeyValuePair<string, List<ArcInterval>> item in groups)
            {
                List<ArcInterval> intervals = item.Value;
                intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
                Tuple<double, double, double> value = values[item.Key];
                ArcInterval current = intervals[0];
                for (int index = 1; index < intervals.Count; index++)
                {
                    ArcInterval next = intervals[index];
                    if (next.Start <= current.End + ArcMergeAngleToleranceDeg &&
                        next.End - current.Start <= ArcMaxSweepDeg)
                    {
                        current.End = Math.Max(current.End, next.End);
                        continue;
                    }

                    merged.Add(StepSilhouettePrimitive.Arc(RoundCoord(value.Item1), RoundCoord(value.Item2), RoundCoord(value.Item3), RoundCoord(current.Start), RoundCoord(current.End)));
                    current = next;
                }

                merged.Add(StepSilhouettePrimitive.Arc(RoundCoord(value.Item1), RoundCoord(value.Item2), RoundCoord(value.Item3), RoundCoord(current.Start), RoundCoord(current.End)));
            }

            passthrough.AddRange(merged);
            return passthrough;
        }

        private static List<StepSilhouettePrimitive> RemoveSmallVisiblePrimitives(List<StepSilhouettePrimitive> primitives)
        {
            double minLineLength = ProjectionLineWidthMm * MinVisibleLineLengthFactor;
            double minArcLength = ProjectionLineWidthMm * MinVisibleArcLengthFactor;
            double minArcRadius = ProjectionLineWidthMm * MinVisibleArcRadiusFactor;
            return primitives.Where(primitive =>
            {
                double length = PrimitiveLength(primitive);
                if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                    return length >= minLineLength;
                return length >= minArcLength && primitive.Radius >= minArcRadius;
            }).ToList();
        }

        private static List<StepSilhouettePrimitive> RemoveStrokeCoveredPrimitives(List<StepSilhouettePrimitive> primitives)
        {
            double maxCandidateLength = ProjectionLineWidthMm * StrokeCoverageMaxLengthFactor;
            double coverageTolerance = ProjectionLineWidthMm * StrokeCoverageDistanceFactor;
            double sampleStep = Math.Max(ProjectionLineWidthMm * StrokeCoverageSampleStepFactor, OptimizePointGridMm);
            double[] lengths = primitives.Select(PrimitiveLength).ToArray();
            var result = new List<StepSilhouettePrimitive>();
            for (int index = 0; index < primitives.Count; index++)
            {
                StepSilhouettePrimitive primitive = primitives[index];
                double length = lengths[index];
                bool covered = false;
                if (length <= maxCandidateLength)
                {
                    for (int otherIndex = 0; otherIndex < primitives.Count; otherIndex++)
                    {
                        if (otherIndex == index)
                            continue;
                        double otherLength = lengths[otherIndex];
                        if (otherLength < length - PointEpsilonMm)
                            continue;
                        if (Math.Abs(otherLength - length) <= PointEpsilonMm && otherIndex > index)
                            continue;
                        if (PrimitiveIsStrokeCovered(primitive, primitives[otherIndex], coverageTolerance, sampleStep))
                        {
                            covered = true;
                            break;
                        }
                    }
                }

                if (!covered)
                    result.Add(primitive);
            }

            return result;
        }

        private static List<StepSilhouettePrimitive> AddLineGapBridges(List<StepSilhouettePrimitive> primitives)
        {
            double maxGap = ProjectionLineWidthMm * LineGapBridgeMaxFactor;
            var grouped = new Dictionary<string, List<BridgeSpan>>();
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                string key = LineGapBridgeKey(primitive);
                if (key == null)
                    continue;

                bool vertical = key.StartsWith("V|", StringComparison.Ordinal);
                double a = vertical ? primitive.Y1 : primitive.X1;
                double b = vertical ? primitive.Y2 : primitive.X2;
                double start = Math.Min(a, b);
                double end = Math.Max(a, b);
                if (!grouped.TryGetValue(key, out List<BridgeSpan> spans))
                {
                    spans = new List<BridgeSpan>();
                    grouped[key] = spans;
                }
                spans.Add(new BridgeSpan(start, end, end - start));
            }

            var bridged = new List<StepSilhouettePrimitive>(primitives);
            var existingKeys = new HashSet<SegmentKey>();
            foreach (StepSilhouettePrimitive primitive in primitives.Where(p => p.Kind == StepSilhouettePrimitiveKind.Line))
                existingKeys.Add(CanonicalSegmentKey(new Segment2d(primitive.X1, primitive.Y1, primitive.X2, primitive.Y2)));

            foreach (KeyValuePair<string, List<BridgeSpan>> item in grouped)
            {
                List<BridgeSpan> spans = item.Value;
                spans.Sort((a, b) =>
                {
                    int startCompare = a.Start.CompareTo(b.Start);
                    return startCompare != 0 ? startCompare : a.End.CompareTo(b.End);
                });

                string[] keyParts = item.Key.Split('|');
                bool vertical = keyParts[0] == "V";
                double fixedCoord = int.Parse(keyParts[1], CultureInfo.InvariantCulture) * OptimizePointGridMm;
                for (int index = 0; index < spans.Count - 1; index++)
                {
                    BridgeSpan current = spans[index];
                    BridgeSpan next = spans[index + 1];
                    double gap = next.Start - current.End;
                    if (gap <= PointEpsilonMm || gap > maxGap + PointEpsilonMm)
                        continue;
                    if (gap > Math.Max(current.Length, next.Length) + PointEpsilonMm)
                        continue;

                    StepSilhouettePrimitive bridge = vertical
                        ? StepSilhouettePrimitive.Line(fixedCoord, current.End, fixedCoord, next.Start)
                        : StepSilhouettePrimitive.Line(current.End, fixedCoord, next.Start, fixedCoord);
                    SegmentKey bridgeKey = CanonicalSegmentKey(new Segment2d(bridge.X1, bridge.Y1, bridge.X2, bridge.Y2));
                    if (!existingKeys.Add(bridgeKey))
                        continue;

                    bridged.Add(bridge);
                }
            }

            return bridged;
        }

        private static string LineGapBridgeKey(StepSilhouettePrimitive primitive)
        {
            if (primitive.Kind != StepSilhouettePrimitiveKind.Line)
                return null;
            if (Math.Abs(primitive.X1 - primitive.X2) <= PointEpsilonMm)
                return "V|" + ((int)Math.Round(primitive.X1 / OptimizePointGridMm)).ToString(CultureInfo.InvariantCulture);
            if (Math.Abs(primitive.Y1 - primitive.Y2) <= PointEpsilonMm)
                return "H|" + ((int)Math.Round(primitive.Y1 / OptimizePointGridMm)).ToString(CultureInfo.InvariantCulture);
            return null;
        }

        private static StepSilhouettePrimitive FitArcToPoints(List<Point2d> points)
        {
            if (points.Count < 4 || PointsAreCollinear(points))
                return null;

            Point2d middle = points[points.Count / 2];
            Circle2d? circle = CircleFromPoints(points[0], middle, points[points.Count - 1]);
            if (!circle.HasValue)
                return null;

            Circle2d value = circle.Value;
            double maxRadialError = points.Max(point => Math.Abs(Distance(point, new Point2d(value.CenterX, value.CenterY)) - value.Radius));
            if (maxRadialError > ArcRadialToleranceMm)
                return null;

            List<double> angles = points.Select(point => Math.Atan2(point.Y - value.CenterY, point.X - value.CenterX)).ToList();
            List<double> deltas = new List<double>();
            for (int index = 0; index < angles.Count - 1; index++)
                deltas.Add(SignedAngleDelta(angles[index], angles[index + 1]));

            List<double> nonzeroDeltas = deltas.Where(delta => Math.Abs(RadiansToDegrees(delta)) > 0.05).ToList();
            if (nonzeroDeltas.Count == 0)
                return null;
            bool hasPositive = nonzeroDeltas.Any(delta => delta > 0);
            bool hasNegative = nonzeroDeltas.Any(delta => delta < 0);
            if (hasPositive && hasNegative)
                return null;

            double sweep = nonzeroDeltas.Sum();
            double sweepDeg = Math.Abs(RadiansToDegrees(sweep));
            if (sweepDeg < ArcMinSweepDeg || sweepDeg > ArcMaxSweepDeg)
                return null;

            double startAngle;
            double endAngle;
            if (sweep > 0)
            {
                startAngle = RadiansToDegrees(angles[0]);
                endAngle = startAngle + sweepDeg;
            }
            else
            {
                startAngle = RadiansToDegrees(angles[angles.Count - 1]);
                endAngle = startAngle + sweepDeg;
            }

            while (startAngle < 0)
            {
                startAngle += 360.0;
                endAngle += 360.0;
            }
            while (startAngle >= 360.0)
            {
                startAngle -= 360.0;
                endAngle -= 360.0;
            }

            StepSilhouetteBounds pointBounds = BoundsForPoints(points);
            StepSilhouetteBounds arcBounds = BoundsForArc(value.CenterX, value.CenterY, value.Radius, startAngle, endAngle);
            if (arcBounds.Left < pointBounds.Left - ArcBboxToleranceMm ||
                arcBounds.Bottom < pointBounds.Bottom - ArcBboxToleranceMm ||
                arcBounds.Right > pointBounds.Right + ArcBboxToleranceMm ||
                arcBounds.Top > pointBounds.Top + ArcBboxToleranceMm)
                return null;

            return StepSilhouettePrimitive.Arc(
                RoundCoord(value.CenterX),
                RoundCoord(value.CenterY),
                RoundCoord(value.Radius),
                RoundCoord(startAngle),
                RoundCoord(endAngle));
        }

        private static List<Point2d> RdpSimplify(List<Point2d> points, double tolerance)
        {
            if (points.Count <= 2)
                return points;

            Point2d start = points[0];
            Point2d end = points[points.Count - 1];
            double maxDistance = -1.0;
            int splitIndex = 0;
            for (int index = 1; index < points.Count - 1; index++)
            {
                double distance = DistancePointToLine(points[index], start, end);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    splitIndex = index;
                }
            }

            if (maxDistance <= tolerance)
                return new List<Point2d> { start, end };

            List<Point2d> left = RdpSimplify(points.Take(splitIndex + 1).ToList(), tolerance);
            List<Point2d> right = RdpSimplify(points.Skip(splitIndex).ToList(), tolerance);
            left.RemoveAt(left.Count - 1);
            left.AddRange(right);
            return left;
        }

        private static bool PointsAreCollinear(List<Point2d> points, double tolerance = CollinearDistanceToleranceMm)
        {
            if (points.Count <= 2)
                return true;

            Point2d start = points[0];
            Point2d end = points[points.Count - 1];
            if (Distance(start, end) < OutputMinLineLengthMm)
                return false;

            for (int index = 1; index < points.Count - 1; index++)
            {
                if (DistancePointToLine(points[index], start, end) > tolerance)
                    return false;
            }

            return true;
        }

        private static Circle2d? CircleFromPoints(Point2d a, Point2d b, Point2d c)
        {
            double determinant = 2.0 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
            if (Math.Abs(determinant) <= 1e-9)
                return null;

            double aSq = a.X * a.X + a.Y * a.Y;
            double bSq = b.X * b.X + b.Y * b.Y;
            double cSq = c.X * c.X + c.Y * c.Y;
            double centerX = (aSq * (b.Y - c.Y) + bSq * (c.Y - a.Y) + cSq * (a.Y - b.Y)) / determinant;
            double centerY = (aSq * (c.X - b.X) + bSq * (a.X - c.X) + cSq * (b.X - a.X)) / determinant;
            double radius = Hypot(a.X - centerX, a.Y - centerY);
            if (radius <= OutputMinLineLengthMm)
                return null;

            return new Circle2d(centerX, centerY, radius);
        }

        private static StepSilhouetteBounds BoundsForSegments(List<Segment2d> segments)
        {
            return new StepSilhouetteBounds
            {
                Left = segments.Min(segment => Math.Min(segment.X1, segment.X2)),
                Bottom = segments.Min(segment => Math.Min(segment.Y1, segment.Y2)),
                Right = segments.Max(segment => Math.Max(segment.X1, segment.X2)),
                Top = segments.Max(segment => Math.Max(segment.Y1, segment.Y2))
            };
        }

        private static StepSilhouetteBounds BoundsForPoints(List<Point2d> points)
        {
            return new StepSilhouetteBounds
            {
                Left = points.Min(point => point.X),
                Bottom = points.Min(point => point.Y),
                Right = points.Max(point => point.X),
                Top = points.Max(point => point.Y)
            };
        }

        private static StepSilhouetteBounds BoundsForArc(double centerX, double centerY, double radius, double startAngle, double endAngle)
        {
            var angles = new List<double> { startAngle, endAngle };
            foreach (double cardinalAngle in new[] { 0.0, 90.0, 180.0, 270.0, 360.0, 450.0, 540.0, 630.0, 720.0 })
            {
                if (AngleInArcSweep(cardinalAngle, startAngle, endAngle))
                    angles.Add(cardinalAngle);
            }

            List<Point2d> points = angles
                .Select(angle => new Point2d(
                    centerX + radius * Math.Cos(DegreesToRadians(angle)),
                    centerY + radius * Math.Sin(DegreesToRadians(angle))))
                .ToList();
            return BoundsForPoints(points);
        }

        private static bool AngleInArcSweep(double testAngle, double startAngle, double endAngle)
        {
            while (testAngle < startAngle)
                testAngle += 360.0;
            return testAngle >= startAngle && testAngle <= endAngle;
        }

        private static double PrimitiveArcSweepDegrees(StepSilhouettePrimitive primitive)
        {
            double end = primitive.EndAngle;
            while (end < primitive.StartAngle)
                end += 360.0;
            return end - primitive.StartAngle;
        }

        private static double PrimitiveLength(StepSilhouettePrimitive primitive)
        {
            if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                return Hypot(primitive.X2 - primitive.X1, primitive.Y2 - primitive.Y1);
            return primitive.Radius * DegreesToRadians(PrimitiveArcSweepDegrees(primitive));
        }

        private static Point2d ArcEndpoint(StepSilhouettePrimitive primitive, double angleDegrees)
        {
            double angle = DegreesToRadians(angleDegrees);
            return new Point2d(
                primitive.CenterX + primitive.Radius * Math.Cos(angle),
                primitive.CenterY + primitive.Radius * Math.Sin(angle));
        }

        private static double DistancePointToPrimitive(Point2d point, StepSilhouettePrimitive primitive)
        {
            if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                return DistancePointToSegment(point, new Point2d(primitive.X1, primitive.Y1), new Point2d(primitive.X2, primitive.Y2));

            Point2d center = new Point2d(primitive.CenterX, primitive.CenterY);
            double pointAngle = RadiansToDegrees(Math.Atan2(point.Y - center.Y, point.X - center.X));
            while (pointAngle < 0)
                pointAngle += 360.0;

            if (AngleInArcSweep(pointAngle, primitive.StartAngle, primitive.EndAngle))
                return Math.Abs(Distance(point, center) - primitive.Radius);

            Point2d start = ArcEndpoint(primitive, primitive.StartAngle);
            Point2d end = ArcEndpoint(primitive, primitive.EndAngle);
            return Math.Min(Distance(point, start), Distance(point, end));
        }

        private static List<Point2d> PrimitiveSamplePoints(StepSilhouettePrimitive primitive, double stepMm)
        {
            double length = PrimitiveLength(primitive);
            int sampleCount = Math.Max(2, Math.Min(64, (int)Math.Ceiling(length / stepMm) + 1));
            var result = new List<Point2d>(sampleCount);
            if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
            {
                for (int index = 0; index < sampleCount; index++)
                {
                    double t = index / (double)(sampleCount - 1);
                    result.Add(new Point2d(
                        primitive.X1 + (primitive.X2 - primitive.X1) * t,
                        primitive.Y1 + (primitive.Y2 - primitive.Y1) * t));
                }
            }
            else
            {
                double sweep = PrimitiveArcSweepDegrees(primitive);
                for (int index = 0; index < sampleCount; index++)
                    result.Add(ArcEndpoint(primitive, primitive.StartAngle + sweep * index / (sampleCount - 1)));
            }

            return result;
        }

        private static bool PrimitiveIsStrokeCovered(StepSilhouettePrimitive candidate, StepSilhouettePrimitive cover, double toleranceMm, double sampleStepMm)
        {
            List<Point2d> samples = PrimitiveSamplePoints(candidate, sampleStepMm);
            return samples.Count > 0 && samples.All(point => DistancePointToPrimitive(point, cover) <= toleranceMm);
        }

        private static double DistancePointToLine(Point2d point, Point2d start, Point2d end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double length = Hypot(dx, dy);
            if (length <= 1e-12)
                return Distance(point, start);
            return Math.Abs((point.X - start.X) * dy - (point.Y - start.Y) * dx) / length;
        }

        private static double DistancePointToSegment(Point2d point, Point2d start, Point2d end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSq = dx * dx + dy * dy;
            if (lengthSq <= 1e-12)
                return Distance(point, start);

            double projection = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSq;
            projection = Math.Max(0.0, Math.Min(1.0, projection));
            return Distance(point, new Point2d(start.X + projection * dx, start.Y + projection * dy));
        }

        private static SegmentKey CanonicalSegmentKey(Segment2d segment)
        {
            PointKey a = PointKeyFromPoint(new Point2d(segment.X1, segment.Y1));
            PointKey b = PointKeyFromPoint(new Point2d(segment.X2, segment.Y2));
            return ComparePointKeys(a, b) <= 0 ? new SegmentKey(a, b) : new SegmentKey(b, a);
        }

        private static PointKey PointKeyFromPoint(Point2d point)
        {
            return new PointKey(
                (int)Math.Round(point.X / OptimizePointGridMm),
                (int)Math.Round(point.Y / OptimizePointGridMm));
        }

        private static Point2d PointFromKey(PointKey key)
        {
            return new Point2d(key.X * OptimizePointGridMm, key.Y * OptimizePointGridMm);
        }

        private static Point2d RoundedPoint(Point2d point)
        {
            return new Point2d(RoundCoord(point.X), RoundCoord(point.Y));
        }

        private static int ComparePointKeys(PointKey a, PointKey b)
        {
            int xCompare = a.X.CompareTo(b.X);
            return xCompare != 0 ? xCompare : a.Y.CompareTo(b.Y);
        }

        private static void AddGraphEdge(Dictionary<PointKey, List<int>> graph, PointKey key, int edgeIndex)
        {
            if (!graph.TryGetValue(key, out List<int> edges))
            {
                edges = new List<int>();
                graph[key] = edges;
            }
            edges.Add(edgeIndex);
        }

        private static double SegmentLength(Segment2d segment)
        {
            return Hypot(segment.X2 - segment.X1, segment.Y2 - segment.Y1);
        }

        private static double ProjectionRotationFromModelState(ModelState state)
        {
            if (state == null || !state.RotZ.HasValue)
                return 0.0;

            double baseline = AltiumTopProjectionZBaselineDeg;
            if (state.RotX.HasValue)
            {
                double normalizedRotX = NormalizeRotationDegrees(state.RotX.Value);
                if (normalizedRotX == 0.0 || normalizedRotX == 90.0 || normalizedRotX == 270.0)
                    baseline = 0.0;
            }

            return NormalizeRotationDegrees(baseline - state.RotZ.Value + (state.Rotation2D ?? 0.0));
        }

        private static double NormalizeRotationDegrees(double angleDeg)
        {
            double normalized = PositiveModulo(angleDeg, 360.0);
            if (Math.Abs(normalized) <= RotationEpsilonDeg || Math.Abs(normalized - 360.0) <= RotationEpsilonDeg)
                return 0.0;
            return normalized;
        }

        private static double SignedAngleDelta(double start, double end)
        {
            return PositiveModulo(end - start + Math.PI, 2.0 * Math.PI) - Math.PI;
        }

        private static double CircleAngle(Vec3d point, Vec3d center, Vec3d xDir, Vec3d yDir)
        {
            Vec3d rel = Subtract(point, center);
            return Math.Atan2(Dot(rel, yDir), Dot(rel, xDir));
        }

        private static Vec3d Subtract(Vec3d a, Vec3d b)
        {
            return new Vec3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        private static Vec3d Negate(Vec3d value)
        {
            return new Vec3d(-value.X, -value.Y, -value.Z);
        }

        private static double Dot(Vec3d a, Vec3d b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private static Vec3d Cross(Vec3d a, Vec3d b)
        {
            return new Vec3d(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        private static Vec3d Normalize(Vec3d value)
        {
            double magnitude = Math.Sqrt(Dot(value, value));
            if (magnitude <= 1e-12)
                return new Vec3d(0.0, 0.0, 0.0);
            return new Vec3d(value.X / magnitude, value.Y / magnitude, value.Z / magnitude);
        }

        private static double Distance(Vec3d a, Vec3d b)
        {
            return Math.Sqrt(
                (a.X - b.X) * (a.X - b.X) +
                (a.Y - b.Y) * (a.Y - b.Y) +
                (a.Z - b.Z) * (a.Z - b.Z));
        }

        private static double Distance(Point2d a, Point2d b)
        {
            return Hypot(a.X - b.X, a.Y - b.Y);
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static double RadiansToDegrees(double radians)
        {
            return radians * 180.0 / Math.PI;
        }

        private static double PositiveModulo(double value, double modulus)
        {
            double result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static double RoundCoord(double value)
        {
            return Math.Round(value, OutputCoordDecimals);
        }

        private static int ParseInt(string text)
        {
            return int.Parse(text, CultureInfo.InvariantCulture);
        }

        private static double ParseDouble(string text)
        {
            return double.Parse(text, CultureInfo.InvariantCulture);
        }

        private sealed class StepGeometry
        {
            public Dictionary<int, Vec3d> Points { get; } = new Dictionary<int, Vec3d>();
            public Dictionary<int, Vec3d> Directions { get; } = new Dictionary<int, Vec3d>();
            public Dictionary<int, int> Vertices { get; } = new Dictionary<int, int>();
            public Dictionary<int, Placement3d> Placements { get; } = new Dictionary<int, Placement3d>();
            public Dictionary<int, Circle3d> Circles { get; } = new Dictionary<int, Circle3d>();
            public Dictionary<int, int> Planes { get; } = new Dictionary<int, int>();
            public Dictionary<int, EdgeCurve> Edges { get; } = new Dictionary<int, EdgeCurve>();
            public Dictionary<int, OrientedEdge> OrientedEdges { get; } = new Dictionary<int, OrientedEdge>();
            public Dictionary<int, List<int>> EdgeLoops { get; } = new Dictionary<int, List<int>>();
            public Dictionary<int, FaceBound> FaceBounds { get; } = new Dictionary<int, FaceBound>();
            public Dictionary<int, AdvancedFace> Faces { get; } = new Dictionary<int, AdvancedFace>();
        }

        private sealed class ModelState
        {
            public double? RotX { get; set; }
            public double? RotY { get; set; }
            public double? RotZ { get; set; }
            public double? Rotation2D { get; set; }
        }

        private sealed class AdvancedFace
        {
            public AdvancedFace(List<int> boundIds, int surfaceId, bool sameSense)
            {
                BoundIds = boundIds;
                SurfaceId = surfaceId;
                SameSense = sameSense;
            }

            public List<int> BoundIds { get; }
            public int SurfaceId { get; }
            public bool SameSense { get; }
        }

        private sealed class FaceInfo
        {
            public FaceInfo(int faceId, Vec3d normal, Vec3d planePoint, List<List<Point2d>> outers, List<List<Point2d>> holes)
            {
                FaceId = faceId;
                Normal = normal;
                PlanePoint = planePoint;
                Outers = outers;
                Holes = holes;
            }

            public int FaceId { get; }
            public Vec3d Normal { get; }
            public Vec3d PlanePoint { get; }
            public List<List<Point2d>> Outers { get; }
            public List<List<Point2d>> Holes { get; }
        }

        private sealed class FacePolygons
        {
            public FacePolygons(List<List<Point2d>> outers, List<List<Point2d>> holes)
            {
                Outers = outers;
                Holes = holes;
            }

            public List<List<Point2d>> Outers { get; }
            public List<List<Point2d>> Holes { get; }
        }

        private sealed class VisibleProjection
        {
            public VisibleProjection(List<Segment2d> segments, StepSilhouetteBounds sourceBounds, int visibleProjectedSegments, int visibleEdges)
            {
                Segments = segments;
                SourceBounds = sourceBounds;
                VisibleProjectedSegments = visibleProjectedSegments;
                VisibleEdges = visibleEdges;
            }

            public List<Segment2d> Segments { get; }
            public StepSilhouetteBounds SourceBounds { get; }
            public int VisibleProjectedSegments { get; }
            public int VisibleEdges { get; }
        }

        private sealed class ProjectedSegmentsResult
        {
            public ProjectedSegmentsResult(List<Segment2d> segments, int occludedSegments)
            {
                Segments = segments;
                OccludedSegments = occludedSegments;
            }

            public List<Segment2d> Segments { get; }
            public int OccludedSegments { get; }
        }

        private sealed class LineInterval
        {
            public LineInterval(double start, double end, double ux, double uy, double normalX, double normalY, double offset)
            {
                Start = start;
                End = end;
                Ux = ux;
                Uy = uy;
                NormalX = normalX;
                NormalY = normalY;
                Offset = offset;
            }

            public double Start { get; set; }
            public double End { get; set; }
            public double Ux { get; }
            public double Uy { get; }
            public double NormalX { get; }
            public double NormalY { get; }
            public double Offset { get; }
        }

        private sealed class ArcInterval
        {
            public ArcInterval(double start, double end)
            {
                Start = start;
                End = end;
            }

            public double Start { get; }
            public double End { get; set; }
        }

        private sealed class BridgeSpan
        {
            public BridgeSpan(double start, double end, double length)
            {
                Start = start;
                End = end;
                Length = length;
            }

            public double Start { get; }
            public double End { get; }
            public double Length { get; }
        }

        private struct Vec3d
        {
            public Vec3d(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public double X { get; }
            public double Y { get; }
            public double Z { get; }
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

        private struct Segment2d
        {
            public Segment2d(double x1, double y1, double x2, double y2)
            {
                X1 = x1;
                Y1 = y1;
                X2 = x2;
                Y2 = y2;
            }

            public double X1 { get; }
            public double Y1 { get; }
            public double X2 { get; }
            public double Y2 { get; }
        }

        private struct Placement3d
        {
            public Placement3d(int centerId, int axisId, int refId)
            {
                CenterId = centerId;
                AxisId = axisId;
                RefId = refId;
            }

            public int CenterId { get; }
            public int AxisId { get; }
            public int RefId { get; }
        }

        private struct Circle3d
        {
            public Circle3d(int placementId, double radius)
            {
                PlacementId = placementId;
                Radius = radius;
            }

            public int PlacementId { get; }
            public double Radius { get; }
        }

        private struct EdgeCurve
        {
            public EdgeCurve(int startVertex, int endVertex, int curveId, bool sameSense)
            {
                StartVertex = startVertex;
                EndVertex = endVertex;
                CurveId = curveId;
                SameSense = sameSense;
            }

            public int StartVertex { get; }
            public int EndVertex { get; }
            public int CurveId { get; }
            public bool SameSense { get; }
        }

        private struct OrientedEdge
        {
            public OrientedEdge(int edgeId, bool forward)
            {
                EdgeId = edgeId;
                Forward = forward;
            }

            public int EdgeId { get; }
            public bool Forward { get; }
        }

        private struct FaceBound
        {
            public FaceBound(int loopId, bool isOuter)
            {
                LoopId = loopId;
                IsOuter = isOuter;
            }

            public int LoopId { get; }
            public bool IsOuter { get; }
        }

        private struct Circle2d
        {
            public Circle2d(double centerX, double centerY, double radius)
            {
                CenterX = centerX;
                CenterY = centerY;
                Radius = radius;
            }

            public double CenterX { get; }
            public double CenterY { get; }
            public double Radius { get; }
        }

        private struct PointKey : IEquatable<PointKey>
        {
            public PointKey(int x, int y)
            {
                X = x;
                Y = y;
            }

            public int X { get; }
            public int Y { get; }

            public bool Equals(PointKey other)
            {
                return X == other.X && Y == other.Y;
            }

            public override bool Equals(object obj)
            {
                return obj is PointKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (X * 397) ^ Y;
                }
            }
        }

        private struct SegmentKey : IEquatable<SegmentKey>
        {
            public SegmentKey(PointKey a, PointKey b)
            {
                A = a;
                B = b;
            }

            public PointKey A { get; }
            public PointKey B { get; }

            public bool Equals(SegmentKey other)
            {
                return A.Equals(other.A) && B.Equals(other.B);
            }

            public override bool Equals(object obj)
            {
                return obj is SegmentKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (A.GetHashCode() * 397) ^ B.GetHashCode();
                }
            }
        }
    }
}
