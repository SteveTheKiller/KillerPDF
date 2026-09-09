// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreJ2K.Configuration
{
    /// <summary>
    /// Modern fluent API for configuring JPEG 2000 metadata (comments, XML, UUIDs).
    /// Can be used standalone or as part of encoder configuration.
    /// </summary>
    public class MetadataConfigurationBuilder
    {
        private readonly List<string> _comments = new List<string>();
        private readonly List<string> _xmlData = new List<string>();
        private readonly List<UuidData> _uuids = new List<UuidData>();
        private string? _intellectualPropertyRights = null;
        
        /// <summary>
        /// Gets the list of comments to include in the JP2 file.
        /// </summary>
        public IReadOnlyList<string> Comments => _comments.AsReadOnly();
        
        /// <summary>
        /// Gets the list of XML data to include in the JP2 file.
        /// </summary>
        public IReadOnlyList<string> XmlData => _xmlData.AsReadOnly();
        
        /// <summary>
        /// Gets the list of UUID data to include in the JP2 file.
        /// </summary>
        public IReadOnlyList<UuidData> Uuids => _uuids.AsReadOnly();
        
        /// <summary>
        /// Gets the intellectual property rights information.
        /// </summary>
        public string? IntellectualPropertyRights => _intellectualPropertyRights;
        
        /// <summary>
        /// Adds a comment to the JP2 file.
        /// Comments are human-readable text annotations.
        /// </summary>
        /// <param name="comment">The comment text to add.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public MetadataConfigurationBuilder WithComment(string comment)
        {
            if (string.IsNullOrEmpty(comment))
                throw new ArgumentException("Comment cannot be null or empty", nameof(comment));
            
            _comments.Add(comment);
            return this;
        }
        
        /// <summary>
        /// Adds multiple comments to the JP2 file.
        /// </summary>
        /// <param name="comments">The comments to add.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public MetadataConfigurationBuilder WithComments(params string[] comments)
        {
            if (comments == null)
                throw new ArgumentNullException(nameof(comments));
            
            foreach (var comment in comments)
            {
                if (!string.IsNullOrEmpty(comment))
                    _comments.Add(comment);
            }
            
            return this;
        }
        
        /// <summary>
        /// Adds XML data to the JP2 file.
        /// XML can contain structured metadata in any schema.
        /// </summary>
        /// <param name="xml">The XML data to add.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public MetadataConfigurationBuilder WithXml(string xml)
        {
            if (string.IsNullOrEmpty(xml))
                throw new ArgumentException("XML data cannot be null or empty", nameof(xml));
            
            _xmlData.Add(xml);
            return this;
        }
        
        /// <summary>
        /// Adds multiple XML data blocks to the JP2 file.
        /// </summary>
        /// <param name="xmlBlocks">The XML data blocks to add.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public MetadataConfigurationBuilder WithXmlData(params string[] xmlBlocks)
        {
            if (xmlBlocks == null)
                throw new ArgumentNullException(nameof(xmlBlocks));
            
            foreach (var xml in xmlBlocks)
            {
                if (!string.IsNullOrEmpty(xml))
                    _xmlData.Add(xml);
            }
            
            return this;
        }
        
        /// <summary>
        /// Adds UUID data to the JP2 file.
        /// UUIDs can contain vendor-specific or application-specific data.
        /// </summary>
        /// <param name="uuid">The UUID (must be 16 bytes).</param>
        /// <param name="data">The data associated with this UUID.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public MetadataConfigurationBuilder WithUuid(Guid uuid, byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            
            _uuids.Add(new UuidData { Uuid = uuid, Data = data });
            return this;
        }
        
        /// <summary>
        /// Adds UUID data to the JP2 file with string data.
        /// </summary>
        /// <param name="uuid">The UUID (must be 16 bytes).</param>
        /// <param name="data">The string data (will be UTF-8 encoded).</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public MetadataConfigurationBuilder WithUuid(Guid uuid, string data)
        {
            if (string.IsNullOrEmpty(data))
                throw new ArgumentException("UUID data cannot be null or empty", nameof(data));
            
            var bytes = System.Text.Encoding.UTF8.GetBytes(data);
            return WithUuid(uuid, bytes);
        }
        
        /// <summary>
        /// Sets the intellectual property rights information.
        /// This is typically copyright information.
        /// </summary>
        /// <param name="ipr">The intellectual property rights text.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public MetadataConfigurationBuilder WithIntellectualPropertyRights(string ipr)
        {
            if (string.IsNullOrEmpty(ipr))
                throw new ArgumentException("IPR cannot be null or empty", nameof(ipr));
            
            _intellectualPropertyRights = ipr;
            return this;
        }
        
        /// <summary>
        /// Sets copyright information (alias for WithIntellectualPropertyRights).
        /// </summary>
        /// <param name="copyright">The copyright text.</param>
        /// <returns>This configuration instance for method chaining.</returns>
        public MetadataConfigurationBuilder WithCopyright(string copyright)
        {
            return WithIntellectualPropertyRights(copyright);
        }
        
        /// <summary>
        /// Clears all comments.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public MetadataConfigurationBuilder ClearComments()
        {
            _comments.Clear();
            return this;
        }
        
        /// <summary>
        /// Clears all XML data.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public MetadataConfigurationBuilder ClearXml()
        {
            _xmlData.Clear();
            return this;
        }
        
        /// <summary>
        /// Clears all UUID data.
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public MetadataConfigurationBuilder ClearUuids()
        {
            _uuids.Clear();
            return this;
        }
        
        /// <summary>
        /// Clears all metadata (comments, XML, UUIDs, IPR).
        /// </summary>
        /// <returns>This configuration instance for method chaining.</returns>
        public MetadataConfigurationBuilder ClearAll()
        {
            _comments.Clear();
            _xmlData.Clear();
            _uuids.Clear();
            _intellectualPropertyRights = null;
            return this;
        }
        
        /// <summary>
        /// Converts this metadata configuration to a J2KMetadata object.
        /// </summary>
        /// <returns>A J2KMetadata object with all configured metadata.</returns>
        public j2k.fileformat.metadata.J2KMetadata ToJ2KMetadata()
        {
            var metadata = new j2k.fileformat.metadata.J2KMetadata();
            
            // Add comments
            foreach (var comment in _comments)
            {
                metadata.AddComment(comment);
            }
            
            // Add XML data
            foreach (var xml in _xmlData)
            {
                metadata.AddXml(xml);
            }
            
            // Add UUID data
            foreach (var uuid in _uuids)
            {
                metadata.AddUuid(uuid.Uuid, uuid.Data);
            }
            
            // Add IPR
            if (!string.IsNullOrEmpty(_intellectualPropertyRights))
            {
                metadata.AddIntellectualPropertyRights(_intellectualPropertyRights);
            }
            
            return metadata;
        }
        
        /// <summary>
        /// Validates the metadata configuration.
        /// </summary>
        /// <returns>List of validation errors, empty if valid.</returns>
        public List<string> Validate()
        {
            var errors = new List<string>();
            
            // Validate comments
            foreach (var comment in _comments)
            {
                if (string.IsNullOrEmpty(comment))
                    errors.Add("Comment cannot be null or empty");
            }
            
            // Validate XML
            foreach (var xml in _xmlData)
            {
                if (string.IsNullOrEmpty(xml))
                    errors.Add("XML data cannot be null or empty");
            }
            
            // Validate UUIDs
            foreach (var uuid in _uuids)
            {
                if (uuid.Data == null || uuid.Data.Length == 0)
                    errors.Add($"UUID {uuid.Uuid} has no data");
            }
            
            return errors;
        }
        
        /// <summary>
        /// Checks if the configuration is valid.
        /// </summary>
        public bool IsValid => Validate().Count == 0;
        
        /// <summary>
        /// Checks if any metadata is configured.
        /// </summary>
        public bool HasMetadata => 
            _comments.Count > 0 || 
            _xmlData.Count > 0 || 
            _uuids.Count > 0 || 
            !string.IsNullOrEmpty(_intellectualPropertyRights);
        
        /// <summary>
        /// Creates a copy of this metadata configuration.
        /// </summary>
        /// <returns>A new instance with the same settings.</returns>
        public MetadataConfigurationBuilder Clone()
        {
            var clone = new MetadataConfigurationBuilder();
            clone._comments.AddRange(_comments);
            clone._xmlData.AddRange(_xmlData);
            clone._uuids.AddRange(_uuids.Select(u => new UuidData { Uuid = u.Uuid, Data = (byte[])u.Data.Clone() }));
            clone._intellectualPropertyRights = _intellectualPropertyRights;
            return clone;
        }
        
        /// <summary>
        /// Gets a string representation of this configuration.
        /// </summary>
        public override string ToString()
        {
            var parts = new List<string>();
            
            if (_comments.Count > 0)
                parts.Add($"{_comments.Count} comment(s)");
            
            if (_xmlData.Count > 0)
                parts.Add($"{_xmlData.Count} XML block(s)");
            
            if (_uuids.Count > 0)
                parts.Add($"{_uuids.Count} UUID(s)");
            
            if (!string.IsNullOrEmpty(_intellectualPropertyRights))
                parts.Add("IPR");
            
            return parts.Count > 0 ? $"Metadata: {string.Join(", ", parts)}" : "Metadata: none";
        }
    }
    
    /// <summary>
    /// Represents UUID data for JP2 metadata.
    /// </summary>
    public class UuidData
    {
        /// <summary>Gets or sets the UUID identifier.</summary>
        public Guid Uuid { get; set; }
        
        /// <summary>Gets or sets the data associated with this UUID.</summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }
    
    /// <summary>
    /// Preset metadata configurations for common use cases.
    /// </summary>
    public static class MetadataPresets
    {
        /// <summary>
        /// Creates a basic copyright metadata configuration.
        /// </summary>
        /// <param name="copyrightText">The copyright text.</param>
        /// <returns>Metadata configuration with copyright.</returns>
        public static MetadataConfigurationBuilder WithCopyright(string copyrightText)
        {
            return new MetadataConfigurationBuilder()
                .WithCopyright(copyrightText);
        }
        
        /// <summary>
        /// Creates a metadata configuration with title and description.
        /// </summary>
        /// <param name="title">The image title.</param>
        /// <param name="description">The image description.</param>
        /// <returns>Metadata configuration with title and description as comments.</returns>
        public static MetadataConfigurationBuilder WithTitleAndDescription(string title, string description)
        {
            return new MetadataConfigurationBuilder()
                .WithComment($"Title: {title}")
                .WithComment($"Description: {description}");
        }
        
        /// <summary>
        /// Creates a metadata configuration with EXIF-style XML.
        /// </summary>
        /// <param name="exifXml">EXIF XML data.</param>
        /// <returns>Metadata configuration with EXIF XML.</returns>
        public static MetadataConfigurationBuilder WithExif(string exifXml)
        {
            return new MetadataConfigurationBuilder()
                .WithXml(exifXml);
        }
    }
}
