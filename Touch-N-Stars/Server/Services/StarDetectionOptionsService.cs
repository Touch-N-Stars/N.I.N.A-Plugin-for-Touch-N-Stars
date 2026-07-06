using System;
using System.Reflection;
using System.Collections.Generic;
using NINA.Core.Utility;
using TouchNStars.Server.Models;

namespace TouchNStars.Server.Services {

    /// <summary>
    /// Service for accessing HocusFocus StarDetectionOptions using reflection
    /// Avoids direct dependency on NINA.Joko.Plugins.HocusFocus assembly
    /// </summary>
    public class StarDetectionOptionsService {

        /// <summary>
        /// Get current star detection options from HocusFocus plugin
        /// </summary>
        public static StarDetectionOptionsDto GetHocusFocusStarDetectionOptions() {
            try {
                var dto = new StarDetectionOptionsDto();
                var starDetectionOptions = GetHocusFocusDetectorOptions();

                if (starDetectionOptions == null) {
                    Logger.Error("StarDetectionOptions not found on HocusFocus plugin");
                    return null;
                }

                // Map DTO properties
                dto.UseAdvanced = GetPropertyValueAsType<bool>(starDetectionOptions, "UseAdvanced", false);
                dto.ModelPSF = GetPropertyValueAsType<bool>(starDetectionOptions, "ModelPSF", false);
                dto.Simple_NoiseLevel = ConvertEnumToString(GetPropertyValue(starDetectionOptions, "Simple_NoiseLevel"));
                dto.Simple_PixelScale = ConvertEnumToString(GetPropertyValue(starDetectionOptions, "Simple_PixelScale"));
                dto.Simple_FocusRange = ConvertEnumToString(GetPropertyValue(starDetectionOptions, "Simple_FocusRange"));
                dto.HotpixelFiltering = GetPropertyValueAsType<bool>(starDetectionOptions, "HotpixelFiltering", false);
                dto.HotpixelThresholdingEnabled = GetPropertyValueAsType<bool>(starDetectionOptions, "HotpixelThresholdingEnabled", false);
                dto.UseAutoFocusCrop = GetPropertyValueAsType<bool>(starDetectionOptions, "UseAutoFocusCrop", false);
                dto.NoiseReductionRadius = GetPropertyValueAsType<int>(starDetectionOptions, "NoiseReductionRadius", 0);
                dto.NoiseClippingMultiplier = GetPropertyValueAsType<double>(starDetectionOptions, "NoiseClippingMultiplier", 0);
                dto.StarClippingMultiplier = GetPropertyValueAsType<double>(starDetectionOptions, "StarClippingMultiplier", 0);
                dto.StructureLayers = GetPropertyValueAsType<int>(starDetectionOptions, "StructureLayers", 0);
                dto.BrightnessSensitivity = GetPropertyValueAsType<double>(starDetectionOptions, "BrightnessSensitivity", 0);
                dto.StarPeakResponse = GetPropertyValueAsType<double>(starDetectionOptions, "StarPeakResponse", 0);
                dto.MaxDistortion = GetPropertyValueAsType<double>(starDetectionOptions, "MaxDistortion", 0);
                dto.StarCenterTolerance = GetPropertyValueAsType<double>(starDetectionOptions, "StarCenterTolerance", 0);
                dto.StarBackgroundBoxExpansion = GetPropertyValueAsType<int>(starDetectionOptions, "StarBackgroundBoxExpansion", 0);
                dto.MinStarBoundingBoxSize = GetPropertyValueAsType<int>(starDetectionOptions, "MinStarBoundingBoxSize", 0);
                dto.MinHFR = GetPropertyValueAsType<double>(starDetectionOptions, "MinHFR", 0);
                dto.StructureDilationSize = GetPropertyValueAsType<int>(starDetectionOptions, "StructureDilationSize", 0);
                dto.StructureDilationCount = GetPropertyValueAsType<int>(starDetectionOptions, "StructureDilationCount", 0);
                dto.PixelSampleSize = GetPropertyValueAsType<double>(starDetectionOptions, "PixelSampleSize", 0);
                dto.DebugMode = GetPropertyValueAsType<bool>(starDetectionOptions, "DebugMode", false);
                dto.IntermediateSavePath = GetPropertyValueAsType<string>(starDetectionOptions, "IntermediateSavePath", "");
                dto.SaveIntermediateImages = GetPropertyValueAsType<bool>(starDetectionOptions, "SaveIntermediateImages", false);
                dto.PSFParallelPartitionSize = GetPropertyValueAsType<int>(starDetectionOptions, "PSFParallelPartitionSize", 0);
                dto.StarMeasurementNoiseReductionEnabled = GetPropertyValueAsType<bool>(starDetectionOptions, "StarMeasurementNoiseReductionEnabled", false);
                dto.PSFFitType = ConvertEnumToString(GetPropertyValue(starDetectionOptions, "PSFFitType"));
                dto.PSFResolution = GetPropertyValueAsType<int>(starDetectionOptions, "PSFResolution", 0);
                dto.PSFFitThreshold = GetPropertyValueAsType<double>(starDetectionOptions, "PSFFitThreshold", 0);
                dto.UsePSFAbsoluteDeviation = GetPropertyValueAsType<bool>(starDetectionOptions, "UsePSFAbsoluteDeviation", false);
                dto.HotpixelThreshold = GetPropertyValueAsType<double>(starDetectionOptions, "HotpixelThreshold", 0);
                dto.SaturationThreshold = GetPropertyValueAsType<double>(starDetectionOptions, "SaturationThreshold", 0);
                dto.MeasurementAverage = ConvertEnumToString(GetPropertyValue(starDetectionOptions, "MeasurementAverage"));
                dto.PSFPixelIntegration = GetPropertyValueAsType<bool>(starDetectionOptions, "PSFPixelIntegration", false);
                dto.ContaminationSensitivity = GetPropertyValueAsType<double>(starDetectionOptions, "ContaminationSensitivity", 0);
                dto.RejectContaminatedStars = GetPropertyValueAsType<bool>(starDetectionOptions, "RejectContaminatedStars", false);
                dto.DefocusAwareGates = GetPropertyValueAsType<bool>(starDetectionOptions, "DefocusAwareGates", false);
                dto.DefocusDistortionSizeReference = GetPropertyValueAsType<double>(starDetectionOptions, "DefocusDistortionSizeReference", 0);
                dto.DefocusDistortionMinFactor = GetPropertyValueAsType<double>(starDetectionOptions, "DefocusDistortionMinFactor", 0);
                dto.DefocusCenteringToleranceFactor = GetPropertyValueAsType<double>(starDetectionOptions, "DefocusCenteringToleranceFactor", 0);
                dto.DefocusAwareDonutDetection = GetPropertyValueAsType<bool>(starDetectionOptions, "DefocusAwareDonutDetection", false);
                dto.DonutMorphCloseSize = GetPropertyValueAsType<int>(starDetectionOptions, "DonutMorphCloseSize", 0);
                dto.DonutMinAnnularityHoleFraction = GetPropertyValueAsType<double>(starDetectionOptions, "DonutMinAnnularityHoleFraction", 0);
                dto.DonutMaxStreakEccentricity = GetPropertyValueAsType<double>(starDetectionOptions, "DonutMaxStreakEccentricity", 0);
                dto.DonutSaturationBloomRadius = GetPropertyValueAsType<double>(starDetectionOptions, "DonutSaturationBloomRadius", 0);
                dto.DefocusAwareStructure = GetPropertyValueAsType<bool>(starDetectionOptions, "DefocusAwareStructure", false);
                dto.StructureLayerBoost = GetPropertyValueAsType<int>(starDetectionOptions, "StructureLayerBoost", 0);
                dto.LocallyAdaptiveBinarization = GetPropertyValueAsType<bool>(starDetectionOptions, "LocallyAdaptiveBinarization", false);
                dto.AdaptiveNoiseBlockSize = GetPropertyValueAsType<int>(starDetectionOptions, "AdaptiveNoiseBlockSize", 0);
                dto.ExcludeSaturatedStarsFromHFR = GetPropertyValueAsType<bool>(starDetectionOptions, "ExcludeSaturatedStarsFromHFR", false);
                dto.UseOptimizedSettings = GetPropertyValueAsType<bool>(starDetectionOptions, "UseOptimizedSettings", false);
                dto.HasOptimizedSettings = GetPropertyValueAsType<bool>(starDetectionOptions, "HasOptimizedSettings", false);

                return dto;
            } catch (Exception ex) {
                Logger.Error($"Error getting star detection options: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Set star detection options on HocusFocus plugin
        /// </summary>
        public static bool SetHocusFocusStarDetectionOptions(StarDetectionOptionsDto dto) {
            try {
                var starDetectionOptions = GetHocusFocusDetectorOptions();

                if (starDetectionOptions == null) {
                    Logger.Error("StarDetectionOptions not found on HocusFocus plugin");
                    return false;
                }

                // Set DTO properties
                SetPropertyValue(starDetectionOptions, "UseAdvanced", dto.UseAdvanced);
                SetPropertyValue(starDetectionOptions, "ModelPSF", dto.ModelPSF);
                SetPropertyValueFromString(starDetectionOptions, "Simple_NoiseLevel", dto.Simple_NoiseLevel);
                SetPropertyValueFromString(starDetectionOptions, "Simple_PixelScale", dto.Simple_PixelScale);
                SetPropertyValueFromString(starDetectionOptions, "Simple_FocusRange", dto.Simple_FocusRange);
                SetPropertyValue(starDetectionOptions, "HotpixelFiltering", dto.HotpixelFiltering);
                SetPropertyValue(starDetectionOptions, "HotpixelThresholdingEnabled", dto.HotpixelThresholdingEnabled);
                SetPropertyValue(starDetectionOptions, "UseAutoFocusCrop", dto.UseAutoFocusCrop);
                SetPropertyValue(starDetectionOptions, "NoiseReductionRadius", dto.NoiseReductionRadius);
                SetPropertyValue(starDetectionOptions, "NoiseClippingMultiplier", dto.NoiseClippingMultiplier);
                SetPropertyValue(starDetectionOptions, "StarClippingMultiplier", dto.StarClippingMultiplier);
                SetPropertyValue(starDetectionOptions, "StructureLayers", dto.StructureLayers);
                SetPropertyValue(starDetectionOptions, "BrightnessSensitivity", dto.BrightnessSensitivity);
                SetPropertyValue(starDetectionOptions, "StarPeakResponse", dto.StarPeakResponse);
                SetPropertyValue(starDetectionOptions, "MaxDistortion", dto.MaxDistortion);
                SetPropertyValue(starDetectionOptions, "StarCenterTolerance", dto.StarCenterTolerance);
                SetPropertyValue(starDetectionOptions, "StarBackgroundBoxExpansion", dto.StarBackgroundBoxExpansion);
                SetPropertyValue(starDetectionOptions, "MinStarBoundingBoxSize", dto.MinStarBoundingBoxSize);
                SetPropertyValue(starDetectionOptions, "MinHFR", dto.MinHFR);
                SetPropertyValue(starDetectionOptions, "StructureDilationSize", dto.StructureDilationSize);
                SetPropertyValue(starDetectionOptions, "StructureDilationCount", dto.StructureDilationCount);
                SetPropertyValue(starDetectionOptions, "PixelSampleSize", dto.PixelSampleSize);
                SetPropertyValue(starDetectionOptions, "DebugMode", dto.DebugMode);
                SetPropertyValue(starDetectionOptions, "IntermediateSavePath", dto.IntermediateSavePath);
                SetPropertyValue(starDetectionOptions, "SaveIntermediateImages", dto.SaveIntermediateImages);
                SetPropertyValue(starDetectionOptions, "PSFParallelPartitionSize", dto.PSFParallelPartitionSize);
                SetPropertyValue(starDetectionOptions, "StarMeasurementNoiseReductionEnabled", dto.StarMeasurementNoiseReductionEnabled);
                SetPropertyValueFromString(starDetectionOptions, "PSFFitType", dto.PSFFitType);
                SetPropertyValue(starDetectionOptions, "PSFResolution", dto.PSFResolution);
                SetPropertyValue(starDetectionOptions, "PSFFitThreshold", dto.PSFFitThreshold);
                SetPropertyValue(starDetectionOptions, "UsePSFAbsoluteDeviation", dto.UsePSFAbsoluteDeviation);
                SetPropertyValue(starDetectionOptions, "HotpixelThreshold", dto.HotpixelThreshold);
                SetPropertyValue(starDetectionOptions, "SaturationThreshold", dto.SaturationThreshold);
                SetPropertyValueFromString(starDetectionOptions, "MeasurementAverage", dto.MeasurementAverage);
                SetPropertyValue(starDetectionOptions, "PSFPixelIntegration", dto.PSFPixelIntegration);
                SetPropertyValue(starDetectionOptions, "ContaminationSensitivity", dto.ContaminationSensitivity);
                SetPropertyValue(starDetectionOptions, "RejectContaminatedStars", dto.RejectContaminatedStars);
                SetPropertyValue(starDetectionOptions, "DefocusAwareGates", dto.DefocusAwareGates);
                SetPropertyValue(starDetectionOptions, "DefocusDistortionSizeReference", dto.DefocusDistortionSizeReference);
                SetPropertyValue(starDetectionOptions, "DefocusDistortionMinFactor", dto.DefocusDistortionMinFactor);
                SetPropertyValue(starDetectionOptions, "DefocusCenteringToleranceFactor", dto.DefocusCenteringToleranceFactor);
                SetPropertyValue(starDetectionOptions, "DefocusAwareDonutDetection", dto.DefocusAwareDonutDetection);
                SetPropertyValue(starDetectionOptions, "DonutMorphCloseSize", dto.DonutMorphCloseSize);
                SetPropertyValue(starDetectionOptions, "DonutMinAnnularityHoleFraction", dto.DonutMinAnnularityHoleFraction);
                SetPropertyValue(starDetectionOptions, "DonutMaxStreakEccentricity", dto.DonutMaxStreakEccentricity);
                SetPropertyValue(starDetectionOptions, "DonutSaturationBloomRadius", dto.DonutSaturationBloomRadius);
                SetPropertyValue(starDetectionOptions, "DefocusAwareStructure", dto.DefocusAwareStructure);
                SetPropertyValue(starDetectionOptions, "StructureLayerBoost", dto.StructureLayerBoost);
                SetPropertyValue(starDetectionOptions, "LocallyAdaptiveBinarization", dto.LocallyAdaptiveBinarization);
                SetPropertyValue(starDetectionOptions, "AdaptiveNoiseBlockSize", dto.AdaptiveNoiseBlockSize);
                SetPropertyValue(starDetectionOptions, "ExcludeSaturatedStarsFromHFR", dto.ExcludeSaturatedStarsFromHFR);
                SetPropertyValue(starDetectionOptions, "UseOptimizedSettings", dto.UseOptimizedSettings);
                // HasOptimizedSettings is read-only (derived) — not set

                return true;
            } catch (Exception ex) {
                Logger.Error($"Error setting star detection options: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Reset star detection options to defaults
        /// </summary>
        public static bool ResetStarDetectionDefaults() {
            try {
                var starDetectionOptions = GetHocusFocusDetectorOptions();

                if (starDetectionOptions == null) {
                    Logger.Error("DetectorOptions not found");
                    return false;
                }

                // Call ResetDefaults method via reflection
                var method = starDetectionOptions.GetType().GetMethod("ResetDefaults", BindingFlags.Public | BindingFlags.Instance);
                if (method != null) {
                    method.Invoke(starDetectionOptions, null);
                    return true;
                }

                Logger.Error("ResetDefaults method not found");
                return false;
            } catch (Exception ex) {
                Logger.Error($"Error resetting star detection options: {ex}");
                return false;
            }
        }

        // ============ Helper Methods ============

        private static object GetHocusFocusDetectorOptions() {
            try {
                var hocusFocusPluginType = Type.GetType("NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus");
                if (hocusFocusPluginType == null) {
                    Logger.Warning("HocusFocusPlugin type not found");
                    return null;
                }

                var starDetectionOptionsProperty = hocusFocusPluginType.GetProperty("StarDetectionOptions", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (starDetectionOptionsProperty == null) {
                    Logger.Warning("StarDetectionOptions property not found on HocusFocusPlugin");
                    return null;
                }

                var starDetectionOptions = starDetectionOptionsProperty.GetValue(null);
                if (starDetectionOptions == null) {
                    Logger.Warning("StarDetectionOptions instance is null");
                    return null;
                }

                return starDetectionOptions;
            } catch (Exception ex) {
                Logger.Error("Error getting HocusFocusPlugin.StarDetectionOptions via reflection", ex);
                return null;
            }
        }

        private static object GetPropertyValue(object obj, string propertyName) {
            try {
                var property = obj?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                return property?.GetValue(obj);
            } catch (Exception ex) {
                Logger.Error($"Error getting property {propertyName}: {ex}");
                return null;
            }
        }

        private static T GetPropertyValueAsType<T>(object obj, string propertyName, T defaultValue) {
            var value = GetPropertyValue(obj, propertyName);
            if (value is T typedValue) {
                return typedValue;
            }
            return defaultValue;
        }

        private static void SetPropertyValue(object obj, string propertyName, object value) {
            try {
                var property = obj?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                property?.SetValue(obj, value);
            } catch (Exception ex) {
                Logger.Error($"Error setting property {propertyName}: {ex}");
            }
        }

        private static void SetPropertyValueFromString(object obj, string propertyName, string stringValue) {
            try {
                var property = obj?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || string.IsNullOrEmpty(stringValue)) return;

                if (property.PropertyType.IsEnum) {
                    var enumValue = Enum.Parse(property.PropertyType, stringValue);
                    property.SetValue(obj, enumValue);
                } else {
                    property.SetValue(obj, stringValue);
                }
            } catch (Exception ex) {
                Logger.Error($"Error setting enum property {propertyName} to {stringValue}: {ex}");
            }
        }

        private static string ConvertEnumToString(object enumValue) {
            return enumValue?.ToString() ?? "";
        }
    }
}
