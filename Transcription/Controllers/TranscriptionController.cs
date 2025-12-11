using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Transcription.Interfaces;
using Transcription.Models;

namespace Transcription.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TranscriptionController : ControllerBase
    {
        private readonly ITranscriptionService _service;
        private readonly ILogger<TranscriptionController> _logger;
        private readonly IHttpClientFactory _httpFactory;
        private readonly AssemblyAiOptions _opts;

        public TranscriptionController(
            ITranscriptionService service,
            ILogger<TranscriptionController> logger,
            IHttpClientFactory httpFactory,
            IOptions<AssemblyAiOptions> opts)
        {
            _service = service;
            _logger = logger;
            _httpFactory = httpFactory;
            _opts = opts.Value;
        }

        [HttpPost]
        [RequestSizeLimit(250_000_000)] // adjust as needed
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> Post(IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0)
                return BadRequest(new ProblemDetails { Title = "No file", Detail = "Please upload a non-empty audio file.", Status = 400 });

            try
            {
                var dto = await _service.TranscribeAsync(file, ct);
                return Ok(dto);
            }
            catch (InvalidOperationException ex)
            {
                // used by service for upstream API errors (bad payloads, invalid responses, etc.)
                _logger.LogWarning(ex, "Upstream API returned an error");
                return StatusCode(502, new ProblemDetails { Title = "Upstream API error", Detail = ex.Message, Status = 502 });
            }
            catch (TimeoutException ex)
            {
                _logger.LogError(ex, "Transcription timed out");
                return StatusCode(504, new ProblemDetails { Title = "Timeout", Detail = ex.Message, Status = 504 });
            }
            catch (OperationCanceledException)
            {
                // client cancelled or cancellation requested
                return StatusCode(499, new ProblemDetails { Title = "Client cancelled", Detail = "Request cancelled by client", Status = 499 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected server error");
                return StatusCode(500, new ProblemDetails { Title = "Internal error", Detail = "Unexpected server error", Status = 500 });
            }
        }

    }
}