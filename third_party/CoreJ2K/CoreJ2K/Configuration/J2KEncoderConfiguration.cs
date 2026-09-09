// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using CoreJ2K.j2k.roi;
using CoreJ2K.j2k.util;

namespace CoreJ2K.Configuration
{
    /// <summary>
    /// Fluent API for configuring JPEG 2000 encoding parameters.
    /// Provides a modern, type-safe alternative to ParameterList.
    /// </summary>
    public class J2KEncoderConfiguration
    {
        private float _targetBitrate = -1f; // -1 means lossless
        private bool _lossless = false;
        private bool _useFileFormat = true;
        private TileConfiguration _tileConfig = new TileConfiguration();
        private WaveletConfiguration _waveletConfig = new WaveletConfiguration();
        private QuantizationConfiguration _quantizationConfig = new QuantizationConfiguration();
        private ProgressionConfiguration _progressionConfig = new ProgressionConfiguration();
        private CodeBlockConfiguration _codeBlockConfig = new CodeBlockConfiguration();
        private EntropyCodingConfiguration _entropyConfig = new EntropyCodingConfiguration();
        private ErrorResilienceConfiguration _resilienceConfig = new ErrorResilienceConfiguration();
        private ROIConfiguration? _roiConfig = null;
        
        /// <summary>
        /// Gets or sets the target bitrate in bits per pixel.
        /// -1 means no rate limit (lossless if using reversible transform).
        /// </summary>
        public float TargetBitrate
        {
            get => _targetBitrate;
            set => _targetBitrate = value;
        }
        
        /// <summary>
        /// Gets or sets whether to use lossless compression.
        /// This automatically sets reversible quantization and 5-3 wavelet filter.
        /// </summary>
        public bool Lossless
        {
            get => _lossless;
            set => _lossless = value;
        }
        
        /// <summary>
        /// Gets or sets whether to wrap the codestream in JP2 file format.
        /// </summary>
        public bool UseFileFormat
        {
            get => _useFileFormat;
            set => _useFileFormat = value;
        }
        
        /// <summary>
        /// Gets the tile configuration.
        /// </summary>
        public TileConfiguration Tiles => _tileConfig;
        
        /// <summary>
        /// Gets the wavelet transform configuration.
        /// </summary>
        public WaveletConfiguration Wavelet => _waveletConfig;
        
        /// <summary>
        /// Gets the quantization configuration.
        /// </summary>
        public QuantizationConfiguration Quantization => _quantizationConfig;
        
        /// <summary>
        /// Gets the progression order configuration.
        /// </summary>
        public ProgressionConfiguration Progression => _progressionConfig;
        
        /// <summary>
        /// Gets the code-block configuration.
        /// </summary>
        public CodeBlockConfiguration CodeBlocks => _codeBlockConfig;
        
        /// <summary>
        /// Gets the entropy coding configuration.
        /// </summary>
        public EntropyCodingConfiguration EntropyCoding => _entropyConfig;
        
        /// <summary>
        /// Gets the error resilience configuration.
        /// </summary>
        public ErrorResilienceConfiguration ErrorResilience => _resilienceConfig;
        
        /// <summary>
        /// Gets the ROI configuration, if any.
        /// </summary>
        public ROIConfiguration? ROI => _roiConfig;
        
        /// <summary>
        /// Sets the target bitrate in bits per pixel.
        /// </summary>
        /// <param name="bitrate">Target bitrate (0.1 to 10.0 typical range). Use -1 for no limit.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KEncoderConfiguration WithBitrate(float bitrate)
        {
            if (bitrate < -1)
                throw new ArgumentException("Bitrate must be -1 (unlimited) or positive", nameof(bitrate));
            
            _targetBitrate = bitrate;
            _lossless = false;
            return this;
        }
        
        /// <summary>
        /// Sets the target quality level (0.0 to 1.0).
        /// This is converted to an appropriate bitrate.
        /// </summary>
        /// <param name="quality">Quality level where 1.0 is highest quality, 0.0 is lowest.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KEncoderConfiguration WithQuality(double quality)
        {
            if (quality < 0.0 || quality > 1.0)
                throw new ArgumentException("Quality must be between 0.0 and 1.0", nameof(quality));
            
            // Convert quality to bitrate (approximate mapping)
            // Quality 1.0 ? 5.0 bpp (very high)
            // Quality 0.5 ? 1.0 bpp (medium)
            // Quality 0.1 ? 0.2 bpp (low)
            _targetBitrate = (float)(quality * quality * 5.0);
            _lossless = false;
            return this;
        }
        
        /// <summary>
        /// Enables lossless compression.
        /// This automatically configures reversible quantization and 5-3 wavelet filter.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KEncoderConfiguration WithLossless()
        {
            _lossless = true;
            _targetBitrate = -1;
            _waveletConfig.Filter = WaveletFilter.Reversible53;
            _quantizationConfig.Type = QuantizationType.Reversible;
            return this;
        }
        
        /// <summary>
        /// Configures whether to use JP2 file format wrapper.
        /// </summary>
        /// <param name="useFileFormat">True to use JP2 format, false for raw codestream.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KEncoderConfiguration WithFileFormat(bool useFileFormat)
        {
            _useFileFormat = useFileFormat;
            return this;
        }
        
        /// <summary>
        /// Configures tile settings.
        /// </summary>
        /// <param name="configurator">Action to configure tile settings.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KEncoderConfiguration WithTiles(Action<TileConfiguration> configurator)
        {
            configurator?.Invoke(_tileConfig);
            return this;
        }
        
        /// <summary>
        /// Configures wavelet transform settings.
        /// </summary>
        /// <param name="configurator">Action to configure wavelet settings.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KEncoderConfiguration WithWavelet(Action<WaveletConfiguration> configurator)
        {
            configurator?.Invoke(_waveletConfig);
            return this;
        }
        
        /// <summary>
        /// Configures quantization settings.
        /// </summary>
        /// <param name="configurator">Action to configure quantization settings.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KEncoderConfiguration WithQuantization(Action<QuantizationConfiguration> configurator)
        {
            configurator?.Invoke(_quantizationConfig);
            return this;
        }
        
        /// <summary>
        /// Configures progression order settings.
        /// </summary>
        /// <param name="configurator">Action to configure progression settings.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KEncoderConfiguration WithProgression(Action<ProgressionConfiguration> configurator)
        {
            configurator?.Invoke(_progressionConfig);
            return this;
        }
        
        /// <summary>
        /// Configures code-block settings.
        /// </summary>
        /// <param name="configurator">Action to configure code-block settings.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KEncoderConfiguration WithCodeBlocks(Action<CodeBlockConfiguration> configurator)
        {
            configurator?.Invoke(_codeBlockConfig);
            return this;
        }
        
        /// <summary>
        /// Configures entropy coding settings.
        /// </summary>
        /// <param name="configurator">Action to configure entropy coding settings.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KEncoderConfiguration WithEntropyCoding(Action<EntropyCodingConfiguration> configurator)
        {
            configurator?.Invoke(_entropyConfig);
            return this;
        }
        
        /// <summary>
        /// Configures error resilience settings.
        /// </summary>
        /// <param name="configurator">Action to configure error resilience settings.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KEncoderConfiguration WithErrorResilience(Action<ErrorResilienceConfiguration> configurator)
        {
            configurator?.Invoke(_resilienceConfig);
            return this;
        }
        
        /// <summary>
        /// Configures Region of Interest (ROI) encoding.
        /// </summary>
        /// <param name="roiConfig">ROI configuration.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KEncoderConfiguration WithROI(ROIConfiguration roiConfig)
        {
            _roiConfig = roiConfig;
            return this;
        }
        
        /// <summary>
        /// Converts this configuration to a ParameterList for use with the encoder.
        /// </summary>
        /// <returns>ParameterList with all configured parameters.</returns>
        public ParameterList ToParameterList()
        {
            // Create parameter list with default parameters as fallback
            var defaultPl = J2kImage.GetDefaultEncoderParameterList();
            var pl = new ParameterList(defaultPl);
            
            // File format
            pl["file_format"] = _useFileFormat ? "on" : "off";
            
            // Rate/Quality
            if (_lossless)
            {
                pl["lossless"] = "on";
                pl["rate"] = "-1";
            }
            else
            {
                pl["lossless"] = "off";
                pl["rate"] = _targetBitrate.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            
            // Tiles
            _tileConfig.ApplyTo(pl);
            
            // Wavelet
            _waveletConfig.ApplyTo(pl);
            
            // Quantization
            _quantizationConfig.ApplyTo(pl);
            
            // Progression
            _progressionConfig.ApplyTo(pl);
            
            // Code blocks
            _codeBlockConfig.ApplyTo(pl);
            
            // Entropy coding
            _entropyConfig.ApplyTo(pl);
            
            // Error resilience
            _resilienceConfig.ApplyTo(pl);
            
            // ROI (handled separately in encoding pipeline)
            
            return pl;
        }
        
        /// <summary>
        /// Validates the configuration and returns any validation errors.
        /// </summary>
        /// <returns>List of validation error messages, empty if valid.</returns>
        public List<string> Validate()
        {
            var errors = new List<string>();
            
            if (_lossless && _targetBitrate > 0 && _targetBitrate != -1)
            {
                errors.Add("Cannot specify both lossless mode and a target bitrate");
            }
            
            if (_targetBitrate < -1)
            {
                errors.Add("Target bitrate must be -1 (unlimited) or positive");
            }
            
            errors.AddRange(_tileConfig.Validate());
            errors.AddRange(_waveletConfig.Validate());
            errors.AddRange(_quantizationConfig.Validate());
            errors.AddRange(_progressionConfig.Validate());
            errors.AddRange(_codeBlockConfig.Validate());
            
            if (_roiConfig != null && !_roiConfig.IsValid)
            {
                errors.AddRange(_roiConfig.Validate());
            }
            
            return errors;
        }
        
        /// <summary>
        /// Checks if the configuration is valid.
        /// </summary>
        public bool IsValid => Validate().Count == 0;
    }
}
