using System.Collections.Generic;
using Transcription.Models;

namespace Transcription.Models
{
    public class SimpleTranscriptDto
    {
        public string FullText { get; set; } = "";
        public List<SentenceDTO> Sentences { get; set; } = new List<SentenceDTO>();
        public List<WordDTO> Words { get; set; } = new List<WordDTO>();
        public string RawJson { get; set; } = "";
    }
}
