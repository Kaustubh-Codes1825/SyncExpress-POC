
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Transcription.Models;

namespace Transcription.Services
{
    public class AssemblyAIService
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly ILogger<AssemblyAIService>? _logger;

        // AssemblyAI v2 endpoints
        private const string UploadEndpoint = "https://api.assemblyai.com/v2/upload";
        //private const string UploadEndpoint = "https://INVALID_HOST/upload";

        private const string TranscriptBaseEndpoint = "https://api.assemblyai.com/v2/transcript";

        public AssemblyAIService(
            HttpClient httpClient,
            IOptions<AssemblyAiOptions> options,
            ILogger<AssemblyAIService>? logger = null)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiKey = options?.Value?.ApiKey ?? throw new ArgumentNullException(nameof(options.Value.ApiKey));
            _logger = logger;

            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("authorization", _apiKey);

            // For large uploads, give enough time.
            _http.Timeout = TimeSpan.FromMinutes(5);
        }

        public async Task<SimpleTranscriptDto> TranscribeFileAsync(Stream fileStream, CancellationToken ct = default)
        {
            if (fileStream == null || !fileStream.CanRead)
                throw new ArgumentException("Invalid audio stream.", nameof(fileStream));

            // Ensure we have a seekable stream for retries
            Stream workingStream = fileStream;
            if (!fileStream.CanSeek)
            {
                var buffer = new MemoryStream();
                await fileStream.CopyToAsync(buffer, ct);
                buffer.Position = 0;
                workingStream = buffer;
            }

            var uploadUrl = await UploadToAssemblyAiWithRetryAsync(workingStream, maxRetries: 3, baseDelayMs: 500, ct);
            var transcriptId = await CreateTranscriptAsync(uploadUrl, ct);
            var rawJson = await WaitForTranscriptAsync(transcriptId, ct);

            return MapToDto(rawJson);
        }

        public async Task<string> CreateTranscriptAsync(string audioUrl, CancellationToken ct = default)
        {
            var body = new
            {
                audio_url = audioUrl,
                punctuate = true,
                format_text = true,
                speaker_labels = true
            };

            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(TranscriptBaseEndpoint, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await SafeReadBodyAsync(response, ct);
                _logger?.LogError("CreateTranscript failed: {Status} {Reason}. Body: {Body}",
                    (int)response.StatusCode, response.ReasonPhrase, Truncate(errorText, 500));
                throw new InvalidOperationException($"AssemblyAI error creating transcript: {errorText}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetString();
            return id!;
        }

        public async Task<JsonElement> WaitForTranscriptAsync(string transcriptId, CancellationToken ct = default)
        {
            while (true)
            {
                await Task.Delay(3000, ct); // poll every 3s

                var response = await _http.GetAsync($"{TranscriptBaseEndpoint}/{transcriptId}", ct);
                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await SafeReadBodyAsync(response, ct);
                    _logger?.LogError("Polling failed: {Status} {Reason}. Body: {Body}",
                        (int)response.StatusCode, response.ReasonPhrase, Truncate(errorText, 500));
                    throw new InvalidOperationException($"AssemblyAI error while polling transcript: {errorText}");
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var status = root.GetProperty("status").GetString();

                if (status == "completed")
                {
                    return root.Clone(); // survive after doc dispose
                }

                if (status == "error")
                {
                    var errorMsg = root.GetProperty("error").GetString();
                    _logger?.LogError("Transcription error: {Error}", errorMsg);
                    throw new InvalidOperationException($"AssemblyAI transcription error: {errorMsg}");
                }

                // queued/processing → continue
            }
        }


        public SimpleTranscriptDto MapToDto(JsonElement root)
        {
            var dto = new SimpleTranscriptDto();

            // Full text
            if (root.TryGetProperty("text", out var textProp))
            {
                dto.FullText = textProp.GetString() ?? string.Empty;
            }

            // Utterances -> treat as sentences with speaker
            if (root.TryGetProperty("utterances", out var uttProp) &&
                uttProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in uttProp.EnumerateArray())
                {
                    var sentence = new SentenceDto
                    {
                        Speaker = u.TryGetProperty("speaker", out var spProp) ? spProp.GetString() ?? string.Empty : string.Empty,
                        Text = u.TryGetProperty("text", out var tProp) ? tProp.GetString() ?? string.Empty : string.Empty
                    };

                    // Start
                    if (u.TryGetProperty("start", out var sProp) && sProp.ValueKind == JsonValueKind.Number)
                    {
                        int startMs = sProp.GetInt32();
                        sentence.StartTime = FormatTime(startMs);
                    }

                    // End
                    if (u.TryGetProperty("end", out var eProp) && eProp.ValueKind == JsonValueKind.Number)
                    {
                        int endMs = eProp.GetInt32();
                        sentence.EndTime = FormatTime(endMs);
                    }

                    // Confidence
                    if (u.TryGetProperty("confidence", out var cProp) && cProp.ValueKind == JsonValueKind.Number)
                    {
                        float conf = cProp.GetSingle();
                        sentence.ConfidencePercent = FormatConfidence(conf);
                    }

                    dto.Sentences.Add(sentence);
                }
            }

            return dto;
        }

        private async Task<string> UploadToAssemblyAiWithRetryAsync(
            Stream fileStream,
            int maxRetries = 3,
            int baseDelayMs = 500,
            CancellationToken ct = default)
        {
            if (fileStream == null || !fileStream.CanRead)
                throw new ArgumentException("Invalid audio stream.", nameof(fileStream));

            int attempts = 0;
            var rng = new Random();
            Exception? lastException = null;

            while (attempts < maxRetries)
            {
                attempts++;

                try
                {
                    // Rewind if possible; previous attempt may have consumed the stream.
                    if (fileStream.CanSeek)
                        fileStream.Seek(0, SeekOrigin.Begin);

                    var uploadUrl = await UploadSingleAttemptAsync(fileStream, ct);
                    if (!string.IsNullOrWhiteSpace(uploadUrl))
                        return uploadUrl;

                    // Treat empty upload_url as non-retryable logic error
                    lastException = new InvalidOperationException("Upload returned empty upload_url.");
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    // Consider InvalidOperationException as non-retryable client error (e.g., 4xx propagated)
                    lastException = ex;
                    break;
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    // Timeout – retry
                    lastException = ex;
                    await BackoffDelayAsync(attempts, baseDelayMs, rng, ct);
                }
                catch (HttpRequestException ex)
                {
                    // Transient network/server – retry
                    lastException = ex;
                    await BackoffDelayAsync(attempts, baseDelayMs, rng, ct);
                }
                catch (Exception ex)
                {
                    // Other transient failures – retry
                    lastException = ex;
                    await BackoffDelayAsync(attempts, baseDelayMs, rng, ct);
                }
            }

            var msg = $"Upload failed after {attempts} attempt(s).";
            if (lastException != null)
                throw new Exception(msg, lastException);

            throw new Exception(msg);
        }

        private async Task<string> UploadSingleAttemptAsync(Stream fileStream, CancellationToken ct)
        {
            // Set a plausible MIME; some servers are picky.
            var content = new StreamContent(fileStream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg"); // or audio/wav

            using var request = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint)
            {
                Content = content
            };

            // Disable Expect: 100-Continue to avoid extra round-trip in some environments.
            request.Headers.ExpectContinue = false;

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Upload attempt failed to send request.");
                throw; // Let retry wrapper handle
            }

            var status = (int)response.StatusCode;
            string body = await SafeReadBodyAsync(response, ct);

            _logger?.LogInformation("AssemblyAI upload response: {Status} {Reason}; Body: {Body}",
                status, response.ReasonPhrase, Truncate(body, 500));

            if (!response.IsSuccessStatusCode)
            {
                if (status == 401 || status == 403)
                {
                    _logger?.LogError("Authorization failed. Check API key and 'authorization' header.");
                }

                // Non-retryable client errors: 4xx (except 408)
                if (status >= 400 && status < 500 && status != (int)HttpStatusCode.RequestTimeout)
                {
                    throw new InvalidOperationException($"Non-retryable upload error {status}: {response.ReasonPhrase}. Body: {body}");
                }

                // Transient: 5xx or 408 → let caller retry
                throw new HttpRequestException($"Transient upload error {status}: {response.ReasonPhrase}. Body: {body}");
            }

            // Parse upload_url
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("upload_url", out var urlProp))
                {
                    var uploadUrl = urlProp.GetString();
                    if (string.IsNullOrWhiteSpace(uploadUrl))
                        throw new InvalidOperationException("Upload succeeded but upload_url is empty.");
                    return uploadUrl!;
                }

                throw new InvalidOperationException($"Upload succeeded but 'upload_url' not found in response: {Truncate(body, 200)}");
            }
            catch (JsonException jex)
            {
                _logger?.LogError(jex, "Failed to parse upload response JSON. Body: {Body}", Truncate(body, 500));
                throw new InvalidOperationException($"Upload succeeded but response JSON is invalid: {Truncate(body, 200)}", jex);
            }
        }

        private static async Task BackoffDelayAsync(int attempt, int baseDelayMs, Random rng, CancellationToken ct)
        {
            var maxDelay = baseDelayMs * (int)Math.Pow(2, attempt);
            var delay = rng.Next(0, Math.Max(baseDelayMs, maxDelay));
            await Task.Delay(delay, ct);
        }

        private static string FormatTime(int ms)
        {
            var totalSeconds = ms / 1000;
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            return $"{minutes:D2}:{seconds:D2}";
        }

        private static string? FormatConfidence(float? value)
        {
            if (!value.HasValue) return null;
            return $"{(value.Value * 100):0.0}%";
        }

        private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch
            {
                return "<unable to read response body>";
            }
        }

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
