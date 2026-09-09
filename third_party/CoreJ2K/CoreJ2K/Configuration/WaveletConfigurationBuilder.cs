// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using CoreJ2K.j2k.util;

namespace CoreJ2K.Configuration
{
    /// <summary>
    /// Modern fluent API for configuring JPEG 2000 wavelet transform parameters.
    /// Can be used standalone or as part of encoder configuration.
    /// </summary>
    public class WaveletConfigurationBuilder
    {
        private WaveletFilter _filter = WaveletFilter.Reversible53;
        private int _decompositionLevels = 5;
        private Dictionary<int, WaveletFilter> _componentFilters = new Dictionary<int, WaveletFilter>();
        private bool _useDefaultFilters = true;
        
        /// <summary>
        /// Gets or sets the default wavelet filter for all components.
        /// </summary>
        public WaveletFilter Filter
        {
            get => _filter;
            set => _filter = value;
        }
        
        /// <summary>
        /// Gets or sets the number of wavelet decomposition levels (1-32).
        /// More levels = more frequency separation, better compression.
        /// </summary>
        public int DecompositionLevels
        {
            get => _decompositionLevels;
            set => _decompositionLevels = value;
        }
        
        /// <summary>
        /// Gets whether to use default filter for all components.
        /// </summary>
        public bool UseDefaultFilters
        {
            get => _useDefaultFilters;
            set => _useDefaultFilters = value;
        }
        
        /// <summary>
        /// Uses the reversible 5/3 filter (for lossless compression).
        /// This is the only filter that allows perfect reconstruction.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public WaveletConfigurationBuilder UseReversible_5_3()
        {
            _filter = WaveletFilter.Reversible53;
            return this;
        }
        
        /// <summary>
        /// Uses the irreversible 9/7 filter (for lossy compression).
        /// Provides better compression and visual quality for lossy encoding.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public WaveletConfigurationBuilder UseIrreversible_9_7()
        {
            _filter = WaveletFilter.Irreversible97;
            return this;
        }
        
        /// <summary>
        /// Uses the reversible 5/3 filter (alias for UseReversible_5_3).
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public WaveletConfigurationBuilder UseReversible()
        {
            return UseReversible_5_3();
        }
        
        /// <summary>
        /// Uses the irreversible 9/7 filter (alias for UseIrreversible_9_7).
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public WaveletConfigurationBuilder UseIrreversible()
        {
            return UseIrreversible_9_7();
        }
        
        /// <summary>
        /// Sets the number of wavelet decomposition levels.
        /// More levels provide better compression but increase processing time.
        /// </summary>
        /// <param name="levels">Number of decomposition levels (1-32, typically 3-6).</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public WaveletConfigurationBuilder WithDecompositionLevels(int levels)
        {
            if (levels < 1 || levels > 32)
                throw new ArgumentException("Decomposition levels must be between 1 and 32", nameof(levels));
            
            _decompositionLevels = levels;
            return this;
        }
        
        /// <summary>
        /// Sets the wavelet filter for a specific component.
        /// Allows different filters for different components (e.g., Y vs CbCr).
        /// </summary>
        /// <param name="component">Component index (0-based).</param>
        /// <param name="filter">Wavelet filter to use for this component.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public WaveletConfigurationBuilder WithComponentFilter(int component, WaveletFilter filter)
        {
            if (component < 0)
                throw new ArgumentException("Component index must be non-negative", nameof(component));
            
            _componentFilters[component] = filter;
            _useDefaultFilters = false;
            return this;
        }
        
        /// <summary>
        /// Clears per-component filters and uses the default filter for all components.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public WaveletConfigurationBuilder UseDefaultComponentFilters()
        {
            _componentFilters.Clear();
            _useDefaultFilters = true;
            return this;
        }
        
        /// <summary>
        /// Configures for lossless compression.
        /// Uses reversible 5/3 filter with appropriate decomposition levels.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public WaveletConfigurationBuilder ForLossless()
        {
            UseReversible_5_3();
            WithDecompositionLevels(5);
            return this;
        }
        
        /// <summary>
        /// Configures for high quality lossy compression.
        /// Uses irreversible 9/7 filter with more decomposition levels.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public WaveletConfigurationBuilder ForHighQuality()
        {
            UseIrreversible_9_7();
            WithDecompositionLevels(6);
            return this;
        }
        
        /// <summary>
        /// Configures for balanced lossy compression.
        /// Uses irreversible 9/7 filter with standard decomposition levels.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public WaveletConfigurationBuilder ForBalanced()
        {
            UseIrreversible_9_7();
            WithDecompositionLevels(5);
            return this;
        }
        
        /// <summary>
        /// Configures for fast compression.
        /// Uses fewer decomposition levels for faster encoding/decoding.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public WaveletConfigurationBuilder ForFast()
        {
            UseIrreversible_9_7();
            WithDecompositionLevels(3);
            return this;
        }
        
        /// <summary>
        /// Applies this wavelet configuration to a ParameterList.
        /// </summary>
        /// <param name="pl">The parameter list to configure.</param>
        public void ApplyTo(ParameterList pl)
        {
            // Set wavelet filter
            string filterValue;
            switch (_filter)
            {
                case WaveletFilter.Reversible53:
                    filterValue = "w5x3";
                    break;
                case WaveletFilter.Irreversible97:
                    filterValue = "w9x7";
                    break;
                default:
                    filterValue = "w5x3";
                    break;
            }
            pl["Ffilters"] = filterValue;
            
            // Set decomposition levels
            pl["Wlev"] = _decompositionLevels.ToString();
            
            // Apply per-component filters if specified
            if (!_useDefaultFilters && _componentFilters.Count > 0)
            {
                var filterSpecs = new List<string>();
                foreach (var comp in _componentFilters.Keys)
                {
                    var compFilter = _componentFilters[comp];
                    var compFilterValue = compFilter == WaveletFilter.Reversible53 ? "w5x3" : "w9x7";
                    filterSpecs.Add($"c{comp}:{compFilterValue}");
                }
                
                if (filterSpecs.Count > 0)
                {
                    pl["Ffilters_comp"] = string.Join(",", filterSpecs);
                }
            }
        }
        
        /// <summary>
        /// Validates the wavelet configuration.
        /// </summary>
        /// <returns>List of validation errors, empty if valid.</returns>
        public List<string> Validate()
        {
            var errors = new List<string>();
            
            if (_decompositionLevels < 1 || _decompositionLevels > 32)
                errors.Add("Decomposition levels must be between 1 and 32");
            
            foreach (var comp in _componentFilters.Keys)
            {
                if (comp < 0)
                    errors.Add($"Invalid component index: {comp}");
            }
            
            return errors;
        }
        
        /// <summary>
        /// Checks if the configuration is valid.
        /// </summary>
        public bool IsValid => Validate().Count == 0;
        
        /// <summary>
        /// Creates a copy of this wavelet configuration.
        /// </summary>
        /// <returns>A new instance with the same settings.</returns>
        public WaveletConfigurationBuilder Clone()
        {
            var clone = new WaveletConfigurationBuilder
            {
                _filter = this._filter,
                _decompositionLevels = this._decompositionLevels,
                _useDefaultFilters = this._useDefaultFilters
            };
            
            foreach (var comp in _componentFilters.Keys)
            {
                clone._componentFilters[comp] = _componentFilters[comp];
            }
            
            return clone;
        }
        
        /// <summary>
        /// Gets a string representation of this configuration.
        /// </summary>
        public override string ToString()
        {
            var filterName = _filter == WaveletFilter.Reversible53 ? "5/3 (reversible)" : "9/7 (irreversible)";
            var customFilters = _useDefaultFilters ? "" : $", {_componentFilters.Count} custom component filters";
            return $"Wavelet: {filterName}, Levels: {_decompositionLevels}{customFilters}";
        }
    }
    
    /// <summary>
    /// Preset wavelet configurations for common use cases.
    /// </summary>
    public static class WaveletPresets
    {
        /// <summary>
        /// Lossless compression configuration.
        /// Uses reversible 5/3 filter with 5 decomposition levels.
        /// </summary>
        public static WaveletConfigurationBuilder Lossless =>
            new WaveletConfigurationBuilder()
                .ForLossless();
        
        /// <summary>
        /// High quality lossy compression.
        /// Uses irreversible 9/7 filter with 6 decomposition levels.
        /// </summary>
        public static WaveletConfigurationBuilder HighQuality =>
            new WaveletConfigurationBuilder()
                .ForHighQuality();
        
        /// <summary>
        /// Balanced quality and compression.
        /// Uses irreversible 9/7 filter with 5 decomposition levels.
        /// </summary>
        public static WaveletConfigurationBuilder Balanced =>
            new WaveletConfigurationBuilder()
                .ForBalanced();
        
        /// <summary>
        /// Fast compression with fewer decomposition levels.
        /// Uses irreversible 9/7 filter with 3 decomposition levels.
        /// </summary>
        public static WaveletConfigurationBuilder Fast =>
            new WaveletConfigurationBuilder()
                .ForFast();
        
        /// <summary>
        /// Medical imaging configuration (lossless).
        /// Uses reversible 5/3 filter with 5 decomposition levels.
        /// </summary>
        public static WaveletConfigurationBuilder Medical =>
            new WaveletConfigurationBuilder()
                .UseReversible_5_3()
                .WithDecompositionLevels(5);
        
        /// <summary>
        /// Archival storage configuration (high quality).
        /// Uses irreversible 9/7 filter with 6 decomposition levels.
        /// </summary>
        public static WaveletConfigurationBuilder Archival =>
            new WaveletConfigurationBuilder()
                .UseIrreversible_9_7()
                .WithDecompositionLevels(6);
        
        /// <summary>
        /// Web delivery configuration (balanced).
        /// Uses irreversible 9/7 filter with 5 decomposition levels.
        /// </summary>
        public static WaveletConfigurationBuilder Web =>
            new WaveletConfigurationBuilder()
                .UseIrreversible_9_7()
                .WithDecompositionLevels(5);
        
        /// <summary>
        /// Thumbnail generation (fast with fewer levels).
        /// Uses irreversible 9/7 filter with 3 decomposition levels.
        /// </summary>
        public static WaveletConfigurationBuilder Thumbnail =>
            new WaveletConfigurationBuilder()
                .UseIrreversible_9_7()
                .WithDecompositionLevels(3);
        
        /// <summary>
        /// Maximum compression (very few levels).
        /// Uses irreversible 9/7 filter with 2 decomposition levels.
        /// </summary>
        public static WaveletConfigurationBuilder MaximumCompression =>
            new WaveletConfigurationBuilder()
                .UseIrreversible_9_7()
                .WithDecompositionLevels(2);
    }
}
