// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.Drawing;

namespace CoreJ2K.j2k.roi
{
    /// <summary>
    /// Fluent API for configuring Region of Interest (ROI) encoding settings.
    /// Provides a modern, developer-friendly interface for specifying ROI parameters.
    /// </summary>
    public class ROIConfiguration
    {
        private readonly List<ROIDescriptor> _rois = new List<ROIDescriptor>();
        
        /// <summary>
        /// Gets the list of configured ROI descriptors.
        /// </summary>
        public IReadOnlyList<ROIDescriptor> Regions => _rois.AsReadOnly();
        
        /// <summary>
        /// Gets or sets whether to use block-aligned ROI encoding.
        /// When enabled, entire code-blocks are treated as ROI if any coefficient belongs to the ROI.
        /// Default is false.
        /// </summary>
        public bool BlockAligned { get; set; }
        
        /// <summary>
        /// Gets or sets the starting resolution level for ROI.
        /// Forces the lowest N resolution levels to belong entirely to the ROI.
        /// -1 deactivates this feature. Default is -1.
        /// </summary>
        public int StartLevel { get; set; } = -1;
        
        /// <summary>
        /// Gets or sets whether to force generic mask generation even for rectangular ROIs.
        /// When false (default), rectangular ROIs use optimized fast mask generation.
        /// </summary>
        public bool ForceGenericMaskGeneration { get; set; }
        
        /// <summary>
        /// Gets or sets the ROI scaling mode.
        /// </summary>
        public ROIScalingMode ScalingMode { get; set; } = ROIScalingMode.MaxShift;
        
        /// <summary>
        /// Adds a rectangular ROI to the configuration.
        /// </summary>
        /// <param name="component">Component index (0-based) to apply ROI to, or -1 for all components</param>
        /// <param name="x">X coordinate of upper-left corner</param>
        /// <param name="y">Y coordinate of upper-left corner</param>
        /// <param name="width">Width of the rectangle</param>
        /// <param name="height">Height of the rectangle</param>
        /// <returns>This configuration instance for method chaining</returns>
        public ROIConfiguration AddRectangle(int component, int x, int y, int width, int height)
        {
            _rois.Add(new RectangularROI(component, x, y, width, height));
            return this;
        }
        
        /// <summary>
        /// Adds a rectangular ROI using a Rectangle structure.
        /// </summary>
        /// <param name="component">Component index (0-based) to apply ROI to, or -1 for all components</param>
        /// <param name="rect">Rectangle defining the ROI bounds</param>
        /// <returns>This configuration instance for method chaining</returns>
        public ROIConfiguration AddRectangle(int component, Rectangle rect)
        {
            return AddRectangle(component, rect.X, rect.Y, rect.Width, rect.Height);
        }
        
        /// <summary>
        /// Adds a circular ROI to the configuration.
        /// </summary>
        /// <param name="component">Component index (0-based) to apply ROI to, or -1 for all components</param>
        /// <param name="centerX">X coordinate of circle center</param>
        /// <param name="centerY">Y coordinate of circle center</param>
        /// <param name="radius">Radius of the circle</param>
        /// <returns>This configuration instance for method chaining</returns>
        public ROIConfiguration AddCircle(int component, int centerX, int centerY, int radius)
        {
            _rois.Add(new CircularROI(component, centerX, centerY, radius));
            return this;
        }
        
        /// <summary>
        /// Adds a circular ROI using a Point structure for the center.
        /// </summary>
        /// <param name="component">Component index (0-based) to apply ROI to, or -1 for all components</param>
        /// <param name="center">Center point of the circle</param>
        /// <param name="radius">Radius of the circle</param>
        /// <returns>This configuration instance for method chaining</returns>
        public ROIConfiguration AddCircle(int component, Point center, int radius)
        {
            return AddCircle(component, center.X, center.Y, radius);
        }
        
        /// <summary>
        /// Adds an arbitrary-shaped ROI from a PGM mask file.
        /// </summary>
        /// <param name="component">Component index (0-based) to apply ROI to, or -1 for all components</param>
        /// <param name="maskFilePath">Path to PGM file containing the mask (non-zero = ROI)</param>
        /// <returns>This configuration instance for method chaining</returns>
        public ROIConfiguration AddArbitraryShape(int component, string maskFilePath)
        {
            if (string.IsNullOrWhiteSpace(maskFilePath))
                throw new ArgumentException("Mask file path cannot be null or empty", nameof(maskFilePath));
            
            _rois.Add(new ArbitraryROI(component, maskFilePath));
            return this;
        }
        
        /// <summary>
        /// Sets the block alignment mode.
        /// </summary>
        /// <param name="enabled">True to enable block-aligned ROI encoding</param>
        /// <returns>This configuration instance for method chaining</returns>
        public ROIConfiguration SetBlockAlignment(bool enabled)
        {
            BlockAligned = enabled;
            return this;
        }
        
        /// <summary>
        /// Sets the starting resolution level for ROI.
        /// </summary>
        /// <param name="level">Resolution level (0 = lowest resolution, -1 to disable)</param>
        /// <returns>This configuration instance for method chaining</returns>
        public ROIConfiguration SetStartLevel(int level)
        {
            StartLevel = level;
            return this;
        }
        
        /// <summary>
        /// Sets the ROI scaling mode.
        /// </summary>
        /// <param name="mode">The scaling mode to use</param>
        /// <returns>This configuration instance for method chaining</returns>
        public ROIConfiguration SetScalingMode(ROIScalingMode mode)
        {
            ScalingMode = mode;
            return this;
        }
        
        /// <summary>
        /// Forces the use of generic mask generation even for rectangular ROIs.
        /// </summary>
        /// <param name="force">True to force generic mask generation</param>
        /// <returns>This configuration instance for method chaining</returns>
        public ROIConfiguration ForceGenericMask(bool force = true)
        {
            ForceGenericMaskGeneration = force;
            return this;
        }
        
        /// <summary>
        /// Validates the configuration and returns any validation errors.
        /// </summary>
        /// <returns>List of validation error messages, empty if valid</returns>
        public List<string> Validate()
        {
            var errors = new List<string>();
            
            if (_rois.Count == 0)
            {
                errors.Add("At least one ROI must be defined");
            }
            
            if (StartLevel < -1)
            {
                errors.Add("StartLevel must be -1 or greater");
            }
            
            foreach (var roi in _rois)
            {
                var roiErrors = roi.Validate();
                errors.AddRange(roiErrors);
            }
            
            return errors;
        }
        
        /// <summary>
        /// Checks if the configuration is valid.
        /// </summary>
        public bool IsValid => Validate().Count == 0;
    }
    
    /// <summary>
    /// ROI scaling modes.
    /// </summary>
    public enum ROIScalingMode
    {
        /// <summary>
        /// MaxShift method - scales background coefficients down by shifting.
        /// This is the standard JPEG 2000 Part 1 method.
        /// </summary>
        MaxShift
    }
    
    /// <summary>
    /// Base class for ROI descriptors.
    /// </summary>
    public abstract class ROIDescriptor
    {
        /// <summary>
        /// Gets the component index this ROI applies to (-1 for all components).
        /// </summary>
        public int Component { get; protected set; }
        
        /// <summary>
        /// Gets the type of ROI shape.
        /// </summary>
        public abstract ROIShapeType ShapeType { get; }
        
        /// <summary>
        /// Validates the ROI descriptor.
        /// </summary>
        /// <returns>List of validation error messages</returns>
        public abstract List<string> Validate();
    }
    
    /// <summary>
    /// ROI shape types.
    /// </summary>
    public enum ROIShapeType
    {
        /// <summary>Rectangular ROI</summary>
        Rectangle,
        /// <summary>Circular ROI</summary>
        Circle,
        /// <summary>Arbitrary shape from mask file</summary>
        Arbitrary
    }
    
    /// <summary>
    /// Describes a rectangular ROI.
    /// </summary>
    public class RectangularROI : ROIDescriptor
    {
        /// <summary>
        /// Gets the X coordinate of the upper-left corner.
        /// </summary>
        public int X { get; }
        
        /// <summary>
        /// Gets the Y coordinate of the upper-left corner.
        /// </summary>
        public int Y { get; }
        
        /// <summary>
        /// Gets the width of the rectangle.
        /// </summary>
        public int Width { get; }
        
        /// <summary>
        /// Gets the height of the rectangle.
        /// </summary>
        public int Height { get; }
        
        /// <inheritdoc/>
        public override ROIShapeType ShapeType => ROIShapeType.Rectangle;
        
        /// <summary>
        /// Creates a new rectangular ROI descriptor.
        /// </summary>
        public RectangularROI(int component, int x, int y, int width, int height)
        {
            Component = component;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
        
        /// <inheritdoc/>
        public override List<string> Validate()
        {
            var errors = new List<string>();
            
            if (Width <= 0)
                errors.Add($"Rectangular ROI width must be positive (got {Width})");
            if (Height <= 0)
                errors.Add($"Rectangular ROI height must be positive (got {Height})");
            if (X < 0)
                errors.Add($"Rectangular ROI X coordinate must be non-negative (got {X})");
            if (Y < 0)
                errors.Add($"Rectangular ROI Y coordinate must be non-negative (got {Y})");
                
            return errors;
        }
        
        /// <inheritdoc/>
        public override string ToString()
        {
            return $"Rectangle ROI: Component={Component}, X={X}, Y={Y}, Width={Width}, Height={Height}";
        }
    }
    
    /// <summary>
    /// Describes a circular ROI.
    /// </summary>
    public class CircularROI : ROIDescriptor
    {
        /// <summary>
        /// Gets the X coordinate of the circle center.
        /// </summary>
        public int CenterX { get; }
        
        /// <summary>
        /// Gets the Y coordinate of the circle center.
        /// </summary>
        public int CenterY { get; }
        
        /// <summary>
        /// Gets the radius of the circle.
        /// </summary>
        public int Radius { get; }
        
        /// <inheritdoc/>
        public override ROIShapeType ShapeType => ROIShapeType.Circle;
        
        /// <summary>
        /// Creates a new circular ROI descriptor.
        /// </summary>
        public CircularROI(int component, int centerX, int centerY, int radius)
        {
            Component = component;
            CenterX = centerX;
            CenterY = centerY;
            Radius = radius;
        }
        
        /// <inheritdoc/>
        public override List<string> Validate()
        {
            var errors = new List<string>();
            
            if (Radius <= 0)
                errors.Add($"Circular ROI radius must be positive (got {Radius})");
            if (CenterX < 0)
                errors.Add($"Circular ROI center X must be non-negative (got {CenterX})");
            if (CenterY < 0)
                errors.Add($"Circular ROI center Y must be non-negative (got {CenterY})");
                
            return errors;
        }
        
        /// <inheritdoc/>
        public override string ToString()
        {
            return $"Circular ROI: Component={Component}, Center=({CenterX},{CenterY}), Radius={Radius}";
        }
    }
    
    /// <summary>
    /// Describes an arbitrary-shaped ROI from a mask file.
    /// </summary>
    public class ArbitraryROI : ROIDescriptor
    {
        /// <summary>
        /// Gets the path to the PGM mask file.
        /// </summary>
        public string MaskFilePath { get; }
        
        /// <inheritdoc/>
        public override ROIShapeType ShapeType => ROIShapeType.Arbitrary;
        
        /// <summary>
        /// Creates a new arbitrary ROI descriptor.
        /// </summary>
        public ArbitraryROI(int component, string maskFilePath)
        {
            Component = component;
            MaskFilePath = maskFilePath;
        }
        
        /// <inheritdoc/>
        public override List<string> Validate()
        {
            var errors = new List<string>();
            
            if (string.IsNullOrWhiteSpace(MaskFilePath))
                errors.Add("Arbitrary ROI mask file path cannot be empty");
            else if (!System.IO.File.Exists(MaskFilePath))
                errors.Add($"Arbitrary ROI mask file not found: {MaskFilePath}");
                
            return errors;
        }
        
        /// <inheritdoc/>
        public override string ToString()
        {
            return $"Arbitrary ROI: Component={Component}, MaskFile={MaskFilePath}";
        }
    }
}
