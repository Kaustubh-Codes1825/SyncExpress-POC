using System.Collections.Generic;
using Transcription.Models;

namespace Transcription.Models
{
    public class SimpleTranscriptDto
    {
        public string FullText { get; set; } = "";
        public List<SentenceDTO> Sentences { get; set; } = new List<SentenceDTO>();
        public List<WordDTO> Words { get; set; } = new List<WordDTO>();
        //public string RawJson { get; set; } = "";

        // Number of upload attempts used (1..N)
        public int UploadAttempts { get; set; } = 0;

        // NEW: runtime logs to return to caller (retry attempts, reasons, success/failure)
        public List<string> Logs { get; set; } = new List<string>();
    }
}
