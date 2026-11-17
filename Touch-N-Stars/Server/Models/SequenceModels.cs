using System;
using System.Collections.Generic;

namespace TouchNStars.Server.Models
{
    /// <summary>
    /// Response for listing available sequences
    /// </summary>
    public class SequenceListResponse
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<SequenceFileInfo> Sequences { get; set; } = new List<SequenceFileInfo>();
    }

    /// <summary>
    /// Information about a sequence file
    /// </summary>
    public class SequenceFileInfo
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public string Name { get; set; }
        public DateTime LastModified { get; set; }
        public long FileSize { get; set; }
    }
}
