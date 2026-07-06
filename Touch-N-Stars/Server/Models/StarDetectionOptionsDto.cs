namespace TouchNStars.Server.Models {

    /// <summary>
    /// DTO for serializing/deserializing StarDetectionOptions
    /// </summary>
    public class StarDetectionOptionsDto {
        public bool UseAdvanced { get; set; }
        public bool ModelPSF { get; set; }
        public string Simple_NoiseLevel { get; set; }
        public string Simple_PixelScale { get; set; }
        public string Simple_FocusRange { get; set; }
        public bool HotpixelFiltering { get; set; }
        public bool HotpixelThresholdingEnabled { get; set; }
        public bool UseAutoFocusCrop { get; set; }
        public int NoiseReductionRadius { get; set; }
        public double NoiseClippingMultiplier { get; set; }
        public double StarClippingMultiplier { get; set; }
        public int StructureLayers { get; set; }
        public double BrightnessSensitivity { get; set; }
        public double StarPeakResponse { get; set; }
        public double MaxDistortion { get; set; }
        public double StarCenterTolerance { get; set; }
        public int StarBackgroundBoxExpansion { get; set; }
        public int MinStarBoundingBoxSize { get; set; }
        public double MinHFR { get; set; }
        public int StructureDilationSize { get; set; }
        public int StructureDilationCount { get; set; }
        public double PixelSampleSize { get; set; }
        public bool DebugMode { get; set; }
        public string IntermediateSavePath { get; set; }
        public bool SaveIntermediateImages { get; set; }
        public int PSFParallelPartitionSize { get; set; }
        public bool StarMeasurementNoiseReductionEnabled { get; set; }
        public string PSFFitType { get; set; }
        public int PSFResolution { get; set; }
        public double PSFFitThreshold { get; set; }
        public bool UsePSFAbsoluteDeviation { get; set; }
        public double HotpixelThreshold { get; set; }
        public double SaturationThreshold { get; set; }
        public string MeasurementAverage { get; set; }
        public bool PSFPixelIntegration { get; set; }
        public double ContaminationSensitivity { get; set; }
        public bool RejectContaminatedStars { get; set; }
        public bool DefocusAwareGates { get; set; }
        public double DefocusDistortionSizeReference { get; set; }
        public double DefocusDistortionMinFactor { get; set; }
        public double DefocusCenteringToleranceFactor { get; set; }
        public bool DefocusAwareDonutDetection { get; set; }
        public int DonutMorphCloseSize { get; set; }
        public double DonutMinAnnularityHoleFraction { get; set; }
        public double DonutMaxStreakEccentricity { get; set; }
        public double DonutSaturationBloomRadius { get; set; }
        public bool DefocusAwareStructure { get; set; }
        public int StructureLayerBoost { get; set; }
        public bool LocallyAdaptiveBinarization { get; set; }
        public int AdaptiveNoiseBlockSize { get; set; }
        public bool ExcludeSaturatedStarsFromHFR { get; set; }
        public bool UseOptimizedSettings { get; set; }
        public bool HasOptimizedSettings { get; set; }
    }
}
