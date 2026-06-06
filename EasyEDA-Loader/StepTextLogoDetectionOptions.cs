namespace EasyEDA_Loader
{
    public sealed class StepTextLogoDetectionOptions
    {
        public bool DetectArbitraryText { get; set; }
        public int MinimumRegionWidth { get; set; } = 6;
        public int MinimumRegionHeight { get; set; } = 6;
        public int MinimumEdgePixels { get; set; } = 24;
        public double MinimumKnownTemplateScore { get; set; } = 0.20;
        public double MinimumArbitraryTextScore { get; set; } = 0.58;
        public double MaximumRegionExpansionRatio { get; set; } = 1.12;
        public string LogoReferenceImagePath { get; set; }
        public bool UseColorProjectionCandidates { get; set; }
        public bool UseGrayscaleLogoMatching { get; set; }
        public bool UseSiftLogoMatching { get; set; }
        public bool UseGeneralizedHoughLogoMatching { get; set; }
        public bool IncludeCombinedWatermarkRegion { get; set; }
    }
}
