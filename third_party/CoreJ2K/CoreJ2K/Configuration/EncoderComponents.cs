// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using CoreJ2K.j2k.util;

namespace CoreJ2K.Configuration
{
    /// <summary>
    /// Configuration for tile partitioning.
    /// </summary>
    public class TileConfiguration
    {
        /// <summary>Gets or sets the tile width in pixels.</summary>
        public int Width { get; set; } = 0; // 0 means no tiling
        
        /// <summary>Gets or sets the tile height in pixels.</summary>
        public int Height { get; set; } = 0;
        
        /// <summary>Gets or sets the image reference point X coordinate.</summary>
        public int ReferenceX { get; set; } = 0;
        
        /// <summary>Gets or sets the image reference point Y coordinate.</summary>
        public int ReferenceY { get; set; } = 0;
        
        /// <summary>Gets or sets the tiling reference point X coordinate.</summary>
        public int TilingReferenceX { get; set; } = 0;
        
        /// <summary>Gets or sets the tiling reference point Y coordinate.</summary>
        public int TilingReferenceY { get; set; } = 0;
        
        /// <summary>Gets or sets the maximum packets per tile-part.</summary>
        public int PacketsPerTilePart { get; set; } = 0; // 0 means all in first tile-part
        
        /// <summary>
        /// Sets the tile size. Use (0, 0) to disable tiling.
        /// </summary>
        public TileConfiguration SetSize(int width, int height)
        {
            Width = width;
            Height = height;
            return this;
        }
        
        /// <summary>
        /// Sets the image reference point (origin on canvas).
        /// </summary>
        public TileConfiguration WithImageReference(int x, int y)
        {
            ReferenceX = x;
            ReferenceY = y;
            return this;
        }
        
        /// <summary>
        /// Sets the tiling reference point.
        /// </summary>
        public TileConfiguration WithTilingReference(int x, int y)
        {
            TilingReferenceX = x;
            TilingReferenceY = y;
            return this;
        }
        
        /// <summary>
        /// Sets the maximum number of packets per tile-part.
        /// </summary>
        public TileConfiguration WithPacketsPerTilePart(int packets)
        {
            PacketsPerTilePart = packets;
            return this;
        }
        
        internal void ApplyTo(ParameterList pl)
        {
            pl["tiles"] = $"{Width} {Height}";
            pl["ref"] = $"{ReferenceX} {ReferenceY}";
            pl["tref"] = $"{TilingReferenceX} {TilingReferenceY}";
            pl["tile_parts"] = PacketsPerTilePart.ToString();
        }
        
        internal List<string> Validate()
        {
            var errors = new List<string>();
            
            if (Width < 0 || Height < 0)
                errors.Add("Tile dimensions must be non-negative");
            
            if (ReferenceX < 0 || ReferenceY < 0)
                errors.Add("Image reference point must be non-negative");
            
            if (TilingReferenceX < 0 || TilingReferenceY < 0)
                errors.Add("Tiling reference point must be non-negative");
            
            if (TilingReferenceX > ReferenceX || TilingReferenceY > ReferenceY)
                errors.Add("Tiling reference must not exceed image reference");
            
            if (PacketsPerTilePart < 0)
                errors.Add("Packets per tile-part must be non-negative");
            
            return errors;
        }
    }
    
    /// <summary>
    /// Wavelet filter types for JPEG 2000.
    /// </summary>
    public enum WaveletFilter
    {
        /// <summary>5-3 reversible filter (lossless compression).</summary>
        Reversible53,
        
        /// <summary>9-7 irreversible filter (lossy compression, better performance).</summary>
        Irreversible97
    }
    
    /// <summary>
    /// Configuration for wavelet transform.
    /// </summary>
    public class WaveletConfiguration
    {
        /// <summary>Gets or sets the wavelet filter type.</summary>
        public WaveletFilter Filter { get; set; } = WaveletFilter.Irreversible97;
        
        /// <summary>Gets or sets the number of decomposition levels.</summary>
        public int DecompositionLevels { get; set; } = 5;
        
        /// <summary>Gets or sets the code-block partition origin X.</summary>
        public int CodeBlockOriginX { get; set; } = 0;
        
        /// <summary>Gets or sets the code-block partition origin Y.</summary>
        public int CodeBlockOriginY { get; set; } = 0;
        
        /// <summary>
        /// Uses the 5-3 reversible filter (for lossless compression).
        /// </summary>
        public WaveletConfiguration UseReversible53()
        {
            Filter = WaveletFilter.Reversible53;
            return this;
        }
        
        /// <summary>
        /// Uses the 9-7 irreversible filter (for lossy compression).
        /// </summary>
        public WaveletConfiguration UseIrreversible97()
        {
            Filter = WaveletFilter.Irreversible97;
            return this;
        }
        
        /// <summary>
        /// Sets the number of wavelet decomposition levels.
        /// </summary>
        public WaveletConfiguration WithDecompositionLevels(int levels)
        {
            DecompositionLevels = levels;
            return this;
        }
        
        /// <summary>
        /// Sets the code-block partition origin.
        /// </summary>
        public WaveletConfiguration WithCodeBlockOrigin(int x, int y)
        {
            CodeBlockOriginX = x;
            CodeBlockOriginY = y;
            return this;
        }
        
        internal void ApplyTo(ParameterList pl)
        {
            pl["Ffilters"] = Filter == WaveletFilter.Reversible53 ? "w5x3" : "w9x7";
            pl["Wlev"] = DecompositionLevels.ToString();
            pl["Wcboff"] = $"{CodeBlockOriginX} {CodeBlockOriginY}";
        }
        
        internal List<string> Validate()
        {
            var errors = new List<string>();
            
            if (DecompositionLevels < 0 || DecompositionLevels > 32)
                errors.Add("Decomposition levels must be between 0 and 32");
            
            if (CodeBlockOriginX < 0 || CodeBlockOriginX > 1)
                errors.Add("Code-block origin X must be 0 or 1");
            
            if (CodeBlockOriginY < 0 || CodeBlockOriginY > 1)
                errors.Add("Code-block origin Y must be 0 or 1");
            
            return errors;
        }
    }
    
    /// <summary>
    /// Quantization type for JPEG 2000.
    /// </summary>
    public enum QuantizationType
    {
        /// <summary>Reversible quantization (lossless).</summary>
        Reversible,
        
        /// <summary>Scalar derived quantization.</summary>
        Derived,
        
        /// <summary>Scalar expounded quantization.</summary>
        Expounded
    }
    
    /// <summary>
    /// Configuration for quantization.
    /// </summary>
    public class QuantizationConfiguration
    {
        /// <summary>Gets or sets the quantization type.</summary>
        public QuantizationType Type { get; set; } = QuantizationType.Expounded;
        
        /// <summary>Gets or sets the base quantization step size.</summary>
        public float BaseStepSize { get; set; } = 0.0078125f;
        
        /// <summary>Gets or sets the number of guard bits.</summary>
        public int GuardBits { get; set; } = 1;
        
        /// <summary>
        /// Uses reversible quantization (for lossless).
        /// </summary>
        public QuantizationConfiguration UseReversible()
        {
            Type = QuantizationType.Reversible;
            return this;
        }
        
        /// <summary>
        /// Uses derived quantization.
        /// </summary>
        public QuantizationConfiguration UseDerived()
        {
            Type = QuantizationType.Derived;
            return this;
        }
        
        /// <summary>
        /// Uses expounded quantization (default).
        /// </summary>
        public QuantizationConfiguration UseExpounded()
        {
            Type = QuantizationType.Expounded;
            return this;
        }
        
        /// <summary>
        /// Sets the base quantization step size.
        /// </summary>
        public QuantizationConfiguration WithBaseStepSize(float stepSize)
        {
            BaseStepSize = stepSize;
            return this;
        }
        
        /// <summary>
        /// Sets the number of guard bits.
        /// </summary>
        public QuantizationConfiguration WithGuardBits(int bits)
        {
            GuardBits = bits;
            return this;
        }
        
        internal void ApplyTo(ParameterList pl)
        {
            string qtypeValue;
            switch (Type)
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
            
            if (Type != QuantizationType.Reversible)
            {
                pl["Qstep"] = BaseStepSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            
            pl["Qguard_bits"] = GuardBits.ToString();
        }
        
        internal List<string> Validate()
        {
            var errors = new List<string>();
            
            if (Type != QuantizationType.Reversible && BaseStepSize <= 0)
                errors.Add("Base step size must be positive for non-reversible quantization");
            
            if (GuardBits < 0 || GuardBits > 7)
                errors.Add("Guard bits must be between 0 and 7");
            
            return errors;
        }
    }
    
    /// <summary>
    /// Progression order types for JPEG 2000.
    /// </summary>
    public enum ProgressionOrder
    {
        /// <summary>Layer-Resolution-Component-Position.</summary>
        LRCP,
        
        /// <summary>Resolution-Layer-Component-Position.</summary>
        RLCP,
        
        /// <summary>Resolution-Position-Component-Layer.</summary>
        RPCL,
        
        /// <summary>Position-Component-Resolution-Layer.</summary>
        PCRL,
        
        /// <summary>Component-Position-Resolution-Layer.</summary>
        CPRL
    }
    
    /// <summary>
    /// Configuration for progression order and quality layers.
    /// </summary>
    public class ProgressionConfiguration
    {
        /// <summary>Gets or sets the progression order.</summary>
        public ProgressionOrder Order { get; set; } = ProgressionOrder.LRCP;
        
        /// <summary>Gets the quality layers specification.</summary>
        public List<float> QualityLayers { get; } = new List<float>();
        
        /// <summary>
        /// Sets the progression order.
        /// </summary>
        public ProgressionConfiguration WithOrder(ProgressionOrder order)
        {
            Order = order;
            return this;
        }
        
        /// <summary>
        /// Adds quality layers with specified bitrates.
        /// </summary>
        public ProgressionConfiguration WithQualityLayers(params float[] layers)
        {
            QualityLayers.Clear();
            QualityLayers.AddRange(layers);
            return this;
        }
        
        internal void ApplyTo(ParameterList pl)
        {
            string aptypeValue;
            switch (Order)
            {
                case ProgressionOrder.LRCP:
                    aptypeValue = "layer";
                    break;
                case ProgressionOrder.RLCP:
                    aptypeValue = "res";
                    break;
                case ProgressionOrder.RPCL:
                    aptypeValue = "res-pos";
                    break;
                case ProgressionOrder.PCRL:
                    aptypeValue = "pos-comp";
                    break;
                case ProgressionOrder.CPRL:
                    aptypeValue = "comp-pos";
                    break;
                default:
                    aptypeValue = "layer";
                    break;
            }
            pl["Aptype"] = aptypeValue;
            
            if (QualityLayers.Count > 0)
            {
                var layerSpec = string.Join(" ", QualityLayers);
                pl["Alayers"] = layerSpec;
            }
        }
        
        internal List<string> Validate()
        {
            var errors = new List<string>();
            
            foreach (var layer in QualityLayers)
            {
                if (layer <= 0)
                    errors.Add("Quality layer bitrates must be positive");
            }
            
            return errors;
        }
    }
    
    /// <summary>
    /// Configuration for code-block settings.
    /// </summary>
    public class CodeBlockConfiguration
    {
        /// <summary>Gets or sets the code-block width (must be power of 2, 4-1024).</summary>
        public int Width { get; set; } = 64;
        
        /// <summary>Gets or sets the code-block height (must be power of 2, 4-1024).</summary>
        public int Height { get; set; } = 64;
        
        /// <summary>
        /// Sets the code-block size.
        /// </summary>
        public CodeBlockConfiguration SetSize(int width, int height)
        {
            Width = width;
            Height = height;
            return this;
        }
        
        internal void ApplyTo(ParameterList pl)
        {
            pl["Cblksiz"] = $"{Width} {Height}";
        }
        
        internal List<string> Validate()
        {
            var errors = new List<string>();
            
            if (!IsPowerOfTwo(Width) || Width < 4 || Width > 1024)
                errors.Add("Code-block width must be power of 2 between 4 and 1024");
            
            if (!IsPowerOfTwo(Height) || Height < 4 || Height > 1024)
                errors.Add("Code-block height must be power of 2 between 4 and 1024");
            
            if (Width * Height > 4096)
                errors.Add("Code-block area (width × height) must not exceed 4096");
            
            return errors;
        }
        
        private static bool IsPowerOfTwo(int n)
        {
            return n > 0 && (n & (n - 1)) == 0;
        }
    }
    
    /// <summary>
    /// Length calculation method for entropy coding.
    /// </summary>
    public enum LengthCalculation
    {
        /// <summary>Near optimal length calculation.</summary>
        NearOptimal,
        
        /// <summary>Lazy good length calculation.</summary>
        LazyGood,
        
        /// <summary>Lazy length calculation.</summary>
        Lazy
    }
    
    /// <summary>
    /// Termination type for entropy coding.
    /// </summary>
    public enum TerminationType
    {
        /// <summary>Near optimal termination.</summary>
        NearOptimal,
        
        /// <summary>Easy termination.</summary>
        Easy,
        
        /// <summary>Predictable termination.</summary>
        Predict,
        
        /// <summary>Full termination.</summary>
        Full
    }
    
    /// <summary>
    /// Configuration for entropy coding.
    /// </summary>
    public class EntropyCodingConfiguration
    {
        /// <summary>Gets or sets the length calculation method.</summary>
        public LengthCalculation LengthCalculation { get; set; } = LengthCalculation.NearOptimal;
        
        /// <summary>Gets or sets the termination type.</summary>
        public TerminationType Termination { get; set; } = TerminationType.NearOptimal;
        
        /// <summary>Gets or sets whether to use segmentation symbols.</summary>
        public bool SegmentationSymbol { get; set; } = false;
        
        /// <summary>Gets or sets whether to use causal context formation.</summary>
        public bool CausalMode { get; set; } = false;
        
        /// <summary>Gets or sets whether to reset MQ coder.</summary>
        public bool ResetMQ { get; set; } = false;
        
        /// <summary>Gets or sets whether to use bypass mode.</summary>
        public bool BypassMode { get; set; } = false;
        
        /// <summary>Gets or sets whether to use regular termination.</summary>
        public bool RegularTermination { get; set; } = false;
        
        internal void ApplyTo(ParameterList pl)
        {
            string lenCalcValue;
            switch (LengthCalculation)
            {
                case LengthCalculation.NearOptimal:
                    lenCalcValue = "near_opt";
                    break;
                case LengthCalculation.LazyGood:
                    lenCalcValue = "lazy_good";
                    break;
                case LengthCalculation.Lazy:
                    lenCalcValue = "lazy";
                    break;
                default:
                    lenCalcValue = "near_opt";
                    break;
            }
            pl["Clen_calc"] = lenCalcValue;
            
            string termTypeValue;
            switch (Termination)
            {
                case TerminationType.NearOptimal:
                    termTypeValue = "near_opt";
                    break;
                case TerminationType.Easy:
                    termTypeValue = "easy";
                    break;
                case TerminationType.Predict:
                    termTypeValue = "predict";
                    break;
                case TerminationType.Full:
                    termTypeValue = "full";
                    break;
                default:
                    termTypeValue = "near_opt";
                    break;
            }
            pl["Cterm_type"] = termTypeValue;
            
            pl["Cseg_symbol"] = SegmentationSymbol ? "on" : "off";
            pl["Ccausal"] = CausalMode ? "on" : "off";
            pl["CresetMQ"] = ResetMQ ? "on" : "off";
            pl["Cbypass"] = BypassMode ? "on" : "off";
            pl["Cterminate"] = RegularTermination ? "on" : "off";
        }
    }
    
    /// <summary>
    /// Configuration for error resilience features.
    /// </summary>
    public class ErrorResilienceConfiguration
    {
        /// <summary>Gets or sets whether to use SOP (Start of Packet) markers.</summary>
        public bool SOPMarkers { get; set; } = false;
        
        /// <summary>Gets or sets whether to use EPH (End of Packet Header) markers.</summary>
        public bool EPHMarkers { get; set; } = false;
        
        /// <summary>
        /// Enables SOP markers for error resilience.
        /// </summary>
        public ErrorResilienceConfiguration EnableSOPMarkers()
        {
            SOPMarkers = true;
            return this;
        }
        
        /// <summary>
        /// Enables EPH markers for error resilience.
        /// </summary>
        public ErrorResilienceConfiguration EnableEPHMarkers()
        {
            EPHMarkers = true;
            return this;
        }
        
        /// <summary>
        /// Enables both SOP and EPH markers.
        /// </summary>
        public ErrorResilienceConfiguration EnableAll()
        {
            SOPMarkers = true;
            EPHMarkers = true;
            return this;
        }
        
        internal void ApplyTo(ParameterList pl)
        {
            pl["Psop"] = SOPMarkers ? "on" : "off";
            pl["Peph"] = EPHMarkers ? "on" : "off";
        }
    }
}
