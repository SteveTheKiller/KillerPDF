// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CoreJ2K.j2k.fileformat.metadata
{
    /// <summary>
    /// Serializes/deserializes <see cref="J2KMetadata"/> instances to and from XML, providing a
    /// JPEG 2000 Part 14 (JPXML / XML representation) style view of file-format metadata.
    /// </summary>
    /// <remarks>
    /// This is a pragmatic, codec-friendly subset of ISO/IEC 15444-14: it captures the metadata that
    /// CoreJ2K already models (comments, XML/UUID/UUID-info boxes, Part 2 JPR/Label boxes, JP2
    /// Intellectual Property boxes, Reader Requirements, resolution, palette/component mapping,
    /// bits-per-component, channel definitions, component registration, and codestream COM markers)
    /// rather than the full JPXML schema. The element/attribute naming follows the spec's spirit so
    /// the output is human-readable and round-trips cleanly through <see cref="FromXml"/>.
    /// </remarks>
    public static class J2KMetadataXml
    {
        /// <summary>JPXML-style namespace used for the root metadata document.</summary>
        public const string Namespace = "urn:iso:std:iso-iec:15444:-14";

        private static readonly XNamespace Ns = Namespace;

        /// <summary>
        /// Serializes the supplied metadata to an XML string.
        /// </summary>
        /// <param name="metadata">Metadata to serialize.</param>
        /// <param name="indent">Whether to indent the output for readability.</param>
        public static string ToXml(J2KMetadata metadata, bool indent = true)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            var doc = new XDocument(BuildRoot(metadata));

            var settings = new XmlWriterSettings
            {
                Indent = indent,
                OmitXmlDeclaration = false,
                Encoding = new UTF8Encoding(false)
            };

            using (var ms = new MemoryStream())
            {
                using (var xw = XmlWriter.Create(ms, settings))
                {
                    doc.Save(xw);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        /// <summary>
        /// Writes the metadata XML representation to the supplied stream.
        /// </summary>
        public static void ToXml(J2KMetadata metadata, Stream destination, bool indent = true)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            var doc = new XDocument(BuildRoot(metadata));
            var settings = new XmlWriterSettings
            {
                Indent = indent,
                OmitXmlDeclaration = false,
                Encoding = new UTF8Encoding(false),
                CloseOutput = false
            };
            using (var xw = XmlWriter.Create(destination, settings))
            {
                doc.Save(xw);
            }
        }

        /// <summary>
        /// Parses an XML representation previously produced by <see cref="ToXml(J2KMetadata, bool)"/>
        /// and returns a populated <see cref="J2KMetadata"/> instance.
        /// </summary>
        public static J2KMetadata FromXml(string xml)
        {
            if (string.IsNullOrEmpty(xml)) throw new ArgumentException("XML input is empty.", nameof(xml));
            var doc = XDocument.Parse(xml);
            return FromXDocument(doc);
        }

        /// <summary>
        /// Parses an XML representation from a stream and returns a populated <see cref="J2KMetadata"/>.
        /// </summary>
        public static J2KMetadata FromXml(Stream source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var doc = XDocument.Load(source);
            return FromXDocument(doc);
        }

        private static J2KMetadata FromXDocument(XDocument doc)
        {
            var root = doc.Root;
            if (root == null) throw new InvalidDataException("Empty JPXML document.");
            if (root.Name.LocalName != "J2KMetadata")
                throw new InvalidDataException($"Unexpected root element '{root.Name.LocalName}'. Expected 'J2KMetadata'.");

            var md = new J2KMetadata();

            foreach (var c in root.Elements(Ns + "Comment"))
            {
                md.AddComment((string?)c ?? string.Empty, (string?)c.Attribute("lang") ?? "en");
            }

            foreach (var x in root.Elements(Ns + "XmlBox"))
            {
                md.AddXml(x.Value);
            }

            foreach (var u in root.Elements(Ns + "UuidBox"))
            {
                var idAttr = (string?)u.Attribute("uuid");
                if (idAttr == null || !Guid.TryParse(idAttr, out var guid)) continue;
                var data = DecodeBytes(u.Value);
                md.AddUuid(guid, data);
            }

            var info = root.Element(Ns + "UuidInfo");
            if (info != null)
            {
                md.UuidInfo = new UuidInfoBox
                {
                    Url = (string?)info.Element(Ns + "Url"),
                    UrlVersion = (byte)((int?)info.Attribute("urlVersion") ?? 0),
                    UrlFlags = (byte)((int?)info.Attribute("urlFlags") ?? 0)
                };
                foreach (var idElem in info.Elements(Ns + "Uuid"))
                {
                    if (Guid.TryParse(idElem.Value, out var g)) md.UuidInfo.UuidList.Add(g);
                }
            }

            foreach (var ipr in root.Elements(Ns + "IntellectualPropertyRights"))
            {
                var binary = (bool?)ipr.Attribute("binary") == true;
                if (binary)
                    md.IntellectualPropertyRights.Add(new JprBox { RawData = DecodeBytes(ipr.Value) });
                else
                    md.AddIntellectualPropertyRights(ipr.Value);
            }

            foreach (var lbl in root.Elements(Ns + "Label"))
            {
                md.AddLabel(lbl.Value);
            }

            foreach (var ip in root.Elements(Ns + "IntellectualPropertyBox"))
            {
                var binary = (bool?)ip.Attribute("binary") == true;
                md.IntellectualPropertyBoxes.Add(new IntellectualPropertyBox
                {
                    RawData = binary ? DecodeBytes(ip.Value) : Encoding.UTF8.GetBytes(ip.Value),
                    Text = binary ? null : ip.Value
                });
            }

            var resolution = root.Element(Ns + "Resolution");
            if (resolution != null)
            {
                var capH = (double?)resolution.Attribute("captureHorizontalDpi");
                var capV = (double?)resolution.Attribute("captureVerticalDpi");
                var dispH = (double?)resolution.Attribute("displayHorizontalDpi");
                var dispV = (double?)resolution.Attribute("displayVerticalDpi");
                if (capH.HasValue && capV.HasValue) md.SetResolutionDpi(capH.Value, capV.Value, true);
                if (dispH.HasValue && dispV.HasValue) md.SetResolutionDpi(dispH.Value, dispV.Value, false);
            }

            foreach (var cm in root.Elements(Ns + "CodestreamComment"))
            {
                var binary = (bool?)cm.Attribute("binary") == true;
                var method = (int?)cm.Attribute("registrationMethod") ?? 1;
                var isMain = (bool?)cm.Attribute("mainHeader") ?? true;
                var tile = (int?)cm.Attribute("tileIndex") ?? -1;
                if (binary)
                    md.AddCodestreamComment(DecodeBytes(cm.Value), method, isMain, tile);
                else
                    md.AddCodestreamComment(cm.Value, method, isMain, tile);
            }

            return md;
        }

        private static XElement BuildRoot(J2KMetadata m)
        {
            var root = new XElement(Ns + "J2KMetadata",
                new XAttribute("version", "1.0"));

            foreach (var c in m.Comments)
            {
                root.Add(new XElement(Ns + "Comment",
                    new XAttribute("lang", c.Language ?? "en"),
                    c.IsBinary ? string.Empty : (c.Text ?? string.Empty)));
            }

            foreach (var x in m.XmlBoxes)
            {
                root.Add(new XElement(Ns + "XmlBox", new XCData(x.XmlContent ?? string.Empty)));
            }

            foreach (var u in m.UuidBoxes)
            {
                root.Add(new XElement(Ns + "UuidBox",
                    new XAttribute("uuid", u.Uuid.ToString()),
                    EncodeBytes(u.Data)));
            }

            if (m.UuidInfo != null)
            {
                var info = new XElement(Ns + "UuidInfo",
                    new XAttribute("urlVersion", m.UuidInfo.UrlVersion),
                    new XAttribute("urlFlags", m.UuidInfo.UrlFlags));
                foreach (var g in m.UuidInfo.UuidList)
                    info.Add(new XElement(Ns + "Uuid", g.ToString()));
                if (!string.IsNullOrEmpty(m.UuidInfo.Url))
                    info.Add(new XElement(Ns + "Url", m.UuidInfo.Url));
                root.Add(info);
            }

            if (m.ReaderRequirements != null)
            {
                var rr = new XElement(Ns + "ReaderRequirements",
                    new XAttribute("jp2Compatible", m.ReaderRequirements.IsJp2Compatible));
                foreach (var f in m.ReaderRequirements.StandardFeatures)
                    rr.Add(new XElement(Ns + "StandardFeature", new XAttribute("number", f)));
                foreach (var f in m.ReaderRequirements.VendorFeatures)
                    rr.Add(new XElement(Ns + "VendorFeature", new XAttribute("uuid", f.ToString())));
                root.Add(rr);
            }

            foreach (var ipr in m.IntellectualPropertyRights)
            {
                if (ipr.IsBinary)
                    root.Add(new XElement(Ns + "IntellectualPropertyRights",
                        new XAttribute("binary", true), EncodeBytes(ipr.RawData)));
                else
                    root.Add(new XElement(Ns + "IntellectualPropertyRights", ipr.GetText() ?? string.Empty));
            }

            foreach (var lbl in m.Labels)
            {
                root.Add(new XElement(Ns + "Label", lbl.GetLabel() ?? string.Empty));
            }

            foreach (var ip in m.IntellectualPropertyBoxes)
            {
                var text = ip.GetText();
                var hasRaw = ip.RawData != null && ip.RawData.Length > 0;
                if (!string.IsNullOrEmpty(text) && (!hasRaw || IsLikelyText(ip.RawData)))
                    root.Add(new XElement(Ns + "IntellectualPropertyBox", text));
                else
                    root.Add(new XElement(Ns + "IntellectualPropertyBox",
                        new XAttribute("binary", true), EncodeBytes(ip.RawData)));
            }

            if (m.Resolution != null)
            {
                var attrs = new List<XAttribute>();
                if (m.Resolution.HasCaptureResolution)
                {
                    attrs.Add(new XAttribute("captureHorizontalDpi",
                        m.Resolution.HorizontalCaptureDpi!.Value.ToString("R", CultureInfo.InvariantCulture)));
                    attrs.Add(new XAttribute("captureVerticalDpi",
                        m.Resolution.VerticalCaptureDpi!.Value.ToString("R", CultureInfo.InvariantCulture)));
                }
                if (m.Resolution.HasDisplayResolution)
                {
                    attrs.Add(new XAttribute("displayHorizontalDpi",
                        m.Resolution.HorizontalDisplayDpi!.Value.ToString("R", CultureInfo.InvariantCulture)));
                    attrs.Add(new XAttribute("displayVerticalDpi",
                        m.Resolution.VerticalDisplayDpi!.Value.ToString("R", CultureInfo.InvariantCulture)));
                }
                if (attrs.Count > 0)
                    root.Add(new XElement(Ns + "Resolution", attrs.Cast<object>().ToArray()));
            }

            if (m.Palette != null)
            {
                var palette = new XElement(Ns + "Palette",
                    new XAttribute("numEntries", m.Palette.NumEntries),
                    new XAttribute("numColumns", m.Palette.NumColumns));
                if (m.Palette.BitDepths != null)
                    palette.Add(new XAttribute("bitDepths",
                        string.Join(",", m.Palette.BitDepths.Select(b => b.ToString(CultureInfo.InvariantCulture)))));
                root.Add(palette);
            }

            if (m.BitsPerComponent != null && m.BitsPerComponent.ComponentBitDepths != null)
            {
                root.Add(new XElement(Ns + "BitsPerComponent",
                    new XAttribute("values",
                        string.Join(",", m.BitsPerComponent.ComponentBitDepths.Select(b => b.ToString(CultureInfo.InvariantCulture))))));
            }

            if (m.ChannelDefinitions != null)
            {
                var cdef = new XElement(Ns + "ChannelDefinitions");
                foreach (var d in m.ChannelDefinitions.Channels)
                    cdef.Add(new XElement(Ns + "Channel",
                        new XAttribute("index", d.ChannelIndex),
                        new XAttribute("type", (ushort)d.ChannelType),
                        new XAttribute("association", d.Association)));
                root.Add(cdef);
            }

            if (m.ComponentMapping != null)
            {
                var cmap = new XElement(Ns + "ComponentMapping");
                foreach (var e in m.ComponentMapping.Mappings)
                    cmap.Add(new XElement(Ns + "Map",
                        new XAttribute("componentIndex", e.ComponentIndex),
                        new XAttribute("mappingType", e.MappingType),
                        new XAttribute("paletteColumn", e.PaletteColumn)));
                root.Add(cmap);
            }

            if (m.ComponentRegistration != null)
            {
                var crg = new XElement(Ns + "ComponentRegistration",
                    new XAttribute("numComponents", m.ComponentRegistration.NumComponents));
                if (m.ComponentRegistration.HorizontalOffsets != null)
                    crg.Add(new XAttribute("horizontalOffsets",
                        string.Join(",", m.ComponentRegistration.HorizontalOffsets.Select(o => o.ToString(CultureInfo.InvariantCulture)))));
                if (m.ComponentRegistration.VerticalOffsets != null)
                    crg.Add(new XAttribute("verticalOffsets",
                        string.Join(",", m.ComponentRegistration.VerticalOffsets.Select(o => o.ToString(CultureInfo.InvariantCulture)))));
                root.Add(crg);
            }

            if (m.IccProfile != null && m.IccProfile.ProfileBytes != null)
            {
                root.Add(new XElement(Ns + "IccProfile",
                    new XAttribute("size", m.IccProfile.ProfileBytes.Length),
                    EncodeBytes(m.IccProfile.ProfileBytes)));
            }

            foreach (var cc in m.CodestreamComments)
            {
                var elem = new XElement(Ns + "CodestreamComment",
                    new XAttribute("registrationMethod", cc.RegistrationMethod),
                    new XAttribute("mainHeader", cc.IsMainHeader),
                    new XAttribute("tileIndex", cc.TileIndex));
                if (cc.IsBinary)
                {
                    elem.Add(new XAttribute("binary", true));
                    elem.Add(EncodeBytes(cc.Data));
                }
                else
                {
                    elem.Add(cc.Text ?? string.Empty);
                }
                root.Add(elem);
            }

            return root;
        }

        private static string EncodeBytes(byte[]? data) =>
            data == null || data.Length == 0 ? string.Empty : Convert.ToBase64String(data);

        private static byte[] DecodeBytes(string value) =>
            string.IsNullOrWhiteSpace(value) ? Array.Empty<byte>() : Convert.FromBase64String(value.Trim());

        private static bool IsLikelyText(byte[]? data)
        {
            if (data == null || data.Length == 0) return false;
            // Cheap heuristic: treat as text only when every byte is printable ASCII or common whitespace.
            foreach (var b in data)
            {
                if (b == 0x09 || b == 0x0A || b == 0x0D) continue;
                if (b < 0x20 || b == 0x7F) return false;
            }
            return true;
        }
    }
}
