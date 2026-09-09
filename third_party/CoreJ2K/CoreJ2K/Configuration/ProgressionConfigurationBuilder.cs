// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using CoreJ2K.j2k.util;

namespace CoreJ2K.Configuration
{
    /// <summary>
    /// Modern fluent API for configuring JPEG 2000 progression order parameters.
    /// Can be used standalone or as part of encoder configuration.
    /// </summary>
    public class ProgressionConfigurationBuilder
    {
        private ProgressionOrder _defaultOrder = ProgressionOrder.LRCP;
        private Dictionary<int, ProgressionOrder> _tileOrders = new Dictionary<int, ProgressionOrder>();
        private bool _useDefaultOrder = true;
        
        /// <summary>
        /// Gets or sets the default progression order for all tiles.
        /// </summary>
        public ProgressionOrder DefaultOrder
        {
            get => _defaultOrder;
            set => _defaultOrder = value;
        }
        
        /// <summary>
        /// Gets whether to use default progression order for all tiles.
        /// </summary>
        public bool UseDefaultOrder
        {
            get => _useDefaultOrder;
            set => _useDefaultOrder = value;
        }
        
        /// <summary>
        /// Uses Layer-Resolution-Component-Position (LRCP) progression order.
        /// Best for: Random access by quality, progressive quality transmission.
        /// Order: Quality layers ? Resolution levels ? Components ? Spatial positions.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public ProgressionConfigurationBuilder UseLRCP()
        {
            _defaultOrder = ProgressionOrder.LRCP;
            return this;
        }
        
        /// <summary>
        /// Uses Resolution-Layer-Component-Position (RLCP) progression order.
        /// Best for: Progressive resolution display, thumbnail to full resolution.
        /// Order: Resolution levels ? Quality layers ? Components ? Spatial positions.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public ProgressionConfigurationBuilder UseRLCP()
        {
            _defaultOrder = ProgressionOrder.RLCP;
            return this;
        }
        
        /// <summary>
        /// Uses Resolution-Position-Component-Layer (RPCL) progression order.
        /// Best for: Spatial browsing, region of interest access.
        /// Order: Resolution levels ? Spatial positions ? Components ? Quality layers.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public ProgressionConfigurationBuilder UseRPCL()
        {
            _defaultOrder = ProgressionOrder.RPCL;
            return this;
        }
        
        /// <summary>
        /// Uses Position-Component-Resolution-Layer (PCRL) progression order.
        /// Best for: Spatial random access, tile-based browsing.
        /// Order: Spatial positions ? Components ? Resolution levels ? Quality layers.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public ProgressionConfigurationBuilder UsePCRL()
        {
            _defaultOrder = ProgressionOrder.PCRL;
            return this;
        }
        
        /// <summary>
        /// Uses Component-Position-Resolution-Layer (CPRL) progression order.
        /// Best for: Component-based access, multi-spectral imaging.
        /// Order: Components ? Spatial positions ? Resolution levels ? Quality layers.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public ProgressionConfigurationBuilder UseCPRL()
        {
            _defaultOrder = ProgressionOrder.CPRL;
            return this;
        }
        
        /// <summary>
        /// Sets a specific progression order for a particular tile.
        /// Allows different progression orders for different tiles.
        /// </summary>
        /// <param name="tileIndex">Tile index (0-based).</param>
        /// <param name="order">Progression order for this tile.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public ProgressionConfigurationBuilder WithTileOrder(int tileIndex, ProgressionOrder order)
        {
            if (tileIndex < 0)
                throw new ArgumentException("Tile index must be non-negative", nameof(tileIndex));
            
            _tileOrders[tileIndex] = order;
            _useDefaultOrder = false;
            return this;
        }
        
        /// <summary>
        /// Clears per-tile progression orders and uses the default order for all tiles.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public ProgressionConfigurationBuilder UseDefaultTileOrders()
        {
            _tileOrders.Clear();
            _useDefaultOrder = true;
            return this;
        }
        
        /// <summary>
        /// Configures for quality-progressive transmission.
        /// Uses LRCP (Layer-Resolution-Component-Position) for progressive quality.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public ProgressionConfigurationBuilder ForQualityProgressive()
        {
            UseLRCP();
            return this;
        }
        
        /// <summary>
        /// Configures for resolution-progressive transmission.
        /// Uses RLCP (Resolution-Layer-Component-Position) for progressive resolution.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public ProgressionConfigurationBuilder ForResolutionProgressive()
        {
            UseRLCP();
            return this;
        }
        
        /// <summary>
        /// Configures for spatial browsing and region of interest access.
        /// Uses RPCL (Resolution-Position-Component-Layer) for spatial access.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public ProgressionConfigurationBuilder ForSpatialBrowsing()
        {
            UseRPCL();
            return this;
        }
        
        /// <summary>
        /// Configures for tile-based access and random spatial access.
        /// Uses PCRL (Position-Component-Resolution-Layer) for tile access.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public ProgressionConfigurationBuilder ForTileAccess()
        {
            UsePCRL();
            return this;
        }
        
        /// <summary>
        /// Configures for component-based access (e.g., multi-spectral imaging).
        /// Uses CPRL (Component-Position-Resolution-Layer) for component access.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public ProgressionConfigurationBuilder ForComponentAccess()
        {
            UseCPRL();
            return this;
        }
        
        /// <summary>
        /// Applies this progression configuration to a ParameterList.
        /// </summary>
        /// <param name="pl">The parameter list to configure.</param>
        public void ApplyTo(ParameterList pl)
        {
            // Set default progression order
            pl["Porder"] = _defaultOrder.ToParameterString();
            
            // Apply per-tile progression orders if specified
            if (!_useDefaultOrder && _tileOrders.Count > 0)
            {
                var orderSpecs = new List<string>();
                foreach (var tile in _tileOrders.Keys)
                {
                    var tileOrder = _tileOrders[tile];
                    orderSpecs.Add($"t{tile}:{tileOrder.ToParameterString()}");
                }
                
                if (orderSpecs.Count > 0)
                {
                    pl["Porder_tile"] = string.Join(",", orderSpecs);
                }
            }
        }
        
        /// <summary>
        /// Validates the progression configuration.
        /// </summary>
        /// <returns>List of validation errors, empty if valid.</returns>
        public List<string> Validate()
        {
            var errors = new List<string>();
            
            foreach (var tile in _tileOrders.Keys)
            {
                if (tile < 0)
                    errors.Add($"Invalid tile index: {tile}");
            }
            
            return errors;
        }
        
        /// <summary>
        /// Checks if the configuration is valid.
        /// </summary>
        public bool IsValid => Validate().Count == 0;
        
        /// <summary>
        /// Creates a copy of this progression configuration.
        /// </summary>
        /// <returns>A new instance with the same settings.</returns>
        public ProgressionConfigurationBuilder Clone()
        {
            var clone = new ProgressionConfigurationBuilder
            {
                _defaultOrder = this._defaultOrder,
                _useDefaultOrder = this._useDefaultOrder
            };
            
            foreach (var tile in _tileOrders.Keys)
            {
                clone._tileOrders[tile] = _tileOrders[tile];
            }
            
            return clone;
        }
        
        /// <summary>
        /// Gets a string representation of this configuration.
        /// </summary>
        public override string ToString()
        {
            var orderName = _defaultOrder.GetDescription();
            var customTiles = _useDefaultOrder ? "" : $", {_tileOrders.Count} custom tile orders";
            return $"Progression: {orderName}{customTiles}";
        }
    }
    
    /// <summary>
    /// Extension methods for ProgressionOrder enum.
    /// </summary>
    public static class ProgressionOrderExtensions
    {
        /// <summary>
        /// Converts the progression order to its parameter string representation.
        /// </summary>
        public static string ToParameterString(this ProgressionOrder order)
        {
            return order.ToString();
        }
        
        /// <summary>
        /// Gets a human-readable description of the progression order.
        /// </summary>
        public static string GetDescription(this ProgressionOrder order)
        {
            switch (order)
            {
                case ProgressionOrder.LRCP:
                    return "LRCP (Layer-Resolution-Component-Position) - Quality progressive";
                case ProgressionOrder.RLCP:
                    return "RLCP (Resolution-Layer-Component-Position) - Resolution progressive";
                case ProgressionOrder.RPCL:
                    return "RPCL (Resolution-Position-Component-Layer) - Spatial browsing";
                case ProgressionOrder.PCRL:
                    return "PCRL (Position-Component-Resolution-Layer) - Tile access";
                case ProgressionOrder.CPRL:
                    return "CPRL (Component-Position-Resolution-Layer) - Component access";
                default:
                    return order.ToString();
            }
        }
        
        /// <summary>
        /// Gets the best use case for this progression order.
        /// </summary>
        public static string GetBestUseCase(this ProgressionOrder order)
        {
            switch (order)
            {
                case ProgressionOrder.LRCP:
                    return "Progressive quality transmission, random quality access";
                case ProgressionOrder.RLCP:
                    return "Progressive resolution display, thumbnail to full resolution";
                case ProgressionOrder.RPCL:
                    return "Spatial browsing, region of interest access";
                case ProgressionOrder.PCRL:
                    return "Tile-based access, random spatial access";
                case ProgressionOrder.CPRL:
                    return "Component-based access, multi-spectral imaging";
                default:
                    return "General purpose";
            }
        }
    }
    
    /// <summary>
    /// Preset progression configurations for common use cases.
    /// </summary>
    public static class ProgressionPresets
    {
        /// <summary>
        /// Quality progressive configuration.
        /// Uses LRCP for progressive quality transmission.
        /// Best for: Streaming, progressive download, quality refinement.
        /// </summary>
        public static ProgressionConfigurationBuilder QualityProgressive =>
            new ProgressionConfigurationBuilder()
                .ForQualityProgressive();
        
        /// <summary>
        /// Resolution progressive configuration.
        /// Uses RLCP for progressive resolution display.
        /// Best for: Image browsing, thumbnail to full resolution, web delivery.
        /// </summary>
        public static ProgressionConfigurationBuilder ResolutionProgressive =>
            new ProgressionConfigurationBuilder()
                .ForResolutionProgressive();
        
        /// <summary>
        /// Spatial browsing configuration.
        /// Uses RPCL for region of interest access.
        /// Best for: Large images, panning/zooming, ROI applications.
        /// </summary>
        public static ProgressionConfigurationBuilder SpatialBrowsing =>
            new ProgressionConfigurationBuilder()
                .ForSpatialBrowsing();
        
        /// <summary>
        /// Tile-based access configuration.
        /// Uses PCRL for random tile access.
        /// Best for: Tiled displays, distributed rendering, tile servers.
        /// </summary>
        public static ProgressionConfigurationBuilder TileAccess =>
            new ProgressionConfigurationBuilder()
                .ForTileAccess();
        
        /// <summary>
        /// Component-based access configuration.
        /// Uses CPRL for component access.
        /// Best for: Multi-spectral imaging, hyperspectral data, component extraction.
        /// </summary>
        public static ProgressionConfigurationBuilder ComponentAccess =>
            new ProgressionConfigurationBuilder()
                .ForComponentAccess();
        
        /// <summary>
        /// Web streaming configuration.
        /// Uses LRCP for progressive quality over limited bandwidth.
        /// </summary>
        public static ProgressionConfigurationBuilder WebStreaming =>
            new ProgressionConfigurationBuilder()
                .UseLRCP();
        
        /// <summary>
        /// Medical imaging configuration.
        /// Uses RLCP for progressive resolution in diagnostic viewing.
        /// </summary>
        public static ProgressionConfigurationBuilder Medical =>
            new ProgressionConfigurationBuilder()
                .UseRLCP();
        
        /// <summary>
        /// Geospatial/mapping configuration.
        /// Uses RPCL for spatial browsing and ROI access.
        /// </summary>
        public static ProgressionConfigurationBuilder Geospatial =>
            new ProgressionConfigurationBuilder()
                .UseRPCL();
    }
}
