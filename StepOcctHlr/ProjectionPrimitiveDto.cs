using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace StepOcctHlr
{
    internal sealed class ProjectionResultDto
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<ProjectionPrimitiveDto> Primitives { get; set; } = new List<ProjectionPrimitiveDto>();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ProjectionViewResultDto> Views { get; set; }
    }

    internal sealed class ProjectionViewResultDto
    {
        public string Name { get; set; }
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
        public double[] Points { get; set; }
        public string OriginalKind { get; set; }
        public double Tolerance { get; set; }
    }
}
