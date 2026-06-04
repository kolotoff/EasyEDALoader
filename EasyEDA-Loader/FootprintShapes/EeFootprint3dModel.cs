using Newtonsoft.Json;
using PCB;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace EasyEDA_Loader
{
    public class EeFootprint3dModel : EeFootprintShape
    {
        public static EeFootprint3dModel FromString(string data)
        {
            var parts = data.Split(new[] { "~" }, StringSplitOptions.None);
            SvgNode node = JsonConvert.DeserializeObject<SvgNode>(parts[1]);
            var originParts = node.Attrs.COrigin.Split(new[] { "," }, StringSplitOptions.None);
            var rotationParts = node.Attrs.CRotation.Split(new[] { "," }, StringSplitOptions.None);

            double CenterX = EeShape.ParseDouble(originParts[0]);
            double CenterY = EeShape.ParseDouble(originParts[1]);

            // Center compute, shouldnt be needed, the GL engine does this for verification of somekind
            /*
                        if(node.Attrs.CEtype == "outline3D")
                        {
                            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
                            foreach (var childNode in node.ChildNodes)
                            {
                                var points = childNode.Attrs.Points.Split(' ');
                                for (var i = 0; i < points.Length; i += 2)
                                {
                                    minX = Math.Min(EeShape.ParseDouble(points[i]), minX);
                                    minY = Math.Min(EeShape.ParseDouble(points[i + 1]), minY);
                                    maxX = Math.Max(EeShape.ParseDouble(points[i]), maxX);
                                    maxY = Math.Max(EeShape.ParseDouble(points[i + 1]), maxY);
                                }
                            }
                            // Only use computed centers if they were computed
                            if (!double.IsPositiveInfinity(minX) && !double.IsPositiveInfinity(minY) && !double.IsNegativeInfinity(maxX) && !double.IsNegativeInfinity(maxY))
                            {
                                CenterX = ConvertToMM(maxX - (maxX - minX) / 2);
                                CenterY = ConvertToMM(maxY - (maxY - minY) / 2);
                            }
                        }
            */

            return new EeFootprint3dModel
            {
                Name = node.Attrs.Title,
                Uuid = node.Attrs.Uuid,
                Height = ConvertToMM(EeShape.ParseDouble(node.Attrs.CHeight)),
                Width = ConvertToMM(EeShape.ParseDouble(node.Attrs.CWidth)),
                Translation = new Vec3
                {
                    X = ConvertToMM(CenterX),
                    Y = ConvertToMM(CenterY),
                    Z = ConvertToMM(EeShape.ParseDouble(node.Attrs.Z))
                },
                Rotation = new Vec3
                {
                    X = EeShape.ParseDouble(rotationParts[0]),
                    Y = EeShape.ParseDouble(rotationParts[1]),
                    Z = EeShape.ParseDouble(rotationParts[2])
                }
            };
        }
        public async Task<ModelZInfo> GetZInfoFromOrigin(EeFootprintContext ctx)
        {
            return await ModelZInfoCache.GetOrCreateAsync(
                Uuid,
                async () => ctx.RawModelTask != null
                    ? await ctx.RawModelTask.ConfigureAwait(false)
                    : await ModelCache.GetRawObjModelAsync(new EasyedaApi(), Uuid, ctx.CancelToken).ConfigureAwait(false),
                ctx.CancelToken).ConfigureAwait(false);
        }

        public override bool AddToComponent(IPCB_LibComponent c, EeFootprintContext ctx)
        {
            try
            {
                string modelTraceIdentifier = FirstNonEmpty(ctx.PartNumber, Name, Uuid);
                var modelTask = ctx.ModelTask ?? ModelImportTrace.MeasureAsync("model_download_cache_read", modelTraceIdentifier, () => ModelCache.GetStepModelAsync(new EasyedaApi(), Uuid, ctx.CancelToken));
                var zInfoTask = ModelImportTrace.MeasureAsync("raw_obj_z_info", modelTraceIdentifier, () => GetZInfoFromOrigin(ctx));
                Task.WhenAll(modelTask, zInfoTask).ConfigureAwait(false).GetAwaiter().GetResult();

                byte[] originalModel = modelTask.GetAwaiter().GetResult();

                byte[] footprintModel = originalModel;
                if (ctx.RemoveWatermark)
                {
                    string cleanCacheKey = CleanStepCacheKeys.GetCleanModeKey(GetSafeCacheFileName(), ctx.CleanText);
                    ModelCacheResult cleanResult = ModelImportTrace.Measure("watermark_clean_cache", modelTraceIdentifier, () => ModelCache.GetCleanStepModelWithStatusAsync(
                            cleanCacheKey,
                            () => Task.Run(() => StepWatermarkCleanVerifier.CleanOrThrow(
                                originalModel,
                                GetSafeCacheFileName(),
                                CreateVerificationDirectory(),
                                ctx.CleanText),
                                ctx.CancelToken),
                            ctx.CancelToken)
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult());
                    EasyEDALoaderModule.Trace(
                        "Clean STEP cache " +
                        (cleanResult.CacheHit ? "hit" : "miss") +
                        ": model=" +
                        modelTraceIdentifier +
                        " path=" +
                        cleanResult.CachePath);
                    footprintModel = cleanResult.Data;
                }

                string modelIdentifier = modelTraceIdentifier;
                string temp = Path.Combine(Path.GetTempPath(), $"{GetSafeFileName(modelIdentifier)}.step");
                File.WriteAllBytes(temp, footprintModel);

                // Prefer the footprint SVGNODE origin when the product search transform carries a placeholder 0,0 offset.
                // The Z is the lowest Z of the mesh plus the Z offset (hence why we download the Raw mesh and search for the lowest vert.z as this offset is not part of the info)

                double footprintModelX = ConvertX(Translation.X, ctx);
                double footprintModelY = ConvertY(Translation.Y, ctx);
                FootprintModelMove resolvedModelCenter = FootprintModelPlacement.ResolveModelCenterMm(
                    ctx.ModelOffset?.X,
                    ctx.ModelOffset?.Y,
                    footprintModelX,
                    footprintModelY);
                double modelX = resolvedModelCenter.XMm;
                double modelY = resolvedModelCenter.YMm;
                ModelZInfo zInfo = zInfoTask.GetAwaiter().GetResult();
                double modelZOffset = ctx.ModelOffset != null ? ctx.ModelOffset.Z : Translation.Z;
                double standoffHeight = modelZOffset + zInfo.OffsetFromOrigin;
                double modelHeight = ctx.HeightMm > 0 ? ctx.HeightMm : zInfo.Height;
                double overallHeight = modelHeight > 0 ? standoffHeight + modelHeight : 0;
                if (overallHeight > 0)
                    EEPCB.SetFootprintMetadata(c, ctx.Description, overallHeight);

                FootprintModelRotation modelRotation = FootprintModelPlacement.ResolveAltiumModelRotationDeg(
                    Rotation.X,
                    Rotation.Y,
                    Rotation.Z);

                var body = EEPCB.CreateComponentBody(
                    c,
                    temp,
                    modelRotation.X,
                    modelRotation.Y,
                    modelRotation.Z,
                    modelX,
                    modelY,
                    standoffHeight,
                    modelIdentifier,
                    overallHeight);
                EEPCB.AddToPCB(c, body);
                EEPCB.CenterComponentBodyMm(c, body, modelX, modelY);
                EEPCB.SetComponentBodyIdentifier(body, modelIdentifier);
                EEPCB.SetComponentBodyHeights(body, standoffHeight, overallHeight);

                try
                {
                    FootprintModelRotation projectionRotation = FootprintModelPlacement.ResolveProjectionModelRotationDeg(modelRotation);
                    StepSilhouetteBounds projectionBounds = EEPCB.GetComponentBodyBoundsMm(c, body, modelX, modelY, Width, Height);
                    IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives = StepSilhouetteProjection.Generate(
                        footprintModel,
                        new StepSilhouettePlacement
                        {
                            TargetBounds = projectionBounds,
                            RotX = projectionRotation.X,
                            RotY = projectionRotation.Y,
                            RotZ = projectionRotation.Z,
                            Rotation2D = FootprintModelPlacement.ProjectionPlacementRotationDeg()
                        });
                    int projectionCount = EEPCB.Add3dBodyProjection(c, projectionPrimitives, true);
                    if (projectionCount > 0)
                    {
                        ctx.ProjectionPrimitives.AddRange(projectionPrimitives);
                        ctx.Has3dBodyProjection = true;
                    }
                }
                catch (Exception projectionEx)
                {
                    EasyEDALoaderModule.Trace("3D body projection failed: " + projectionEx);
                    if (ctx.Exception != null && !ctx.Exception(new InvalidOperationException(
                        "3D body projection failed: " + projectionEx.Message,
                        projectionEx)))
                    {
                        return false;
                    }
                }

                File.Delete(temp);
            }
            catch (StepWatermarkCleanFailedException ex)
            {
                ShowMarkdownReport(ex.ReportPath);
                throw;
            }
            catch (Exception ex)
            {
                if (ctx.Exception != null && !ctx.Exception(ex))
                    return false;
            }

            return true;
        }

        private string CreateVerificationDirectory()
        {
            string root = ModelCache.GetLocalDataRoot();
            string reportName =
                GetSafeCacheFileName() +
                "_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string reportDirectory = Path.Combine(root, "StepCleanerReports", reportName);
            Directory.CreateDirectory(reportDirectory);
            return reportDirectory;
        }

        private static void ShowMarkdownReport(string reportPath)
        {
            if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = reportPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("Failed to open StepCleaner report: " + ex);
            }
        }

        private string GetSafeCacheFileName()
        {
            string fileName = !string.IsNullOrWhiteSpace(Uuid) ? Uuid : Name;
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = Guid.NewGuid().ToString("N");

            return ModelCache.GetSafeFileName(fileName);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return Guid.NewGuid().ToString("N");
        }

        private static string GetSafeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = Guid.NewGuid().ToString("N");

            return ModelCache.GetSafeFileName(fileName);
        }

        public string Name { get; set; }
        public string Uuid { get; set; }
        public double Height { get; set; }
        public double Width { get; set; }
        public Vec3 Translation { get; set; }
        public Vec3 Rotation { get; set; }
        public string Raw { get; set; }
        public byte[] Step { get; set; }
    }

}
