// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreJ2K.j2k.fileformat.metadata
{
    /// <summary>
    /// Represents channel definition metadata for JPEG2000 images.
    /// Defines the interpretation and association of image components/channels.
    /// Essential for proper alpha channel and multi-component image handling.
    /// </summary>
    public class ChannelDefinitionData
    {
        /// <summary>
        /// Gets the list of channel definitions.
        /// </summary>
        public List<ChannelDefinition> Channels { get; } = new List<ChannelDefinition>();

        /// <summary>
        /// Adds a channel definition.
        /// </summary>
        /// <param name="channelIndex">Zero-based index of the channel in the codestream.</param>
        /// <param name="channelType">Type of channel (color, opacity, premultiplied opacity, etc.).</param>
        /// <param name="association">Association (0=whole image, 1=color-1, 2=color-2, 3=color-3, etc.).</param>
        public void AddChannel(int channelIndex, ChannelType channelType, int association)
        {
            Channels.Add(new ChannelDefinition
            {
                ChannelIndex = channelIndex,
                ChannelType = channelType,
                Association = association
            });
        }

        /// <summary>
        /// Adds a color channel (RGB or grayscale).
        /// </summary>
        /// <param name="channelIndex">Channel index in codestream.</param>
        /// <param name="colorNumber">Color number (1=Red/Gray, 2=Green, 3=Blue).</param>
        public void AddColorChannel(int channelIndex, int colorNumber)
        {
            AddChannel(channelIndex, ChannelType.Color, colorNumber);
        }

        /// <summary>
        /// Adds an opacity (alpha) channel.
        /// </summary>
        /// <param name="channelIndex">Channel index in codestream.</param>
        /// <param name="associatedColor">Associated color (0=whole image, 1-3=specific color).</param>
        public void AddOpacityChannel(int channelIndex, int associatedColor = 0)
        {
            AddChannel(channelIndex, ChannelType.Opacity, associatedColor);
        }

        /// <summary>
        /// Adds a premultiplied opacity channel.
        /// </summary>
        /// <param name="channelIndex">Channel index in codestream.</param>
        /// <param name="associatedColor">Associated color (0=whole image, 1-3=specific color).</param>
        public void AddPremultipliedOpacityChannel(int channelIndex, int associatedColor = 0)
        {
            AddChannel(channelIndex, ChannelType.PremultipliedOpacity, associatedColor);
        }

        /// <summary>
        /// Gets the channel definition for a specific channel index.
        /// </summary>
        public ChannelDefinition GetChannel(int channelIndex)
        {
            return Channels.FirstOrDefault(c => c.ChannelIndex == channelIndex);
        }

        /// <summary>
        /// Gets all color channels.
        /// </summary>
        public IEnumerable<ChannelDefinition> GetColorChannels()
        {
            return Channels.Where(c => c.ChannelType == ChannelType.Color);
        }

        /// <summary>
        /// Gets all opacity channels.
        /// </summary>
        public IEnumerable<ChannelDefinition> GetOpacityChannels()
        {
            return Channels.Where(c => c.ChannelType == ChannelType.Opacity || 
                                      c.ChannelType == ChannelType.PremultipliedOpacity);
        }

        /// <summary>
        /// Returns true if any channel is defined as opacity or premultiplied opacity.
        /// </summary>
        public bool HasAlphaChannel => GetOpacityChannels().Any();

        /// <summary>
        /// Returns true if there are any channel definitions.
        /// </summary>
        public bool HasDefinitions => Channels.Count > 0;

        /// <summary>
        /// Creates a standard RGB channel definition.
        /// </summary>
        public static ChannelDefinitionData CreateRgb()
        {
            var def = new ChannelDefinitionData();
            def.AddColorChannel(0, 1); // Red
            def.AddColorChannel(1, 2); // Green
            def.AddColorChannel(2, 3); // Blue
            return def;
        }

        /// <summary>
        /// Creates a standard RGBA channel definition.
        /// </summary>
        public static ChannelDefinitionData CreateRgba()
        {
            var def = new ChannelDefinitionData();
            def.AddColorChannel(0, 1); // Red
            def.AddColorChannel(1, 2); // Green
            def.AddColorChannel(2, 3); // Blue
            def.AddOpacityChannel(3, 0); // Alpha for whole image
            return def;
        }

        /// <summary>
        /// Creates a standard grayscale channel definition.
        /// </summary>
        public static ChannelDefinitionData CreateGrayscale()
        {
            var def = new ChannelDefinitionData();
            def.AddColorChannel(0, 1); // Gray
            return def;
        }

        /// <summary>
        /// Creates a standard grayscale + alpha channel definition.
        /// </summary>
        public static ChannelDefinitionData CreateGrayscaleAlpha()
        {
            var def = new ChannelDefinitionData();
            def.AddColorChannel(0, 1); // Gray
            def.AddOpacityChannel(1, 0); // Alpha for whole image
            return def;
        }

        /// <summary>
        /// Returns a string representation of the channel definitions.
        /// </summary>
        public override string ToString()
        {
            if (!HasDefinitions)
                return "No channel definitions";

            var parts = new List<string>();
            foreach (var ch in Channels)
            {
                parts.Add($"Ch{ch.ChannelIndex}: {ch.ChannelType} (Assoc={ch.Association})");
            }
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Represents a single channel definition entry.
    /// </summary>
    public class ChannelDefinition
    {
        /// <summary>
        /// Gets or sets the zero-based index of the channel in the codestream.
        /// </summary>
        public int ChannelIndex { get; set; }

        /// <summary>
        /// Gets or sets the type of channel.
        /// </summary>
        public ChannelType ChannelType { get; set; }

        /// <summary>
        /// Gets or sets the association value.
        /// 0 = whole image
        /// 1 = first color (Red or Gray)
        /// 2 = second color (Green)
        /// 3 = third color (Blue)
        /// 65535 = unassociated
        /// </summary>
        public int Association { get; set; }

        public override string ToString()
        {
            return $"Ch{ChannelIndex}: {ChannelType} (Assoc={Association})";
        }
    }

    /// <summary>
    /// Defines the type of channel according to ISO/IEC 15444-1.
    /// </summary>
    public enum ChannelType : ushort
    {
        /// <summary>
        /// Color channel (RGB component or grayscale).
        /// </summary>
        Color = 0,

        /// <summary>
        /// Opacity channel (alpha).
        /// </summary>
        Opacity = 1,

        /// <summary>
        /// Premultiplied opacity channel.
        /// </summary>
        PremultipliedOpacity = 2,

        /// <summary>
        /// Unspecified channel type.
        /// </summary>
        Unspecified = 65535
    }
}
