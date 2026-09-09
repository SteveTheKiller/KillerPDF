// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using CoreJ2K.j2k.util;

namespace CoreJ2K.Configuration
{
    /// <summary>
    /// Modern fluent API for configuring JPEG 2000 quantization parameters.
    /// Can be used standalone or as part of encoder configuration.
    /// </summary>
    public class QuantizationConfigurationBuilder
    {
        private QuantizationType _type = QuantizationType.Expounded;
        private float _baseStepSize = 0.0078125f;
        private int _guardBits = 1;
        private Dictionary<int, Dictionary<string, float>> _subbandSteps = new Dictionary<int, Dictionary<string, float>>();
        private bool _useDefaultSteps = true;
        
        /// <summary>
        /// Gets or sets the quantization type.
        /// </summary>
        public QuantizationType Type
        {
            get => _type;
            set => _type = value;
        }
        
        /// <summary>
        /// Gets or sets the base quantization step size.
        /// Used as the default for all subbands unless overridden.
        /// </summary>
        public float BaseStepSize
        {
            get => _baseStepSize;
            set => _baseStepSize = value;
        }
        
        /// <summary>
        /// Gets or sets the number of guard bits (0-7).
        /// Guard bits protect against overflow in the quantization process.
        /// </summary>
        public int GuardBits
        {
            get => _guardBits;
            set => _guardBits = value;
        }
        
        /// <summary>
        /// Gets whether to use default step sizes for all subbands.
        /// </summary>
        public bool UseDefaultSteps
        {
            get => _useDefaultSteps;
            set => _useDefaultSteps = value;
        }
        
        /// <summary>
        /// Uses reversible quantization (for lossless compression).
        /// This is the only quantization mode that allows perfect reconstruction.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public QuantizationConfigurationBuilder UseReversible()
        {
            _type = QuantizationType.Reversible;
            return this;
        }
        
        /// <summary>
        /// Uses scalar derived quantization.
        /// All subbands use steps derived from a single base step size.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public QuantizationConfigurationBuilder UseDerived()
        {
            _type = QuantizationType.Derived;
            _useDefaultSteps = true;
            return this;
        }
        
        /// <summary>
        /// Uses scalar expounded quantization (default).
        /// Allows independent step sizes for each subband.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public QuantizationConfigurationBuilder UseExpounded()
        {
            _type = QuantizationType.Expounded;
            return this;
        }
        
        /// <summary>
        /// Sets the base quantization step size.
        /// Smaller values = higher quality, larger values = more compression.
        /// </summary>
        /// <param name="stepSize">Base step size (typical range: 0.001 to 0.1).</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public QuantizationConfigurationBuilder WithBaseStepSize(float stepSize)
        {
            if (stepSize <= 0)
                throw new ArgumentException("Step size must be positive", nameof(stepSize));
            
            _baseStepSize = stepSize;
            return this;
        }
        
        /// <summary>
        /// Sets the number of guard bits.
        /// More guard bits = less chance of overflow, but slightly less compression.
        /// </summary>
        /// <param name="bits">Number of guard bits (0-7, typically 1-2).</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public QuantizationConfigurationBuilder WithGuardBits(int bits)
        {
            if (bits < 0 || bits > 7)
                throw new ArgumentException("Guard bits must be between 0 and 7", nameof(bits));
            
            _guardBits = bits;
            return this;
        }
        
        /// <summary>
        /// Sets the quantization step size for a specific resolution level and subband.
        /// Only applicable for expounded quantization.
        /// </summary>
        /// <param name="resolutionLevel">Resolution level (0 = coarsest).</param>
        /// <param name="subband">Subband orientation ("LL", "HL", "LH", "HH").</param>
        /// <param name="stepSize">Step size for this subband.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public QuantizationConfigurationBuilder WithSubbandStep(int resolutionLevel, string subband, float stepSize)
        {
            if (resolutionLevel < 0)
                throw new ArgumentException("Resolution level must be non-negative", nameof(resolutionLevel));
            
            if (string.IsNullOrEmpty(subband))
                throw new ArgumentNullException(nameof(subband));
            
            if (stepSize <= 0)
                throw new ArgumentException("Step size must be positive", nameof(stepSize));
            
            var validSubbands = new[] { "LL", "HL", "LH", "HH" };
            if (Array.IndexOf(validSubbands, subband.ToUpper()) == -1)
                throw new ArgumentException("Subband must be LL, HL, LH, or HH", nameof(subband));
            
            if (!_subbandSteps.ContainsKey(resolutionLevel))
                _subbandSteps[resolutionLevel] = new Dictionary<string, float>();
            
            _subbandSteps[resolutionLevel][subband.ToUpper()] = stepSize;
            _useDefaultSteps = false;
            
            return this;
        }
        
        /// <summary>
        /// Sets quantization step sizes for all subbands at a resolution level.
        /// Only applicable for expounded quantization.
        /// </summary>
        /// <param name="resolutionLevel">Resolution level (0 = coarsest).</param>
        /// <param name="llStep">Step size for LL subband (low-low).</param>
        /// <param name="hlStep">Step size for HL subband (high-low).</param>
        /// <param name="lhStep">Step size for LH subband (low-high).</param>
        /// <param name="hhStep">Step size for HH subband (high-high).</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public QuantizationConfigurationBuilder WithResolutionSteps(
            int resolutionLevel,
            float llStep,
            float hlStep,
            float lhStep,
            float hhStep)
        {
            WithSubbandStep(resolutionLevel, "LL", llStep);
            WithSubbandStep(resolutionLevel, "HL", hlStep);
            WithSubbandStep(resolutionLevel, "LH", lhStep);
            WithSubbandStep(resolutionLevel, "HH", hhStep);
            
            return this;
        }
        
        /// <summary>
        /// Clears all custom subband step sizes and uses default derived steps.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public QuantizationConfigurationBuilder UseDefaultSubbandSteps()
        {
            _subbandSteps.Clear();
            _useDefaultSteps = true;
            return this;
        }
        
        /// <summary>
        /// Configures for high quality lossy compression.
        /// Uses expounded quantization with small step sizes.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public QuantizationConfigurationBuilder ForHighQuality()
        {
            UseExpounded();
            WithBaseStepSize(0.002f);
            WithGuardBits(2);
            return this;
        }
        
        /// <summary>
        /// Configures for balanced lossy compression.
        /// Uses expounded quantization with medium step sizes.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public QuantizationConfigurationBuilder ForBalanced()
        {
            UseExpounded();
            WithBaseStepSize(0.0078125f);
            WithGuardBits(1);
            return this;
        }
        
        /// <summary>
        /// Configures for high compression lossy mode.
        /// Uses expounded quantization with larger step sizes.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public QuantizationConfigurationBuilder ForHighCompression()
        {
            UseExpounded();
            WithBaseStepSize(0.02f);
            WithGuardBits(1);
            return this;
        }
        
        /// <summary>
        /// Applies this quantization configuration to a ParameterList.
        /// </summary>
        /// <param name="pl">The parameter list to configure.</param>
        public void ApplyTo(ParameterList pl)
        {
            string qtypeValue;
            switch (_type)
            {
                case QuantizationType.Reversible:
                    qtypeValue = "reversible";
                    break;
                case QuantizationType.Derived:
                    qtypeValue = "derived";
                    break;
                case QuantizationType.Expounded:
                    qtypeValue = "expounded";
                    break;
                default:
                    qtypeValue = "expounded";
                    break;
            }
            pl["Qtype"] = qtypeValue;
            
            // Always set Qstep parameter, even for reversible quantization
            // The QuantStepSizeSpec constructor expects it to be present
            pl["Qstep"] = _baseStepSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            
            // Apply custom subband steps if specified (only for non-reversible)
            if (_type != QuantizationType.Reversible && !_useDefaultSteps && _subbandSteps.Count > 0)
            {
                // Build subband step specification string
                var stepSpecs = new List<string>();
                foreach (var level in _subbandSteps.Keys)
                {
                    foreach (var subband in _subbandSteps[level].Keys)
                    {
                        var step = _subbandSteps[level][subband];
                        stepSpecs.Add($"{level}-{subband}:{step.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    }
                }
                
                if (stepSpecs.Count > 0)
                {
                    pl["Qstep_subband"] = string.Join(",", stepSpecs);
                }
            }
            
            pl["Qguard_bits"] = _guardBits.ToString();
        }
        
        /// <summary>
        /// Validates the quantization configuration.
        /// </summary>
        /// <returns>List of validation errors, empty if valid.</returns>
        public List<string> Validate()
        {
            var errors = new List<string>();
            
            if (_type != QuantizationType.Reversible && _baseStepSize <= 0)
                errors.Add("Base step size must be positive for non-reversible quantization");
            
            if (_guardBits < 0 || _guardBits > 7)
                errors.Add("Guard bits must be between 0 and 7");
            
            if (_type == QuantizationType.Reversible && !_useDefaultSteps)
                errors.Add("Custom subband steps are not applicable for reversible quantization");
            
            if (_type == QuantizationType.Derived && !_useDefaultSteps)
                errors.Add("Custom subband steps are not applicable for derived quantization");
            
            foreach (var level in _subbandSteps.Keys)
            {
                if (level < 0)
                    errors.Add($"Invalid resolution level: {level}");
                
                foreach (var step in _subbandSteps[level].Values)
                {
                    if (step <= 0)
                        errors.Add($"Invalid step size at level {level}: {step}");
                }
            }
            
            return errors;
        }
        
        /// <summary>
        /// Checks if the configuration is valid.
        /// </summary>
        public bool IsValid => Validate().Count == 0;
        
        /// <summary>
        /// Creates a copy of this quantization configuration.
        /// </summary>
        /// <returns>A new instance with the same settings.</returns>
        public QuantizationConfigurationBuilder Clone()
        {
            var clone = new QuantizationConfigurationBuilder
            {
                _type = this._type,
                _baseStepSize = this._baseStepSize,
                _guardBits = this._guardBits,
                _useDefaultSteps = this._useDefaultSteps
            };
            
            foreach (var level in _subbandSteps.Keys)
            {
                clone._subbandSteps[level] = new Dictionary<string, float>(_subbandSteps[level]);
            }
            
            return clone;
        }
        
        /// <summary>
        /// Gets a string representation of this configuration.
        /// </summary>
        public override string ToString()
        {
            var type = _type.ToString();
            if (_type == QuantizationType.Reversible)
                return $"Quantization: {type} (lossless)";
            
            var customSteps = _useDefaultSteps ? "" : $", {_subbandSteps.Count} custom subbands";
            return $"Quantization: {type}, BaseStep: {_baseStepSize}, GuardBits: {_guardBits}{customSteps}";
        }
    }
    
    /// <summary>
    /// Preset quantization configurations for common use cases.
    /// </summary>
    public static class QuantizationPresets
    {
        /// <summary>
        /// Lossless compression with reversible quantization.
        /// Perfect reconstruction guaranteed.
        /// </summary>
        public static QuantizationConfigurationBuilder Lossless =>
            new QuantizationConfigurationBuilder()
                .UseReversible();
        
        /// <summary>
        /// Near-lossless compression with very small step size.
        /// Visually indistinguishable from original, high bitrate.
        /// </summary>
        public static QuantizationConfigurationBuilder NearLossless =>
            new QuantizationConfigurationBuilder()
                .UseExpounded()
                .WithBaseStepSize(0.001f)
                .WithGuardBits(2);
        
        /// <summary>
        /// High quality lossy compression.
        /// Excellent visual quality, good compression ratio.
        /// </summary>
        public static QuantizationConfigurationBuilder HighQuality =>
            new QuantizationConfigurationBuilder()
                .ForHighQuality();
        
        /// <summary>
        /// Balanced quality and compression.
        /// Good visual quality, moderate compression.
        /// </summary>
        public static QuantizationConfigurationBuilder Balanced =>
            new QuantizationConfigurationBuilder()
                .ForBalanced();
        
        /// <summary>
        /// High compression with acceptable quality.
        /// Visible artifacts possible, small file size.
        /// </summary>
        public static QuantizationConfigurationBuilder HighCompression =>
            new QuantizationConfigurationBuilder()
                .ForHighCompression();
        
        /// <summary>
        /// Maximum compression with low quality.
        /// Significant artifacts, very small file size.
        /// </summary>
        public static QuantizationConfigurationBuilder MaximumCompression =>
            new QuantizationConfigurationBuilder()
                .UseExpounded()
                .WithBaseStepSize(0.05f)
                .WithGuardBits(1);
        
        /// <summary>
        /// Medical imaging preset.
        /// High quality with emphasis on detail preservation.
        /// </summary>
        public static QuantizationConfigurationBuilder Medical =>
            new QuantizationConfigurationBuilder()
                .UseReversible(); // Medical often requires lossless
        
        /// <summary>
        /// Archival preset.
        /// Very high quality for long-term storage.
        /// </summary>
        public static QuantizationConfigurationBuilder Archival =>
            new QuantizationConfigurationBuilder()
                .UseExpounded()
                .WithBaseStepSize(0.0015f)
                .WithGuardBits(2);
        
        /// <summary>
        /// Web delivery preset.
        /// Balanced for web display with moderate compression.
        /// </summary>
        public static QuantizationConfigurationBuilder Web =>
            new QuantizationConfigurationBuilder()
                .UseExpounded()
                .WithBaseStepSize(0.01f)
                .WithGuardBits(1);
        
        /// <summary>
        /// Thumbnail preview preset.
        /// Higher compression acceptable for small preview images.
        /// </summary>
        public static QuantizationConfigurationBuilder Thumbnail =>
            new QuantizationConfigurationBuilder()
                .UseExpounded()
                .WithBaseStepSize(0.03f)
                .WithGuardBits(1);
    }
}
