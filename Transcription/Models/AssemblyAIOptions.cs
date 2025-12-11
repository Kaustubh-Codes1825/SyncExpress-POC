namespace Transcription.Models
{
    public class AssemblyAiOptions
    {
        public string ApiKey { get; set; } = "";
        public string BaseUrl { get; set; } = "https://api.assemblyai.com/v2";
        public int PollIntervalMs { get; set; } = 2500;

        public int OverallTimeoutSeconds { get; set; } = 300;

    }
}
