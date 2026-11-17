using System;

namespace TouchNStars.Server.Models
{
    /// <summary>
    /// DTO containing metadata about a sequence item
    /// </summary>
    public class SequenceItemMetadata
    {
        /// <summary>
        /// Display name of the sequence item (localization key or localized name)
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Description of what the sequence item does
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Category the sequence item belongs to
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Full type name of the sequence item class
        /// </summary>
        public string FullTypeName { get; set; }
    }

    /// <summary>
    /// API Response wrapper for sequence items list
    /// </summary>
    public class SequenceItemsResponse
    {
        /// <summary>
        /// Whether the request was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// List of available sequence items
        /// </summary>
        public SequenceItemMetadata[] Items { get; set; } = Array.Empty<SequenceItemMetadata>();

        /// <summary>
        /// Error message if request was unsuccessful
        /// </summary>
        public string Error { get; set; }

        /// <summary>
        /// Total number of sequence items available
        /// </summary>
        public int Total { get; set; }
    }
}