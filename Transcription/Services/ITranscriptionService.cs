using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Transcription.Models;

namespace Transcription.Interfaces
{
    public interface ITranscriptionService
    {
        Task<SimpleTranscriptDto> TranscribeAsync(IFormFile file, CancellationToken ct = default);
    }
}
