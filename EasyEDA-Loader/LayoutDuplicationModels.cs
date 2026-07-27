using PCB;
using System;
using System.Collections.Generic;

namespace EasyEDA_Loader
{
    internal static class LayoutDuplicationDefaults
    {
        public const string DefaultModelName = "qwen3.5:9b";
        public const string FallbackModelName = "qwen2.5-coder:7b-instruct";
        public const string LastModelFileName = "layout-duplicator-model.txt";
    }

    internal sealed class LayoutDuplicationSession
    {
        public IPCB_Board Board { get; set; }
        public List<LayoutComponentSnapshot> SourceComponents { get; } = new List<LayoutComponentSnapshot>();
        public List<LayoutComponentSnapshot> BoardComponents { get; } = new List<LayoutComponentSnapshot>();
        public int SelectedRoutingPrimitiveCount { get; set; }
        public string LastUsedModel { get; set; }
    }

    internal sealed class LayoutComponentSnapshot
    {
        public string Designator { get; set; }
        public string PartNumber { get; set; }
        public string Comment { get; set; }
        public string Description { get; set; }
        public string Footprint { get; set; }
        public string Layer { get; set; }
        public double XMm { get; set; }
        public double YMm { get; set; }
        public double Rotation { get; set; }
        public object PcbObject { get; set; }
        public List<LayoutPadSnapshot> Pads { get; } = new List<LayoutPadSnapshot>();
    }

    internal sealed class LayoutPadSnapshot
    {
        public string Name { get; set; }
        public string Net { get; set; }
    }

    internal sealed class LayoutMappingRequest
    {
        public LayoutComponentSnapshot SourceAnchor { get; set; }
        public IReadOnlyList<LayoutComponentSnapshot> SourceComponents { get; set; }
        public IReadOnlyList<LayoutComponentSnapshot> TargetAnchors { get; set; }
        public IReadOnlyList<LayoutComponentSnapshot> DestinationCandidates { get; set; }
        public bool UseSchematicMatching { get; set; }
        public IReadOnlyList<LayoutSchematicComponentHint> SchematicHints { get; set; }
            = Array.Empty<LayoutSchematicComponentHint>();
    }

    internal sealed class LayoutSchematicMatchContext
    {
        public List<LayoutSchematicComponentHint> Hints { get; } = new List<LayoutSchematicComponentHint>();
        public bool HasHints => Hints.Count > 0;
    }

    internal sealed class LayoutSchematicComponentHint
    {
        public string Designator { get; set; }
        public string SheetPath { get; set; }
        public string PartNumber { get; set; }
        public string Comment { get; set; }
        public string Description { get; set; }
        public string Footprint { get; set; }
        public string ComponentKind { get; set; }
        public List<string> NetNames { get; } = new List<string>();
        public List<string> PinNames { get; } = new List<string>();
    }

    internal sealed class LayoutMappingValidationResult
    {
        public List<LayoutValidatedGroup> ValidGroups { get; } = new List<LayoutValidatedGroup>();
        public List<string> Errors { get; } = new List<string>();
        public bool HasValidGroups => ValidGroups.Count > 0;
    }

    internal sealed class LayoutValidatedGroup
    {
        public string TargetAnchorDesignator { get; set; }
        public Dictionary<string, string> SourceToDestination { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class LayoutDuplicationResult
    {
        public int PlacedComponents { get; set; }
        public int CopiedRoutingPrimitives { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    internal sealed class OllamaModelInfo
    {
        public string Name { get; set; }
        public bool IsInstalled { get; set; }
        public bool IsLoaded { get; set; }

        public override string ToString()
        {
            if (IsLoaded)
                return Name + " (loaded)";
            if (IsInstalled)
                return Name;
            return Name + " (not installed)";
        }
    }

    internal sealed class LayoutDuplicationProgress
    {
        public string Message { get; set; }
        public double? Percent { get; set; }
        public bool IsIndeterminate { get; set; } = true;
    }
}
