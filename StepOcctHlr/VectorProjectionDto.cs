using System.Collections.Generic;

namespace StepOcctHlr
{
    internal sealed class VectorProjectionResultDto
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string Engine { get; set; }
        public List<VectorProjectionViewDto> Views { get; set; } = new List<VectorProjectionViewDto>();
    }

    internal sealed class VectorProjectionViewDto
    {
        public string Name { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public VectorProjectionBoundsDto Bounds { get; set; }
        public List<VectorProjectionPrimitiveDto> Primitives { get; set; } = new List<VectorProjectionPrimitiveDto>();
    }

    internal sealed class VectorProjectionBoundsDto
    {
        public double Left { get; set; }
        public double Bottom { get; set; }
        public double Right { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    internal sealed class VectorProjectionPrimitiveDto
    {
        public string Kind { get; set; }
        public string Visibility { get; set; }
        public string Category { get; set; }
        public int SourceIndex { get; set; }
        public double[] Points { get; set; }
        public double[] Knots { get; set; }
        public double[] Weights { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double Radius { get; set; }
        public double StartAngle { get; set; }
        public double EndAngle { get; set; }
        public string OriginalKind { get; set; }
        public double Tolerance { get; set; }
    }
}
