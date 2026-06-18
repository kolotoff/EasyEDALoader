using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EasyEDA_Loader
{
    public static partial class StepProjectionRenderer
    {
        public static StepVectorWatermarkDetectionInput ProjectVectorWatermarkDetectionInput(
            byte[] stepData,
            string modelName,
            string viewName,
            StepProjectionOptions options = null)
        {
            if (stepData == null)
                throw new ArgumentNullException(nameof(stepData));
            if (string.IsNullOrWhiteSpace(viewName))
                throw new ArgumentException("Projection view name is required.", nameof(viewName));

            StepProjectionOptions vectorOptions = CloneSingleViewOptions(options, viewName);
            vectorOptions.RenderMode = StepProjectionRenderMode.EdgeVisibleRaw;

            string stepText = Encoding.Latin1.GetString(stepData);
            StepModel model = StepModel.Parse(stepText);
            model.BuildIndexes();
            ProjectionModel drawingModel = ProjectionModel.Build(model);

            ViewSpec view = GetSelectedViews(vectorOptions).First();
            ProjectionTransform transform = ProjectionTransform.Create(drawingModel.Bounds, view, vectorOptions);
            var placements = new Dictionary<string, StepSilhouettePlacement>(StringComparer.OrdinalIgnoreCase)
            {
                [view.Name] = CreateRawSilhouettePlacement(transform)
            };

            StepVectorWatermarkImageMapping mapping = CreateVectorWatermarkImageMapping(transform, vectorOptions);
            var mappings = new Dictionary<string, StepVectorWatermarkImageMapping>(StringComparer.OrdinalIgnoreCase)
            {
                [view.Name] = mapping
            };
            IReadOnlyDictionary<string, IReadOnlyList<StepVectorWatermarkPrimitive>> primitivesByView =
                StepSilhouetteProjection.GenerateVectorWatermarkViews(stepData, placements, mappings);
            primitivesByView.TryGetValue(view.Name, out IReadOnlyList<StepVectorWatermarkPrimitive> primitives);

            return new StepVectorWatermarkDetectionInput
            {
                ModelName = string.IsNullOrWhiteSpace(modelName) ? "model" : modelName,
                ViewName = view.Name,
                ImageWidth = GetImageWidthPixels(vectorOptions),
                ImageHeight = GetImageHeightPixels(vectorOptions),
                ImageMapping = mapping,
                Primitives = primitives ?? Array.Empty<StepVectorWatermarkPrimitive>()
            };
        }

        public static IReadOnlyDictionary<string, StepVectorWatermarkDetectionInput> ProjectVectorWatermarkDetectionInputs(
            byte[] stepData,
            string modelName,
            IEnumerable<string> viewNames,
            StepProjectionOptions options = null)
        {
            if (stepData == null)
                throw new ArgumentNullException(nameof(stepData));

            StepProjectionOptions vectorOptions = CloneVectorWatermarkOptions(options, viewNames);
            vectorOptions.RenderMode = StepProjectionRenderMode.EdgeVisibleRaw;

            string stepText = Encoding.Latin1.GetString(stepData);
            StepModel model = StepModel.Parse(stepText);
            model.BuildIndexes();
            ProjectionModel drawingModel = ProjectionModel.Build(model);

            IReadOnlyList<ViewSpec> selectedViews = GetSelectedViews(vectorOptions);
            Dictionary<string, ProjectionTransform> transformsByView = selectedViews.ToDictionary(
                view => view.Name,
                view => ProjectionTransform.Create(drawingModel.Bounds, view, vectorOptions),
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, StepSilhouettePlacement> placements = selectedViews.ToDictionary(
                view => view.Name,
                view => CreateRawSilhouettePlacement(transformsByView[view.Name]),
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, StepVectorWatermarkImageMapping> mappings = selectedViews.ToDictionary(
                view => view.Name,
                view => CreateVectorWatermarkImageMapping(transformsByView[view.Name], vectorOptions),
                StringComparer.OrdinalIgnoreCase);

            IReadOnlyDictionary<string, IReadOnlyList<StepVectorWatermarkPrimitive>> primitivesByView =
                StepSilhouetteProjection.GenerateVectorWatermarkViews(stepData, placements, mappings);

            var result = new Dictionary<string, StepVectorWatermarkDetectionInput>(StringComparer.OrdinalIgnoreCase);
            foreach (ViewSpec view in selectedViews)
            {
                primitivesByView.TryGetValue(view.Name, out IReadOnlyList<StepVectorWatermarkPrimitive> primitives);
                result[view.Name] = new StepVectorWatermarkDetectionInput
                {
                    ModelName = string.IsNullOrWhiteSpace(modelName) ? "model" : modelName,
                    ViewName = view.Name,
                    ImageWidth = GetImageWidthPixels(vectorOptions),
                    ImageHeight = GetImageHeightPixels(vectorOptions),
                    ImageMapping = mappings[view.Name],
                    Primitives = primitives ?? Array.Empty<StepVectorWatermarkPrimitive>()
                };
            }

            return result;
        }

        private static StepProjectionOptions CloneVectorWatermarkOptions(
            StepProjectionOptions options,
            IEnumerable<string> viewNames)
        {
            var clone = new StepProjectionOptions
            {
                ImageSizePixels = options?.ImageSizePixels ?? 1600,
                ImageWidthPixels = options?.ImageWidthPixels ?? 0,
                ImageHeightPixels = options?.ImageHeightPixels ?? 0,
                PaddingPixels = options?.PaddingPixels ?? 80,
                WriteMetadata = false,
                SkipGeometryModelForExternalRender = options?.SkipGeometryModelForExternalRender ?? false,
                MaxParallelFiles = options?.MaxParallelFiles ?? 1,
                RenderMode = options?.RenderMode ?? StepProjectionRenderMode.Color
            };

            foreach (string viewName in viewNames ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(viewName) &&
                    !clone.ViewNames.Any(existing => string.Equals(existing, viewName, StringComparison.OrdinalIgnoreCase)))
                {
                    clone.ViewNames.Add(viewName);
                }
            }

            return NormalizeOptions(clone);
        }

        private static StepVectorWatermarkImageMapping CreateVectorWatermarkImageMapping(
            ProjectionTransform transform,
            StepProjectionOptions options)
        {
            return new StepVectorWatermarkImageMapping
            {
                ImageWidth = GetImageWidthPixels(options),
                ImageHeight = GetImageHeightPixels(options),
                PaddingPixels = options.PaddingPixels,
                UMin = transform.UMin,
                UMax = transform.UMax,
                VMin = transform.VMin,
                VMax = transform.VMax,
                Scale = transform.Scale
            };
        }

    }
}
