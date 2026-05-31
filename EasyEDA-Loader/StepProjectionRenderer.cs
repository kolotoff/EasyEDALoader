using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyEDA_Loader
{
    public sealed class StepProjectionOptions
    {
        public int ImageSizePixels { get; set; } = 1600;
        public int PaddingPixels { get; set; } = 80;
        public bool WriteMetadata { get; set; } = true;
    }

    public sealed class StepProjectionReport
    {
        public string InputPath { get; internal set; }
        public int FaceCount { get; internal set; }
        public int EdgeCount { get; internal set; }
        public IReadOnlyList<string> OutputFiles { get; internal set; }
    }

    public static class StepProjectionRenderer
    {
        private static readonly ViewSpec[] Views =
        {
            new ViewSpec("x_plus", 0, 1, 1, -1, 2, 1),
            new ViewSpec("x_minus", 0, -1, 1, 1, 2, 1),
            new ViewSpec("y_plus", 1, 1, 0, 1, 2, 1),
            new ViewSpec("y_minus", 1, -1, 0, -1, 2, 1),
            new ViewSpec("z_plus", 2, 1, 0, 1, 1, -1),
            new ViewSpec("z_minus", 2, -1, 0, 1, 1, 1)
        };

        public static IReadOnlyList<StepProjectionReport> ProjectDirectory(
            string inputDirectory,
            string outputDirectory,
            StepProjectionOptions options = null)
        {
            if (inputDirectory == null)
                throw new ArgumentNullException(nameof(inputDirectory));

            if (outputDirectory == null)
                throw new ArgumentNullException(nameof(outputDirectory));

            options = NormalizeOptions(options);
            Directory.CreateDirectory(outputDirectory);

            var reports = new List<StepProjectionReport>();
            foreach (string inputFile in GetStepFiles(inputDirectory))
                reports.Add(ProjectFile(inputFile, outputDirectory, options));

            return reports;
        }

        public static StepProjectionReport ProjectFile(string inputPath, string outputDirectory, StepProjectionOptions options = null)
        {
            if (inputPath == null)
                throw new ArgumentNullException(nameof(inputPath));

            if (outputDirectory == null)
                throw new ArgumentNullException(nameof(outputDirectory));

            options = NormalizeOptions(options);
            Directory.CreateDirectory(outputDirectory);

            string stepText = Encoding.Latin1.GetString(File.ReadAllBytes(inputPath));
            StepModel model = StepModel.Parse(stepText);
            model.BuildIndexes();
            var drawingModel = ProjectionModel.Build(model);

            var outputFiles = new List<string>();
            string modelName = Path.GetFileNameWithoutExtension(inputPath);

            foreach (ViewSpec view in Views)
            {
                ProjectionTransform transform = ProjectionTransform.Create(drawingModel.Bounds, view, options);
                string outputPath = Path.Combine(outputDirectory, modelName + "__" + view.Name + ".png");
                RenderProjection(drawingModel, view, transform, outputPath, options);
                outputFiles.Add(outputPath);

                if (options.WriteMetadata)
                {
                    string metadataPath = Path.Combine(outputDirectory, modelName + "__" + view.Name + ".json");
                    File.WriteAllText(metadataPath, WriteMetadata(inputPath, outputPath, view, transform, options), Encoding.UTF8);
                    outputFiles.Add(metadataPath);
                }
            }

            return new StepProjectionReport
            {
                InputPath = inputPath,
                FaceCount = drawingModel.Faces.Count,
                EdgeCount = drawingModel.EdgeCount,
                OutputFiles = outputFiles
            };
        }

        public static IReadOnlyList<string> ViewNames => Views.Select(v => v.Name).ToList();

        private static void RenderProjection(
            ProjectionModel model,
            ViewSpec view,
            ProjectionTransform transform,
            string outputPath,
            StepProjectionOptions options)
        {
            var image = new RgbaImage(options.ImageSizePixels, options.ImageSizePixels);
            image.Clear(new Rgba(250, 250, 250, 255));

            var sortedFaces = model.Faces
                .Where(f => f.Points.Count >= 2)
                .OrderBy(f => f.Depth(view))
                .ThenBy(f => f.Id)
                .ToList();

            foreach (ProjectionFace face in sortedFaces)
            {
                Rgba fill = Shade(face.Color, face.Normal, view);
                ProjectionLoop outerLoop = face.Loops
                    .Where(l => l.Points.Count >= 3)
                    .OrderByDescending(l => Math.Abs(ProjectedArea(l.Points, transform)))
                    .FirstOrDefault();

                if (outerLoop != null)
                {
                    var polygon = outerLoop.Points.Select(transform.Project).ToList();
                    image.FillPolygon(polygon, fill);
                }

                Rgba line = ContrastLine(fill);
                foreach (ProjectionLoop loop in face.Loops)
                {
                    DrawLoop(image, loop.Points, transform, line);
                }
            }

            image.SavePng(outputPath);
        }

        private static void DrawLoop(RgbaImage image, List<Vec3d> points, ProjectionTransform transform, Rgba color)
        {
            if (points.Count < 2)
                return;

            Point2i previous = transform.Project(points[0]);
            for (int i = 1; i < points.Count; i++)
            {
                Point2i current = transform.Project(points[i]);
                image.DrawLine(previous.X, previous.Y, current.X, current.Y, color);
                previous = current;
            }

            Point2i first = transform.Project(points[0]);
            if (first.X != previous.X || first.Y != previous.Y)
                image.DrawLine(previous.X, previous.Y, first.X, first.Y, color);
        }

        private static double ProjectedArea(List<Vec3d> points, ProjectionTransform transform)
        {
            if (points.Count < 3)
                return 0.0;

            double area = 0.0;
            Point2d previous = transform.ProjectDouble(points[points.Count - 1]);
            foreach (Vec3d point in points)
            {
                Point2d current = transform.ProjectDouble(point);
                area += previous.X * current.Y - current.X * previous.Y;
                previous = current;
            }

            return area / 2.0;
        }

        private static Rgba Shade(ColorRgb color, Vec3d normal, ViewSpec view)
        {
            double viewAlignment = Math.Abs(normal.Get(view.DepthAxis)) < 0.000001
                ? 0.25
                : Math.Abs(normal.Get(view.DepthAxis));

            double factor = 0.72 + 0.28 * Math.Min(1.0, viewAlignment);
            return new Rgba(
                ClampToByte(color.R * 255.0 * factor),
                ClampToByte(color.G * 255.0 * factor),
                ClampToByte(color.B * 255.0 * factor),
                255);
        }

        private static Rgba ContrastLine(Rgba fill)
        {
            double luminance = (0.2126 * fill.R + 0.7152 * fill.G + 0.0722 * fill.B) / 255.0;
            return luminance >= 0.55
                ? new Rgba(30, 30, 30, 145)
                : new Rgba(230, 230, 230, 155);
        }

        private static byte ClampToByte(double value)
        {
            if (value <= 0.0)
                return 0;

            if (value >= 255.0)
                return 255;

            return (byte)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private static string WriteMetadata(
            string inputPath,
            string outputPath,
            ViewSpec view,
            ProjectionTransform transform,
            StepProjectionOptions options)
        {
            var builder = new StringBuilder();
            builder.AppendLine("{");
            AppendJson(builder, "input", Path.GetFullPath(inputPath), comma: true, indent: 2);
            AppendJson(builder, "projection", Path.GetFullPath(outputPath), comma: true, indent: 2);
            AppendJson(builder, "view", view.Name, comma: true, indent: 2);
            builder.AppendLine("  \"image\": {");
            AppendJson(builder, "width", options.ImageSizePixels, comma: true, indent: 4);
            AppendJson(builder, "height", options.ImageSizePixels, comma: true, indent: 4);
            AppendJson(builder, "padding", options.PaddingPixels, comma: false, indent: 4);
            builder.AppendLine("  },");
            builder.AppendLine("  \"model_axes\": {");
            AppendJson(builder, "u_axis", AxisName(view.UAxis), comma: true, indent: 4);
            AppendJson(builder, "u_sign", view.USign, comma: true, indent: 4);
            AppendJson(builder, "v_axis", AxisName(view.VAxis), comma: true, indent: 4);
            AppendJson(builder, "v_sign", view.VSign, comma: true, indent: 4);
            AppendJson(builder, "depth_axis", AxisName(view.DepthAxis), comma: true, indent: 4);
            AppendJson(builder, "depth_sign", view.DepthSign, comma: false, indent: 4);
            builder.AppendLine("  },");
            builder.AppendLine("  \"mapping\": {");
            AppendJson(builder, "scale_pixels_per_model_unit", transform.Scale, comma: true, indent: 4);
            AppendJson(builder, "u_min", transform.UMin, comma: true, indent: 4);
            AppendJson(builder, "u_max", transform.UMax, comma: true, indent: 4);
            AppendJson(builder, "v_min", transform.VMin, comma: true, indent: 4);
            AppendJson(builder, "v_max", transform.VMax, comma: false, indent: 4);
            builder.AppendLine("  }");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static void AppendJson(StringBuilder builder, string key, string value, bool comma, int indent)
        {
            builder.Append(' ', indent);
            builder.Append('"');
            builder.Append(EscapeJson(key));
            builder.Append("\": \"");
            builder.Append(EscapeJson(value));
            builder.Append('"');
            if (comma)
                builder.Append(',');
            builder.AppendLine();
        }

        private static void AppendJson(StringBuilder builder, string key, int value, bool comma, int indent)
        {
            builder.Append(' ', indent);
            builder.Append('"');
            builder.Append(EscapeJson(key));
            builder.Append("\": ");
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
            if (comma)
                builder.Append(',');
            builder.AppendLine();
        }

        private static void AppendJson(StringBuilder builder, string key, double value, bool comma, int indent)
        {
            builder.Append(' ', indent);
            builder.Append('"');
            builder.Append(EscapeJson(key));
            builder.Append("\": ");
            builder.Append(value.ToString("G17", CultureInfo.InvariantCulture));
            if (comma)
                builder.Append(',');
            builder.AppendLine();
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string AxisName(int axis)
        {
            switch (axis)
            {
                case 0: return "X";
                case 1: return "Y";
                case 2: return "Z";
                default: return "?";
            }
        }

        private static List<string> GetStepFiles(string directory)
        {
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

        private static StepProjectionOptions NormalizeOptions(StepProjectionOptions options)
        {
            options = options ?? new StepProjectionOptions();
            if (options.ImageSizePixels < 256)
                throw new ArgumentOutOfRangeException(nameof(options.ImageSizePixels), "Projection image size must be at least 256 pixels.");

            if (options.PaddingPixels < 0 || options.PaddingPixels * 2 >= options.ImageSizePixels)
                throw new ArgumentOutOfRangeException(nameof(options.PaddingPixels), "Projection padding must fit inside the image.");

            return options;
        }

        private sealed class ProjectionModel
        {
            public List<ProjectionFace> Faces { get; private set; }
            public Bounds Bounds { get; private set; }
            public int EdgeCount { get; private set; }

            public static ProjectionModel Build(StepModel step)
            {
                var colors = BuildTargetColors(step);
                var faces = new List<ProjectionFace>();
                var bounds = new Bounds();
                bool hasBounds = false;
                int edgeCount = 0;

                foreach (StepEntity entity in step.Entities.Values)
                {
                    if (entity.Type != "ADVANCED_FACE")
                        continue;

                    ProjectionFace face = BuildFace(step, entity, colors);
                    if (face.Points.Count < 2)
                        continue;

                    faces.Add(face);
                    edgeCount += face.Loops.Sum(l => Math.Max(0, l.Points.Count));
                    foreach (Vec3d point in face.Points)
                    {
                        bounds.Include(point);
                        hasBounds = true;
                    }
                }

                if (!hasBounds)
                    throw new InvalidOperationException("STEP model does not contain drawable ADVANCED_FACE point data.");

                return new ProjectionModel
                {
                    Faces = faces,
                    Bounds = bounds,
                    EdgeCount = edgeCount
                };
            }

            private static ProjectionFace BuildFace(StepModel step, StepEntity faceEntity, Dictionary<int, ColorRgb> colors)
            {
                var loops = new List<ProjectionLoop>();
                var allPoints = new List<Vec3d>();
                int surfaceReferenceIndex = faceEntity.References.Count - 1;

                for (int i = 0; i < surfaceReferenceIndex; i++)
                {
                    int boundId = faceEntity.References[i];
                    string boundType = step.GetTypeName(boundId);
                    if (boundType != "FACE_OUTER_BOUND" && boundType != "FACE_BOUND")
                        continue;

                    ProjectionLoop loop = BuildLoop(step, boundId);
                    if (loop.Points.Count < 2)
                        continue;

                    loops.Add(loop);
                    allPoints.AddRange(loop.Points);
                }

                if (allPoints.Count == 0)
                    allPoints.AddRange(step.GetReferencedPoints(faceEntity.Id, includeSurface: false));

                if (loops.Count == 0 && allPoints.Count >= 2)
                    loops.Add(new ProjectionLoop(DeduplicatePoints(allPoints)));

                ColorRgb color;
                if (!colors.TryGetValue(faceEntity.Id, out color))
                    color = new ColorRgb(0.62, 0.62, 0.62);

                var distinctPoints = DeduplicatePoints(allPoints);
                return new ProjectionFace
                {
                    Id = faceEntity.Id,
                    Color = color,
                    Loops = loops,
                    Points = distinctPoints,
                    Normal = ComputeNormal(distinctPoints)
                };
            }

            private static ProjectionLoop BuildLoop(StepModel step, int boundId)
            {
                if (!step.Entities.TryGetValue(boundId, out StepEntity boundEntity))
                    return new ProjectionLoop(new List<Vec3d>());

                int edgeLoopId = boundEntity.References.FirstOrDefault(id => step.GetTypeName(id) == "EDGE_LOOP");
                if (edgeLoopId == 0 || !step.Entities.TryGetValue(edgeLoopId, out StepEntity edgeLoopEntity))
                    return new ProjectionLoop(DeduplicatePoints(step.GetReferencedPoints(boundId, includeSurface: true)));

                var points = new List<Vec3d>();
                foreach (int orientedEdgeId in edgeLoopEntity.References)
                {
                    if (step.GetTypeName(orientedEdgeId) != "ORIENTED_EDGE")
                        continue;

                    List<Vec3d> edgePoints = BuildOrientedEdge(step, orientedEdgeId);
                    AppendPolyline(points, edgePoints);
                }

                if (points.Count < 2)
                    points = DeduplicatePoints(step.GetReferencedPoints(boundId, includeSurface: true));

                return new ProjectionLoop(points);
            }

            private static List<Vec3d> BuildOrientedEdge(StepModel step, int orientedEdgeId)
            {
                if (!step.Entities.TryGetValue(orientedEdgeId, out StepEntity orientedEdge))
                    return new List<Vec3d>();

                int edgeCurveId = orientedEdge.References.FirstOrDefault(id => step.GetTypeName(id) == "EDGE_CURVE");
                if (edgeCurveId == 0 || !step.Entities.TryGetValue(edgeCurveId, out StepEntity edgeCurve))
                    return new List<Vec3d>();

                var edgePoints = new List<Vec3d>();
                var vertexPointIds = edgeCurve.References
                    .Where(id => step.GetTypeName(id) == "VERTEX_POINT")
                    .Take(2)
                    .ToList();

                if (vertexPointIds.Count > 0 && step.TryGetVertexPoint(vertexPointIds[0], out Vec3d startPoint))
                    edgePoints.Add(startPoint);

                int curveId = edgeCurve.References.FirstOrDefault(id => id != 0 && step.GetTypeName(id).Contains("B_SPLINE_CURVE"));
                if (curveId != 0)
                    AppendPolyline(edgePoints, step.GetReferencedPoints(curveId, includeSurface: true));

                if (vertexPointIds.Count > 1 && step.TryGetVertexPoint(vertexPointIds[1], out Vec3d endPoint))
                    edgePoints.Add(endPoint);

                if (!ParseLastLogical(orientedEdge.Definition))
                    edgePoints.Reverse();

                return DeduplicatePoints(edgePoints);
            }

            private static void AppendPolyline(List<Vec3d> target, List<Vec3d> source)
            {
                foreach (Vec3d point in source)
                {
                    if (target.Count == 0 || !AlmostSame(target[target.Count - 1], point))
                        target.Add(point);
                }
            }

            private static List<Vec3d> DeduplicatePoints(IEnumerable<Vec3d> points)
            {
                var result = new List<Vec3d>();
                foreach (Vec3d point in points)
                {
                    if (result.Count == 0 || !AlmostSame(result[result.Count - 1], point))
                        result.Add(point);
                }

                if (result.Count > 1 && AlmostSame(result[0], result[result.Count - 1]))
                    result.RemoveAt(result.Count - 1);

                return result;
            }

            private static bool AlmostSame(Vec3d a, Vec3d b)
            {
                return Math.Abs(a.X - b.X) < 0.0000001
                    && Math.Abs(a.Y - b.Y) < 0.0000001
                    && Math.Abs(a.Z - b.Z) < 0.0000001;
            }

            private static Vec3d ComputeNormal(List<Vec3d> points)
            {
                if (points.Count < 3)
                    return new Vec3d(0, 0, 1);

                Vec3d origin = points[0];
                for (int i = 1; i < points.Count - 1; i++)
                {
                    Vec3d a = points[i] - origin;
                    Vec3d b = points[i + 1] - origin;
                    Vec3d normal = Vec3d.Cross(a, b).Normalized();
                    if (normal.Length > 0.000001)
                        return normal;
                }

                return new Vec3d(0, 0, 1);
            }

            private static bool ParseLastLogical(string definition)
            {
                int trueIndex = definition.LastIndexOf(".T.", StringComparison.OrdinalIgnoreCase);
                int falseIndex = definition.LastIndexOf(".F.", StringComparison.OrdinalIgnoreCase);
                return trueIndex >= falseIndex;
            }

            private static Dictionary<int, ColorRgb> BuildTargetColors(StepModel step)
            {
                var result = new Dictionary<int, ColorRgb>();

                foreach (StepEntity entity in step.Entities.Values)
                {
                    if (entity.Type != "STYLED_ITEM" || entity.References.Count < 2)
                        continue;

                    int styleId = entity.References[0];
                    int targetId = entity.References[entity.References.Count - 1];
                    if (step.ResolveColor(styleId, out ColorRgb color))
                        result[targetId] = color;
                }

                return result;
            }
        }

        private sealed class ProjectionFace
        {
            public int Id { get; set; }
            public ColorRgb Color { get; set; }
            public Vec3d Normal { get; set; }
            public List<ProjectionLoop> Loops { get; set; }
            public List<Vec3d> Points { get; set; }

            public double Depth(ViewSpec view)
            {
                double total = 0.0;
                foreach (Vec3d point in Points)
                    total += point.Get(view.DepthAxis) * view.DepthSign;

                return total / Math.Max(1, Points.Count);
            }
        }

        private sealed class ProjectionLoop
        {
            public ProjectionLoop(List<Vec3d> points)
            {
                Points = points;
            }

            public List<Vec3d> Points { get; private set; }
        }

        private sealed class StepModel
        {
            private static readonly Regex ReferenceRegex = new Regex(@"#(\d+)", RegexOptions.Compiled);
            private static readonly Regex EntityTypeRegex = new Regex(@"^\s*([A-Z0-9_]+)\s*\(", RegexOptions.Compiled);
            private static readonly Regex ColourRegex = new Regex(
                @"COLOUR_RGB\s*\(\s*'[^']*'\s*,\s*([-+0-9.Ee]+)\s*,\s*([-+0-9.Ee]+)\s*,\s*([-+0-9.Ee]+)\s*\)",
                RegexOptions.Compiled);
            private static readonly Regex CartesianPointRegex = new Regex(
                @"CARTESIAN_POINT\s*\(\s*(?:'[^']*'|\$)\s*,\s*\(([^)]*)\)",
                RegexOptions.Compiled);

            private readonly Dictionary<int, ColorRgb?> _colorCache = new Dictionary<int, ColorRgb?>();
            private readonly Dictionary<string, List<Vec3d>> _pointListCache = new Dictionary<string, List<Vec3d>>();

            private StepModel(Dictionary<int, StepEntity> entities)
            {
                Entities = entities;
            }

            public Dictionary<int, StepEntity> Entities { get; private set; }

            public static StepModel Parse(string text)
            {
                var entities = new Dictionary<int, StepEntity>();
                int cursor = 0;

                while (cursor < text.Length)
                {
                    int hash = text.IndexOf('#', cursor);
                    if (hash < 0)
                        break;

                    int idStart = hash + 1;
                    int idEnd = idStart;
                    while (idEnd < text.Length && char.IsDigit(text[idEnd]))
                        idEnd++;

                    if (idEnd == idStart)
                    {
                        cursor = hash + 1;
                        continue;
                    }

                    int afterId = SkipWhiteSpace(text, idEnd);
                    if (afterId >= text.Length || text[afterId] != '=')
                    {
                        cursor = idEnd;
                        continue;
                    }

                    int definitionStart = afterId + 1;
                    int semicolon = FindEntityEnd(text, definitionStart);
                    if (semicolon < 0)
                        break;

                    int id = int.Parse(text.Substring(idStart, idEnd - idStart), CultureInfo.InvariantCulture);
                    string definition = text.Substring(definitionStart, semicolon - definitionStart).Trim();
                    entities[id] = new StepEntity
                    {
                        Id = id,
                        Definition = definition,
                        Type = GetEntityType(definition)
                    };

                    cursor = semicolon + 1;
                }

                return new StepModel(entities);
            }

            public void BuildIndexes()
            {
                foreach (StepEntity entity in Entities.Values)
                    entity.References = ParseReferences(entity.Definition);
            }

            public string GetTypeName(int id)
            {
                return Entities.TryGetValue(id, out StepEntity entity) ? entity.Type : string.Empty;
            }

            public bool TryGetVertexPoint(int vertexId, out Vec3d point)
            {
                point = default;
                if (!Entities.TryGetValue(vertexId, out StepEntity vertex) || vertex.Type != "VERTEX_POINT")
                    return false;

                int pointId = vertex.References.FirstOrDefault(id => GetTypeName(id) == "CARTESIAN_POINT");
                return pointId != 0 && TryGetPoint(pointId, out point);
            }

            public List<Vec3d> GetReferencedPoints(int rootId, bool includeSurface)
            {
                string key = rootId.ToString(CultureInfo.InvariantCulture) + "|" + includeSurface.ToString(CultureInfo.InvariantCulture);
                if (_pointListCache.TryGetValue(key, out List<Vec3d> cached))
                    return new List<Vec3d>(cached);

                var result = new List<Vec3d>();
                var startIds = new List<int>();

                if (GetTypeName(rootId) == "ADVANCED_FACE" && !includeSurface && Entities[rootId].References.Count > 1)
                {
                    for (int i = 0; i < Entities[rootId].References.Count - 1; i++)
                        startIds.Add(Entities[rootId].References[i]);
                }
                else
                {
                    startIds.Add(rootId);
                }

                foreach (int startId in startIds)
                {
                    foreach (int id in TraverseReferences(startId))
                    {
                        if (GetTypeName(id) == "CARTESIAN_POINT" && TryGetPoint(id, out Vec3d point))
                            result.Add(point);
                    }
                }

                _pointListCache[key] = new List<Vec3d>(result);
                return result;
            }

            public bool ResolveColor(int rootId, out ColorRgb color)
            {
                if (_colorCache.TryGetValue(rootId, out ColorRgb? cached))
                {
                    if (cached.HasValue)
                    {
                        color = cached.Value;
                        return true;
                    }

                    color = default;
                    return false;
                }

                foreach (int id in TraverseReferences(rootId))
                {
                    if (!Entities.TryGetValue(id, out StepEntity entity) || entity.Type != "COLOUR_RGB")
                        continue;

                    if (TryParseColour(entity.Definition, out color))
                    {
                        _colorCache[rootId] = color;
                        return true;
                    }
                }

                _colorCache[rootId] = null;
                color = default;
                return false;
            }

            private IEnumerable<int> TraverseReferences(int rootId)
            {
                var visited = new HashSet<int>();
                var stack = new Stack<int>();
                stack.Push(rootId);

                while (stack.Count > 0)
                {
                    int id = stack.Pop();
                    if (!visited.Add(id))
                        continue;

                    yield return id;

                    if (!Entities.TryGetValue(id, out StepEntity entity))
                        continue;

                    for (int i = entity.References.Count - 1; i >= 0; i--)
                    {
                        int childId = entity.References[i];
                        if (!visited.Contains(childId))
                            stack.Push(childId);
                    }
                }
            }

            private bool TryGetPoint(int pointId, out Vec3d point)
            {
                point = default;
                if (!Entities.TryGetValue(pointId, out StepEntity entity) || entity.Type != "CARTESIAN_POINT")
                    return false;

                return TryParsePoint(entity.Definition, out point);
            }

            private static bool TryParseColour(string definition, out ColorRgb color)
            {
                Match match = ColourRegex.Match(definition);
                if (!match.Success)
                {
                    color = default;
                    return false;
                }

                color = new ColorRgb(
                    ParseDouble(match.Groups[1].Value),
                    ParseDouble(match.Groups[2].Value),
                    ParseDouble(match.Groups[3].Value));
                return true;
            }

            private static bool TryParsePoint(string definition, out Vec3d point)
            {
                Match match = CartesianPointRegex.Match(definition);
                if (!match.Success)
                {
                    point = default;
                    return false;
                }

                string[] parts = match.Groups[1].Value.Split(',');
                if (parts.Length < 3)
                {
                    point = default;
                    return false;
                }

                point = new Vec3d(ParseDouble(parts[0]), ParseDouble(parts[1]), ParseDouble(parts[2]));
                return true;
            }

            private static List<int> ParseReferences(string definition)
            {
                var result = new List<int>();
                foreach (Match match in ReferenceRegex.Matches(definition))
                {
                    if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                        result.Add(id);
                }

                return result;
            }

            private static int SkipWhiteSpace(string text, int index)
            {
                while (index < text.Length && char.IsWhiteSpace(text[index]))
                    index++;

                return index;
            }

            private static int FindEntityEnd(string text, int start)
            {
                bool inString = false;

                for (int i = start; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '\'')
                    {
                        if (inString && i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            i++;
                            continue;
                        }

                        inString = !inString;
                        continue;
                    }

                    if (!inString && c == ';')
                        return i;
                }

                return -1;
            }

            private static string GetEntityType(string definition)
            {
                Match match = EntityTypeRegex.Match(definition);
                return match.Success ? match.Groups[1].Value : string.Empty;
            }

            private static double ParseDouble(string text)
            {
                return double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        private sealed class StepEntity
        {
            public int Id { get; set; }
            public string Definition { get; set; }
            public string Type { get; set; }
            public List<int> References { get; set; } = new List<int>();
        }

        private sealed class ProjectionTransform
        {
            public double Scale { get; private set; }
            public double UMin { get; private set; }
            public double UMax { get; private set; }
            public double VMin { get; private set; }
            public double VMax { get; private set; }
            private ViewSpec View { get; set; }
            private int Padding { get; set; }
            private int ImageSize { get; set; }

            public static ProjectionTransform Create(Bounds bounds, ViewSpec view, StepProjectionOptions options)
            {
                double u0 = bounds.Min.Get(view.UAxis) * view.USign;
                double u1 = bounds.Max.Get(view.UAxis) * view.USign;
                double v0 = bounds.Min.Get(view.VAxis) * view.VSign;
                double v1 = bounds.Max.Get(view.VAxis) * view.VSign;

                double uMin = Math.Min(u0, u1);
                double uMax = Math.Max(u0, u1);
                double vMin = Math.Min(v0, v1);
                double vMax = Math.Max(v0, v1);

                double usable = options.ImageSizePixels - options.PaddingPixels * 2.0;
                double uSize = Math.Max(0.000001, uMax - uMin);
                double vSize = Math.Max(0.000001, vMax - vMin);
                double scale = usable / Math.Max(uSize, vSize);

                double uPad = (usable / scale - uSize) / 2.0;
                double vPad = (usable / scale - vSize) / 2.0;

                return new ProjectionTransform
                {
                    View = view,
                    ImageSize = options.ImageSizePixels,
                    Padding = options.PaddingPixels,
                    Scale = scale,
                    UMin = uMin - uPad,
                    UMax = uMax + uPad,
                    VMin = vMin - vPad,
                    VMax = vMax + vPad
                };
            }

            public Point2i Project(Vec3d point)
            {
                Point2d projected = ProjectDouble(point);
                return new Point2i(
                    (int)Math.Round(projected.X, MidpointRounding.AwayFromZero),
                    (int)Math.Round(projected.Y, MidpointRounding.AwayFromZero));
            }

            public Point2d ProjectDouble(Vec3d point)
            {
                double u = point.Get(View.UAxis) * View.USign;
                double v = point.Get(View.VAxis) * View.VSign;

                double x = Padding + (u - UMin) * Scale;
                double y = ImageSize - Padding - (v - VMin) * Scale;
                return new Point2d(x, y);
            }
        }

        private sealed class RgbaImage
        {
            private readonly byte[] _pixels;

            public RgbaImage(int width, int height)
            {
                Width = width;
                Height = height;
                _pixels = new byte[width * height * 4];
            }

            public int Width { get; private set; }
            public int Height { get; private set; }

            public void Clear(Rgba color)
            {
                for (int i = 0; i < _pixels.Length; i += 4)
                {
                    _pixels[i] = color.R;
                    _pixels[i + 1] = color.G;
                    _pixels[i + 2] = color.B;
                    _pixels[i + 3] = color.A;
                }
            }

            public void FillPolygon(List<Point2i> points, Rgba color)
            {
                if (points.Count < 3)
                    return;

                int minY = Math.Max(0, points.Min(p => p.Y));
                int maxY = Math.Min(Height - 1, points.Max(p => p.Y));
                var nodes = new List<int>();

                for (int y = minY; y <= maxY; y++)
                {
                    nodes.Clear();
                    int j = points.Count - 1;
                    for (int i = 0; i < points.Count; i++)
                    {
                        int yi = points[i].Y;
                        int yj = points[j].Y;
                        if ((yi < y && yj >= y) || (yj < y && yi >= y))
                        {
                            int xi = points[i].X;
                            int xj = points[j].X;
                            int x = xi + (int)Math.Round((double)(y - yi) / (yj - yi) * (xj - xi), MidpointRounding.AwayFromZero);
                            nodes.Add(x);
                        }

                        j = i;
                    }

                    nodes.Sort();
                    for (int i = 0; i + 1 < nodes.Count; i += 2)
                    {
                        int startX = Math.Max(0, nodes[i]);
                        int endX = Math.Min(Width - 1, nodes[i + 1]);
                        for (int x = startX; x <= endX; x++)
                            BlendPixel(x, y, color);
                    }
                }
            }

            public void DrawLine(int x0, int y0, int x1, int y1, Rgba color)
            {
                int dx = Math.Abs(x1 - x0);
                int sx = x0 < x1 ? 1 : -1;
                int dy = -Math.Abs(y1 - y0);
                int sy = y0 < y1 ? 1 : -1;
                int err = dx + dy;

                while (true)
                {
                    BlendPixel(x0, y0, color);
                    if (x0 == x1 && y0 == y1)
                        break;

                    int e2 = 2 * err;
                    if (e2 >= dy)
                    {
                        err += dy;
                        x0 += sx;
                    }

                    if (e2 <= dx)
                    {
                        err += dx;
                        y0 += sy;
                    }
                }
            }

            public void SavePng(string path)
            {
                using (var stream = File.Create(path))
                {
                    byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
                    stream.Write(signature, 0, signature.Length);

                    var ihdr = new MemoryStream();
                    WriteUInt32(ihdr, (uint)Width);
                    WriteUInt32(ihdr, (uint)Height);
                    ihdr.WriteByte(8);
                    ihdr.WriteByte(6);
                    ihdr.WriteByte(0);
                    ihdr.WriteByte(0);
                    ihdr.WriteByte(0);
                    WriteChunk(stream, "IHDR", ihdr.ToArray());

                    byte[] raw = BuildRawScanlines();
                    byte[] compressed;
                    using (var compressedStream = new MemoryStream())
                    {
                        using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
                            zlib.Write(raw, 0, raw.Length);

                        compressed = compressedStream.ToArray();
                    }

                    WriteChunk(stream, "IDAT", compressed);
                    WriteChunk(stream, "IEND", Array.Empty<byte>());
                }
            }

            private byte[] BuildRawScanlines()
            {
                int stride = Width * 4;
                byte[] raw = new byte[(stride + 1) * Height];
                for (int y = 0; y < Height; y++)
                {
                    int sourceOffset = y * stride;
                    int targetOffset = y * (stride + 1);
                    raw[targetOffset] = 0;
                    Buffer.BlockCopy(_pixels, sourceOffset, raw, targetOffset + 1, stride);
                }

                return raw;
            }

            private void BlendPixel(int x, int y, Rgba color)
            {
                if (x < 0 || x >= Width || y < 0 || y >= Height)
                    return;

                int offset = (y * Width + x) * 4;
                if (color.A == 255)
                {
                    _pixels[offset] = color.R;
                    _pixels[offset + 1] = color.G;
                    _pixels[offset + 2] = color.B;
                    _pixels[offset + 3] = color.A;
                    return;
                }

                double alpha = color.A / 255.0;
                double inverse = 1.0 - alpha;
                _pixels[offset] = ClampToByte(color.R * alpha + _pixels[offset] * inverse);
                _pixels[offset + 1] = ClampToByte(color.G * alpha + _pixels[offset + 1] * inverse);
                _pixels[offset + 2] = ClampToByte(color.B * alpha + _pixels[offset + 2] * inverse);
                _pixels[offset + 3] = 255;
            }

            private static void WriteChunk(Stream stream, string type, byte[] data)
            {
                byte[] typeBytes = Encoding.ASCII.GetBytes(type);
                WriteUInt32(stream, (uint)data.Length);
                stream.Write(typeBytes, 0, typeBytes.Length);
                stream.Write(data, 0, data.Length);

                uint crc = Crc32.Compute(typeBytes, data);
                WriteUInt32(stream, crc);
            }

            private static void WriteUInt32(Stream stream, uint value)
            {
                stream.WriteByte((byte)((value >> 24) & 0xff));
                stream.WriteByte((byte)((value >> 16) & 0xff));
                stream.WriteByte((byte)((value >> 8) & 0xff));
                stream.WriteByte((byte)(value & 0xff));
            }
        }

        private static class Crc32
        {
            private static readonly uint[] Table = BuildTable();

            public static uint Compute(byte[] typeBytes, byte[] data)
            {
                uint crc = 0xffffffff;
                crc = Update(crc, typeBytes);
                crc = Update(crc, data);
                return crc ^ 0xffffffff;
            }

            private static uint Update(uint crc, byte[] bytes)
            {
                for (int i = 0; i < bytes.Length; i++)
                    crc = Table[(crc ^ bytes[i]) & 0xff] ^ (crc >> 8);

                return crc;
            }

            private static uint[] BuildTable()
            {
                var table = new uint[256];
                for (uint n = 0; n < table.Length; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;

                    table[n] = c;
                }

                return table;
            }
        }

        private struct ViewSpec
        {
            public readonly string Name;
            public readonly int DepthAxis;
            public readonly int DepthSign;
            public readonly int UAxis;
            public readonly int USign;
            public readonly int VAxis;
            public readonly int VSign;

            public ViewSpec(string name, int depthAxis, int depthSign, int uAxis, int uSign, int vAxis, int vSign)
            {
                Name = name;
                DepthAxis = depthAxis;
                DepthSign = depthSign;
                UAxis = uAxis;
                USign = uSign;
                VAxis = vAxis;
                VSign = vSign;
            }
        }

        private struct ColorRgb
        {
            public readonly double R;
            public readonly double G;
            public readonly double B;

            public ColorRgb(double r, double g, double b)
            {
                R = r;
                G = g;
                B = b;
            }
        }

        private struct Rgba
        {
            public readonly byte R;
            public readonly byte G;
            public readonly byte B;
            public readonly byte A;

            public Rgba(byte r, byte g, byte b, byte a)
            {
                R = r;
                G = g;
                B = b;
                A = a;
            }
        }

        private struct Point2i
        {
            public readonly int X;
            public readonly int Y;

            public Point2i(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        private struct Point2d
        {
            public readonly double X;
            public readonly double Y;

            public Point2d(double x, double y)
            {
                X = x;
                Y = y;
            }
        }

        private struct Vec3d
        {
            public readonly double X;
            public readonly double Y;
            public readonly double Z;

            public Vec3d(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

            public double Get(int axis)
            {
                switch (axis)
                {
                    case 0: return X;
                    case 1: return Y;
                    case 2: return Z;
                    default: throw new ArgumentOutOfRangeException(nameof(axis));
                }
            }

            public Vec3d Normalized()
            {
                double length = Length;
                if (length <= 0.000000001)
                    return new Vec3d(0, 0, 0);

                return new Vec3d(X / length, Y / length, Z / length);
            }

            public static Vec3d Cross(Vec3d a, Vec3d b)
            {
                return new Vec3d(
                    a.Y * b.Z - a.Z * b.Y,
                    a.Z * b.X - a.X * b.Z,
                    a.X * b.Y - a.Y * b.X);
            }

            public static Vec3d operator -(Vec3d a, Vec3d b)
            {
                return new Vec3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            }
        }

        private struct Bounds
        {
            private bool _initialized;
            private Vec3d _min;
            private Vec3d _max;

            public Vec3d Min => _min;
            public Vec3d Max => _max;

            public void Include(Vec3d point)
            {
                if (!_initialized)
                {
                    _min = point;
                    _max = point;
                    _initialized = true;
                    return;
                }

                _min = new Vec3d(
                    Math.Min(_min.X, point.X),
                    Math.Min(_min.Y, point.Y),
                    Math.Min(_min.Z, point.Z));
                _max = new Vec3d(
                    Math.Max(_max.X, point.X),
                    Math.Max(_max.Y, point.Y),
                    Math.Max(_max.Z, point.Z));
            }
        }
    }
}
