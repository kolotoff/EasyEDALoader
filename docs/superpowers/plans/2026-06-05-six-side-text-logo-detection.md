# Six-Side Text And Logo Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a reliable automatic watermark detector that searches known `LCEDA`, `EasyEDA`, and known logo templates from six-side color and edge projections before cleanup touches STEP geometry.

**Architecture:** Add a projection-analysis layer that consumes aligned six-side color PNGs and edge-map PNGs, searches a committed known-watermark template library, and emits `VerifiedCleanupRegion` candidates separate from debug-only candidates. The template library is built from existing marked projection data in `Test\StepCleaner\Data\Marked`, but normal cleanup must not load `Marked`; runtime uses only the generated `LCEDA`, `EasyEDA`, and logo templates. Keep cleanup geometry conservative: projection detection may propose regions, but STEP edits are allowed only when a projected text/logo match is confirmed against shallow host-loop or relief topology inside a bounded cleanup box.

**Tech Stack:** C#/.NET 8, SkiaSharp image analysis, existing `StepProjectionRenderer` F3D color projections, aligned edge projections from STEP/HLR edge rasterization, existing `StepWatermarkCleaner` and `StepCleaner.Tests` harness.

---

## File Structure

- Modify `EasyEDA-Loader\StepProjectionRenderer.cs`
  - Add edge projection output support aligned to current six-side color projections.
  - Add in-memory paired projection APIs for tests and detector use.
- Create `EasyEDA-Loader\StepTextLogoProjectionDetector.cs`
  - Own image-only known template search from color and edge maps.
  - Return view-space rectangles, matched template names, and scores; do not know STEP topology.
- Create `EasyEDA-Loader\StepWatermarkTemplateLibrary.cs`
  - Store committed normalized templates for `LCEDA`, `EasyEDA`, and known logo marks.
  - Expose templates as edge masks and optional color priors.
- Create `EasyEDA-Loader\StepWatermarkTemplateExtractor.cs`
  - Test/debug-only helper that reads projection PNGs, edge PNGs, projection metadata, and `Marked` rectangles to derive/refine templates.
  - Must not be called by normal cleanup.
- Modify `EasyEDA-Loader\StepWatermarkCleaner.cs`
  - Call the projection detector during automatic detection.
  - Store projection text/logo regions as verified cleanup regions only after topology matching.
  - Keep candidate-only projection regions distinct from cleanup regions.
  - Keep the existing `CleanText` option; improve it by allowing template-backed text matches to seed the current text-string cleanup, while preserving the old geometry-only text path as a fallback.
- Modify `StepCleaner\Program.cs`
  - Add a detector-debug command for projection text/logo candidates.
  - Keep `project` command able to generate color and edge images for marking.
- Modify `Test\StepCleaner\Program.cs`
  - Add red tests for the user-reported false passes and residual text/logo cases.
  - Add tests that no-op cleans fail when marked/projection text-logo evidence exists.
- Create `Test\StepCleaner\Data\TextLogoDetectionExpected.json`
  - Store explicit expected detection views/rectangles for known fixtures.
- Create `Test\StepCleaner\Data\WatermarkTemplateSources.json`
  - Store which marked rectangles are used to derive each committed template.

---

### Task 1: Projection Mode Support

**Files:**
- Modify: `EasyEDA-Loader\StepProjectionRenderer.cs`
- Modify: `StepCleaner\Program.cs`
- Test: `Test\StepCleaner\Program.cs`

- [ ] **Step 1: Write failing projection-mode test**

Add a test command branch in `RunCommand`:

```csharp
if (IsOption(args[0], "--projection-edge-mode"))
    return RunProjectionEdgeModeTests();
```

Add this test method:

```csharp
private static int RunProjectionEdgeModeTests()
{
    var failures = new List<string>();
    string dataRoot = FindDataRoot();
    string inputPath = Path.Combine(dataRoot, "Original", "CONN-TH_XT60PB-M.step");

    var colorOptions = new StepProjectionOptions
    {
        ImageSizePixels = 900,
        PaddingPixels = 40,
        WriteMetadata = false,
        RenderMode = StepProjectionRenderMode.Color
    };
    colorOptions.ViewNames.Add("z_minus");

    var edgeOptions = new StepProjectionOptions
    {
        ImageSizePixels = 900,
        PaddingPixels = 40,
        WriteMetadata = false,
        RenderMode = StepProjectionRenderMode.Edge
    };
    edgeOptions.ViewNames.Add("z_minus");

    IReadOnlyList<StepProjectionImage> colorImages = StepProjectionRenderer.ProjectFileImages(
        File.ReadAllBytes(inputPath),
        "xt60-color",
        colorOptions);
    IReadOnlyList<StepProjectionImage> edgeImages = StepProjectionRenderer.ProjectFileImages(
        File.ReadAllBytes(inputPath),
        "xt60-edge",
        edgeOptions);

    if (colorImages.Count != 1)
        failures.Add("Color projection should return one z_minus image.");
    if (edgeImages.Count != 1)
        failures.Add("Edge projection should return one z_minus image.");
    if (colorImages.Count == 1 && edgeImages.Count == 1)
    {
        if (colorImages[0].Width != edgeImages[0].Width || colorImages[0].Height != edgeImages[0].Height)
            failures.Add("Color and edge projections should have identical dimensions.");

        using (SKBitmap edge = edgeImages[0].ToBitmap())
        {
            int darkPixels = CountDarkPixels(edge, threshold: 32);
            if (darkPixels < 500)
                failures.Add("Edge projection should contain visible edge pixels; darkPixels=" + darkPixels.ToString(CultureInfo.InvariantCulture));
        }
    }

    if (failures.Count > 0)
    {
        Console.Error.WriteLine("Projection edge-mode test failed.");
        foreach (string failure in failures)
            Console.Error.WriteLine("  " + failure);
        return 1;
    }

    Console.WriteLine("Projection edge-mode test passed.");
    return 0;
}
```

Add helper:

```csharp
private static int CountDarkPixels(SKBitmap image, byte threshold)
{
    int count = 0;
    for (int y = 0; y < image.Height; y++)
    {
        for (int x = 0; x < image.Width; x++)
        {
            SKColor color = image.GetPixel(x, y);
            if (color.Red <= threshold && color.Green <= threshold && color.Blue <= threshold && color.Alpha > 0)
                count++;
        }
    }

    return count;
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --projection-edge-mode
```

Expected: compile failure because `StepProjectionRenderMode` and `RenderMode` do not exist.

- [ ] **Step 3: Add render mode API**

In `StepProjectionRenderer.cs`, add:

```csharp
public enum StepProjectionRenderMode
{
    Color,
    Edge
}
```

Add to `StepProjectionOptions`:

```csharp
public StepProjectionRenderMode RenderMode { get; set; } = StepProjectionRenderMode.Color;
```

Update `CloneSingleViewOptions` and every manual `new StepProjectionOptions` clone to copy:

```csharp
RenderMode = options.RenderMode
```

- [ ] **Step 4: Implement edge image path**

In `ProjectFileImages`, before the F3D raw-image path, branch on edge mode:

```csharp
if (options.RenderMode == StepProjectionRenderMode.Edge)
    return ProjectFileEdgeImages(stepData, modelName, selectedViews, options);
```

Add:

```csharp
private static IReadOnlyList<StepProjectionImage> ProjectFileEdgeImages(
    byte[] stepData,
    string modelName,
    IReadOnlyList<ViewSpec> selectedViews,
    StepProjectionOptions options)
{
    string stepText = Encoding.Latin1.GetString(stepData);
    StepModel model = StepModel.Parse(stepText);
    model.BuildIndexes();
    ProjectionModel drawingModel = ProjectionModel.Build(model);
    var result = new List<StepProjectionImage>();

    foreach (ViewSpec view in selectedViews)
    {
        ProjectionTransform transform = ProjectionTransform.Create(drawingModel.Bounds, view, options);
        RgbaImage image = RenderEdgeProjectionImage(drawingModel, view, transform, options);
        result.Add(image.ToProjectionImage(view.Name));
    }

    return result;
}
```

Add `RenderEdgeProjectionImage` using the existing face loop points already built in `ProjectionModel`; draw all loops as black antialiased polylines on white background with the same transform used by color projections:

```csharp
private static RgbaImage RenderEdgeProjectionImage(
    ProjectionModel model,
    ViewSpec view,
    ProjectionTransform transform,
    StepProjectionOptions options)
{
    var image = new RgbaImage(GetImageWidthPixels(options), GetImageHeightPixels(options), new Rgba(255, 255, 255, 255));
    foreach (ProjectionFace face in model.Faces)
    {
        foreach (ProjectionLoop loop in face.Loops)
            DrawProjectedPolyline(image, view, transform, loop.Points, new Rgba(0, 0, 0, 255), 1);
    }

    return image;
}
```

If `DrawProjectedPolyline` does not exist, create it next to the existing projection drawing helpers. It should project each 3D point to image coordinates and draw connected segments.

- [ ] **Step 5: Run projection-mode test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --projection-edge-mode
```

Expected: PASS.

- [ ] **Step 6: Update CLI project output naming**

When `StepProjectionOptions.RenderMode == Edge`, write files with `__<view>__edge.png` and `__<view>__edge.json`. Keep color files as current `__<view>.png` to avoid breaking existing marked-region paths.

Add CLI option:

```powershell
dotnet run --project StepCleaner\StepCleaner.csproj -- project Test\StepCleaner\Data\Original Test\StepCleaner\Data\Projection --edge
```

If `--edge` is passed, generate edge projections. Without it, generate current color projections.

- [ ] **Step 7: Commit projection-mode task**

```powershell
git add EasyEDA-Loader\StepProjectionRenderer.cs StepCleaner\Program.cs Test\StepCleaner\Program.cs
git commit -m "feat: add aligned edge projection mode"
```

---

### Task 2: Known Template Library From Marked Data

**Files:**
- Create: `EasyEDA-Loader\StepWatermarkTemplateLibrary.cs`
- Create: `EasyEDA-Loader\StepWatermarkTemplateExtractor.cs`
- Create: `EasyEDA-Loader\StepTextLogoProjectionDetector.cs`
- Modify: `Test\StepCleaner\StepCleaner.Tests.csproj`
- Modify: `Test\StepCleaner\Program.cs`
- Create: `Test\StepCleaner\Data\WatermarkTemplateSources.json`
- Create: `Test\StepCleaner\Data\TextLogoDetectionExpected.json`

- [ ] **Step 1: Add template source fixture file**

Create `Test\StepCleaner\Data\WatermarkTemplateSources.json` from existing marked rectangles:

```json
[
  {
    "templateName": "LCEDA",
    "kind": "text",
    "fileName": "CONN-TH_XT60PB-M.step",
    "viewName": "z_minus",
    "projectionFile": "CONN-TH_XT60PB-M__z_minus.png",
    "edgeProjectionFile": "CONN-TH_XT60PB-M__z_minus__edge.png",
    "markedFile": "CONN-TH_XT60PB-M__z_minus.json",
    "text": "LCEDA"
  },
  {
    "templateName": "EasyEDA",
    "kind": "text",
    "fileName": "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
    "viewName": "z_plus",
    "projectionFile": "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30__z_plus.png",
    "edgeProjectionFile": "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30__z_plus__edge.png",
    "markedFile": "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30__z_plus.json",
    "text": "EasyEDA"
  },
  {
    "templateName": "easyeda-logo",
    "kind": "logo",
    "fileName": "BUZ-SMD_4P-L7.5-W7.5-H2.5.step",
    "viewName": "x_plus",
    "projectionFile": "BUZ-SMD_4P-L7.5-W7.5-H2.5__x_plus.png",
    "edgeProjectionFile": "BUZ-SMD_4P-L7.5-W7.5-H2.5__x_plus__edge.png",
    "markedFile": "BUZ-SMD_4P-L7.5-W7.5-H2.5__x_plus.json",
    "text": ""
  }
]
```

The implementer may add more source entries from `Test\StepCleaner\Data\Marked` only when the marked rectangle contains one of the two known text strings or the same EasyEDA logo family. Do not add arbitrary component outlines as templates.

- [ ] **Step 2: Add expected fixture file**

Create `Test\StepCleaner\Data\TextLogoDetectionExpected.json`:

```json
[
  {
    "fileName": "CONN-TH_XT60PB-M.step",
    "viewName": "z_minus",
    "minDetections": 1,
    "requiredTemplate": "LCEDA"
  },
  {
    "fileName": "CONN-TH_MR30PW-M30-G-Y.step",
    "viewName": "z_plus",
    "minDetections": 1,
    "requiredTemplate": "LCEDA"
  },
  {
    "fileName": "USB-A-TH_FUS264-FDSW3K.step",
    "viewName": "x_plus",
    "minDetections": 1,
    "requiredTemplate": "EasyEDA"
  },
  {
    "fileName": "USB-B-TH_USB-B10-BRW.step",
    "viewName": "x_plus",
    "minDetections": 1,
    "requiredTemplate": "EasyEDA"
  },
  {
    "fileName": "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
    "viewName": "z_plus",
    "minDetections": 1,
    "requiredTemplate": "EasyEDA"
  }
]
```

- [ ] **Step 3: Write failing template library test**

Add command branch:

```csharp
if (IsOption(args[0], "--watermark-template-library"))
    return RunWatermarkTemplateLibraryTests();
```

Add:

```csharp
private static int RunWatermarkTemplateLibraryTests()
{
    var failures = new List<string>();
    string dataRoot = FindDataRoot();
    string sourcePath = Path.Combine(dataRoot, "WatermarkTemplateSources.json");
    var sources = JsonSerializer.Deserialize<List<WatermarkTemplateSource>>(File.ReadAllText(sourcePath));

    IReadOnlyList<StepWatermarkTemplate> templates = StepWatermarkTemplateExtractor.ExtractFromMarkedData(
        Path.Combine(dataRoot, "Projection"),
        Path.Combine(dataRoot, "Marked"),
        sources);

    AssertTemplatePresent(templates, "LCEDA", failures);
    AssertTemplatePresent(templates, "EasyEDA", failures);
    AssertTemplatePresent(templates, "easyeda-logo", failures);

    foreach (StepWatermarkTemplate template in templates)
    {
        if (template.Width <= 8 || template.Height <= 8)
            failures.Add(template.Name + " template is too small.");
        if (template.EdgePoints.Count < 40)
            failures.Add(template.Name + " template has too few edge points: " + template.EdgePoints.Count.ToString(CultureInfo.InvariantCulture));
    }

    if (failures.Count > 0)
    {
        Console.Error.WriteLine("Watermark template library test failed.");
        foreach (string failure in failures)
            Console.Error.WriteLine("  " + failure);
        return 1;
    }

    Console.WriteLine("Watermark template library test passed.");
    return 0;
}

private static void AssertTemplatePresent(IReadOnlyList<StepWatermarkTemplate> templates, string name, List<string> failures)
{
    if (!templates.Any(template => string.Equals(template.Name, name, StringComparison.OrdinalIgnoreCase)))
        failures.Add("Missing template: " + name);
}

private sealed class WatermarkTemplateSource
{
    public string TemplateName { get; set; }
    public string Kind { get; set; }
    public string FileName { get; set; }
    public string ViewName { get; set; }
    public string ProjectionFile { get; set; }
    public string EdgeProjectionFile { get; set; }
    public string MarkedFile { get; set; }
    public string Text { get; set; }
}
```

- [ ] **Step 4: Run template library test to verify it fails**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --watermark-template-library
```

Expected: compile failure because `StepWatermarkTemplateExtractor` and `StepWatermarkTemplate` do not exist.

- [ ] **Step 5: Create template data model and extractor**

Create `EasyEDA-Loader\StepWatermarkTemplateLibrary.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace EasyEDA_Loader
{
    public sealed class StepWatermarkTemplate
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Text { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public IReadOnlyList<StepWatermarkTemplatePoint> EdgePoints { get; set; }
    }

    public readonly struct StepWatermarkTemplatePoint
    {
        public StepWatermarkTemplatePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; }
        public int Y { get; }
    }

    public static class StepWatermarkTemplateLibrary
    {
        public static IReadOnlyList<StepWatermarkTemplate> GetKnownTemplates()
        {
            return KnownTemplates;
        }

        private static readonly StepWatermarkTemplate[] KnownTemplates = Array.Empty<StepWatermarkTemplate>();
    }
}
```

Create `EasyEDA-Loader\StepWatermarkTemplateExtractor.cs`:

```csharp
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EasyEDA_Loader
{
    public static class StepWatermarkTemplateExtractor
    {
        public static IReadOnlyList<StepWatermarkTemplate> ExtractFromMarkedData<TSource>(
            string projectionDirectory,
            string markedDirectory,
            IReadOnlyList<TSource> sources)
        {
            var templates = new List<StepWatermarkTemplate>();
            foreach (TSource source in sources)
            {
                string templateName = ReadStringProperty(source, "TemplateName");
                string kind = ReadStringProperty(source, "Kind");
                string text = ReadStringProperty(source, "Text");
                string edgeProjectionFile = ReadStringProperty(source, "EdgeProjectionFile");
                string markedFile = ReadStringProperty(source, "MarkedFile");
                string edgePath = Path.Combine(projectionDirectory, edgeProjectionFile);
                string markedPath = Path.Combine(markedDirectory, markedFile);
                using (SKBitmap edge = SKBitmap.Decode(edgePath))
                using (JsonDocument marker = JsonDocument.Parse(File.ReadAllText(markedPath)))
                {
                    foreach (JsonElement rectangle in marker.RootElement.GetProperty("Rectangles").EnumerateArray())
                    {
                        int x = rectangle.GetProperty("X").GetInt32();
                        int y = rectangle.GetProperty("Y").GetInt32();
                        int width = rectangle.GetProperty("Width").GetInt32();
                        int height = rectangle.GetProperty("Height").GetInt32();
                        templates.Add(ExtractTemplate(templateName, kind, text, edge, x, y, width, height));
                    }
                }
            }

            return templates;
        }

        private static StepWatermarkTemplate ExtractTemplate(string name, string kind, string text, SKBitmap edge, int x, int y, int width, int height)
        {
            var points = new List<StepWatermarkTemplatePoint>();
            for (int py = Math.Max(0, y); py < Math.Min(edge.Height, y + height); py++)
            {
                for (int px = Math.Max(0, x); px < Math.Min(edge.Width, x + width); px++)
                {
                    SKColor color = edge.GetPixel(px, py);
                    if (color.Alpha > 0 && color.Red < 96 && color.Green < 96 && color.Blue < 96)
                        points.Add(new StepWatermarkTemplatePoint(px - x, py - y));
                }
            }

            return new StepWatermarkTemplate
            {
                Name = name,
                Kind = kind,
                Text = text,
                Width = width,
                Height = height,
                EdgePoints = points
            };
        }

        private static string ReadStringProperty<TSource>(TSource source, string propertyName)
        {
            object value = source.GetType().GetProperty(propertyName).GetValue(source);
            return value == null ? string.Empty : value.ToString();
        }
    }
}
```

Include both files in both test and cleaner projects:

```xml
<Compile Include="..\EasyEDA-Loader\StepWatermarkTemplateLibrary.cs" Link="StepWatermarkTemplateLibrary.cs" />
<Compile Include="..\EasyEDA-Loader\StepWatermarkTemplateExtractor.cs" Link="StepWatermarkTemplateExtractor.cs" />
```

- [ ] **Step 6: Run template library test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --watermark-template-library
```

Expected: PASS after edge projection files exist. If `__edge` files are missing, run:

```powershell
dotnet run --project StepCleaner\StepCleaner.csproj -- project Test\StepCleaner\Data\Original Test\StepCleaner\Data\Projection --edge
```

- [ ] **Step 7: Generate committed templates**

Add a CLI/test helper that prints C# initializer data from `ExtractFromMarkedData`, then replace `KnownTemplates = Array.Empty<StepWatermarkTemplate>()` with the generated LCEDA/EasyEDA/logo template initializers. Runtime cleanup must call `StepWatermarkTemplateLibrary.GetKnownTemplates()` and must not read `Test\StepCleaner\Data\Marked`.

The generated initializer shape must be:

```csharp
private static readonly StepWatermarkTemplate[] KnownTemplates =
{
    new StepWatermarkTemplate
    {
        Name = "LCEDA",
        Kind = "text",
        Text = "LCEDA",
        Width = 144,
        Height = 433,
        EdgePoints = new[]
        {
            new StepWatermarkTemplatePoint(12, 18)
        }
    }
};
```

- [ ] **Step 8: Write failing known-template detector test**

Add command branch:

```csharp
if (IsOption(args[0], "--text-logo-detection"))
    return RunTextLogoDetectionTests();
```

Add:

```csharp
private static int RunTextLogoDetectionTests()
{
    var failures = new List<string>();
    string dataRoot = FindDataRoot();
    string expectedPath = Path.Combine(dataRoot, "TextLogoDetectionExpected.json");
    var expectations = JsonSerializer.Deserialize<List<TextLogoExpectation>>(File.ReadAllText(expectedPath));

    foreach (TextLogoExpectation expectation in expectations)
    {
        string inputPath = Path.Combine(dataRoot, "Original", expectation.FileName);
        byte[] stepBytes = File.ReadAllBytes(inputPath);

        var colorOptions = new StepProjectionOptions { ImageSizePixels = 1000, PaddingPixels = 50, WriteMetadata = false };
        colorOptions.ViewNames.Add(expectation.ViewName);
        var edgeOptions = new StepProjectionOptions { ImageSizePixels = 1000, PaddingPixels = 50, WriteMetadata = false, RenderMode = StepProjectionRenderMode.Edge };
        edgeOptions.ViewNames.Add(expectation.ViewName);

        StepProjectionImage color = StepProjectionRenderer.ProjectFileImages(stepBytes, expectation.FileName + ".color", colorOptions)[0];
        StepProjectionImage edge = StepProjectionRenderer.ProjectFileImages(stepBytes, expectation.FileName + ".edge", edgeOptions)[0];
        IReadOnlyList<StepTextLogoDetectionRegion> detections = StepTextLogoProjectionDetector.Detect(color, edge);

        int matching = detections.Count(region => string.Equals(region.TemplateName, expectation.RequiredTemplate, StringComparison.OrdinalIgnoreCase));
        if (matching < expectation.MinDetections)
        {
            failures.Add(
                expectation.FileName +
                " should detect " +
                expectation.RequiredTemplate +
                " on " +
                expectation.ViewName +
                "; detected=" +
                matching.ToString(CultureInfo.InvariantCulture));
        }
    }

    if (failures.Count > 0)
    {
        Console.Error.WriteLine("Text/logo detection test failed.");
        foreach (string failure in failures)
            Console.Error.WriteLine("  " + failure);
        return 1;
    }

    Console.WriteLine("Text/logo detection test passed.");
    return 0;
}

private sealed class TextLogoExpectation
{
    public string FileName { get; set; }
    public string ViewName { get; set; }
    public int MinDetections { get; set; }
    public string RequiredTemplate { get; set; }
}
```

- [ ] **Step 9: Run detector test to verify it fails**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-detection
```

Expected: compile failure because `StepTextLogoProjectionDetector` does not exist.

- [ ] **Step 10: Create detector data model**

Create `EasyEDA-Loader\StepTextLogoProjectionDetector.cs`:

```csharp
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EasyEDA_Loader
{
    public sealed class StepTextLogoDetectionRegion
    {
        public string TemplateName { get; internal set; }
        public string Kind { get; internal set; }
        public string Text { get; internal set; }
        public int X { get; internal set; }
        public int Y { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public double Score { get; internal set; }
        public double ChamferDistance { get; internal set; }
        public int EdgePixelCount { get; internal set; }
    }

    public static class StepTextLogoProjectionDetector
    {
        public static IReadOnlyList<StepTextLogoDetectionRegion> Detect(StepProjectionImage colorProjection, StepProjectionImage edgeProjection)
        {
            if (colorProjection == null)
                throw new ArgumentNullException(nameof(colorProjection));
            if (edgeProjection == null)
                throw new ArgumentNullException(nameof(edgeProjection));
            if (colorProjection.Width != edgeProjection.Width || colorProjection.Height != edgeProjection.Height)
                throw new InvalidOperationException("Color and edge projections must have identical dimensions.");

            using (SKBitmap color = colorProjection.ToBitmap())
            using (SKBitmap edge = edgeProjection.ToBitmap())
                return Detect(color, edge, StepWatermarkTemplateLibrary.GetKnownTemplates());
        }

        internal static IReadOnlyList<StepTextLogoDetectionRegion> Detect(
            SKBitmap color,
            SKBitmap edge,
            IReadOnlyList<StepWatermarkTemplate> templates)
        {
            return Array.Empty<StepTextLogoDetectionRegion>();
        }
    }
}
```

Include the file in both projects:

```xml
<Compile Include="..\EasyEDA-Loader\StepTextLogoProjectionDetector.cs" Link="StepTextLogoProjectionDetector.cs" />
```

- [ ] **Step 11: Run detector test to verify expected red failure**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-detection
```

Expected: FAIL with missing detections for all expected fixtures.

- [ ] **Step 12: Implement edge distance transform and template search**

In `StepTextLogoProjectionDetector`, implement:

```csharp
private static bool IsEdgePixel(SKColor color)
{
    return color.Alpha > 0 && color.Red < 80 && color.Green < 80 && color.Blue < 80;
}
```

Build a binary edge map from the edge projection and a distance transform:

```csharp
bool[,] edgeMap = BuildEdgeMap(edge);
double[,] distance = BuildDistanceTransform(edgeMap);
```

Search every template over a coarse-to-fine grid:

```csharp
foreach (StepWatermarkTemplate template in templates)
{
    foreach (double scale in new[] { 0.70, 0.85, 1.00, 1.15, 1.30 })
    {
        foreach (int rotationDegrees in new[] { 0, 90, 180, 270 })
            SearchTemplateVariant(template, scale, rotationDegrees, distance, edgeMap, results);
    }
}
```

Use chamfer score:

```csharp
double averageDistance = templatePoints.Sum(point => distance[x + point.X, y + point.Y]) / templatePoints.Count;
double coverage = CountTemplatePointsNearEdges(templatePoints, x, y, distance, maxDistance: 3.0) / (double)templatePoints.Count;
double score = Math.Max(0.0, 1.0 - averageDistance / 12.0) * coverage;
```

Return non-overlapping detections with:

```csharp
Score >= 0.62
ChamferDistance <= 5.0
TemplateName = template.Name
Kind = template.Kind
Text = template.Text
```

- [ ] **Step 13: Add color and host-face priors**

Use the color projection to reject obvious non-watermarks:

```csharp
if (OverlapsProtectedMetalColor(color, candidateRectangle, minimumRatio: 0.18))
    reject;
if (BackgroundColorVariance(color, candidateRectangle) > 0.12 && candidate.Kind == "text")
    reject;
```

Implement protected metal color as:

```csharp
private static bool IsProtectedMetalColor(SKColor color)
{
    bool gold = color.Red >= 130 && color.Green >= 95 && color.Blue <= 95;
    bool silver = Math.Abs(color.Red - color.Green) <= 18 && Math.Abs(color.Green - color.Blue) <= 18 && color.Red >= 145;
    return gold || silver;
}
```

- [ ] **Step 14: Run detector test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-detection
```

Expected: PASS for the expected fixture list.

- [ ] **Step 15: Commit known-template detector**

```powershell
git add EasyEDA-Loader\StepTextLogoProjectionDetector.cs EasyEDA-Loader\StepWatermarkTemplateLibrary.cs EasyEDA-Loader\StepWatermarkTemplateExtractor.cs EasyEDA-Loader\StepProjectionRenderer.cs Test\StepCleaner\Program.cs Test\StepCleaner\StepCleaner.Tests.csproj Test\StepCleaner\Data\WatermarkTemplateSources.json Test\StepCleaner\Data\TextLogoDetectionExpected.json
git commit -m "feat: detect known watermark templates from projections"
```

---

### Task 3: Improve Existing CleanText Option With Templates

**Files:**
- Modify: `EasyEDA-Loader\StepWatermarkCleaner.cs`
- Modify: `Test\StepCleaner\Program.cs`

- [ ] **Step 1: Write failing CleanText template test**

Extend `RunCleanTextTests()` so `CleanText = true` must remove known `LCEDA` or `EasyEDA` text when watermark-only cleanup does not:

```csharp
var templateBackedCleanTextFixtures = new[]
{
    "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
    "USB-B-TH_USB-B10-BRW.step"
};
```

For each fixture, compare watermark-only output to clean-text output:

```csharp
if (templateBackedCleanTextFixtures.Contains(Path.GetFileName(originalFile), StringComparer.OrdinalIgnoreCase) &&
    BytesEqual(watermarkOnly, textCleaned))
{
    failures.Add(Path.GetFileName(originalFile) + " should be additionally cleaned by template-backed CleanText.");
}
```

- [ ] **Step 2: Run CleanText test to verify it fails**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --clean-text
```

Expected: FAIL for at least one template-backed fixture that currently keeps text.

- [ ] **Step 3: Feed template matches into text-string cleanup**

In `BuildCleanupContext`, when `options.CleanText` is true, render six-side color and edge projections in memory and run:

```csharp
IReadOnlyList<StepTextLogoDetectionRegion> templateTextRegions =
    StepTextLogoProjectionDetector.Detect(colorProjection, edgeProjection)
        .Where(region => string.Equals(region.Kind, "text", StringComparison.OrdinalIgnoreCase))
        .ToList();
```

Map each projected rectangle to model bounds using the same projection metadata path used by `StepProjectionRenderer.ProjectDetectionRegions`. Add these regions as text cleanup seeds before `FindAutomaticTextStringFaces` finalizes `TextFaceIds`.

- [ ] **Step 4: Preserve geometry-only CleanText fallback**

Keep the existing `FindAutomaticTextStringFaces` path active after template seeding:

```csharp
TextStringDetectionResult geometryText = FindAutomaticTextStringFaces(...);
textDetection.FaceIds.AddRange(geometryText.FaceIds);
textDetection.FaceIds = textDetection.FaceIds.Distinct().ToList();
```

This keeps old behavior for non-template text on model while improving known EasyEDA watermark text.

- [ ] **Step 5: Run CleanText test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --clean-text
```

Expected: PASS.

- [ ] **Step 6: Commit CleanText improvement**

```powershell
git add EasyEDA-Loader\StepWatermarkCleaner.cs Test\StepCleaner\Program.cs
git commit -m "feat: seed clean-text cleanup from known watermark templates"
```

---

### Task 4: Promote Projection Detections To Cleanup Regions

**Files:**
- Modify: `EasyEDA-Loader\StepWatermarkCleaner.cs`
- Modify: `Test\StepCleaner\Program.cs`

- [ ] **Step 1: Write failing promotion test**

Add command:

```csharp
if (IsOption(args[0], "--text-logo-cleanup-promotion"))
    return RunTextLogoCleanupPromotionTests();
```

Add test:

```csharp
private static int RunTextLogoCleanupPromotionTests()
{
    var failures = new List<string>();
    string dataRoot = FindDataRoot();
    var fixtureNames = new[]
    {
        "CONN-TH_MR30PW-M30-G-Y.step",
        "USB-A-TH_FUS264-FDSW3K.step",
        "USB-B-TH_USB-B10-BRW.step",
        "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
        "CONN-TH_XT60PB-M.step"
    };

    foreach (string fixtureName in fixtureNames)
    {
        string inputPath = Path.Combine(dataRoot, "Original", fixtureName);
        var report = StepWatermarkCleaner.CleanWithReport(
            Encoding.Latin1.GetString(File.ReadAllBytes(inputPath)),
            new StepWatermarkCleanerOptions());
        var verifiedReport = StepWatermarkCleaner.CreateVerifiedCleanupDetectionReport(report.DetectionReport);
        if (verifiedReport.Regions.Count == 0)
            failures.Add(fixtureName + " should have at least one verified text/logo cleanup region.");
    }

    if (failures.Count > 0)
    {
        Console.Error.WriteLine("Text/logo cleanup promotion test failed.");
        foreach (string failure in failures)
            Console.Error.WriteLine("  " + failure);
        return 1;
    }

    Console.WriteLine("Text/logo cleanup promotion test passed.");
    return 0;
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-cleanup-promotion
```

Expected: FAIL at least for `CONN-TH_MR30PW-M30-G-Y.step`.

- [ ] **Step 3: Add projection detection stage to automatic detection**

In `DetectAutomaticWatermarks`, after existing topology detection, call:

```csharp
var projectionTextLogoRegions = MeasureCleanerTiming(
    timings,
    "detect_projection_text_logo_regions",
    () => FindProjectionTextLogoRegions(context));
PromoteProjectionTextLogoRegions(data, context, detection, projectionTextLogoRegions);
```

Create internal model:

```csharp
private sealed class ProjectionTextLogoRegion
{
    public string ViewName { get; set; }
    public string TemplateName { get; set; }
    public string Kind { get; set; }
    public string Text { get; set; }
    public Bounds ModelBounds { get; set; }
    public double Score { get; set; }
    public double ChamferDistance { get; set; }
}
```

- [ ] **Step 4: Map projection rectangles to model bounds**

Use `StepProjectionRenderer.ProjectDetectionRegions` style transform logic to convert detector rectangles back into model-space bounds. Add public/internal helper if needed:

```csharp
public static StepWatermarkMarkedRegion ConvertProjectionRectangleToMarkedRegion(
    string viewName,
    int imageWidth,
    int imageHeight,
    int x,
    int y,
    int width,
    int height,
    Bounds modelBounds,
    StepProjectionOptions options)
```

Then convert marked region axes into a `Bounds` with generous depth unset:

```csharp
Bounds bounds = BoundsFromMarkedRegion(region, modelBounds);
```

- [ ] **Step 5: Match projection regions to STEP topology**

For each projection text/logo region:

1. Find host faces whose projected bounds contain the region.
2. Find host inner loops inside the projection region.
3. Find shallow faces inside the projection region and within `HostPlaneSearchDistance`.
4. Reject if the region contains protected cylindrical faces.
5. Accept if any of these are true:

```csharp
hostLoopCount >= 1
shallowFaceCount >= options.AutomaticClusterMinFaceCount
projectionRegion.Score >= 0.70 && projectionRegion.ChamferDistance <= 4.0 && shallowFaceCount > 0
```

Add accepted regions to:

```csharp
detection.AutomaticRegions.Add(new AutomaticWatermarkRegion { ... });
MergeHostFaceBounds(detection.HostFaceBoundsToRemove, acceptedHostBounds);
```

For accepted projection-only regions with no host loop but shallow faces, add those faces to `EmbeddedFaceIds` or `CoplanarFaceIds`.

- [ ] **Step 6: Run promotion test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-cleanup-promotion
```

Expected: PASS.

- [ ] **Step 7: Commit promotion**

```powershell
git add EasyEDA-Loader\StepWatermarkCleaner.cs Test\StepCleaner\Program.cs
git commit -m "feat: promote projection text logos to cleanup regions"
```

---

### Task 5: Negative Classifier For Pins, Pads, And Connector Geometry

**Files:**
- Modify: `EasyEDA-Loader\StepTextLogoProjectionDetector.cs`
- Modify: `EasyEDA-Loader\StepWatermarkCleaner.cs`
- Modify: `Test\StepCleaner\Program.cs`

- [ ] **Step 1: Write failing negative tests**

Add command:

```csharp
if (IsOption(args[0], "--text-logo-negative-classifier"))
    return RunTextLogoNegativeClassifierTests();
```

Add:

```csharp
private static int RunTextLogoNegativeClassifierTests()
{
    var failures = new List<string>();
    string dataRoot = FindDataRoot();
    string inputPath = Path.Combine(dataRoot, "Original", "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step");
    var report = StepWatermarkCleaner.CleanWithReport(
        Encoding.Latin1.GetString(File.ReadAllBytes(inputPath)),
        new StepWatermarkCleanerOptions());

    AssertContains(report.CleanedStep, "#14214 = ADVANCED_FACE", "Gold contact #14214 should be preserved.", failures);
    AssertContains(report.CleanedStep, "#26383 = ADVANCED_FACE", "Gold contact #26383 should be preserved.", failures);
    AssertContains(report.CleanedStep, "#34754 = ADVANCED_FACE", "Gold contact #34754 should be preserved.", failures);
    AssertDoesNotContain(report.RemovedGeometryStep ?? string.Empty, "#14214 = ADVANCED_FACE", "Removed geometry should not contain gold contact #14214.", failures);
    AssertDoesNotContain(report.RemovedGeometryStep ?? string.Empty, "#26383 = ADVANCED_FACE", "Removed geometry should not contain gold contact #26383.", failures);
    AssertDoesNotContain(report.RemovedGeometryStep ?? string.Empty, "#34754 = ADVANCED_FACE", "Removed geometry should not contain gold contact #34754.", failures);

    if (failures.Count > 0)
    {
        Console.Error.WriteLine("Text/logo negative classifier test failed.");
        foreach (string failure in failures)
            Console.Error.WriteLine("  " + failure);
        return 1;
    }

    Console.WriteLine("Text/logo negative classifier test passed.");
    return 0;
}
```

- [ ] **Step 2: Run test to verify current behavior**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-negative-classifier
```

Expected: PASS if the current contact-protection fixes are present. If it fails, fix this before proceeding.

- [ ] **Step 3: Add negative image-shape signals**

In `StepTextLogoProjectionDetector`, reject template candidates whose candidate rectangle contains pin-like repeated components. Compute connected components only inside the candidate rectangle after a template match is found:

```csharp
private static bool LooksLikeRegularPinArray(SKBitmap edge, StepTextLogoDetectionRegion candidate)
{
    IReadOnlyList<ComponentBox> components = ExtractEdgeComponents(edge, candidate.X, candidate.Y, candidate.Width, candidate.Height);
    if (components.Count < 4)
        return false;

    var centersX = components.Select(c => (c.Left + c.Right) / 2.0).OrderBy(v => v).ToList();
    var gaps = new List<double>();
    for (int i = 1; i < centersX.Count; i++)
        gaps.Add(centersX[i] - centersX[i - 1]);

    if (gaps.Count < 3)
        return false;

    double avg = gaps.Average();
    double maxDeviation = gaps.Max(gap => Math.Abs(gap - avg));
    return avg > 0 && maxDeviation / avg < 0.18;
}
```

Reject the candidate if the local component structure looks like pins or a body seam instead of `LCEDA`, `EasyEDA`, or the known logo:

```csharp
if (LooksLikeRegularPinArray(edge, candidate))
    reject;
if (candidate.Width / Math.Max(1.0, candidate.Height) > 18.0 && candidate.Score < 0.85)
    reject;
```

- [ ] **Step 4: Add topology negative signals**

In `StepWatermarkCleaner`, when promoting projection regions:

```csharp
if (RegionContainsProtectedCylindricalFace(data, ownerInfo, styledByTarget, projectionRegion.ModelBounds, axis, options))
    continue;
```

Keep the existing `HostLoopContainsProtectedCylindricalFace` behavior for loop-level rejection.

- [ ] **Step 5: Run negative and promotion tests**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-negative-classifier
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-cleanup-promotion
```

Expected: both PASS.

- [ ] **Step 6: Commit negative classifier**

```powershell
git add EasyEDA-Loader\StepTextLogoProjectionDetector.cs EasyEDA-Loader\StepWatermarkCleaner.cs Test\StepCleaner\Program.cs
git commit -m "fix: reject pin geometry in text logo detector"
```

---

### Task 6: Strong Post-Clean Text/Logo Verification

**Files:**
- Modify: `EasyEDA-Loader\StepWatermarkCleanVerifier.cs`
- Modify: `StepCleaner\Program.cs`
- Modify: `Test\StepCleaner\Program.cs`

- [ ] **Step 1: Write no-op verifier failure test**

Add command:

```csharp
if (IsOption(args[0], "--text-logo-verifier"))
    return RunTextLogoVerifierTests();
```

Add:

```csharp
private static int RunTextLogoVerifierTests()
{
    var failures = new List<string>();
    string dataRoot = FindDataRoot();
    string inputPath = Path.Combine(dataRoot, "Original", "USB-B-TH_USB-B10-BRW.step");
    byte[] originalStep = File.ReadAllBytes(inputPath);
    var report = StepWatermarkCleaner.CleanWithReport(
        Encoding.Latin1.GetString(originalStep),
        new StepWatermarkCleanerOptions());

    string verifierDirectory = Path.Combine(dataRoot, "Clean", "TextLogoVerifierNoOp");
    if (Directory.Exists(verifierDirectory))
        Directory.Delete(verifierDirectory, true);

    var verifyMethod = typeof(StepWatermarkCleanVerifier).GetMethod(
        "VerifyPostCleanOutput",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
        null,
        new[]
        {
            typeof(byte[]),
            typeof(byte[]),
            typeof(string),
            typeof(StepWatermarkDetectionReport),
            typeof(string)
        },
        null);

    object verification = verifyMethod.Invoke(
        null,
        new object[]
        {
            originalStep,
            originalStep,
            "usb-b-noop",
            report.DetectionReport,
            verifierDirectory
        });
    bool passed = (bool)verification.GetType().GetProperty("Passed").GetValue(verification);
    if (passed)
        failures.Add("Verifier should fail when original STEP is supplied as clean output for a detected text/logo watermark.");

    if (failures.Count > 0)
    {
        Console.Error.WriteLine("Text/logo verifier test failed.");
        foreach (string failure in failures)
            Console.Error.WriteLine("  " + failure);
        return 1;
    }

    Console.WriteLine("Text/logo verifier test passed.");
    return 0;
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-verifier
```

Expected: FAIL if verifier still accepts no-op projection regions.

- [ ] **Step 3: Add edge-map residual verification**

In both verifier paths, for each verified cleanup view:

1. Render original color and edge projection.
2. Render clean color and edge projection.
3. Inside each verified cleanup rectangle, require:

```csharp
cleanEdgePixels <= originalEdgePixels * 0.35
```

or if the region is a flat shaded logo:

```csharp
cleanColorWatermarkPixels <= originalColorWatermarkPixels * 0.35
```

Add failure message:

```csharp
fileName + " retains text/logo edge detail on " + viewName + ": cleanEdgePixels=" + cleanEdgePixels + ", originalEdgePixels=" + originalEdgePixels
```

- [ ] **Step 4: Include non-visual failures in full markdown**

In `WriteFailedProjectionReport`, include the `failures` list before visual side-by-side sections:

```csharp
if (failures.Count > 0)
{
    lines.Add("Projection verification failures without comparison images: " + failures.Count.ToString(CultureInfo.InvariantCulture));
    lines.Add("");
    foreach (string failure in failures)
        lines.Add("- " + failure);
    lines.Add("");
}
```

- [ ] **Step 5: Run verifier test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-verifier
```

Expected: PASS.

- [ ] **Step 6: Commit verifier**

```powershell
git add EasyEDA-Loader\StepWatermarkCleanVerifier.cs StepCleaner\Program.cs Test\StepCleaner\Program.cs
git commit -m "test: verify text logo removal with edge projections"
```

---

### Task 7: Full Regression And Output Generation

**Files:**
- Modify: `Test\StepCleaner\Program.cs`
- Generated: `Test\StepCleaner\Data\Projection\*.png`
- Generated: `Test\StepCleaner\Data\Projection\*.json`
- Generated: `Test\StepCleaner\Data\Clean\*.step`
- Generated: `Test\StepCleaner\Data\RemovedGeometry\*.removed.step`

- [ ] **Step 1: Generate color projections for marking**

Run:

```powershell
dotnet run --project StepCleaner\StepCleaner.csproj -- project Test\StepCleaner\Data\Original Test\StepCleaner\Data\Projection
```

Expected: 17 files, 102 PNGs and 102 JSON files.

- [ ] **Step 2: Generate edge projections for marking**

Run:

```powershell
dotnet run --project StepCleaner\StepCleaner.csproj -- project Test\StepCleaner\Data\Original Test\StepCleaner\Data\Projection --edge
```

Expected: 17 files, 102 edge PNGs and 102 edge JSON files, using `__edge` suffixes.

- [ ] **Step 3: Run focused tests**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --projection-edge-mode
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --watermark-template-library
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-detection
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --clean-text
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-cleanup-promotion
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-negative-classifier
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-verifier
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --removed-geometry
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --xt60-lceda
```

Expected: all PASS.

- [ ] **Step 4: Run full regression**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

Expected: PASS, or fail only with explicit Clean-vs-Validated projection mismatches that require golden review. No silent ignored mismatches are allowed.

- [ ] **Step 5: Regenerate clean and removed geometry models**

Run:

```powershell
dotnet run --project StepCleaner\StepCleaner.csproj -- Test\StepCleaner\Data\Original Test\StepCleaner\Data\Clean
```

Expected:

- `Test\StepCleaner\Data\Clean` has exactly 17 `.step` files.
- `Test\StepCleaner\Data\RemovedGeometry` has exactly 17 `.removed.step` files.
- Any nonzero exit must list concrete verifier failures, not candidate-only false passes.

- [ ] **Step 6: Commit final integration**

```powershell
git add EasyEDA-Loader StepCleaner Test\StepCleaner docs\superpowers\plans\2026-06-05-six-side-text-logo-detection.md
git commit -m "feat: use six-side projections for text logo watermark detection"
```

---

## Self-Review

- Spec coverage: The plan covers six-side color projections, edge projections, template extraction from existing marked data, known `LCEDA`/`EasyEDA`/logo template matching, existing `CleanText` improvement, cleanup-region promotion, negative pin/contact classification, stronger verifier checks, projection generation for marking, and regenerated clean/removed outputs.
- Placeholder scan: No `TBD`, `TODO`, or unspecified test commands are present.
- Type consistency: `StepProjectionRenderMode`, `StepWatermarkTemplate`, `StepWatermarkTemplateLibrary`, `StepTextLogoProjectionDetector`, `StepTextLogoDetectionRegion`, and `CreateVerifiedCleanupDetectionReport` are introduced before later tasks reference them.
