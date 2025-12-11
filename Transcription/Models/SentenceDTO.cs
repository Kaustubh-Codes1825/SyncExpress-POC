namespace Transcription.Models
{

    public class SentenceDTO
    {
        public string Speaker { get; set; } = "";
        public string Text { get; set; } = "";
        public string Start { get; set; } = "";
        public string End { get; set; } = "";
        public string? Confidence { get; set; }
    }
}

