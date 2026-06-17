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
