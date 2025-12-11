namespace Transcription.Models
{

    public class WordDTO
    {
        public string Text { get; set; } = "";
        public string Speaker { get; set; } = "";
        public int Start { get; set; }
        public int End { get; set; }
        public float Confidence { get; set; }
    }
}
