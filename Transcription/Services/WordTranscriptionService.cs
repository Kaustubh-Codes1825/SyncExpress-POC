
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Transcription.Models;

namespace Transcription.Services
{
    public class WordTranscriptionService
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;

        // v2 endpoints (same as sentence service)
        private const string UploadEndpoint = "https://api.assemblyai.com/v2/upload";
        private const string TranscriptBaseEndpoint = "https://api.assemblyai.com/v2/transcript";

        public WordTranscriptionService(HttpClient httpClient, IOptions<AssemblyAiOptions> options)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiKey = options?.Value?.ApiKey ?? throw new ArgumentNullException(nameof(options.Value.ApiKey));

            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("authorization", _apiKey);
        }

        // --------------------------------------------------------------------------------
        // PUBLIC API — FILES ONLY
        // --------------------------------------------------------------------------------

        /// <summary>
        /// Words-level transcription from an uploaded file stream (no CancellationToken).
        /// </summary>
        public async Task<List<WordDto>> TranscribeWordsAsync(Stream fileStream)
        {
            return await TranscribeWordsAsync(fileStream, CancellationToken.None);
        }

        /// <summary>
        /// Words-level transcription from an uploaded file stream (CancellationToken-aware).
        /// Handles files only. If the incoming stream isn't seekable, buffers once to MemoryStream so retries are possible.
        /// </summary>
        public async Task<List<WordDto>> TranscribeWordsAsync(Stream fileStream, CancellationToken ct)
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

            return MapWords(rawJson);
        }

        // --------------------------------------------------------------------------------
        // INTERNAL: Upload with retry (full re-upload), transcript creation, polling
        // --------------------------------------------------------------------------------

        /// <summary>
        /// Retry wrapper for full file re-upload on transient failures.
        /// Uses exponential backoff + full jitter between attempts.
        /// </summary>
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

            var msg = $"Upload (words) failed after {attempts} attempt(s).";
            if (lastException != null)
                throw new Exception(msg, lastException);

            throw new Exception(msg);
        }

        /// <summary>
        /// Performs a single upload attempt; throws InvalidOperationException for non-retryable (4xx) and HttpRequestException for transient (5xx/408).
        /// </summary>
        private async Task<string> UploadSingleAttemptAsync(Stream fileStream, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint)
            {
                Content = new StreamContent(fileStream)
            };

            // Optional: set Content-Type if known
            // request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var code = (int)response.StatusCode;

                // Non-retryable client errors (typical 4xx other than 408)
                if (code >= 400 && code < 500 && code != (int)HttpStatusCode.RequestTimeout)
                {
                    throw new InvalidOperationException($"Non-retryable upload error {code}: {response.ReasonPhrase}. Body: {body}");
                }

                // Transient: 5xx or 408 → let caller retry
                throw new HttpRequestException($"Transient upload error {code}: {response.ReasonPhrase}. Body: {body}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.TryGetProperty("upload_url", out var urlProp)
                ? (urlProp.GetString() ?? string.Empty)
                : string.Empty;
        }

        /// <summary>
        /// Creates a transcript job using the upload_url, returns transcript id.
        /// </summary>
        private async Task<string> CreateTranscriptAsync(string audioUrl, CancellationToken ct)
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
                var errorText = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"AssemblyAI error creating transcript (words): {errorText}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetString();
            return id!;
        }

        /// <summary>
        /// Polls transcript status until "completed" or throws on "error".
        /// </summary>
        private async Task<JsonElement> WaitForTranscriptAsync(string transcriptId, CancellationToken ct)
        {
            while (true)
            {
                await Task.Delay(3000, ct);

                var response = await _http.GetAsync($"{TranscriptBaseEndpoint}/{transcriptId}", ct);
                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(ct);
                    throw new InvalidOperationException($"AssemblyAI error while polling transcript (words): {errorText}");
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var status = root.GetProperty("status").GetString();

                if (status == "completed")
                {
                    return root.Clone();
                }

                if (status == "error")
                {
                    var errorMsg = root.GetProperty("error").GetString();
                    throw new InvalidOperationException($"AssemblyAI transcription error (words): {errorMsg}");
                }

                // queued/processing → continue
            }
        }

        // --------------------------------------------------------------------------------
        // Mapping
        // --------------------------------------------------------------------------------

        private List<WordDto> MapWords(JsonElement root)
        {
            var words = new List<WordDto>();

            if (!root.TryGetProperty("words", out var wordsProp) ||
                wordsProp.ValueKind != JsonValueKind.Array)
                return words;

            foreach (var w in wordsProp.EnumerateArray())
            {
                var word = new WordDto
                {
                    Text = w.GetProperty("text").GetString() ?? string.Empty,
                    Start = w.GetProperty("start").GetInt32(),
                    End = w.GetProperty("end").GetInt32(),
                    Confidence = w.GetProperty("confidence").GetSingle()
                };

                words.Add(word);
            }

            return words;
        }

        // --------------------------------------------------------------------------------
        // Backoff helper
        // --------------------------------------------------------------------------------

        /// <summary>
        /// Exponential backoff with full jitter: delay = random(0, base * 2^attempt).
        /// </summary>
        private static async Task BackoffDelayAsync(int attempt, int baseDelayMs, Random rng, CancellationToken ct)
        {
            var maxDelay = baseDelayMs * (int)Math.Pow(2, attempt);
            var delay = rng.Next(0, Math.Max(baseDelayMs, maxDelay));
            await Task.Delay(delay, ct);
        }
    }
}
