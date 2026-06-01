using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace EasyEDA_Loader
{
    public sealed class StepProjectionOptions
    {
        public int ImageSizePixels { get; set; } = 1600;
        public int PaddingPixels { get; set; } = 80;
        public bool WriteMetadata { get; set; } = true;
        public List<string> ViewNames { get; } = new List<string>();
    }

    public sealed class StepProjectionReport
    {
        public string InputPath { get; internal set; }
        public int FaceCount { get; internal set; }
        public int EdgeCount { get; internal set; }
        public IReadOnlyList<string> OutputFiles { get; internal set; }
    }

    public sealed class StepProjectionDetectionRegion
    {
        public string InputPath { get; internal set; }
        public string ModelName { get; internal set; }
        public string ViewName { get; internal set; }
        public int RectangleX { get; internal set; }
        public int RectangleY { get; internal set; }
        public int RectangleWidth { get; internal set; }
        public int RectangleHeight { get; internal set; }
        public int EntityId { get; internal set; }
        public string Kind { get; internal set; }
    }

    public static class StepProjectionRenderer
    {
        private const double MarkedDetectionRegionPaddingRatio = 0.15;
        private const int MarkedDetectionRegionMinPaddingPixels = 3;
        private const int MarkedDetectionRegionMaxPaddingPixels = 9;
        private const int RenderSupersampling = 2;
        private const string F3DBackgroundColor = "#fafafa";

        private static readonly ViewSpec[] Views =
        {
            new ViewSpec("x_plus", 0, 1, 1, 1, 2, 1),
            new ViewSpec("x_minus", 0, -1, 1, -1, 2, 1),
            new ViewSpec("y_plus", 1, 1, 0, -1, 2, 1),
            new ViewSpec("y_minus", 1, -1, 0, 1, 2, 1),
            new ViewSpec("z_plus", 2, 1, 0, 1, 1, 1),
            new ViewSpec("z_minus", 2, -1, 0, -1, 1, 1)
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

            foreach (ViewSpec view in GetSelectedViews(options))
            {
                ProjectionTransform transform = ProjectionTransform.Create(drawingModel.Bounds, view, options);
                string outputPath = Path.Combine(outputDirectory, modelName + "__" + view.Name + ".png");
                RenderProjection(inputPath, drawingModel, view, transform, outputPath, options);
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

        public static byte[] ProjectSingleViewPng(byte[] stepData, string viewName, StepProjectionOptions options = null)
        {
            if (stepData == null)
                throw new ArgumentNullException(nameof(stepData));

            if (string.IsNullOrWhiteSpace(viewName))
                throw new ArgumentException("Projection view name is required.", nameof(viewName));

            options = CloneSingleViewOptions(options, viewName);

            string stepText = Encoding.Latin1.GetString(stepData);
            StepModel model = StepModel.Parse(stepText);
            model.BuildIndexes();
            var drawingModel = ProjectionModel.Build(model);
            ViewSpec view = GetSelectedViews(options)[0];
            ProjectionTransform transform = ProjectionTransform.Create(drawingModel.Bounds, view, options);

            return RenderProjectionImage(drawingModel, view, transform, options).ToPngBytes();
        }

        public static StepProjectionReport ProjectDetectionFile(
            string inputPath,
            string outputDirectory,
            StepWatermarkDetectionReport detectionReport,
            StepProjectionOptions options = null,
            IReadOnlyList<StepWatermarkMarkedRegion> markedRegions = null)
        {
            if (inputPath == null)
                throw new ArgumentNullException(nameof(inputPath));

            if (outputDirectory == null)
                throw new ArgumentNullException(nameof(outputDirectory));

            if (detectionReport == null)
                throw new ArgumentNullException(nameof(detectionReport));

            options = NormalizeOptions(options);
            Directory.CreateDirectory(outputDirectory);

            string stepText = Encoding.Latin1.GetString(File.ReadAllBytes(inputPath));
            StepModel model = StepModel.Parse(stepText);
            model.BuildIndexes();
            var drawingModel = ProjectionModel.Build(model);
            var highlights = BuildDetectionHighlights(model, detectionReport, drawingModel.Bounds);
            var compatibleMarkedRegions = GetCompatibleMarkedRegions(markedRegions);
            if (compatibleMarkedRegions.Count > 0)
                highlights = FilterHighlightsByMarkedRegions(highlights, compatibleMarkedRegions);

            var outputFiles = new List<string>();
            string modelName = Path.GetFileNameWithoutExtension(inputPath);
            DeleteExistingDetectionProjectionFiles(outputDirectory, modelName);

            foreach (ViewSpec view in GetSelectedViews(options))
            {
                var viewHighlights = highlights
                    .Where(highlight => string.Equals(highlight.ViewName, view.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (viewHighlights.Count == 0)
                    continue;

                ProjectionTransform transform = ProjectionTransform.Create(drawingModel.Bounds, view, options);
                string outputPath = Path.Combine(outputDirectory, modelName + "__" + view.Name + ".png");
                RenderProjection(inputPath, drawingModel, view, transform, outputPath, options, viewHighlights);
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

        private static void DeleteExistingDetectionProjectionFiles(string outputDirectory, string modelName)
        {
            foreach (string file in Directory.GetFiles(outputDirectory, modelName + "__*.png"))
                File.Delete(file);

            foreach (string file in Directory.GetFiles(outputDirectory, modelName + "__*.json"))
                File.Delete(file);
        }

        public static IReadOnlyList<string> ViewNames => Views.Select(v => v.Name).ToList();

        public static IReadOnlyList<StepProjectionDetectionRegion> ProjectDetectionRegions(
            string inputPath,
            StepWatermarkDetectionReport detectionReport,
            StepProjectionOptions options = null,
            IReadOnlyList<StepWatermarkMarkedRegion> markedRegions = null)
        {
            if (inputPath == null)
                throw new ArgumentNullException(nameof(inputPath));

            if (detectionReport == null)
                throw new ArgumentNullException(nameof(detectionReport));

            options = NormalizeOptions(options);

            string stepText = Encoding.Latin1.GetString(File.ReadAllBytes(inputPath));
            StepModel model = StepModel.Parse(stepText);
            model.BuildIndexes();
            var drawingModel = ProjectionModel.Build(model);
            var highlights = BuildDetectionHighlights(model, detectionReport, drawingModel.Bounds);
            var compatibleMarkedRegions = GetCompatibleMarkedRegions(markedRegions);
            if (compatibleMarkedRegions.Count > 0)
                highlights = FilterHighlightsByMarkedRegions(highlights, compatibleMarkedRegions);

            var result = new List<StepProjectionDetectionRegion>();
            string modelName = Path.GetFileNameWithoutExtension(inputPath);

            foreach (ViewSpec view in GetSelectedViews(options))
            {
                var viewHighlights = highlights
                    .Where(highlight => string.Equals(highlight.ViewName, view.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (viewHighlights.Count == 0)
                    continue;

                ProjectionTransform transform = ProjectionTransform.Create(drawingModel.Bounds, view, options);
                foreach (DetectionRectangle detectionRectangle in BuildDetectionRectangles(
                    options.ImageSizePixels,
                    options.ImageSizePixels,
                    transform,
                    viewHighlights))
                {
                    Rect2i rectangle = detectionRectangle.Rectangle;
                    result.Add(new StepProjectionDetectionRegion
                    {
                        InputPath = inputPath,
                        ModelName = modelName,
                        ViewName = view.Name,
                        RectangleX = rectangle.Left,
                        RectangleY = rectangle.Top,
                        RectangleWidth = rectangle.Width,
                        RectangleHeight = rectangle.Height,
                        EntityId = detectionRectangle.EntityId,
                        Kind = detectionRectangle.Kind
                    });
                }
            }

            return result;
        }

        private static void RenderProjection(
            string inputPath,
            ProjectionModel model,
            ViewSpec view,
            ProjectionTransform transform,
            string outputPath,
            StepProjectionOptions options,
            IReadOnlyList<ProjectionHighlight> highlights = null)
        {
            if (TryRenderWithF3D(inputPath, outputPath, view, options))
            {
                if (highlights != null && highlights.Count > 0)
                {
                    var renderedImage = RgbaImage.LoadPng(outputPath);
                    DrawDetectionHighlights(renderedImage, view, transform, highlights);
                    renderedImage.SavePng(outputPath);
                }

                return;
            }

            var image = RenderProjectionImage(model, view, transform, options, highlights);
            image.SavePng(outputPath);
        }

        private static RgbaImage RenderProjectionImage(
            ProjectionModel model,
            ViewSpec view,
            ProjectionTransform transform,
            StepProjectionOptions options,
            IReadOnlyList<ProjectionHighlight> highlights = null)
        {
            int scale = Math.Max(1, RenderSupersampling);
            StepProjectionOptions renderOptions = scale == 1
                ? options
                : new StepProjectionOptions
                {
                    ImageSizePixels = options.ImageSizePixels * scale,
                    PaddingPixels = options.PaddingPixels * scale,
                    WriteMetadata = options.WriteMetadata
                };
            ProjectionTransform renderTransform = scale == 1
                ? transform
                : ProjectionTransform.Create(model.Bounds, view, renderOptions);

            var image = new RgbaImage(renderOptions.ImageSizePixels, renderOptions.ImageSizePixels);
            image.Clear(new Rgba(250, 250, 250, 255));
            double[] zBuffer = CreateDepthBuffer(image.Width, image.Height);
            double lineDepthTolerance = Math.Max(0.000001, model.Bounds.Size.Get(view.DepthAxis) * 0.00001);

            var sortedFaces = model.Faces
                .Where(f => f.Points.Count >= 2)
                .OrderBy(f => f.Depth(view))
                .ThenBy(f => f.Id)
                .ToList();

            foreach (ProjectionFace face in sortedFaces)
            {
                Rgba fill = Shade(face.Color, face.Normal, view);
                var polygons = BuildFillPolygons(face, renderTransform);
                DepthPlane depthPlane = DepthPlane.Create(face, view);

                if (polygons.Count > 0)
                    image.FillPolygonsEvenOdd(
                        polygons,
                        fill,
                        zBuffer,
                        (x, y) => depthPlane.DepthAtPixel(x + 0.5, y + 0.5, renderTransform, view));

                Rgba line = ContrastLine(fill);
                foreach (ProjectionLoop loop in face.Loops)
                {
                    DrawLoop(image, loop.Points, renderTransform, view, depthPlane, zBuffer, line, lineDepthTolerance);
                }
            }

            if (scale > 1)
                image = image.Downsample(options.ImageSizePixels, options.ImageSizePixels);

            DrawDetectionHighlights(image, view, transform, highlights);
            return image;
        }

        private static bool TryRenderWithF3D(
            string inputPath,
            string outputPath,
            ViewSpec view,
            StepProjectionOptions options)
        {
            string executable = FindF3DConsoleExecutable();
            if (string.IsNullOrEmpty(executable))
                return false;

            if (!File.Exists(inputPath))
                return false;

            string extension = Path.GetExtension(inputPath);
            if (!string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase))
                return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            startInfo.ArgumentList.Add("--no-config");
            startInfo.ArgumentList.Add("--verbose=error");
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("--resolution");
            startInfo.ArgumentList.Add(options.ImageSizePixels.ToString(CultureInfo.InvariantCulture) + "," + options.ImageSizePixels.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--background-color");
            startInfo.ArgumentList.Add(F3DBackgroundColor);
            startInfo.ArgumentList.Add("--camera-orthographic");
            startInfo.ArgumentList.Add("--anti-aliasing=fxaa");
            startInfo.ArgumentList.Add("--ambient-occlusion");
            startInfo.ArgumentList.Add("--scalar-coloring");
            startInfo.ArgumentList.Add("--coloring-by-cells");
            startInfo.ArgumentList.Add("--coloring-array=Colors");
            startInfo.ArgumentList.Add("--coloring-component=-2");
            AddF3DViewArguments(startInfo.ArgumentList, view);
            startInfo.ArgumentList.Add(inputPath);

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                    return false;

                process.WaitForExit();
                if (process.ExitCode != 0)
                    return false;
            }

            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }

        private static void AddF3DViewArguments(System.Collections.ObjectModel.Collection<string> arguments, ViewSpec view)
        {
            switch (view.Name)
            {
                case "x_plus":
                    arguments.Add("--camera-direction=-1,0,0");
                    arguments.Add("--up=+Z");
                    break;
                case "x_minus":
                    arguments.Add("--camera-direction=1,0,0");
                    arguments.Add("--up=+Z");
                    break;
                case "y_plus":
                    arguments.Add("--camera-direction=0,-1,0");
                    arguments.Add("--up=+Z");
                    break;
                case "y_minus":
                    arguments.Add("--camera-direction=0,1,0");
                    arguments.Add("--up=+Z");
                    break;
                case "z_plus":
                    arguments.Add("--camera-direction=0,0,-1");
                    arguments.Add("--up=+Y");
                    break;
                case "z_minus":
                    arguments.Add("--camera-direction=0,0,1");
                    arguments.Add("--up=+Y");
                    break;
            }
        }

        private static string FindF3DConsoleExecutable()
        {
            string configuredPath = Environment.GetEnvironmentVariable("STEPCLEANER_F3D_CONSOLE");
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                return configuredPath;

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidates = new[]
            {
                Path.Combine(programFiles, "F3D", "bin", "f3d-console.exe"),
                "f3d-console.exe"
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static List<ProjectionHighlight> BuildDetectionHighlights(
            StepModel model,
            StepWatermarkDetectionReport detectionReport,
            Bounds modelBounds)
        {
            var result = new List<ProjectionHighlight>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (detectionReport.Regions != null && detectionReport.Regions.Count > 0)
            {
                foreach (var region in detectionReport.Regions)
                    AddDetectionHighlight(model, result, seen, region.EntityId, region.Kind, region.ViewName, modelBounds);

                return result;
            }

            foreach (int solidId in detectionReport.RemovableSolidIds ?? Array.Empty<int>())
                AddDetectionHighlight(model, result, seen, solidId, "solid", null, modelBounds);

            foreach (int faceId in detectionReport.EmbeddedFaceIds ?? Array.Empty<int>())
                AddDetectionHighlight(model, result, seen, faceId, "face", null, modelBounds);

            foreach (int faceId in detectionReport.CoplanarFaceIds ?? Array.Empty<int>())
                AddDetectionHighlight(model, result, seen, faceId, "face", null, modelBounds);

            foreach (var loop in detectionReport.HostLoops ?? Array.Empty<StepWatermarkHostLoopDetection>())
                AddDetectionHighlight(model, result, seen, loop.BoundId, "loop", null, modelBounds);

            return result;
        }

        private static void AddDetectionHighlight(
            StepModel model,
            List<ProjectionHighlight> result,
            HashSet<string> seen,
            int entityId,
            string kind,
            string viewName,
            Bounds modelBounds)
        {
            string key = kind + "|" + entityId.ToString(CultureInfo.InvariantCulture);
            if (!seen.Add(key))
                return;

            bool includeSurface = model.GetTypeName(entityId) != "ADVANCED_FACE";
            List<Vec3d> points = model.GetReferencedPoints(entityId, includeSurface);
            if (points.Count == 0)
                return;

            Bounds bounds = new Bounds();
            foreach (Vec3d point in points)
                bounds.Include(point);

            result.Add(new ProjectionHighlight
            {
                EntityId = entityId,
                Kind = kind,
                Bounds = bounds,
                ViewName = string.IsNullOrEmpty(viewName)
                    ? GetDetectedSideViewName(bounds, modelBounds)
                    : viewName
            });
        }

        private static List<ProjectionHighlight> FilterHighlightsByMarkedRegions(
            List<ProjectionHighlight> highlights,
            IReadOnlyList<StepWatermarkMarkedRegion> markedRegions)
        {
            var validRegions = markedRegions
                .Where(HasMarkedRegionArea)
                .ToList();
            if (validRegions.Count == 0)
                return new List<ProjectionHighlight>();

            var result = new List<ProjectionHighlight>();
            foreach (StepWatermarkMarkedRegion region in validRegions)
            {
                ProjectionHighlight matchedHighlight = null;
                foreach (ProjectionHighlight highlight in highlights)
                {
                    if (!BoundsInsideMarkedRegion(highlight.Bounds, region))
                        continue;

                    matchedHighlight = highlight;
                    break;
                }

                if (matchedHighlight == null)
                    continue;

                result.Add(new ProjectionHighlight
                {
                    EntityId = matchedHighlight.EntityId,
                    Kind = matchedHighlight.Kind,
                    Bounds = matchedHighlight.Bounds,
                    ViewName = region.ViewName,
                    MarkedRegion = region
                });
            }

            return result;
        }

        private static List<StepWatermarkMarkedRegion> GetCompatibleMarkedRegions(
            IReadOnlyList<StepWatermarkMarkedRegion> markedRegions)
        {
            if (markedRegions == null || markedRegions.Count == 0)
                return new List<StepWatermarkMarkedRegion>();

            var result = new List<StepWatermarkMarkedRegion>();
            foreach (var region in markedRegions)
            {
                if (!HasMarkedRegionArea(region))
                    continue;

                ViewSpec view = Views.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, region.ViewName, StringComparison.OrdinalIgnoreCase));
                if (view.Name == null)
                    continue;

                if (region.UAxis != view.UAxis ||
                    region.USign != view.USign ||
                    region.VAxis != view.VAxis ||
                    region.VSign != view.VSign ||
                    region.DepthAxis != view.DepthAxis ||
                    region.DepthSign != view.DepthSign)
                    continue;

                result.Add(region);
            }

            return result;
        }

        private static bool HasMarkedRegionArea(StepWatermarkMarkedRegion region)
        {
            return region != null &&
                region.ModelUMax > region.ModelUMin &&
                region.ModelVMax > region.ModelVMin &&
                region.ScalePixelsPerModelUnit > 0.0;
        }

        private static bool BoundsInsideMarkedRegion(Bounds bounds, StepWatermarkMarkedRegion region)
        {
            double padding = region.ScalePixelsPerModelUnit > 0.0
                ? 2.0 / region.ScalePixelsPerModelUnit
                : 0.0;

            double u0 = bounds.Min.Get(region.UAxis) * region.USign;
            double u1 = bounds.Max.Get(region.UAxis) * region.USign;
            double v0 = bounds.Min.Get(region.VAxis) * region.VSign;
            double v1 = bounds.Max.Get(region.VAxis) * region.VSign;
            double uMin = Math.Min(u0, u1);
            double uMax = Math.Max(u0, u1);
            double vMin = Math.Min(v0, v1);
            double vMax = Math.Max(v0, v1);

            double candidateWidth = Math.Max(uMax - uMin, 0.0);
            double candidateHeight = Math.Max(vMax - vMin, 0.0);
            double centerU = (uMin + uMax) / 2.0;
            double centerV = (vMin + vMax) / 2.0;

            if (candidateWidth <= 0.0000001 || candidateHeight <= 0.0000001)
            {
                return centerU >= region.ModelUMin - padding &&
                    centerU <= region.ModelUMax + padding &&
                    centerV >= region.ModelVMin - padding &&
                    centerV <= region.ModelVMax + padding;
            }

            if (uMin >= region.ModelUMin - padding &&
                uMax <= region.ModelUMax + padding &&
                vMin >= region.ModelVMin - padding &&
                vMax <= region.ModelVMax + padding)
                return true;

            double intersectionUMin = Math.Max(uMin, region.ModelUMin - padding);
            double intersectionUMax = Math.Min(uMax, region.ModelUMax + padding);
            double intersectionVMin = Math.Max(vMin, region.ModelVMin - padding);
            double intersectionVMax = Math.Min(vMax, region.ModelVMax + padding);
            double intersectionWidth = Math.Max(0.0, intersectionUMax - intersectionUMin);
            double intersectionHeight = Math.Max(0.0, intersectionVMax - intersectionVMin);
            if (intersectionWidth <= 0.0 || intersectionHeight <= 0.0)
                return false;

            bool centerInside =
                centerU >= region.ModelUMin - padding &&
                centerU <= region.ModelUMax + padding &&
                centerV >= region.ModelVMin - padding &&
                centerV <= region.ModelVMax + padding;
            if (centerInside)
                return true;

            double candidateArea = Math.Max(candidateWidth * candidateHeight, 0.0000000001);
            double regionArea = Math.Max(
                (region.ModelUMax - region.ModelUMin) * (region.ModelVMax - region.ModelVMin),
                0.0000000001);
            double intersectionArea = intersectionWidth * intersectionHeight;
            return intersectionArea / candidateArea >= 0.05 ||
                intersectionArea / regionArea >= 0.05;
        }

        private static string GetDetectedSideViewName(Bounds bounds, Bounds modelBounds)
        {
            Vec3d size = bounds.Size;
            int axis = 0;
            double best = Math.Abs(size.X);
            if (Math.Abs(size.Y) < best)
            {
                axis = 1;
                best = Math.Abs(size.Y);
            }

            if (Math.Abs(size.Z) < best)
            {
                axis = 2;
            }

            double center = (bounds.Min.Get(axis) + bounds.Max.Get(axis)) / 2.0;
            double modelCenter = (modelBounds.Min.Get(axis) + modelBounds.Max.Get(axis)) / 2.0;
            int sign = center >= modelCenter ? 1 : -1;

            switch (axis)
            {
                case 0: return sign > 0 ? "x_plus" : "x_minus";
                case 1: return sign > 0 ? "y_plus" : "y_minus";
                case 2: return sign > 0 ? "z_plus" : "z_minus";
                default: return "z_plus";
            }
        }

        private static void DrawDetectionHighlights(
            RgbaImage image,
            ViewSpec view,
            ProjectionTransform transform,
            IReadOnlyList<ProjectionHighlight> highlights)
        {
            foreach (DetectionRectangle detectionRectangle in BuildDetectionRectangles(
                image.Width,
                image.Height,
                transform,
                highlights))
            {
                Rect2i rectangle = detectionRectangle.Rectangle;
                image.FillRectangle(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom, new Rgba(255, 0, 0, 35));
                image.DrawRectangle(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom, new Rgba(255, 0, 0, 255), 4);
            }
        }

        private static List<DetectionRectangle> BuildDetectionRectangles(
            int imageWidth,
            int imageHeight,
            ProjectionTransform transform,
            IReadOnlyList<ProjectionHighlight> highlights)
        {
            var rectangles = new List<DetectionRectangle>();
            if (highlights == null || highlights.Count == 0)
                return rectangles;

            foreach (ProjectionHighlight highlight in highlights)
            {
                Rect2i rectangle = highlight.MarkedRegion != null
                    ? new Rect2i(
                        highlight.MarkedRegion.RectangleX,
                        highlight.MarkedRegion.RectangleY,
                        highlight.MarkedRegion.RectangleX + highlight.MarkedRegion.RectangleWidth - 1,
                        highlight.MarkedRegion.RectangleY + highlight.MarkedRegion.RectangleHeight - 1)
                    : transform.ProjectBounds(highlight.Bounds, 0.0);

                if (!rectangle.Intersects(0, 0, imageWidth - 1, imageHeight - 1))
                    continue;

                if (rectangle.Width < 4)
                    rectangle = rectangle.Expand((4 - rectangle.Width) / 2 + 1, 0);

                if (rectangle.Height < 4)
                    rectangle = rectangle.Expand(0, (4 - rectangle.Height) / 2 + 1);

                rectangles.Add(new DetectionRectangle(
                    rectangle.Clamp(0, 0, imageWidth - 1, imageHeight - 1),
                    highlight.MarkedRegion,
                    highlight.EntityId,
                    highlight.Kind));
            }

            var result = new List<DetectionRectangle>();
            foreach (DetectionRectangle detectionRectangle in ClusterRectangles(rectangles, 10))
            {
                Rect2i rectangle = detectionRectangle.Rectangle;
                if (detectionRectangle.MarkedRegion != null)
                {
                    int padding = GetMarkedDetectionRegionPaddingPixels(detectionRectangle.MarkedRegion);
                    rectangle = rectangle
                        .Expand(padding, padding)
                        .Clamp(
                            detectionRectangle.MarkedRegion.RectangleX,
                            detectionRectangle.MarkedRegion.RectangleY,
                            detectionRectangle.MarkedRegion.RectangleX + detectionRectangle.MarkedRegion.RectangleWidth - 1,
                            detectionRectangle.MarkedRegion.RectangleY + detectionRectangle.MarkedRegion.RectangleHeight - 1);
                }

                result.Add(new DetectionRectangle(
                    rectangle.Clamp(0, 0, imageWidth - 1, imageHeight - 1),
                    detectionRectangle.MarkedRegion,
                    detectionRectangle.EntityId,
                    detectionRectangle.Kind));
            }

            return result;
        }

        private static int GetMarkedDetectionRegionPaddingPixels(StepWatermarkMarkedRegion region)
        {
            int shortestSide = Math.Min(region.RectangleWidth, region.RectangleHeight);
            if (shortestSide <= 0)
                return 0;

            int padding = (int)Math.Round(shortestSide * MarkedDetectionRegionPaddingRatio, MidpointRounding.AwayFromZero);
            return Math.Min(
                MarkedDetectionRegionMaxPaddingPixels,
                Math.Max(MarkedDetectionRegionMinPaddingPixels, padding));
        }

        private static List<DetectionRectangle> ClusterRectangles(List<DetectionRectangle> rectangles, int gap)
        {
            var result = new List<DetectionRectangle>();
            var visited = new bool[rectangles.Count];

            for (int i = 0; i < rectangles.Count; i++)
            {
                if (visited[i])
                    continue;

                DetectionRectangle cluster = rectangles[i];
                visited[i] = true;

                bool changed;
                do
                {
                    changed = false;
                    for (int j = 0; j < rectangles.Count; j++)
                    {
                        if (visited[j])
                            continue;

                        if (!ReferenceEquals(cluster.MarkedRegion, rectangles[j].MarkedRegion))
                            continue;

                        if (!cluster.Rectangle.Expand(gap, gap).Overlaps(rectangles[j].Rectangle))
                            continue;

                        cluster = new DetectionRectangle(
                            cluster.Rectangle.Union(rectangles[j].Rectangle),
                            cluster.MarkedRegion,
                            cluster.EntityId,
                            cluster.Kind);
                        visited[j] = true;
                        changed = true;
                    }
                }
                while (changed);

                result.Add(cluster);
            }

            return result;
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

        private static void DrawLoop(
            RgbaImage image,
            List<Vec3d> points,
            ProjectionTransform transform,
            ViewSpec view,
            DepthPlane depthPlane,
            double[] zBuffer,
            Rgba color,
            double depthTolerance)
        {
            if (points.Count < 2)
                return;

            Point2i previous = transform.Project(points[0]);
            for (int i = 1; i < points.Count; i++)
            {
                Point2i current = transform.Project(points[i]);
                DrawLineDepthTested(image, previous.X, previous.Y, current.X, current.Y, transform, view, depthPlane, zBuffer, color, depthTolerance);
                previous = current;
            }

            Point2i first = transform.Project(points[0]);
            if (first.X != previous.X || first.Y != previous.Y)
                DrawLineDepthTested(image, previous.X, previous.Y, first.X, first.Y, transform, view, depthPlane, zBuffer, color, depthTolerance);
        }

        private static void DrawLineDepthTested(
            RgbaImage image,
            int x0,
            int y0,
            int x1,
            int y1,
            ProjectionTransform transform,
            ViewSpec view,
            DepthPlane depthPlane,
            double[] zBuffer,
            Rgba color,
            double depthTolerance)
        {
            int dx = Math.Abs(x1 - x0);
            int sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0);
            int sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            while (true)
            {
                DrawDepthTestedPixel(image, x0, y0, transform, view, depthPlane, zBuffer, color, depthTolerance);
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

        private static void DrawDepthTestedPixel(
            RgbaImage image,
            int x,
            int y,
            ProjectionTransform transform,
            ViewSpec view,
            DepthPlane depthPlane,
            double[] zBuffer,
            Rgba color,
            double depthTolerance)
        {
            if (x < 0 || x >= image.Width || y < 0 || y >= image.Height)
                return;

            int offset = y * image.Width + x;
            double depth = depthPlane.DepthAtPixel(x + 0.5, y + 0.5, transform, view);
            if (depth + depthTolerance < zBuffer[offset])
                return;

            image.BlendPixel(x, y, color);
        }

        private static double[] CreateDepthBuffer(int width, int height)
        {
            var zBuffer = new double[width * height];
            for (int i = 0; i < zBuffer.Length; i++)
                zBuffer[i] = double.NegativeInfinity;

            return zBuffer;
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

        private static List<List<Point2d>> BuildFillPolygons(ProjectionFace face, ProjectionTransform transform)
        {
            var polygons = new List<List<Point2d>>();
            foreach (ProjectionLoop loop in face.Loops)
            {
                var polygon = BuildLoopPolygon(loop, transform);
                if (polygon.Count >= 3)
                    polygons.Add(polygon);
            }

            if (polygons.Count > 0)
                return polygons;

            var points = new List<Point2d>();
            foreach (Vec3d point in face.Points)
                AddDistinctPoint(points, transform.ProjectDouble(point));

            if (points.Count < 3)
                return polygons;

            List<Point2d> hull = ConvexHull(points);
            if (hull.Count < 3 || Math.Abs(Area(hull)) < 0.5)
                return polygons;

            polygons.Add(hull);
            return polygons;
        }

        private static List<Point2d> BuildLoopPolygon(ProjectionLoop loop, ProjectionTransform transform)
        {
            var polygon = new List<Point2d>();
            foreach (Vec3d point in loop.Points)
            {
                Point2d projected = transform.ProjectDouble(point);
                if (polygon.Count == 0 ||
                    Math.Abs(polygon[polygon.Count - 1].X - projected.X) >= 0.001 ||
                    Math.Abs(polygon[polygon.Count - 1].Y - projected.Y) >= 0.001)
                    polygon.Add(projected);
            }

            while (polygon.Count > 1 &&
                Math.Abs(polygon[0].X - polygon[polygon.Count - 1].X) < 0.001 &&
                Math.Abs(polygon[0].Y - polygon[polygon.Count - 1].Y) < 0.001)
                polygon.RemoveAt(polygon.Count - 1);

            if (polygon.Count < 3 || Math.Abs(Area(polygon)) < 0.5)
                return new List<Point2d>();

            return polygon;
        }

        private static void AddDistinctPoint(List<Point2d> points, Point2d point)
        {
            foreach (Point2d existing in points)
            {
                if (Math.Abs(existing.X - point.X) < 0.25 && Math.Abs(existing.Y - point.Y) < 0.25)
                    return;
            }

            points.Add(point);
        }

        private static List<Point2d> ConvexHull(List<Point2d> points)
        {
            var sorted = points
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .ToList();

            var lower = new List<Point2d>();
            foreach (Point2d point in sorted)
            {
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], point) <= 0.0)
                    lower.RemoveAt(lower.Count - 1);

                lower.Add(point);
            }

            var upper = new List<Point2d>();
            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                Point2d point = sorted[i];
                while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], point) <= 0.0)
                    upper.RemoveAt(upper.Count - 1);

                upper.Add(point);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static double Cross(Point2d origin, Point2d a, Point2d b)
        {
            return (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);
        }

        private static double Area(List<Point2d> points)
        {
            double area = 0.0;
            Point2d previous = points[points.Count - 1];
            foreach (Point2d current in points)
            {
                area += previous.X * current.Y - current.X * previous.Y;
                previous = current;
            }

            return area / 2.0;
        }

        private static double Area(List<Point2i> points)
        {
            double area = 0.0;
            Point2i previous = points[points.Count - 1];
            foreach (Point2i current in points)
            {
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

            double factor = 0.58 + 0.30 * Math.Min(1.0, viewAlignment);
            return new Rgba(
                ClampToByte(color.R * 255.0 * factor),
                ClampToByte(color.G * 255.0 * factor),
                ClampToByte(color.B * 255.0 * factor),
                255);
        }

        private static Rgba ContrastLine(Rgba fill)
        {
            double luminance = (0.2126 * fill.R + 0.7152 * fill.G + 0.0722 * fill.B) / 255.0;
            if (luminance <= 0.18)
                return new Rgba(10, 10, 10, 60);

            return luminance >= 0.55
                ? new Rgba(25, 25, 25, 120)
                : new Rgba(20, 20, 20, 85);
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

            GetSelectedViews(options);
            return options;
        }

        private static StepProjectionOptions CloneSingleViewOptions(StepProjectionOptions options, string viewName)
        {
            var clone = new StepProjectionOptions
            {
                ImageSizePixels = options?.ImageSizePixels ?? 1600,
                PaddingPixels = options?.PaddingPixels ?? 80,
                WriteMetadata = false
            };
            clone.ViewNames.Add(viewName);
            return NormalizeOptions(clone);
        }

        private static IReadOnlyList<ViewSpec> GetSelectedViews(StepProjectionOptions options)
        {
            if (options == null || options.ViewNames.Count == 0)
                return Views;

            var selected = new List<ViewSpec>();
            foreach (string viewName in options.ViewNames)
            {
                if (string.IsNullOrWhiteSpace(viewName))
                    continue;

                ViewSpec view = Views.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, viewName, StringComparison.OrdinalIgnoreCase));
                if (view.Name == null)
                    throw new ArgumentException("Unknown projection view name: " + viewName, nameof(options.ViewNames));

                if (!selected.Any(candidate => string.Equals(candidate.Name, view.Name, StringComparison.OrdinalIgnoreCase)))
                    selected.Add(view);
            }

            return selected.Count == 0 ? Views : selected;
        }

        private sealed class ProjectionModel
        {
            public List<ProjectionFace> Faces { get; private set; }
            public Bounds Bounds { get; private set; }
            public int EdgeCount { get; private set; }

            public static ProjectionModel Build(StepModel step)
            {
                var colors = BuildTargetColors(step);
                var drawableFaceIds = step.GetDrawableAdvancedFaceIds();
                var faces = new List<ProjectionFace>();
                var bounds = new Bounds();
                bool hasBounds = false;
                int edgeCount = 0;

                foreach (StepEntity entity in step.Entities.Values)
                {
                    if (entity.Type != "ADVANCED_FACE")
                        continue;

                    if (drawableFaceIds.Count > 0 && !drawableFaceIds.Contains(entity.Id))
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

                Vec3d startPoint = default;
                Vec3d endPoint = default;
                bool hasStartPoint = vertexPointIds.Count > 0 && step.TryGetVertexPoint(vertexPointIds[0], out startPoint);
                bool hasEndPoint = vertexPointIds.Count > 1 && step.TryGetVertexPoint(vertexPointIds[1], out endPoint);

                if (hasStartPoint)
                    edgePoints.Add(startPoint);

                int circleId = edgeCurve.References.FirstOrDefault(id => step.GetTypeName(id) == "CIRCLE");
                if (circleId != 0 && step.TryGetCircleArc(circleId, hasStartPoint, startPoint, hasEndPoint, endPoint, ParseLastLogical(edgeCurve.Definition), out List<Vec3d> circlePoints))
                {
                    AppendPolyline(edgePoints, circlePoints);
                }
                else
                {
                    int curveId = edgeCurve.References.FirstOrDefault(id => step.IsSplineCurve(id));
                    if (curveId != 0 && step.TryGetSplineCurveSamples(curveId, hasStartPoint, startPoint, hasEndPoint, endPoint, out List<Vec3d> splinePoints))
                        AppendPolyline(edgePoints, splinePoints);
                }

                if (hasEndPoint)
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

        private sealed class ProjectionHighlight
        {
            public int EntityId { get; set; }
            public string Kind { get; set; }
            public string ViewName { get; set; }
            public Bounds Bounds { get; set; }
            public StepWatermarkMarkedRegion MarkedRegion { get; set; }
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
            private static readonly Regex DirectionRegex = new Regex(
                @"DIRECTION\s*\(\s*(?:'[^']*'|\$)\s*,\s*\(([^)]*)\)",
                RegexOptions.Compiled);
            private static readonly Regex CircleRegex = new Regex(
                @"CIRCLE\s*\(\s*(?:'[^']*'|\$)\s*,\s*#\d+\s*,\s*([-+0-9.Ee]+)\s*\)",
                RegexOptions.Compiled);
            private static readonly Regex SplineDegreeWithKnotsRegex = new Regex(
                @"B_SPLINE_CURVE_WITH_KNOTS\s*\(\s*(?:'[^']*'|\$)\s*,\s*(\d+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
            private static readonly Regex SplineDegreeRegex = new Regex(
                @"B_SPLINE_CURVE\s*\(\s*(\d+)\s*,",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
            private static readonly Regex RationalWeightsRegex = new Regex(
                @"RATIONAL_B_SPLINE_CURVE\s*\(\s*\(([^)]*)\)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

            public HashSet<int> GetDrawableAdvancedFaceIds()
            {
                var result = new HashSet<int>();
                var representationRoots = Entities.Values
                    .Where(entity => IsShapeRepresentationType(entity.Type))
                    .ToList();

                foreach (StepEntity entity in representationRoots)
                {
                    foreach (int id in TraverseReferences(entity.Id))
                    {
                        if (GetTypeName(id) == "ADVANCED_FACE")
                            result.Add(id);
                    }
                }

                if (result.Count > 0)
                    return result;

                foreach (StepEntity entity in Entities.Values)
                {
                    if (entity.Type != "MANIFOLD_SOLID_BREP" &&
                        entity.Type != "SHELL_BASED_SURFACE_MODEL")
                        continue;

                    foreach (int id in TraverseReferences(entity.Id))
                    {
                        if (GetTypeName(id) == "ADVANCED_FACE")
                            result.Add(id);
                    }
                }

                return result;
            }

            private static bool IsShapeRepresentationType(string type)
            {
                return type == "ADVANCED_BREP_SHAPE_REPRESENTATION" ||
                    type == "SHAPE_REPRESENTATION" ||
                    type == "MANIFOLD_SURFACE_SHAPE_REPRESENTATION" ||
                    type == "GEOMETRICALLY_BOUNDED_SURFACE_SHAPE_REPRESENTATION" ||
                    type == "FACETED_BREP_SHAPE_REPRESENTATION" ||
                    type == "SHELL_BASED_SURFACE_MODEL";
            }

            public bool TryGetVertexPoint(int vertexId, out Vec3d point)
            {
                point = default;
                if (!Entities.TryGetValue(vertexId, out StepEntity vertex) || vertex.Type != "VERTEX_POINT")
                    return false;

                int pointId = vertex.References.FirstOrDefault(id => GetTypeName(id) == "CARTESIAN_POINT");
                return pointId != 0 && TryGetPoint(pointId, out point);
            }

            public bool TryGetCircleArc(
                int circleId,
                bool hasStartPoint,
                Vec3d startPoint,
                bool hasEndPoint,
                Vec3d endPoint,
                bool edgeCurveSameSense,
                out List<Vec3d> points)
            {
                points = new List<Vec3d>();
                if (!TryGetCircle(circleId, out CircleInfo circle))
                    return false;

                bool fullCircle = !hasStartPoint || !hasEndPoint || AlmostSame(startPoint, endPoint);
                double startAngle = hasStartPoint ? circle.AngleOf(startPoint) : 0.0;
                double delta;

                if (fullCircle)
                {
                    delta = edgeCurveSameSense ? Math.PI * 2.0 : -Math.PI * 2.0;
                }
                else
                {
                    double endAngle = circle.AngleOf(endPoint);
                    delta = endAngle - startAngle;
                    if (edgeCurveSameSense)
                    {
                        while (delta <= 0.000000001)
                            delta += Math.PI * 2.0;
                    }
                    else
                    {
                        while (delta >= -0.000000001)
                            delta -= Math.PI * 2.0;
                    }
                }

                int steps = Math.Max(24, Math.Min(720, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 180.0))));
                for (int i = 0; i <= steps; i++)
                {
                    double t = startAngle + delta * i / steps;
                    points.Add(circle.PointAt(t));
                }

                return points.Count > 1;
            }

            public bool IsSplineCurve(int id)
            {
                if (!Entities.TryGetValue(id, out StepEntity entity))
                    return false;

                return entity.Type.IndexOf("B_SPLINE_CURVE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    entity.Definition.IndexOf("B_SPLINE_CURVE", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            public bool TryGetSplineCurveSamples(
                int curveId,
                bool hasStartPoint,
                Vec3d startPoint,
                bool hasEndPoint,
                Vec3d endPoint,
                out List<Vec3d> points)
            {
                points = new List<Vec3d>();
                if (!Entities.TryGetValue(curveId, out StepEntity entity) || !IsSplineCurve(curveId))
                    return false;

                if (!TryParseSplineDegree(entity.Definition, out int degree) || degree < 1)
                    return false;

                var controlPoints = new List<Vec3d>();
                foreach (int pointId in entity.References)
                {
                    if (GetTypeName(pointId) == "CARTESIAN_POINT" && TryGetPoint(pointId, out Vec3d point))
                        controlPoints.Add(point);
                }

                if (controlPoints.Count < degree + 1)
                    return false;

                List<double> weights = ParseSplineWeights(entity.Definition, controlPoints.Count);
                if (!TryParseSplineKnotVector(entity.Definition, controlPoints.Count, degree, out List<double> knots))
                    knots = BuildOpenUniformKnotVector(controlPoints.Count, degree);

                if (!IsValidKnotVector(knots, controlPoints.Count, degree))
                    return false;

                double startParameter = knots[degree];
                double endParameter = knots[controlPoints.Count];
                if (endParameter - startParameter <= 0.000000000001)
                    return false;

                int samples = Math.Max(24, Math.Min(720, controlPoints.Count * 24));
                for (int i = 0; i <= samples; i++)
                {
                    double parameter = i == samples
                        ? endParameter
                        : startParameter + (endParameter - startParameter) * i / samples;
                    if (TryEvaluateRationalSpline(controlPoints, weights, knots, degree, parameter, out Vec3d point))
                        points.Add(point);
                }

                OrientSamplesToVertices(points, hasStartPoint, startPoint, hasEndPoint, endPoint);
                return points.Count > 1;
            }

            private static bool TryParseSplineDegree(string definition, out int degree)
            {
                Match match = SplineDegreeWithKnotsRegex.Match(definition);
                if (!match.Success)
                    match = SplineDegreeRegex.Match(definition);

                if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out degree))
                    return true;

                degree = 0;
                return false;
            }

            private static bool TryParseSplineKnotVector(
                string definition,
                int controlPointCount,
                int degree,
                out List<double> knots)
            {
                knots = null;
                int knotSectionStart = definition.IndexOf("B_SPLINE_CURVE_WITH_KNOTS", StringComparison.OrdinalIgnoreCase);
                if (knotSectionStart < 0)
                    return false;

                string knotSection = definition.Substring(knotSectionStart);
                int rationalSectionStart = knotSection.IndexOf("RATIONAL_B_SPLINE_CURVE", StringComparison.OrdinalIgnoreCase);
                if (rationalSectionStart >= 0)
                    knotSection = knotSection.Substring(0, rationalSectionStart);

                List<List<double>> numericLists = ExtractNumericLists(knotSection);
                if (numericLists.Count < 2)
                    return false;

                List<double> multiplicities = numericLists[0];
                List<double> uniqueKnots = numericLists[1];
                if (multiplicities.Count != uniqueKnots.Count)
                    return false;

                var expanded = new List<double>();
                for (int i = 0; i < multiplicities.Count; i++)
                {
                    int count = (int)Math.Round(multiplicities[i], MidpointRounding.AwayFromZero);
                    if (count < 1)
                        return false;

                    for (int j = 0; j < count; j++)
                        expanded.Add(uniqueKnots[i]);
                }

                if (!IsValidKnotVector(expanded, controlPointCount, degree))
                    return false;

                knots = expanded;
                return true;
            }

            private static List<double> ParseSplineWeights(string definition, int controlPointCount)
            {
                var weights = Enumerable.Repeat(1.0, controlPointCount).ToList();
                Match match = RationalWeightsRegex.Match(definition);
                if (!match.Success)
                    return weights;

                if (!TryParseNumberList(match.Groups[1].Value, out List<double> parsed) || parsed.Count != controlPointCount)
                    return weights;

                for (int i = 0; i < parsed.Count; i++)
                    weights[i] = Math.Abs(parsed[i]) > 0.000000000001 ? parsed[i] : 0.000000000001;

                return weights;
            }

            private static List<double> BuildOpenUniformKnotVector(int controlPointCount, int degree)
            {
                int knotCount = controlPointCount + degree + 1;
                var knots = new List<double>(knotCount);
                int interiorDenominator = controlPointCount - degree;
                for (int i = 0; i < knotCount; i++)
                {
                    if (i <= degree)
                        knots.Add(0.0);
                    else if (i >= controlPointCount)
                        knots.Add(1.0);
                    else
                        knots.Add((double)(i - degree) / Math.Max(1, interiorDenominator));
                }

                return knots;
            }

            private static bool IsValidKnotVector(List<double> knots, int controlPointCount, int degree)
            {
                if (knots == null || knots.Count != controlPointCount + degree + 1)
                    return false;

                for (int i = 1; i < knots.Count; i++)
                {
                    if (knots[i] + 0.000000000001 < knots[i - 1])
                        return false;
                }

                return knots[controlPointCount] - knots[degree] > 0.000000000001;
            }

            private static bool TryEvaluateRationalSpline(
                List<Vec3d> controlPoints,
                List<double> weights,
                List<double> knots,
                int degree,
                double parameter,
                out Vec3d point)
            {
                point = default;
                int span = FindKnotSpan(controlPoints.Count, degree, knots, parameter);
                if (span < degree || span >= controlPoints.Count)
                    return false;

                var workPoints = new Vec3d[degree + 1];
                var workWeights = new double[degree + 1];
                for (int j = 0; j <= degree; j++)
                {
                    int controlIndex = span - degree + j;
                    if (controlIndex < 0 || controlIndex >= controlPoints.Count)
                        return false;

                    double weight = weights[controlIndex];
                    workPoints[j] = controlPoints[controlIndex] * weight;
                    workWeights[j] = weight;
                }

                for (int r = 1; r <= degree; r++)
                {
                    for (int j = degree; j >= r; j--)
                    {
                        int knotIndex = span - degree + j;
                        double denominator = knots[knotIndex + degree - r + 1] - knots[knotIndex];
                        double alpha = Math.Abs(denominator) <= 0.000000000001
                            ? 0.0
                            : (parameter - knots[knotIndex]) / denominator;
                        alpha = Math.Max(0.0, Math.Min(1.0, alpha));

                        workPoints[j] = workPoints[j - 1] * (1.0 - alpha) + workPoints[j] * alpha;
                        workWeights[j] = workWeights[j - 1] * (1.0 - alpha) + workWeights[j] * alpha;
                    }
                }

                if (Math.Abs(workWeights[degree]) <= 0.000000000001)
                    return false;

                point = workPoints[degree] * (1.0 / workWeights[degree]);
                return true;
            }

            private static int FindKnotSpan(int controlPointCount, int degree, List<double> knots, double parameter)
            {
                int lastControlIndex = controlPointCount - 1;
                if (parameter >= knots[lastControlIndex + 1] - 0.000000000001)
                    return lastControlIndex;

                if (parameter <= knots[degree] + 0.000000000001)
                    return degree;

                int low = degree;
                int high = lastControlIndex + 1;
                int middle = (low + high) / 2;
                while (parameter < knots[middle] || parameter >= knots[middle + 1])
                {
                    if (parameter < knots[middle])
                        high = middle;
                    else
                        low = middle;

                    middle = (low + high) / 2;
                }

                return middle;
            }

            private static void OrientSamplesToVertices(
                List<Vec3d> points,
                bool hasStartPoint,
                Vec3d startPoint,
                bool hasEndPoint,
                Vec3d endPoint)
            {
                if (points.Count < 2 || (!hasStartPoint && !hasEndPoint))
                    return;

                Vec3d first = points[0];
                Vec3d last = points[points.Count - 1];
                double forward = 0.0;
                double reversed = 0.0;

                if (hasStartPoint)
                {
                    forward += Distance(first, startPoint);
                    reversed += Distance(last, startPoint);
                }

                if (hasEndPoint)
                {
                    forward += Distance(last, endPoint);
                    reversed += Distance(first, endPoint);
                }

                if (reversed + 0.0000001 < forward)
                    points.Reverse();
            }

            private static List<List<double>> ExtractNumericLists(string text)
            {
                var result = new List<List<double>>();
                var stack = new Stack<int>();
                bool inString = false;

                for (int i = 0; i < text.Length; i++)
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

                    if (inString)
                        continue;

                    if (c == '(')
                    {
                        stack.Push(i);
                    }
                    else if (c == ')' && stack.Count > 0)
                    {
                        int start = stack.Pop();
                        string content = text.Substring(start + 1, i - start - 1);
                        if (content.IndexOf('(') >= 0 || content.IndexOf(')') >= 0)
                            continue;

                        if (TryParseNumberList(content, out List<double> numbers))
                            result.Add(numbers);
                    }
                }

                return result;
            }

            private static bool TryParseNumberList(string text, out List<double> numbers)
            {
                numbers = new List<double>();
                string[] parts = text.Split(',');
                foreach (string part in parts)
                {
                    string trimmed = part.Trim();
                    if (trimmed.Length == 0)
                        return false;

                    if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                        return false;

                    numbers.Add(value);
                }

                return numbers.Count > 0;
            }

            private static double Distance(Vec3d a, Vec3d b)
            {
                double dx = a.X - b.X;
                double dy = a.Y - b.Y;
                double dz = a.Z - b.Z;
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
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

            private bool TryGetCircle(int circleId, out CircleInfo circle)
            {
                circle = default;
                if (!Entities.TryGetValue(circleId, out StepEntity entity) || entity.Type != "CIRCLE")
                    return false;

                if (!TryParseCircleRadius(entity.Definition, out double radius) || radius <= 0.0)
                    return false;

                int placementId = entity.References.FirstOrDefault(id => GetTypeName(id) == "AXIS2_PLACEMENT_3D");
                if (placementId == 0 || !TryGetAxis2Placement(placementId, out Vec3d center, out Vec3d xDirection, out Vec3d yDirection))
                    return false;

                circle = new CircleInfo(center, xDirection, yDirection, radius);
                return true;
            }

            private bool TryGetAxis2Placement(int placementId, out Vec3d center, out Vec3d xDirection, out Vec3d yDirection)
            {
                center = default;
                xDirection = default;
                yDirection = default;

                if (!Entities.TryGetValue(placementId, out StepEntity placement) || placement.Type != "AXIS2_PLACEMENT_3D")
                    return false;

                int centerId = placement.References.FirstOrDefault(id => GetTypeName(id) == "CARTESIAN_POINT");
                if (centerId == 0 || !TryGetPoint(centerId, out center))
                    return false;

                var directionIds = placement.References
                    .Where(id => GetTypeName(id) == "DIRECTION")
                    .ToList();

                Vec3d axis = new Vec3d(0, 0, 1);
                Vec3d reference = new Vec3d(1, 0, 0);

                if (directionIds.Count > 0 && TryGetDirection(directionIds[0], out Vec3d parsedAxis))
                    axis = parsedAxis;

                if (directionIds.Count > 1 && TryGetDirection(directionIds[1], out Vec3d parsedReference))
                    reference = parsedReference;

                axis = axis.Normalized();
                if (axis.Length <= 0.000000001)
                    axis = new Vec3d(0, 0, 1);

                reference = reference - axis * Vec3d.Dot(reference, axis);
                xDirection = reference.Normalized();
                if (xDirection.Length <= 0.000000001)
                    xDirection = ChoosePerpendicular(axis);

                yDirection = Vec3d.Cross(axis, xDirection).Normalized();
                if (yDirection.Length <= 0.000000001)
                    return false;

                return true;
            }

            private bool TryGetDirection(int directionId, out Vec3d direction)
            {
                direction = default;
                if (!Entities.TryGetValue(directionId, out StepEntity entity) || entity.Type != "DIRECTION")
                    return false;

                return TryParseDirection(entity.Definition, out direction);
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

            private static bool TryParseDirection(string definition, out Vec3d direction)
            {
                Match match = DirectionRegex.Match(definition);
                if (!match.Success)
                {
                    direction = default;
                    return false;
                }

                string[] parts = match.Groups[1].Value.Split(',');
                if (parts.Length < 3)
                {
                    direction = default;
                    return false;
                }

                direction = new Vec3d(ParseDouble(parts[0]), ParseDouble(parts[1]), ParseDouble(parts[2]));
                return true;
            }

            private static bool TryParseCircleRadius(string definition, out double radius)
            {
                Match match = CircleRegex.Match(definition);
                if (!match.Success)
                {
                    radius = 0.0;
                    return false;
                }

                radius = ParseDouble(match.Groups[1].Value);
                return true;
            }

            private static Vec3d ChoosePerpendicular(Vec3d axis)
            {
                Vec3d candidate = Math.Abs(axis.X) < 0.9
                    ? new Vec3d(1, 0, 0)
                    : new Vec3d(0, 1, 0);

                return (candidate - axis * Vec3d.Dot(candidate, axis)).Normalized();
            }

            private static bool AlmostSame(Vec3d a, Vec3d b)
            {
                return Math.Abs(a.X - b.X) < 0.0000001
                    && Math.Abs(a.Y - b.Y) < 0.0000001
                    && Math.Abs(a.Z - b.Z) < 0.0000001;
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

        private struct CircleInfo
        {
            private readonly Vec3d _center;
            private readonly Vec3d _xDirection;
            private readonly Vec3d _yDirection;
            private readonly double _radius;

            public CircleInfo(Vec3d center, Vec3d xDirection, Vec3d yDirection, double radius)
            {
                _center = center;
                _xDirection = xDirection;
                _yDirection = yDirection;
                _radius = radius;
            }

            public double AngleOf(Vec3d point)
            {
                Vec3d relative = point - _center;
                double x = Vec3d.Dot(relative, _xDirection);
                double y = Vec3d.Dot(relative, _yDirection);
                return Math.Atan2(y, x);
            }

            public Vec3d PointAt(double angle)
            {
                return _center +
                    _xDirection * (_radius * Math.Cos(angle)) +
                    _yDirection * (_radius * Math.Sin(angle));
            }
        }

        private struct DepthPlane
        {
            private readonly bool _isConstant;
            private readonly Vec3d _normal;
            private readonly double _constant;
            private readonly double _fallbackDepth;

            private DepthPlane(bool isConstant, Vec3d normal, double constant, double fallbackDepth)
            {
                _isConstant = isConstant;
                _normal = normal;
                _constant = constant;
                _fallbackDepth = fallbackDepth;
            }

            public static DepthPlane Create(ProjectionFace face, ViewSpec view)
            {
                double fallbackDepth = face.Depth(view);
                if (face.Points.Count == 0)
                    return Constant(fallbackDepth);

                Vec3d normal = face.Normal.Normalized();
                if (normal.Length <= 0.000000001)
                    return Constant(fallbackDepth);

                if (Math.Abs(normal.Get(view.DepthAxis)) <= 0.000000001)
                    return Constant(fallbackDepth);

                double constant = -Vec3d.Dot(normal, face.Points[0]);
                return new DepthPlane(false, normal, constant, fallbackDepth);
            }

            public double DepthAtPixel(double x, double y, ProjectionTransform transform, ViewSpec view)
            {
                if (_isConstant)
                    return _fallbackDepth;

                double u = transform.UnprojectU(x) * view.USign;
                double v = transform.UnprojectV(y) * view.VSign;
                double denominator = _normal.Get(view.DepthAxis);
                if (Math.Abs(denominator) <= 0.000000001)
                    return _fallbackDepth;

                double known = _constant +
                    _normal.Get(view.UAxis) * u +
                    _normal.Get(view.VAxis) * v;
                double depthCoordinate = -known / denominator;
                return depthCoordinate * view.DepthSign;
            }

            private static DepthPlane Constant(double depth)
            {
                return new DepthPlane(true, default, 0.0, depth);
            }
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

            public double UnprojectU(double x)
            {
                return UMin + (x - Padding) / Scale;
            }

            public double UnprojectV(double y)
            {
                return VMin + (ImageSize - Padding - y) / Scale;
            }

            public Rect2i ProjectBounds(Bounds bounds, double paddingPixels)
            {
                double u0 = bounds.Min.Get(View.UAxis) * View.USign;
                double u1 = bounds.Max.Get(View.UAxis) * View.USign;
                double v0 = bounds.Min.Get(View.VAxis) * View.VSign;
                double v1 = bounds.Max.Get(View.VAxis) * View.VSign;

                double uMin = Math.Min(u0, u1);
                double uMax = Math.Max(u0, u1);
                double vMin = Math.Min(v0, v1);
                double vMax = Math.Max(v0, v1);

                double x0 = Padding + (uMin - UMin) * Scale - paddingPixels;
                double x1 = Padding + (uMax - UMin) * Scale + paddingPixels;
                double y0 = ImageSize - Padding - (vMax - VMin) * Scale - paddingPixels;
                double y1 = ImageSize - Padding - (vMin - VMin) * Scale + paddingPixels;

                return new Rect2i(
                    (int)Math.Floor(Math.Min(x0, x1)),
                    (int)Math.Floor(Math.Min(y0, y1)),
                    (int)Math.Ceiling(Math.Max(x0, x1)),
                    (int)Math.Ceiling(Math.Max(y0, y1)));
            }
        }

        private sealed class RgbaImage
        {
            private readonly SKBitmap _bitmap;
            private readonly SKCanvas _canvas;

            public RgbaImage(int width, int height)
            {
                Width = width;
                Height = height;
                _bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                _canvas = new SKCanvas(_bitmap);
            }

            private RgbaImage(SKBitmap bitmap)
            {
                _bitmap = bitmap;
                Width = bitmap.Width;
                Height = bitmap.Height;
                _canvas = new SKCanvas(_bitmap);
            }

            public int Width { get; private set; }
            public int Height { get; private set; }

            public static RgbaImage LoadPng(string path)
            {
                using (var stream = File.OpenRead(path))
                {
                    SKBitmap bitmap = SKBitmap.Decode(stream);
                    if (bitmap == null)
                        throw new InvalidDataException("Could not decode PNG image: " + path);

                    return new RgbaImage(bitmap);
                }
            }

            public void Clear(Rgba color)
            {
                _canvas.Clear(ToSkColor(color));
            }

            public void FillPolygonsEvenOdd(List<List<Point2d>> polygons, Rgba color)
            {
                polygons = polygons
                    .Where(polygon => polygon != null && polygon.Count >= 3)
                    .ToList();
                if (polygons.Count == 0)
                    return;

                using (SKPath path = BuildPath(polygons))
                using (SKPaint paint = CreatePaint(color, SKPaintStyle.Fill))
                    _canvas.DrawPath(path, paint);
            }

            public void FillPolygonsEvenOdd(
                List<List<Point2d>> polygons,
                Rgba color,
                double[] zBuffer,
                Func<int, int, double> depthAtPixel)
            {
                polygons = polygons
                    .Where(polygon => polygon != null && polygon.Count >= 3)
                    .ToList();
                if (polygons.Count == 0)
                    return;

                FillPolygonsEvenOdd(polygons, color);
                UpdateDepthBufferEvenOdd(polygons, zBuffer, depthAtPixel);
            }

            public void DrawLine(int x0, int y0, int x1, int y1, Rgba color)
            {
                using (SKPaint paint = CreatePaint(color, SKPaintStyle.Stroke))
                {
                    paint.StrokeWidth = 1.0f;
                    _canvas.DrawLine(x0, y0, x1, y1, paint);
                }
            }

            public void FillRectangle(int left, int top, int right, int bottom, Rgba color)
            {
                left = Math.Max(0, left);
                top = Math.Max(0, top);
                right = Math.Min(Width - 1, right);
                bottom = Math.Min(Height - 1, bottom);

                if (right < left || bottom < top)
                    return;

                using (SKPaint paint = CreatePaint(color, SKPaintStyle.Fill))
                    _canvas.DrawRect(SKRect.Create(left, top, right - left + 1, bottom - top + 1), paint);
            }

            public void DrawRectangle(int left, int top, int right, int bottom, Rgba color, int thickness)
            {
                if (thickness < 1)
                    thickness = 1;

                using (SKPaint paint = CreatePaint(color, SKPaintStyle.Stroke))
                {
                    paint.StrokeWidth = thickness;
                    _canvas.DrawRect(SKRect.Create(left, top, right - left + 1, bottom - top + 1), paint);
                }
            }

            public RgbaImage Downsample(int targetWidth, int targetHeight)
            {
                if (targetWidth <= 0 || targetHeight <= 0)
                    throw new ArgumentOutOfRangeException(nameof(targetWidth));

                if (targetWidth == Width && targetHeight == Height)
                    return this;

                var target = new RgbaImage(targetWidth, targetHeight);
                using (SKImage source = SKImage.FromBitmap(_bitmap))
                    target._canvas.DrawImage(
                        source,
                        SKRect.Create(0, 0, Width, Height),
                        SKRect.Create(0, 0, targetWidth, targetHeight),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                        null);

                return target;
            }

            public void SavePng(string path)
            {
                _canvas.Flush();
                using (SKImage image = SKImage.FromBitmap(_bitmap))
                using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
                using (Stream stream = File.Create(path))
                    data.SaveTo(stream);
            }

            public byte[] ToPngBytes()
            {
                _canvas.Flush();
                using (SKImage image = SKImage.FromBitmap(_bitmap))
                using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
                    return data.ToArray();
            }

            public void BlendPixel(int x, int y, Rgba color)
            {
                if (x < 0 || x >= Width || y < 0 || y >= Height)
                    return;

                if (color.A == 255)
                {
                    _bitmap.SetPixel(x, y, ToSkColor(color));
                    return;
                }

                SKColor existing = _bitmap.GetPixel(x, y);
                double alpha = color.A / 255.0;
                double inverse = 1.0 - alpha;
                _bitmap.SetPixel(
                    x,
                    y,
                    new SKColor(
                        ClampToByte(color.R * alpha + existing.Red * inverse),
                        ClampToByte(color.G * alpha + existing.Green * inverse),
                        ClampToByte(color.B * alpha + existing.Blue * inverse),
                        255));
            }

            private void UpdateDepthBufferEvenOdd(
                List<List<Point2d>> polygons,
                double[] zBuffer,
                Func<int, int, double> depthAtPixel)
            {
                Rect2i bounds = GetPixelBounds(polygons);
                if (!bounds.Intersects(0, 0, Width - 1, Height - 1))
                    return;

                int minY = Math.Max(0, bounds.Top);
                int maxY = Math.Min(Height - 1, bounds.Bottom);
                var nodes = new List<double>();

                for (int y = minY; y <= maxY; y++)
                {
                    double scanY = y + 0.5;
                    nodes.Clear();
                    foreach (var polygon in polygons)
                    {
                        int j = polygon.Count - 1;
                        for (int i = 0; i < polygon.Count; i++)
                        {
                            double yi = polygon[i].Y;
                            double yj = polygon[j].Y;
                            if ((yi <= scanY && yj > scanY) || (yj <= scanY && yi > scanY))
                            {
                                double xi = polygon[i].X;
                                double xj = polygon[j].X;
                                double x = xi + (scanY - yi) / (yj - yi) * (xj - xi);
                                nodes.Add(x);
                            }

                            j = i;
                        }
                    }

                    nodes.Sort();
                    for (int i = 0; i + 1 < nodes.Count; i += 2)
                    {
                        int startX = Math.Max(0, (int)Math.Ceiling(nodes[i]));
                        int endX = Math.Min(Width - 1, (int)Math.Floor(nodes[i + 1]));
                        for (int x = startX; x <= endX; x++)
                        {
                            int offset = y * Width + x;
                            double depth = depthAtPixel(x, y);
                            if (depth >= zBuffer[offset])
                                zBuffer[offset] = depth;
                        }
                    }
                }
            }

            private static SKPath BuildPath(List<List<Point2d>> polygons)
            {
                var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                foreach (var polygon in polygons)
                {
                    if (polygon.Count < 3)
                        continue;

                    path.MoveTo((float)polygon[0].X, (float)polygon[0].Y);
                    for (int i = 1; i < polygon.Count; i++)
                        path.LineTo((float)polygon[i].X, (float)polygon[i].Y);
                    path.Close();
                }

                return path;
            }

            private static Rect2i GetPixelBounds(List<List<Point2d>> polygons)
            {
                double minX = polygons.Min(polygon => polygon.Min(point => point.X));
                double minY = polygons.Min(polygon => polygon.Min(point => point.Y));
                double maxX = polygons.Max(polygon => polygon.Max(point => point.X));
                double maxY = polygons.Max(polygon => polygon.Max(point => point.Y));
                return new Rect2i(
                    (int)Math.Floor(minX) - 1,
                    (int)Math.Floor(minY) - 1,
                    (int)Math.Ceiling(maxX) + 1,
                    (int)Math.Ceiling(maxY) + 1);
            }

            private static SKPaint CreatePaint(Rgba color, SKPaintStyle style)
            {
                return new SKPaint
                {
                    Color = ToSkColor(color),
                    IsAntialias = true,
                    Style = style
                };
            }

            private static SKColor ToSkColor(Rgba color)
            {
                return new SKColor(color.R, color.G, color.B, color.A);
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

        private struct Rect2i
        {
            public readonly int Left;
            public readonly int Top;
            public readonly int Right;
            public readonly int Bottom;

            public Rect2i(int left, int top, int right, int bottom)
            {
                Left = Math.Min(left, right);
                Top = Math.Min(top, bottom);
                Right = Math.Max(left, right);
                Bottom = Math.Max(top, bottom);
            }

            public int Width => Right - Left + 1;
            public int Height => Bottom - Top + 1;

            public bool Overlaps(Rect2i other)
            {
                return Left <= other.Right &&
                    Right >= other.Left &&
                    Top <= other.Bottom &&
                    Bottom >= other.Top;
            }

            public bool Intersects(int left, int top, int right, int bottom)
            {
                return Left <= right &&
                    Right >= left &&
                    Top <= bottom &&
                    Bottom >= top;
            }

            public Rect2i Expand(int x, int y)
            {
                return new Rect2i(Left - x, Top - y, Right + x, Bottom + y);
            }

            public Rect2i Clamp(int left, int top, int right, int bottom)
            {
                return new Rect2i(
                    Math.Max(left, Left),
                    Math.Max(top, Top),
                    Math.Min(right, Right),
                    Math.Min(bottom, Bottom));
            }

            public Rect2i Union(Rect2i other)
            {
                return new Rect2i(
                    Math.Min(Left, other.Left),
                    Math.Min(Top, other.Top),
                    Math.Max(Right, other.Right),
                    Math.Max(Bottom, other.Bottom));
            }
        }

        private struct DetectionRectangle
        {
            public readonly Rect2i Rectangle;
            public readonly StepWatermarkMarkedRegion MarkedRegion;
            public readonly int EntityId;
            public readonly string Kind;

            public DetectionRectangle(Rect2i rectangle, StepWatermarkMarkedRegion markedRegion, int entityId, string kind)
            {
                Rectangle = rectangle;
                MarkedRegion = markedRegion;
                EntityId = entityId;
                Kind = kind;
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

            public static double Dot(Vec3d a, Vec3d b)
            {
                return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            }

            public static Vec3d operator +(Vec3d a, Vec3d b)
            {
                return new Vec3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            }

            public static Vec3d operator -(Vec3d a, Vec3d b)
            {
                return new Vec3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            }

            public static Vec3d operator *(Vec3d vector, double scale)
            {
                return new Vec3d(vector.X * scale, vector.Y * scale, vector.Z * scale);
            }
        }

        private struct Bounds
        {
            private bool _initialized;
            private Vec3d _min;
            private Vec3d _max;

            public Vec3d Min => _min;
            public Vec3d Max => _max;
            public Vec3d Size => new Vec3d(
                _max.X - _min.X,
                _max.Y - _min.Y,
                _max.Z - _min.Z);

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
