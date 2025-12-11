using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Transcription.Interfaces;
using Transcription.Models;
using Transcription.Utils;

namespace Transcription.Services
{
    public class AssemblyAiService : ITranscriptionService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly AssemblyAiOptions _opts;
        private readonly ILogger<AssemblyAiService> _logger;
        private readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public AssemblyAiService(IHttpClientFactory httpFactory, IOptions<AssemblyAiOptions> opts, ILogger<AssemblyAiService> logger)
        {
            _httpFactory = httpFactory;
            _opts = opts.Value;
            _logger = logger;
        }

        // New public entrypoint: accepts uploaded file
        public async Task<SimpleTranscriptDto> TranscribeAsync(Microsoft.AspNetCore.Http.IFormFile file, CancellationToken ct = default)
        {
            if (file == null || file.Length == 0)
                throw new ArgumentException("Uploaded file is null or empty.", nameof(file));

            // create a secure temp file path (preserve extension if present)
            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrEmpty(ext)) ext = ".bin";
            var tmpPath = Path.Combine(Path.GetTempPath(), $"assemblyai_{Guid.NewGuid():N}{ext}");

            try
            {
                // Save uploaded IFormFile to temp file (disk)
                await using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await file.CopyToAsync(fs, ct).ConfigureAwait(false);
                }

                // 1) Upload file (with retry). UploadFileAsync must return (uploadUrl, attempts, logs)
                var (uploadUrl, uploadAttempts, uploadLogs) = await UploadFileAsync(tmpPath, ct).ConfigureAwait(false);

                // 2) Create transcript job
                var transcriptId = await CreateTranscriptAsync(uploadUrl, ct).ConfigureAwait(false);

                // 3) Poll until completed
                var root = await PollTranscriptAsync(transcriptId, ct).ConfigureAwait(false);

                // 4) Map into DTO
                var dto = MapTranscript(root);

                // attach upload diagnostics
                dto.UploadAttempts = uploadAttempts;
                dto.Logs = uploadLogs ?? new List<string>();

                return dto;
            }
            finally
            {
                // best-effort cleanup of temp file
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        File.Delete(tmpPath);
                        _logger.LogDebug("Deleted temp file {Path}", tmpPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temp file {Path}", tmpPath);
                }
            }
        }


        // Upload helper that reads the file from disk (no streaming public API)
        private async Task<(string uploadUrl, int attempts, List<string> logs)> UploadFileAsync(string filePath, CancellationToken ct)
        {
            var client = _httpFactory.CreateClient("assemblyai");
            var url = $"{_opts.BaseUrl.TrimEnd('/')}/upload";

            async Task<string> SingleAttempt()
            {
                await using var fs = File.OpenRead(filePath);
                using var content = new StreamContent(fs);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                var apiKey = (_opts.ApiKey ?? "").Trim();
                req.Headers.Remove("authorization");
                req.Headers.Add("authorization", apiKey);

                _logger.LogInformation("Sending upload request to AssemblyAI (single attempt).");

                var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                string body = await SafeRead(resp, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    int status = (int)resp.StatusCode;
                    // Non-retryable: 4xx (client errors) -> throw InvalidOperationException
                    if (status >= 400 && status < 500)
                    {
                        var msg = $"Upload returned non-retryable {status}: {Truncate(body, 500)}";
                        _logger.LogError(msg);
                        throw new InvalidOperationException(msg);
                    }

                    // Treat as transient (5xx etc.) to trigger retry
                    var transientMsg = $"Upload returned transient status {status}";
                    _logger.LogWarning(transientMsg);
                    throw new HttpRequestException(transientMsg);
                }

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("upload_url", out var up))
                    return up.GetString() ?? throw new InvalidOperationException("Upload returned empty upload_url.");
                throw new InvalidOperationException("Upload response missing upload_url.");
            }

            // run with retry helper which returns logs and attempts
            var (result, attempts, logs) = await RetryHelper.RetryAsync(
                SingleAttempt,
                _logger,
                maxAttempts: 5,
                baseDelayMs: 500,
                ct: ct
            );

            return (result, attempts, logs);
        }

        private async Task<string> CreateTranscriptAsync(string uploadUrl, CancellationToken ct)
        {
            var client = _httpFactory.CreateClient("assemblyai");
            var url = $"{_opts.BaseUrl.TrimEnd('/')}/transcript";

            var body = new
            {
                audio_url = uploadUrl,
                speaker_labels = true,
                punctuate = true,
                format_text = true
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body))
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var apiKey = (_opts.ApiKey ?? "").Trim();
            req.Headers.Remove("authorization");
            req.Headers.Add("authorization", apiKey);

            var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var text = await SafeRead(resp, ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Create transcript failed {Status}: {Body}", (int)resp.StatusCode, Truncate(text, 500));
                throw new InvalidOperationException($"Create transcript failed: {text}");
            }

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("id", out var idp))
                return idp.GetString() ?? throw new InvalidOperationException("Transcript create returned empty id.");
            throw new InvalidOperationException("Transcript create returned unexpected payload.");
        }

        private async Task<JsonElement> PollTranscriptAsync(string transcriptId, CancellationToken ct)
        {
            var client = _httpFactory.CreateClient("assemblyai");
            var url = $"{_opts.BaseUrl.TrimEnd('/')}/transcript/{transcriptId}";
            var overallTimeout = TimeSpan.FromSeconds(_opts.OverallTimeoutSeconds);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_opts.PollIntervalMs, ct);

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Remove("authorization");
                req.Headers.Add("authorization", (_opts.ApiKey ?? "").Trim());

                var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                var body = await SafeRead(resp, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogError("Poll failed {Status}: {Body}", (int)resp.StatusCode, Truncate(body, 500));
                    throw new InvalidOperationException($"Polling failed: {body}");
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement.Clone();

                if (root.TryGetProperty("status", out var s) && s.GetString() is string status)
                {
                    if (status == "completed") return root;
                    if (status == "error")
                    {
                        var msg = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                        throw new InvalidOperationException($"Transcription error: {msg}");
                    }
                }

                if (sw.Elapsed > overallTimeout)
                    throw new TimeoutException("Timed out waiting for transcription.");
            }

            throw new OperationCanceledException("Polling cancelled.");
        }

        private SimpleTranscriptDto MapTranscript(JsonElement root)
        {
            var dto = new SimpleTranscriptDto();

            if (root.TryGetProperty("text", out var textProp))
                dto.FullText = textProp.GetString() ?? "";

            if (root.TryGetProperty("utterances", out var uttArr) && uttArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in uttArr.EnumerateArray())
                {
                    var s = new SentenceDTO
                    {
                        Speaker = u.TryGetProperty("speaker", out var sp) ? sp.GetString() ?? "" : "",
                        Text = u.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                        Confidence = u.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? $"{c.GetSingle() * 100:0.0}%" : null
                    };

                    if (u.TryGetProperty("start", out var sStart) && sStart.ValueKind == JsonValueKind.Number)
                        s.Start = FormatMs(sStart.GetInt32());
                    if (u.TryGetProperty("end", out var sEnd) && sEnd.ValueKind == JsonValueKind.Number)
                        s.End = FormatMs(sEnd.GetInt32());

                    dto.Sentences.Add(s);

                    if (u.TryGetProperty("words", out var wordsArr) && wordsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var w in wordsArr.EnumerateArray())
                        {
                            var wd = new WordDTO
                            {
                                Text = w.TryGetProperty("text", out var wt) ? wt.GetString() ?? "" : "",
                                Speaker = w.TryGetProperty("speaker", out var wsp) ? wsp.GetString() ?? "" : s.Speaker,
                                Start = w.TryGetProperty("start", out var ws) && ws.ValueKind == JsonValueKind.Number ? ws.GetInt32() : 0,
                                End = w.TryGetProperty("end", out var we) && we.ValueKind == JsonValueKind.Number ? we.GetInt32() : 0,
                                Confidence = w.TryGetProperty("confidence", out var wc) && wc.ValueKind == JsonValueKind.Number ? wc.GetSingle() : 0f
                            };
                            dto.Words.Add(wd);
                        }
                    }
                }
            }
            else if (root.TryGetProperty("words", out var topWords) && topWords.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in topWords.EnumerateArray())
                {
                    var wd = new WordDTO
                    {
                        Text = w.TryGetProperty("text", out var wt) ? wt.GetString() ?? "" : "",
                        Speaker = w.TryGetProperty("speaker", out var wsp) ? wsp.GetString() ?? "" : "",
                        Start = w.TryGetProperty("start", out var ws) && ws.ValueKind == JsonValueKind.Number ? ws.GetInt32() : 0,
                        End = w.TryGetProperty("end", out var we) && we.ValueKind == JsonValueKind.Number ? we.GetInt32() : 0,
                        Confidence = w.TryGetProperty("confidence", out var wc) && wc.ValueKind == JsonValueKind.Number ? wc.GetSingle() : 0f
                    };
                    dto.Words.Add(wd);
                }
            }

            return dto;
        }

        private static string FormatMs(int ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return $"{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
        }

        private static async Task<string> SafeRead(HttpResponseMessage resp, CancellationToken ct)
        {
            try { return await resp.Content.ReadAsStringAsync(ct); }
            catch { return "<unable to read body>"; }
        }

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
