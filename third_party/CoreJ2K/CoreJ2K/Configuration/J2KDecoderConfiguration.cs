// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreJ2K.j2k.util;

namespace CoreJ2K.Configuration
{
    /// <summary>
    /// Fluent API for configuring JPEG 2000 decoding parameters.
    /// Provides a modern, type-safe alternative to ParameterList for decoding.
    /// </summary>
    public class J2KDecoderConfiguration
    {
        private int _resolutionLevel = -1; // -1 means highest resolution
        private float _decodingRate = -1f; // -1 means decode all
        private int _decodingBytes = -1; // -1 means decode all
        private bool _useColorSpace = true;
        private bool _parsingMode = true;
        private QuitConditions _quitConditions = new QuitConditions();
        private ComponentTransformSettings _componentTransform = new ComponentTransformSettings();
        private bool _verbose = true;
        
        /// <summary>
        /// Gets or sets the resolution level for decoding.
        /// 0 = lowest resolution, -1 or max = highest resolution.
        /// </summary>
        public int ResolutionLevel
        {
            get => _resolutionLevel;
            set => _resolutionLevel = value;
        }
        
        /// <summary>
        /// Gets or sets the decoding rate in bits per pixel.
        /// -1 means decode all data.
        /// </summary>
        public float DecodingRate
        {
            get => _decodingRate;
            set => _decodingRate = value;
        }
        
        /// <summary>
        /// Gets or sets the decoding rate in bytes.
        /// -1 means decode all data.
        /// </summary>
        public int DecodingBytes
        {
            get => _decodingBytes;
            set => _decodingBytes = value;
        }
        
        /// <summary>
        /// Gets or sets whether to apply color space transformations.
        /// </summary>
        public bool UseColorSpace
        {
            get => _useColorSpace;
            set => _useColorSpace = value;
        }
        
        /// <summary>
        /// Gets or sets whether to use parsing mode when rate/byte limit is specified.
        /// True = parse mode (default), False = truncate mode.
        /// </summary>
        public bool ParsingMode
        {
            get => _parsingMode;
            set => _parsingMode = value;
        }
        
        /// <summary>
        /// Gets or sets whether to print verbose information during decoding.
        /// </summary>
        public bool Verbose
        {
            get => _verbose;
            set => _verbose = value;
        }
        
        /// <summary>
        /// Gets the quit conditions configuration.
        /// </summary>
        public QuitConditions QuitConditions => _quitConditions;
        
        /// <summary>
        /// Gets the component transformation settings.
        /// </summary>
        public ComponentTransformSettings ComponentTransform => _componentTransform;
        
        /// <summary>
        /// Sets the resolution level to decode.
        /// 0 = lowest resolution, higher values = higher resolution.
        /// </summary>
        /// <param name="level">Resolution level (0 to max available).</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KDecoderConfiguration WithResolutionLevel(int level)
        {
            if (level < -1)
                throw new ArgumentException("Resolution level must be -1 (highest) or non-negative", nameof(level));
            
            _resolutionLevel = level;
            return this;
        }
        
        /// <summary>
        /// Sets the resolution level to highest available.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KDecoderConfiguration WithHighestResolution()
        {
            _resolutionLevel = -1;
            return this;
        }
        
        /// <summary>
        /// Sets the decoding rate in bits per pixel.
        /// </summary>
        /// <param name="rate">Bits per pixel to decode (positive), or -1 for all.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KDecoderConfiguration WithDecodingRate(float rate)
        {
            if (rate < -1)
                throw new ArgumentException("Decoding rate must be -1 (all) or positive", nameof(rate));
            
            _decodingRate = rate;
            _decodingBytes = -1; // Clear bytes if rate is set
            return this;
        }
        
        /// <summary>
        /// Sets the decoding rate in bytes.
        /// </summary>
        /// <param name="bytes">Number of bytes to decode (positive), or -1 for all.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KDecoderConfiguration WithDecodingBytes(int bytes)
        {
            if (bytes < -1)
                throw new ArgumentException("Decoding bytes must be -1 (all) or non-negative", nameof(bytes));
            
            _decodingBytes = bytes;
            _decodingRate = -1; // Clear rate if bytes is set
            return this;
        }
        
        /// <summary>
        /// Configures whether to apply color space transformations.
        /// </summary>
        /// <param name="useColorSpace">True to apply color space transforms (default), false to skip.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KDecoderConfiguration WithColorSpace(bool useColorSpace)
        {
            _useColorSpace = useColorSpace;
            return this;
        }
        
        /// <summary>
        /// Configures parsing mode for rate-limited decoding.
        /// </summary>
        /// <param name="parsingMode">True for parsing mode (default), false for truncate mode.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KDecoderConfiguration WithParsingMode(bool parsingMode)
        {
            _parsingMode = parsingMode;
            return this;
        }
        
        /// <summary>
        /// Enables progressive decoding with parsing mode.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KDecoderConfiguration WithProgressiveDecoding()
        {
            _parsingMode = true;
            return this;
        }
        
        /// <summary>
        /// Configures verbose output.
        /// </summary>
        /// <param name="verbose">True to enable verbose output, false to disable.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KDecoderConfiguration WithVerbose(bool verbose)
        {
            _verbose = verbose;
            return this;
        }
        
        /// <summary>
        /// Configures quit conditions for early termination.
        /// </summary>
        /// <param name="configurator">Action to configure quit conditions.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KDecoderConfiguration WithQuitConditions(Action<QuitConditions> configurator)
        {
            configurator?.Invoke(_quitConditions);
            return this;
        }
        
        /// <summary>
        /// Configures component transformation settings.
        /// </summary>
        /// <param name="configurator">Action to configure component transform.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public J2KDecoderConfiguration WithComponentTransform(Action<ComponentTransformSettings> configurator)
        {
            configurator?.Invoke(_componentTransform);
            return this;
        }
        
        /// <summary>
        /// Converts this configuration to a ParameterList for use with the decoder.
        /// </summary>
        /// <returns>ParameterList with all configured parameters.</returns>
        public ParameterList ToParameterList()
        {
            // Create ParameterList with defaults to ensure DefaultParameterList is populated
            // This is required by FileBitstreamReaderAgent which accesses pl.DefaultParameterList
            var pl = new ParameterList(J2kImage.GetDefaultDecoderParameterList());
            
            // Resolution level
            if (_resolutionLevel >= 0)
            {
                pl["res"] = _resolutionLevel.ToString();
            }
            
            // Rate and bytes - Override defaults with configured values
            // A value of -1 means "decode all data"
            pl["rate"] = _decodingRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
            pl["nbytes"] = _decodingBytes.ToString();
            
            // Color space
            pl["nocolorspace"] = _useColorSpace ? "off" : "on";
            
            // Parsing mode
            pl["parsing"] = _parsingMode ? "on" : "off";
            
            // Verbose
            pl["verbose"] = _verbose ? "on" : "off";
            
            // Quit conditions
            _quitConditions.ApplyTo(pl);
            
            // Component transform
            _componentTransform.ApplyTo(pl);
            
            return pl;
        }
        
        /// <summary>
        /// Validates the configuration and returns any validation errors.
        /// </summary>
        /// <returns>List of validation error messages, empty if valid.</returns>
        public List<string> Validate()
        {
            var errors = new List<string>();
            
            if (_resolutionLevel < -1)
            {
                errors.Add("Resolution level must be -1 (highest) or non-negative");
            }
            
            if (_decodingRate < -1)
            {
                errors.Add("Decoding rate must be -1 (all) or positive");
            }
            
            if (_decodingBytes < -1)
            {
                errors.Add("Decoding bytes must be -1 (all) or non-negative");
            }
            
            if (_decodingRate > 0 && _decodingBytes > 0)
            {
                errors.Add("Cannot specify both decoding rate and decoding bytes");
            }
            
            errors.AddRange(_quitConditions.Validate());
            
            return errors;
        }
        
        /// <summary>
        /// Checks if the configuration is valid.
        /// </summary>
        public bool IsValid => Validate().Count == 0;

        /// <summary>Decodes JPEG 2000 data from a byte array using this configuration.</summary>
        public J2kDecodeResult Decode(byte[] data)
            => J2kImage.DecodeBytes(data, this);

        /// <summary>Decodes JPEG 2000 data from a <see cref="ReadOnlyMemory{T}"/> buffer using this configuration.</summary>
        public J2kDecodeResult Decode(ReadOnlyMemory<byte> data)
            => J2kImage.DecodeBytes(data, this);

        /// <summary>Decodes a JPEG 2000 stream using this configuration.</summary>
        public J2kDecodeResult Decode(Stream stream)
            => J2kImage.DecodeStream(stream, this);

        /// <summary>Decodes JPEG 2000 data from a byte array asynchronously using this configuration.</summary>
        public Task<J2kDecodeResult> DecodeAsync(byte[] data,
            CancellationToken cancellationToken = default)
            => J2kImage.DecodeBytesAsync(data, this, cancellationToken);

        /// <summary>Decodes JPEG 2000 data from a <see cref="ReadOnlyMemory{T}"/> buffer asynchronously using this configuration.</summary>
        public Task<J2kDecodeResult> DecodeAsync(ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
            => J2kImage.DecodeBytesAsync(data, this, cancellationToken);

        /// <summary>Decodes a JPEG 2000 stream asynchronously using this configuration.</summary>
        public Task<J2kDecodeResult> DecodeAsync(Stream stream,
            CancellationToken cancellationToken = default)
            => J2kImage.DecodeStreamAsync(stream, this, cancellationToken);
    }
    
    /// <summary>
    /// Configuration for decoder quit conditions (early termination).
    /// </summary>
    public class QuitConditions
    {
        /// <summary>Gets or sets the maximum number of code blocks to decode (-1 = no limit).</summary>
        public int MaxCodeBlocks { get; set; } = -1;
        
        /// <summary>Gets or sets the maximum number of layers to decode (-1 = no limit).</summary>
        public int MaxLayers { get; set; } = -1;
        
        /// <summary>Gets or sets the maximum number of bit planes to decode (-1 = no limit).</summary>
        public int MaxBitPlanes { get; set; } = -1;
        
        /// <summary>Gets or sets whether to quit after first progression order.</summary>
        public bool QuitAfterFirstProgressionOrder { get; set; } = false;
        
        /// <summary>Gets or sets whether to decode only first tile part of each tile.</summary>
        public bool OnlyFirstTilePart { get; set; } = false;
        
        /// <summary>
        /// Sets the maximum number of code blocks to decode.
        /// </summary>
        public QuitConditions WithMaxCodeBlocks(int maxCodeBlocks)
        {
            MaxCodeBlocks = maxCodeBlocks;
            return this;
        }
        
        /// <summary>
        /// Sets the maximum number of layers to decode.
        /// </summary>
        public QuitConditions WithMaxLayers(int maxLayers)
        {
            MaxLayers = maxLayers;
            return this;
        }
        
        /// <summary>
        /// Sets the maximum number of bit planes to decode.
        /// </summary>
        public QuitConditions WithMaxBitPlanes(int maxBitPlanes)
        {
            MaxBitPlanes = maxBitPlanes;
            return this;
        }
        
        /// <summary>
        /// Enables quitting after the first progression order.
        /// </summary>
        public QuitConditions QuitAfterFirstProgression()
        {
            QuitAfterFirstProgressionOrder = true;
            return this;
        }
        
        /// <summary>
        /// Enables decoding only the first tile part of each tile.
        /// </summary>
        public QuitConditions DecodeOnlyFirstTilePart()
        {
            OnlyFirstTilePart = true;
            return this;
        }
        
        internal void ApplyTo(ParameterList pl)
        {
            // Always set ncb_quit, l_quit, and m_quit as FileBitstreamReaderAgent expects them
            pl["ncb_quit"] = MaxCodeBlocks.ToString();
            pl["l_quit"] = MaxLayers.ToString();
            
            if (MaxBitPlanes >= 0)
            {
                pl["m_quit"] = MaxBitPlanes.ToString();
            }
            
            pl["poc_quit"] = QuitAfterFirstProgressionOrder ? "on" : "off";
            pl["one_tp"] = OnlyFirstTilePart ? "on" : "off";
        }
        
        internal List<string> Validate()
        {
            var errors = new List<string>();
            
            if (MaxCodeBlocks < -1)
                errors.Add("Max code blocks must be -1 (no limit) or non-negative");
            
            if (MaxLayers < -1)
                errors.Add("Max layers must be -1 (no limit) or non-negative");
            
            if (MaxBitPlanes < -1)
                errors.Add("Max bit planes must be -1 (no limit) or non-negative");
            
            return errors;
        }
    }
    
    /// <summary>
    /// Configuration for component transformation settings during decoding.
    /// </summary>
    public class ComponentTransformSettings
    {
        /// <summary>
        /// Gets or sets whether to apply the component transform indicated in the codestream.
        /// </summary>
        public bool UseComponentTransform { get; set; } = true;
        
        /// <summary>
        /// Enables component transformation (default).
        /// </summary>
        public ComponentTransformSettings Enable()
        {
            UseComponentTransform = true;
            return this;
        }
        
        /// <summary>
        /// Disables component transformation.
        /// </summary>
        public ComponentTransformSettings Disable()
        {
            UseComponentTransform = false;
            return this;
        }
        
        internal void ApplyTo(ParameterList pl)
        {
            pl["comp_transf"] = UseComponentTransform ? "on" : "off";
        }
    }
}
