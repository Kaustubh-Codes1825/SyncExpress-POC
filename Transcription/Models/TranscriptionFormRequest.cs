using Microsoft.AspNetCore.Http;

namespace Transcription.Models
{
    public class TranscriptionFormRequest
    {
        //File Upload
        public IFormFile? File { get; set; }

        //URL
        public string? AudioUrl { get; set; }
    }
}
