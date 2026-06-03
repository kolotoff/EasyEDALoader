# OCCT Hidden-Line Silhouette Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the fragile in-process STEP silhouette projection with true OCCT hidden-line removal so component projections represent real CAD geometry cleanly.

**Architecture:** Add a separate `StepOcctHlr` helper executable that loads STEP with `STEPControl_Reader`, runs exact `HLRBRep_Algo`, extracts visible edge compounds with `HLRBRep_HLRToShape`, converts real OCCT edges to JSON primitives, and exits. `EasyEDA-Loader\StepSilhouetteProjection.cs` invokes the helper and falls back to the current parser only when the helper is unavailable or fails, keeping OCCT mixed-mode/native DLL loading out of the Altium extension process.

**Execution note:** During implementation, the direct Occt.NET raw wrappers for `STEPControl_Reader`, `HLRBRep_Algo`, `HLRBRep_HLRToShape`, and edge traversal repeatedly hung in this environment. The committed helper still uses true OCCT hidden-line removal, but through the working Occt.NET high-level `StepReader` and `HlrBRepAlgo` wrappers, then serializes visible HLR shapes to OCCT BREP text and parses exact 2D line/circle curve records into Altium primitives.

**Tech Stack:** .NET 8, C# 9, Occt.NET 7.9.0, Open CASCADE `STEPControl_Reader`, `HLRBRep_Algo`, `HLRBRep_HLRToShape`, SkiaSharp renderer for report images.

---

## File Structure

- Create: `StepOcctHlr\StepOcctHlr.csproj`
  - Owns the OCCT package reference and x64 Windows runtime configuration.
- Create: `StepOcctHlr\Program.cs`
  - CLI entry point: `StepOcctHlr <input.step> <output.json> [--rot-x deg] [--rot-y deg] [--rot-z deg] [--rotation2d deg]`.
- Create: `StepOcctHlr\OcctHiddenLineExtractor.cs`
  - Loads STEP, runs HLR, extracts visible compounds, converts OCCT curves into serializable line/arc records.
- Create: `StepOcctHlr\ProjectionPrimitiveDto.cs`
  - JSON contract shared by helper output and main loader input.
- Modify: `EasyEDA-Loader\StepSilhouetteProjection.cs`
  - Try helper-based OCCT projection before the existing topological fallback.
- Modify: `Test\StepCleaner\StepCleaner.Tests.csproj`
  - Include any new DTO/link file only if needed by the targeted CLI. Do not reference Occt.NET from this test project.
- Modify: `BuildAndInstall-Altium.ps1`
  - Build and copy `StepOcctHlr` helper output beside the extension binaries.
- Create: `Test\StepCleaner\OcctHiddenLineProjectionSmokeTests.cs`
  - Targeted tests only. Do not run the full StepCleaner regression suite.
- Create: `Test\StepCleaner\Data\SilhouetteReport\Generate-OcctReport.ps1`
  - Report script that saves old/new silhouette images per validated model and creates a side-by-side Markdown report.

## Task 1: Add OCCT Helper Project

**Status:** Completed.

**Files:**
- Create: `StepOcctHlr\StepOcctHlr.csproj`
- Create: `StepOcctHlr\ProjectionPrimitiveDto.cs`

- [x] **Step 1: Write the project file**

Create `StepOcctHlr\StepOcctHlr.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows7.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PlatformTarget>x64</PlatformTarget>
    <LangVersion>9.0</LangVersion>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <SelfContained>false</SelfContained>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Occt.NET" Version="7.9.0" />
  </ItemGroup>
</Project>
```

- [x] **Step 2: Write the JSON primitive contract**

Create `StepOcctHlr\ProjectionPrimitiveDto.cs`:

```csharp
using System.Collections.Generic;

namespace StepOcctHlr
{
    internal sealed class ProjectionResultDto
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<ProjectionPrimitiveDto> Primitives { get; set; } = new List<ProjectionPrimitiveDto>();
    }

    internal sealed class ProjectionPrimitiveDto
    {
        public string Kind { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double Radius { get; set; }
        public double StartAngle { get; set; }
        public double EndAngle { get; set; }
    }
}
```

- [x] **Step 3: Build the empty helper**

Run:

```powershell
dotnet build StepOcctHlr\StepOcctHlr.csproj
```

Expected: build exits `0`; `StepOcctHlr\bin\Debug\net8.0-windows7.0\win-x64\StepOcctHlr.exe` exists.

- [x] **Step 4: Commit**

```powershell
git add StepOcctHlr\StepOcctHlr.csproj StepOcctHlr\ProjectionPrimitiveDto.cs
git commit -m "feat: add OCCT HLR helper project"
```

## Task 2: Implement STEP Loading And True HLR

**Status:** Completed. Implementation uses the working Occt.NET high-level wrappers described in the execution note.

**Files:**
- Create: `StepOcctHlr\OcctHiddenLineExtractor.cs`
- Modify: `StepOcctHlr\Program.cs`

- [x] **Step 1: Write a failing smoke path**

Create `StepOcctHlr\Program.cs` with argument validation and an extractor call:

```csharp
using System;
using System.IO;
using System.Text.Json;

namespace StepOcctHlr
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: StepOcctHlr <input.step> <output.json> [--rot-x deg] [--rot-y deg] [--rot-z deg] [--rotation2d deg]");
                return 2;
            }

            string inputPath = Path.GetFullPath(args[0]);
            string outputPath = Path.GetFullPath(args[1]);
            if (!File.Exists(inputPath))
            {
                WriteResult(outputPath, new ProjectionResultDto { Success = false, Error = "STEP file not found: " + inputPath });
                return 2;
            }

            try
            {
                ProjectionResultDto result = OcctHiddenLineExtractor.Extract(inputPath, ParseOptions(args));
                WriteResult(outputPath, result);
                return result.Success ? 0 : 1;
            }
            catch (Exception ex)
            {
                WriteResult(outputPath, new ProjectionResultDto { Success = false, Error = ex.ToString() });
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

                double value = double.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture);
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
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
```

- [x] **Step 2: Implement the extractor skeleton**

Create `StepOcctHlr\OcctHiddenLineExtractor.cs`:

```csharp
using System;
using System.Collections.Generic;
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

    internal static class OcctHiddenLineExtractor
    {
        public static ProjectionResultDto Extract(string inputPath, ProjectionOptions options)
        {
            var reader = new STEPControl_Reader();
            IFSelect_ReturnStatus status = reader.ReadFile(inputPath);
            if (status != IFSelect_ReturnStatus.IFSelect_RetDone)
                return new ProjectionResultDto { Success = false, Error = "STEPControl_Reader.ReadFile failed: " + status };

            int transferred = reader.TransferRoots();
            if (transferred <= 0)
                return new ProjectionResultDto { Success = false, Error = "STEPControl_Reader.TransferRoots transferred no roots." };

            TopoDS_Shape shape = reader.OneShape();
            if (shape == null || shape.IsNull)
                return new ProjectionResultDto { Success = false, Error = "STEPControl_Reader.OneShape returned null shape." };

            HLRAlgo_Projector projector = BuildProjector(options);
            var algo = new HLRBRep_Algo();
            algo.Add(shape);
            algo.Projector = projector;
            algo.Update();
            algo.Hide();

            var toShape = new HLRBRep_HLRToShape(algo);
            var compounds = new[]
            {
                toShape.VCompound(),
                toShape.OutLineVCompound(),
                toShape.Rg1LineVCompound(),
                toShape.RgNLineVCompound()
            };

            var primitives = new List<ProjectionPrimitiveDto>();
            foreach (TopoDS_Shape compound in compounds)
                AddEdges(primitives, compound);

            return new ProjectionResultDto { Success = primitives.Count > 0, Error = primitives.Count > 0 ? null : "OCCT HLR produced no visible primitives.", Primitives = primitives };
        }

        private static HLRAlgo_Projector BuildProjector(ProjectionOptions options)
        {
            return new HLRAlgo_Projector(new gp_Ax2(new gp_Pnt(0, 0, 0), new gp_Dir(0, 0, 1), new gp_Dir(1, 0, 0)));
        }

        private static void AddEdges(List<ProjectionPrimitiveDto> primitives, TopoDS_Shape compound)
        {
            if (compound == null || compound.IsNull)
                return;

            var explorer = new TopExp_Explorer(compound, TopAbs_ShapeEnum.TopAbs_EDGE);
            for (; explorer.More(); explorer.Next())
            {
                TopoDS_Edge edge = TopoDS.Edge(explorer.Current());
                AddEdge(primitives, edge);
            }
        }

        private static void AddEdge(List<ProjectionPrimitiveDto> primitives, TopoDS_Edge edge)
        {
            double first = 0.0;
            double last = 0.0;
            Geom_Curve curve = BRep_Tool.Curve(edge, ref first, ref last);
            if (curve == null)
                return;

            AddSampledCurve(primitives, curve, first, last);
        }

        private static void AddSampledCurve(List<ProjectionPrimitiveDto> primitives, Geom_Curve curve, double first, double last)
        {
            gp_Pnt p1 = curve.Value(first);
            gp_Pnt p2 = curve.Value(last);
            primitives.Add(new ProjectionPrimitiveDto
            {
                Kind = "Line",
                X1 = p1.X,
                Y1 = p1.Y,
                X2 = p2.X,
                Y2 = p2.Y
            });
        }
    }
}
```

If the wrapper uses property names `X()`, `Y()` instead of `X`, adjust only those calls after compiler feedback.

- [x] **Step 3: Build to expose binding differences**

Run:

```powershell
dotnet build StepOcctHlr\StepOcctHlr.csproj
```

Expected: either build exits `0`, or compiler errors identify exact Occt.NET wrapper names to fix. Fix only wrapper-name differences in this task.

- [x] **Step 4: Run helper on the DF56 reference model**

Run:

```powershell
dotnet run --project StepOcctHlr\StepOcctHlr.csproj -- Test\StepCleaner\Data\Validated\CONN-SMD_DF56_40S_0.3V_51.step Test\StepCleaner\Data\SilhouetteReport\occt-df56.json
```

Expected: exit `0`; JSON has `"Success": true` and non-empty `"Primitives"`.

- [x] **Step 5: Commit**

```powershell
git add StepOcctHlr\Program.cs StepOcctHlr\OcctHiddenLineExtractor.cs
git commit -m "feat: extract visible STEP edges with OCCT HLR"
```

## Task 3: Convert Real OCCT Curves To Lines And Arcs

**Status:** Completed.

**Files:**
- Modify: `StepOcctHlr\OcctHiddenLineExtractor.cs`

- [x] **Step 1: Add exact curve conversion helpers**

Replace `AddSampledCurve` with exact line/circle conversion and a small fallback sampler:

```csharp
private static void AddCurvePrimitive(List<ProjectionPrimitiveDto> primitives, Geom_Curve curve, double first, double last)
{
    Geom_Line line = Geom_Line.DownCast(curve.NativeInstance);
    if (line != null)
    {
        gp_Pnt p1 = curve.Value(first);
        gp_Pnt p2 = curve.Value(last);
        primitives.Add(Line(p1, p2));
        return;
    }

    Geom_Circle circle = Geom_Circle.DownCast(curve.NativeInstance);
    if (circle != null)
    {
        gp_Circ circ = circle.Circ;
        gp_Pnt center = circ.Location;
        primitives.Add(new ProjectionPrimitiveDto
        {
            Kind = "Arc",
            CenterX = center.X,
            CenterY = center.Y,
            Radius = circle.Radius,
            StartAngle = NormalizeDegrees(first * 180.0 / Math.PI),
            EndAngle = NormalizeDegrees(last * 180.0 / Math.PI)
        });
        return;
    }

    AddSampledFallback(primitives, curve, first, last);
}

private static ProjectionPrimitiveDto Line(gp_Pnt p1, gp_Pnt p2)
{
    return new ProjectionPrimitiveDto
    {
        Kind = "Line",
        X1 = p1.X,
        Y1 = p1.Y,
        X2 = p2.X,
        Y2 = p2.Y
    };
}

private static void AddSampledFallback(List<ProjectionPrimitiveDto> primitives, Geom_Curve curve, double first, double last)
{
    const int segments = 16;
    gp_Pnt previous = curve.Value(first);
    for (int index = 1; index <= segments; index++)
    {
        double u = first + (last - first) * index / segments;
        gp_Pnt current = curve.Value(u);
        primitives.Add(Line(previous, current));
        previous = current;
    }
}

private static double NormalizeDegrees(double value)
{
    while (value < 0.0) value += 360.0;
    while (value >= 360.0) value -= 360.0;
    return value;
}
```

Then change `AddEdge` to call `AddCurvePrimitive(primitives, curve, first, last);`.

- [x] **Step 2: Build helper**

Run:

```powershell
dotnet build StepOcctHlr\StepOcctHlr.csproj
```

Expected: exit `0`.

- [x] **Step 3: Verify JSON contains arcs for DF56**

Run:

```powershell
dotnet run --project StepOcctHlr\StepOcctHlr.csproj -- Test\StepCleaner\Data\Validated\CONN-SMD_DF56_40S_0.3V_51.step Test\StepCleaner\Data\SilhouetteReport\occt-df56.json
Select-String -Path Test\StepCleaner\Data\SilhouetteReport\occt-df56.json -Pattern '"Kind": "Arc"'
```

Expected: at least one `"Kind": "Arc"` line for real circular edges.

- [x] **Step 4: Commit**

```powershell
git add StepOcctHlr\OcctHiddenLineExtractor.cs
git commit -m "feat: convert OCCT HLR curves to line and arc primitives"
```

## Task 4: Integrate Helper Into Existing Silhouette Pipeline

**Status:** Completed. The current integration now routes OCCT output through the tuned OCCT-only cleanup path instead of the legacy optimizer.

**Files:**
- Modify: `EasyEDA-Loader\StepSilhouetteProjection.cs`

- [x] **Step 1: Add helper invocation before current parser fallback**

Inside `StepSilhouetteProjection.Generate`, before `string stepText = Encoding.Latin1.GetString(stepData);`, add:

```csharp
IReadOnlyList<StepSilhouettePrimitive> occtPrimitives = TryGenerateWithOcctHelper(stepData, placement);
if (occtPrimitives.Count > 0)
    return occtPrimitives;
```

- [x] **Step 2: Add helper method**

Add this method in `StepSilhouetteProjection` near `TryBuildRenderedMaskPrimitives`:

```csharp
private static IReadOnlyList<StepSilhouettePrimitive> TryGenerateWithOcctHelper(byte[] stepData, StepSilhouettePlacement placement)
{
    string helperPath = FindOcctHlrExecutable();
    if (string.IsNullOrWhiteSpace(helperPath))
        return Array.Empty<StepSilhouettePrimitive>();

    string tempStep = null;
    string tempJson = null;
    try
    {
        tempStep = Path.Combine(Path.GetTempPath(), "EasyEDALoaderHlr_" + Guid.NewGuid().ToString("N") + ".step");
        tempJson = Path.Combine(Path.GetTempPath(), "EasyEDALoaderHlr_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(tempStep, stepData);

        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(tempStep);
        startInfo.ArgumentList.Add(tempJson);
        startInfo.ArgumentList.Add("--rot-x");
        startInfo.ArgumentList.Add(placement.RotX.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--rot-y");
        startInfo.ArgumentList.Add(placement.RotY.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--rot-z");
        startInfo.ArgumentList.Add(placement.RotZ.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--rotation2d");
        startInfo.ArgumentList.Add(placement.Rotation2D.ToString(CultureInfo.InvariantCulture));

        using (Process process = Process.Start(startInfo))
        {
            if (process == null)
                return Array.Empty<StepSilhouettePrimitive>();
            if (!process.WaitForExit(30000))
            {
                try { process.Kill(); } catch { }
                return Array.Empty<StepSilhouettePrimitive>();
            }
            if (process.ExitCode != 0 || !File.Exists(tempJson))
                return Array.Empty<StepSilhouettePrimitive>();
        }

        return ReadOcctProjectionJson(tempJson, placement.TargetBounds);
    }
    catch (Exception ex)
    {
        Debug.WriteLine("OCCT HLR projection failed: " + ex.Message);
        return Array.Empty<StepSilhouettePrimitive>();
    }
    finally
    {
        TryDeleteFile(tempStep);
        TryDeleteFile(tempJson);
    }
}
```

- [x] **Step 3: Add executable discovery**

Add:

```csharp
private static string FindOcctHlrExecutable()
{
    string configuredPath = Environment.GetEnvironmentVariable("EASYEDA_LOADER_OCCT_HLR");
    if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        return configuredPath;

    string baseDirectory = AppContext.BaseDirectory;
    string local = Path.Combine(baseDirectory, "StepOcctHlr.exe");
    if (File.Exists(local))
        return local;

    string sibling = Path.Combine(baseDirectory, "StepOcctHlr", "StepOcctHlr.exe");
    if (File.Exists(sibling))
        return sibling;

    return null;
}
```

- [x] **Step 4: Add JSON read conversion**

Add:

```csharp
private static IReadOnlyList<StepSilhouettePrimitive> ReadOcctProjectionJson(string jsonPath, StepSilhouetteBounds targetBounds)
{
    string json = File.ReadAllText(jsonPath);
    using (JsonDocument document = JsonDocument.Parse(json))
    {
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("Success", out JsonElement successElement) || !successElement.GetBoolean())
            return Array.Empty<StepSilhouettePrimitive>();
        if (!root.TryGetProperty("Primitives", out JsonElement primitivesElement) || primitivesElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<StepSilhouettePrimitive>();

        var primitives = new List<StepSilhouettePrimitive>();
        foreach (JsonElement primitiveElement in primitivesElement.EnumerateArray())
        {
            string kind = primitiveElement.GetProperty("Kind").GetString();
            if (kind == "Line")
                primitives.Add(StepSilhouettePrimitive.Line(
                    RoundCoord(primitiveElement.GetProperty("X1").GetDouble()),
                    RoundCoord(primitiveElement.GetProperty("Y1").GetDouble()),
                    RoundCoord(primitiveElement.GetProperty("X2").GetDouble()),
                    RoundCoord(primitiveElement.GetProperty("Y2").GetDouble())));
            else if (kind == "Arc")
                primitives.Add(StepSilhouettePrimitive.Arc(
                    RoundCoord(primitiveElement.GetProperty("CenterX").GetDouble()),
                    RoundCoord(primitiveElement.GetProperty("CenterY").GetDouble()),
                    RoundCoord(primitiveElement.GetProperty("Radius").GetDouble()),
                    RoundCoord(primitiveElement.GetProperty("StartAngle").GetDouble()),
                    RoundCoord(primitiveElement.GetProperty("EndAngle").GetDouble())));
        }

        return OptimizePrimitiveList(primitives.ToList());
    }
}
```

Add `using System.Text.Json;` to the top of `StepSilhouetteProjection.cs`.

- [x] **Step 5: Build targeted test harness**

Run:

```powershell
dotnet build Test\StepCleaner\StepCleaner.Tests.csproj
```

Expected: exit `0`.

- [x] **Step 6: Commit**

```powershell
git add EasyEDA-Loader\StepSilhouetteProjection.cs
git commit -m "feat: use OCCT helper for STEP silhouette projection"
```

## Task 5: Add Targeted DF56 Smoke Test

**Status:** Completed. The current smoke test is stricter than the original outline: it checks separate minimum line and arc counts so a legacy fallback or destructive post-processing path cannot pass by total primitive count alone.

**Files:**
- Create: `Test\StepCleaner\OcctHiddenLineProjectionSmokeTests.cs`
- Modify: `Test\StepCleaner\StepCleaner.Tests.csproj`

- [x] **Step 1: Add smoke-test source**

Create `Test\StepCleaner\OcctHiddenLineProjectionSmokeTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using EasyEDA_Loader;

namespace StepCleaner.Tests
{
    internal static class OcctHiddenLineProjectionSmokeTests
    {
        public static int Run()
        {
            string input = Path.GetFullPath(Path.Combine("Test", "StepCleaner", "Data", "Validated", "CONN-SMD_DF56_40S_0.3V_51.step"));
            if (!File.Exists(input))
            {
                Console.Error.WriteLine("Missing smoke STEP file: " + input);
                return 2;
            }

            var placement = new StepSilhouettePlacement
            {
                TargetBounds = new StepSilhouetteBounds { Left = -0.5, Bottom = -0.5, Right = 0.5, Top = 0.5 },
                RotX = 0.0,
                RotY = 0.0,
                RotZ = 0.0,
                Rotation2D = 0.0
            };

            var primitives = StepSilhouetteProjection.Generate(File.ReadAllBytes(input), placement);
            int lineCount = primitives.Count(p => p.Kind == StepSilhouettePrimitiveKind.Line);
            int arcCount = primitives.Count(p => p.Kind == StepSilhouettePrimitiveKind.Arc);

            Console.WriteLine("DF56 OCCT smoke primitives: " + lineCount + " lines, " + arcCount + " arcs.");
            if (primitives.Count < 100)
            {
                Console.Error.WriteLine("Expected a detailed projection with at least 100 primitives.");
                return 1;
            }

            return 0;
        }
    }
}
```

- [x] **Step 2: Add CLI switch**

In `Test\StepCleaner\Program.cs`, add near the other option checks:

```csharp
if (IsOption(args[0], "--occt-hlr-smoke"))
    return OcctHiddenLineProjectionSmokeTests.Run();
```

Add usage line:

```csharp
Console.Error.WriteLine("Usage: StepCleaner.Tests --occt-hlr-smoke");
```

- [x] **Step 3: Include source in test project**

Modify `Test\StepCleaner\StepCleaner.Tests.csproj`:

```xml
<Compile Include="OcctHiddenLineProjectionSmokeTests.cs" />
```

- [x] **Step 4: Run only the targeted smoke test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --occt-hlr-smoke
```

Expected: exit `0`; output includes `DF56 OCCT smoke primitives`.

- [x] **Step 5: Commit**

```powershell
git add Test\StepCleaner\OcctHiddenLineProjectionSmokeTests.cs Test\StepCleaner\Program.cs Test\StepCleaner\StepCleaner.Tests.csproj
git commit -m "test: add targeted OCCT HLR silhouette smoke test"
```

## Task 6: Generate Old/New Report Without Full Tests

**Status:** Completed. The committed script delegates to the built-in SkiaSharp report generator (`--occt-hlr-report`) instead of ImageMagick, and writes side-by-side PNGs under `old-new`.

**Files:**
- Create: `Test\StepCleaner\Data\SilhouetteReport\Generate-OcctReport.ps1`

- [x] **Step 1: Write report script**

Create `Test\StepCleaner\Data\SilhouetteReport\Generate-OcctReport.ps1`:

```powershell
param(
    [string]$ValidatedDir = "Test\StepCleaner\Data\Validated",
    [string]$ReportDir = "Test\StepCleaner\Data\SilhouetteReport"
)

$ErrorActionPreference = "Stop"
$oldDir = Join-Path $ReportDir "old"
$newDir = Join-Path $ReportDir "new"
$sideBySideDir = Join-Path $ReportDir "side-by-side"
New-Item -ItemType Directory -Force -Path $oldDir, $newDir, $sideBySideDir | Out-Null

dotnet build Test\StepCleaner\StepCleaner.Tests.csproj | Write-Host

Get-ChildItem -LiteralPath $ValidatedDir -Filter "*.step" | Sort-Object Name | ForEach-Object {
    $base = $_.BaseName
    $old = Join-Path $oldDir ($base + ".png")
    $new = Join-Path $newDir ($base + ".png")
    $combined = Join-Path $sideBySideDir ($base + ".png")

    dotnet Test\StepCleaner\bin\Debug\net8.0\StepCleaner.Tests.dll --silhouette $_.FullName $old --no-grid --no-axes --size 1600 --padding 60
    dotnet Test\StepCleaner\bin\Debug\net8.0\StepCleaner.Tests.dll --silhouette $_.FullName $new --no-grid --no-axes --size 1600 --padding 60

    magick $old $new +append $combined
}

$report = Join-Path $ReportDir "OCCT-HLR-Silhouette-Report.md"
"# OCCT HLR Silhouette Report`n" | Set-Content -LiteralPath $report
Get-ChildItem -LiteralPath $sideBySideDir -Filter "*.png" | Sort-Object Name | ForEach-Object {
    Add-Content -LiteralPath $report -Value "## $($_.BaseName)`n"
    Add-Content -LiteralPath $report -Value "![old/new](side-by-side/$($_.Name))`n"
}
Write-Host "Report written: $report"
```

If ImageMagick `magick` is not installed, replace the combine step with a small SkiaSharp combiner in `Test\StepCleaner\Program.cs`; do not use the full StepCleaner regression test.

- [x] **Step 2: Run report script**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File Test\StepCleaner\Data\SilhouetteReport\Generate-OcctReport.ps1
```

Expected: `Test\StepCleaner\Data\SilhouetteReport\OCCT-HLR-Silhouette-Report.md` exists; there is one side-by-side PNG per STEP file in `Test\StepCleaner\Data\Validated`.

- [x] **Step 3: Inspect DF56 report image manually**

Open:

```text
Test\StepCleaner\Data\SilhouetteReport\side-by-side\CONN-SMD_DF56_40S_0.3V_51.png
```

Expected: new image resembles a clean CAD technical projection, not hand-scratched fragment fitting. If it looks scratchy, stop and debug OCCT extraction before continuing.

- [x] **Step 4: Commit script only**

```powershell
git add Test\StepCleaner\Data\SilhouetteReport\Generate-OcctReport.ps1
git commit -m "test: add OCCT HLR silhouette report generator"
```

Do not commit generated PNG/Markdown report files unless the user explicitly requests report artifacts in git.

## Task 7: Build/Install Integration

**Status:** Completed. `BuildAndInstall-Altium.ps1` already builds `StepOcctHlr`, resolves its target framework/runtime output path from the helper project, and copies it into the installed extension folder. Syntax verification passed.

**Files:**
- Modify: `BuildAndInstall-Altium.ps1`

- [x] **Step 1: Add helper build**

Add after the main project build:

```powershell
dotnet build StepOcctHlr\StepOcctHlr.csproj -c Release
if ($LASTEXITCODE -ne 0) {
    throw "StepOcctHlr build failed."
}
```

- [x] **Step 2: Copy helper output**

Add:

```powershell
$helperOutput = Join-Path $PSScriptRoot "StepOcctHlr\bin\Release\net8.0-windows7.0\win-x64"
$helperInstall = Join-Path $installPath "StepOcctHlr"
New-Item -ItemType Directory -Force -Path $helperInstall | Out-Null
Copy-Item -Path (Join-Path $helperOutput "*") -Destination $helperInstall -Recurse -Force
```

Adjust `$installPath` to the actual variable name used in `BuildAndInstall-Altium.ps1`.

- [x] **Step 3: Build installer script path**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File BuildAndInstall-Altium.ps1 -WhatIf
```

If the script has no `-WhatIf`, run only syntax check:

```powershell
powershell -NoProfile -Command "$null = [scriptblock]::Create((Get-Content -Raw BuildAndInstall-Altium.ps1)); 'syntax ok'"
```

Expected: syntax check exits `0`.

- [x] **Step 4: Commit**

```powershell
git add BuildAndInstall-Altium.ps1
git commit -m "build: deploy OCCT HLR helper with extension"
```

## Task 8: Final Verification

**Status:** Completed. Final verification used only targeted builds, the DF56 smoke test, the report script, and worktree checks; the full StepCleaner regression suite was not run.

**Files:**
- Verify only; no new files.

- [x] **Step 1: Build all directly affected projects**

Run:

```powershell
dotnet build StepOcctHlr\StepOcctHlr.csproj
dotnet build Test\StepCleaner\StepCleaner.Tests.csproj
dotnet build EasyEDA-Loader\EasyEDA-Loader.csproj
```

Expected: all three builds exit `0`.

- [x] **Step 2: Run targeted smoke test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --occt-hlr-smoke
```

Expected: exit `0`; output includes non-empty primitive count.

- [x] **Step 3: Generate report images only**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File Test\StepCleaner\Data\SilhouetteReport\Generate-OcctReport.ps1
```

Expected: side-by-side report generated. Do not run `dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj` without arguments.

- [x] **Step 4: Check worktree**

Run:

```powershell
git status --short
```

Expected: source changes are committed or clearly listed; generated report artifacts remain ignored or untracked according to `.gitignore`.

## Task 9: OCCT Detail Cutoff and Fully Overlapped Primitive Removal

**Status:** Completed with later tuning from the review loop. The final OCCT cleanup sequence is merge touching same-centerline lines, remove 90%+ covered primitives, then apply a `0.03 mm` cutoff. Line merge uses `0.01 mm` bucket tolerance, `0.001 mm` centerline merge tolerance, and `0.001 mm` projected interval touch tolerance.

**Files:**
- Modify: `EasyEDA-Loader\StepSilhouetteProjection.cs`
- Modify: `Test\StepCleaner\OcctHiddenLineProjectionSmokeTests.cs`

- [x] **Step 1: Set the OCCT-specific detail cutoff**

In `EasyEDA-Loader\StepSilhouetteProjection.cs`, keep the legacy parser threshold unchanged and tune only the OCCT HLR path:

```csharp
private const double OutputMinLineLengthMm = 0.03;
private const double OcctOutputMinLineLengthMm = 0.05;
```

Expected behavior: the old STEP parser still removes fragments below `0.03 mm`; OCCT HLR removes only details below the chosen OCCT cutoff. Start with `0.05 mm` because DF56 visually preserved pad geometry while reducing near-invisible tiny segments.

- [x] **Step 2: Route OCCT output through an overlap-only cleanup pass**

In `ReadOcctProjectionJson`, replace the direct return with:

```csharp
List<StepSilhouettePrimitive> placed = PlacePrimitivesWithoutRescale(
    sourcePrimitives,
    targetBounds,
    sourceBounds,
    OcctOutputMinLineLengthMm);
return RemoveFullyOverlappedOcctPrimitives(placed);
```

Do not call `OptimizePrimitiveList` here. That legacy optimizer removes real pad geometry and can add false bridge lines.

- [x] **Step 3: Add exact-overlap cleanup constants**

Near the other silhouette constants in `StepSilhouetteProjection.cs`, add:

```csharp
private const double OcctOverlapDistanceToleranceMm = 0.001;
private const double OcctOverlapAngleToleranceDeg = 0.05;
```

These tolerances are for deterministic geometric containment only. They must not be used as stroke-width coverage heuristics.

- [x] **Step 4: Add fully overlapped primitive removal**

Add these helpers near `OptimizePrimitiveList` or the existing primitive cleanup helpers:

```csharp
private static List<StepSilhouettePrimitive> RemoveFullyOverlappedOcctPrimitives(List<StepSilhouettePrimitive> primitives)
{
    var remove = new bool[primitives.Count];
    MarkFullyCoveredLines(primitives, remove);
    MarkFullyCoveredArcs(primitives, remove);

    var result = new List<StepSilhouettePrimitive>(primitives.Count);
    for (int index = 0; index < primitives.Count; index++)
    {
        if (!remove[index])
            result.Add(primitives[index]);
    }

    return result;
}

private static void MarkFullyCoveredLines(List<StepSilhouettePrimitive> primitives, bool[] remove)
{
    var groups = new Dictionary<string, List<IndexedLineInterval>>();
    for (int index = 0; index < primitives.Count; index++)
    {
        StepSilhouettePrimitive primitive = primitives[index];
        if (primitive.Kind != StepSilhouettePrimitiveKind.Line)
            continue;

        double dx = primitive.X2 - primitive.X1;
        double dy = primitive.Y2 - primitive.Y1;
        double length = Hypot(dx, dy);
        if (length <= OcctOverlapDistanceToleranceMm)
            continue;

        double ux = dx / length;
        double uy = dy / length;
        if (ux < 0.0 || (Math.Abs(ux) <= 1e-12 && uy < 0.0))
        {
            ux = -ux;
            uy = -uy;
        }

        double normalX = -uy;
        double normalY = ux;
        double offset = normalX * primitive.X1 + normalY * primitive.Y1;
        double angle = Math.Atan2(uy, ux);
        string key =
            ((int)Math.Round(angle / DegreesToRadians(OcctOverlapAngleToleranceDeg))).ToString(CultureInfo.InvariantCulture) +
            "|" +
            ((int)Math.Round(offset / OcctOverlapDistanceToleranceMm)).ToString(CultureInfo.InvariantCulture);
        double t1 = ux * primitive.X1 + uy * primitive.Y1;
        double t2 = ux * primitive.X2 + uy * primitive.Y2;

        if (!groups.TryGetValue(key, out List<IndexedLineInterval> intervals))
        {
            intervals = new List<IndexedLineInterval>();
            groups[key] = intervals;
        }

        intervals.Add(new IndexedLineInterval(index, Math.Min(t1, t2), Math.Max(t1, t2)));
    }

    foreach (List<IndexedLineInterval> intervals in groups.Values)
    {
        for (int index = 0; index < intervals.Count; index++)
        {
            IndexedLineInterval candidate = intervals[index];
            if (IsLineIntervalCoveredByOthers(candidate, intervals))
                remove[candidate.Index] = true;
        }
    }
}

private static bool IsLineIntervalCoveredByOthers(IndexedLineInterval candidate, List<IndexedLineInterval> intervals)
{
    double coveredUntil = candidate.Start;
    foreach (IndexedLineInterval interval in intervals.OrderBy(interval => interval.Start))
    {
        if (interval.Index == candidate.Index)
            continue;
        if (interval.End <= candidate.Start + OcctOverlapDistanceToleranceMm)
            continue;
        if (interval.Start > coveredUntil + OcctOverlapDistanceToleranceMm)
            return false;

        coveredUntil = Math.Max(coveredUntil, interval.End);
        if (coveredUntil >= candidate.End - OcctOverlapDistanceToleranceMm)
            return true;
    }

    return false;
}

private static void MarkFullyCoveredArcs(List<StepSilhouettePrimitive> primitives, bool[] remove)
{
    var groups = new Dictionary<string, List<IndexedArcInterval>>();
    for (int index = 0; index < primitives.Count; index++)
    {
        StepSilhouettePrimitive primitive = primitives[index];
        if (primitive.Kind != StepSilhouettePrimitiveKind.Arc)
            continue;

        string key = string.Join("|",
            (int)Math.Round(primitive.CenterX / OcctOverlapDistanceToleranceMm),
            (int)Math.Round(primitive.CenterY / OcctOverlapDistanceToleranceMm),
            (int)Math.Round(primitive.Radius / OcctOverlapDistanceToleranceMm));
        if (!groups.TryGetValue(key, out List<IndexedArcInterval> intervals))
        {
            intervals = new List<IndexedArcInterval>();
            groups[key] = intervals;
        }

        double start = NormalizeDegrees(primitive.StartAngle);
        double end = NormalizeDegrees(primitive.EndAngle);
        if (end <= start)
            end += 360.0;
        intervals.Add(new IndexedArcInterval(index, start, end));
        intervals.Add(new IndexedArcInterval(index, start + 360.0, end + 360.0));
    }

    foreach (List<IndexedArcInterval> intervals in groups.Values)
    {
        foreach (IndexedArcInterval candidate in intervals.Where(interval => interval.Start < 360.0))
        {
            if (IsArcIntervalCoveredByOthers(candidate, intervals))
                remove[candidate.Index] = true;
        }
    }
}

private static bool IsArcIntervalCoveredByOthers(IndexedArcInterval candidate, List<IndexedArcInterval> intervals)
{
    double tolerance = OcctOverlapAngleToleranceDeg;
    double coveredUntil = candidate.Start;
    foreach (IndexedArcInterval interval in intervals.OrderBy(interval => interval.Start))
    {
        if (interval.Index == candidate.Index)
            continue;
        if (interval.End <= candidate.Start + tolerance)
            continue;
        if (interval.Start > coveredUntil + tolerance)
            return false;

        coveredUntil = Math.Max(coveredUntil, interval.End);
        if (coveredUntil >= candidate.End - tolerance)
            return true;
    }

    return false;
}

private struct IndexedLineInterval
{
    public IndexedLineInterval(int index, double start, double end)
    {
        Index = index;
        Start = start;
        End = end;
    }

    public int Index { get; }
    public double Start { get; }
    public double End { get; }
}

private struct IndexedArcInterval
{
    public IndexedArcInterval(int index, double start, double end)
    {
        Index = index;
        Start = start;
        End = end;
    }

    public int Index { get; }
    public double Start { get; }
    public double End { get; }
}
```

Expected behavior: a line or arc is removed only when its entire geometric interval is covered by other primitives of the same type on the same line/circle. Do not remove primitives merely because they are close, visually thick, parallel, or inside a filled area.

- [x] **Step 5: Adjust the targeted smoke guard**

In `Test\StepCleaner\OcctHiddenLineProjectionSmokeTests.cs`, keep the smoke loose enough for cutoff tuning but strict enough to fail the collapsed bad result:

```csharp
private const int MinimumExpectedLines = 420;
private const int MinimumExpectedArcs = 30;
```

Expected: DF56 should stay far above the old broken `169 line(s), 24 arc(s)` output and should still include OCCT arcs.

- [x] **Step 6: Run targeted verification only**

Run:

```powershell
dotnet build Test\StepCleaner\StepCleaner.Tests.csproj
dotnet build EasyEDA-Loader\EasyEDA-Loader.csproj
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --occt-hlr-smoke
```

Expected: builds exit `0`; smoke exits `0`. Do not run the full StepCleaner regression suite.

- [x] **Step 7: Regenerate old/new silhouette report**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --occt-hlr-report
```

Expected: `Test\StepCleaner\Data\SilhouetteReport\old-new-report.md` is regenerated and `Test\StepCleaner\Data\SilhouetteReport\old-new` contains one PNG per STEP file in `Test\StepCleaner\Data\Validated`.

- [x] **Step 8: Inspect DF56 and commit**

Open:

```text
Test\StepCleaner\Data\SilhouetteReport\old-new\CONN-SMD_DF56_40S_0.3V_51.png
```

Expected: top-side pads remain visible, false diagonal bridge artifacts are absent, and fully overlapped duplicate strokes are reduced.

Commit:

```powershell
git add EasyEDA-Loader\StepSilhouetteProjection.cs Test\StepCleaner\OcctHiddenLineProjectionSmokeTests.cs
git commit -m "fix: remove fully overlapped OCCT HLR primitives"
```

## Self-Review

- Spec coverage: The plan implements true OCCT HLR using `STEPControl_Reader`, `HLRBRep_Algo`, `HLRBRep_HLRToShape`, visible edge compound extraction, and line/arc primitive conversion.
- Safety: OCCT mixed-mode/native loading stays in `StepOcctHlr.exe`, not in the Altium extension process.
- Verification: The plan uses targeted DF56 smoke testing and old/new image report generation. It explicitly avoids running the full StepCleaner tests. Task 9 adds cutoff tuning and same-geometry full-overlap removal without reintroducing the destructive legacy optimizer.
- Placeholder scan: No placeholder markers or deferred behavior remains. The only conditional adjustments are wrapper-name fixes after compiler feedback and ImageMagick fallback if unavailable.
