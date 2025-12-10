
using Microsoft.AspNetCore.Mvc;
using Transcription.Models;
using Transcription.Services;
using System.Threading;
using System.Threading.Tasks;

namespace Transcription.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TranscriptionController : ControllerBase
    {
        private readonly AssemblyAIService _assemblyAiService;

        public TranscriptionController(AssemblyAIService assemblyAiService)
        {
            _assemblyAiService = assemblyAiService;
        }

        [HttpPost]
        [RequestSizeLimit(200_000_000)] // ~200 MB; adjust as needed
        [Consumes("multipart/form-data")]
        public async Task<ActionResult<SimpleTranscriptDto>> Transcribe( IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0)
                return BadRequest("Please provide a non-empty audio file.");

            try
            {
                await using var stream = file.OpenReadStream();
                var dto = await _assemblyAiService.TranscribeFileAsync(stream, ct);
                return Ok(dto);
            }
            catch (InvalidOperationException ex)
            {
                // Non-retryable (e.g., bad input/unsupported content, or JSON parsing issues)
                return BadRequest($"Upload or transcription failed due to client error: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                // Transient upload errors after retries exhausted
                return StatusCode(StatusCodes.Status502BadGateway, $"Upload failed; retries exhausted: {ex.Message}");
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // Timeout after retries
                return StatusCode(StatusCodes.Status504GatewayTimeout, $"Upload timed out; retries exhausted: {ex.Message}");
            }
            catch (System.Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Unexpected error: {ex.Message}");
            }
        }
    }
}
