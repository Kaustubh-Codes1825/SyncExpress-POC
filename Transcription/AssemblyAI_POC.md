# AssemblyAI Transcription POC Documentation

### Author(s):
- **Kaustubh Paul**


### Submission Date:
- **DD/MM/YYYY**


## 1. Overview
This Proof of Concept (POC) demonstrates the implementation and workflow of integrating **AssemblyAI** into the backend (**C#/.NET**) to transcribe an audio file into structured text, including timestamps and confidence scores.  
The structured transcript generated here will later be used in the **Autosync** feature for synchronizing text with video timelines.

This documentation includes:

- Objectives  
- End-to-end workflow  
- API endpoints (Assembly AI + Swagger API)
- A full Workflow diagram  
- Backend implementation details  
- JSON mapping  
- Test results  
- Risk and its Mitigations
- Proposed Autosync integration workflow 
- Time Estimate Chart 
- Conclusion  

---

## 2. Objective
The main objectives of this POC are:

1. Understand the full AssemblyAI transcription workflow (upload → transcription → polling → final output).  
2. Implement this workflow in C# backend using an `AssemblyAIService`.  
3. Retrieve complete text, timestamps, speaker labels, and confidence scores.  
4. Convert AssemblyAI’s JSON response into internal DTOs.  
5. Prepare workflow recommendations for integrating transcription into the Autosync system.

---

## 3. High-Level Workflow

### Summary
1. Receive audio stream.  
2. Upload audio to AssemblyAI (`POST /v2/upload`).  
3. Create transcription request (`POST /v2/transcript`).  
4. Poll the transcription job until it completes.  
5. Retrieve the final JSON response.  
6. Parse and map the JSON into internal DTOs.  
7. Output structured transcript for future Autosync integration.

---

## 4. API Planning (AssemblyAI + Swagger for Backend Service)

### 4.1 External API Endpoints (AssemblyAI APIs):

| Purpose | Method | Endpoint |
|--------|--------|----------|
| Upload audio | POST | `https://api.assemblyai.com/v2/upload` |
| Create transcription job | POST | `https://api.assemblyai.com/v2/transcript` |
| Poll transcription status | GET | `https://api.assemblyai.com/v2/transcript/{id}` |
| Fetch final result | GET | `https://api.assemblyai.com/v2/transcript/{id}` |

**Authentication Header:**
authorization: <API_KEY>

---
### 4.2 Swagger-Backend Service:
| Purpose | Method | Endpoint |
|--------|--------|----------|
| Sentence-level transcription | POST | `/api/transcription` |
| Word-level transcription (per-word timestamps, speaker labels, % confidence) | POST | `/api/transcription/words` |
| Check if backend service is running | GET | `/api/health` |
| Fetch supported transcription features | GET | `/api/transcription/features` |
---

## 5. Workflow Diagram 

```mermaid


flowchart TD
  A([Start])
  A --> B[Receive Audio Stream]
  B --> C{Valid Audio Format?}
  C -- No --> X[Return Error]
  X --> Z([End])
  C -- Yes --> D[Upload Audio: POST /v2/upload]

  D --> E{Upload Success?}
  E -- No --> P[Retry]
  E -- Yes --> F[Receive audio_url]
  P --> D


  F --> G[Create Transcription: POST /v2/transcript]
  G --> H{Request Accepted?}
  H -- No --> X
  H -- Yes --> I[Get transcript_id]

  I --> J[Poll: GET /v2/transcript/id]
  J --> K{Status}
  K -- queued --> J
  K -- processing --> J
  K -- error --> X
  K -- completed --> L[Fetch Final JSON: GET /v2/transcript/id]

  L --> M[Parse JSON: text, timestamps, confidence]
  M --> N[Map JSON to Internal DTOs]
  N --> O[Prepare Structured Transcript Output]
  O --> Z([End])
  ```
  ---

## 6. Backend Implementation (C# Business Logic Overview)
The AssemblyAIService is responsible for executing the full transcription workflow.

### 6.1 Constructor Setup
Inject HttpClient and API key.

Set the authorization header for all subsequent API calls.

```csharp
public AssemblyAIService(HttpClient httpClient, IOptions<AssemblyAiOptions> options)
{
    _http = httpClient;
    _apiKey = options.Value.ApiKey ?? throw new ArgumentNullException(nameof(options.Value.ApiKey));

    _http.DefaultRequestHeaders.Clear();
    _http.DefaultRequestHeaders.Add("authorization", _apiKey);
}
```


### 6.2 Orchestration Method - Full Transcription Flow
```csharp
public async Task<SimpleTranscriptDto> TranscribeFileAsync(Stream fileStream)
{
    var uploadUrl = await UploadFileAsync(fileStream);
    var transcriptId = await CreateTranscriptAsync(uploadUrl);
    var rawJson = await WaitForTranscriptAsync(transcriptId);
    return MapToDto(rawJson);
}
```

### Explanation:

Uploads audio -> gets upload URL.

Creates transcription job -> receives transcript ID.

Polls repeatedly until status = "completed".

Retrieves final JSON.

Maps JSON to DTO for the application.

---

## 7. Step-by-Step Explanation of Each Method

### 7.1 Upload Audio — UploadFileAsync
Sends raw audio stream to ```POST /v2/upload```.

Returns upload_url for transcription.

### 7.2 Retry & Fallback (exponential backoff + jitter)

**Strategy:** transient network failures and 5xx/429 responses are retried using exponential backoff + jitter. Retries are bounded; if retries are exhausted an alternative upload path (chunked/resumable or alternate storage) is attempted. All retry attempts are logged and exposed via metrics.

**Implementation notes**
- Use a Polly-based policy in production for robust, testable behavior. For small projects a custom RetryHelper (exponential backoff + jitter) is sufficient.

- Retry only on transient conditions (network exceptions, 5xx, 429). Do not retry non-retryable 4xx errors.

- When retrying uploads, prefer chunked/resumable upload fallback to avoid re-sending entire file.

**Sample pseudocode:** 

```csharp
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
        // Consider InvalidOperationException as non-retryable client error 
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

```

On failing to upload it will show a messege.
```csharp

 var msg = $"Upload failed after {attempts} attempt(s).";
 if (lastException != null)
     throw new Exception(msg, lastException);

  throw new Exception(msg);
```

### 7.3 Create Transcription Job — CreateTranscriptAsync
Body sent to AssemblyAI:

```json
{
  "audio_url": "<upload_url>",
  "punctuate": true,
  "format_text": true,
  "speaker_labels": true
}
```
Calls ```"POST /v2/transcript".```

Receives transcript_id.

### 7.4 Polling Logic — WaitForTranscriptAsync
Calls:

```bash
GET /v2/transcript/{transcript_id}
```
Status values:

- "queued" -> keep polling
- "processing" -> keep polling
- "completed" -> return final JSON
- "error" -> stop and throw exception

Polling interval: 3 seconds



### 7.5 JSON Mapping — MapToDto
Extracts:

- FullText (full transcript)
- Sentence-level segments:
  - speaker
  - text
  - start timestamp
  - end timestamp
  - confidence score

Output DTO:

`SimpleTranscriptDto`

Contains list of `SentenceDto` items

---

## 8. Example Output From Demo Audio

```json
{
  "text": "Hello welcome to the demo",
  "utterances": [
    {
      "speaker": "SPEAKER_0",
      "text": "Hello welcome to the demo",
      "start": 120,
      "end": 520,
      "confidence": 0.96
    }
  ]
}
```

---
## 9. Risk & Mitigation Chart:

| No. | Risk Category                                     | Description                                                                | Impact | Likelihood | Mitigation Strategy                                                                                                        |
| --- | ------------------------------------------------- | -------------------------------------------------------------------------- | ------ | ---------- | -------------------------------------------------------------------------------------------------------------------------- |
| 1   | **Network Failure During Large File Upload**      | Uploading large audio files may fail due to unstable network connectivity. | High   | Medium     | Implement **chunked/resumable uploads**, retries with exponential backoff, and fallback recovery.                          |
| 2   | **AssemblyAI Service Downtime / Slow Response**   | External API may be unavailable or slow, delaying transcription.           | High   | Low        | Add retry logic, timeouts, and surface errors gracefully; implement fallback queue or delayed retry mechanism.             |
| 3   | **Incorrect or Incomplete Transcript Output**     | Transcription accuracy may vary due to noise or unclear speech.            | Medium | Medium     | Apply **noise reduction**, guide users to upload clean audio, and use **confidence scores** to flag low-accuracy segments. |
| 4   | **Exceeding API Rate Limits or Quotas**           | High-frequency uploads/polls may hit AssemblyAI rate limits.               | Medium | Medium     | Add rate-limit awareness, exponential backoff, scheduled polling, and batching strategies.                                 |
| 5   | **Unexpected JSON Schema Changes**                | API updates may modify response structure, breaking mapping.               | Medium | Low        | Use defensive JSON parsing, add version validation, monitor documentation changes.                                         |
| 6   | **File Format Not Supported or Corrupted Audio**  | Users may upload unsupported formats or damaged audio.                     | Low    | Medium     | Validate file format before upload; implement error handling to reject invalid audio early.                                |
| 7   | **Long Transcription Times for Very Large Files** | Processing time may increase significantly for long videos.                | High   | Medium     | Optimize polling frequency, display progress indicators, and consider splitting audio logically.                           |
| 8   | **Security Risk: API Key Exposure**               | API key leakage can compromise system security.                            | High   | Low        | Store keys in environment variables or secure vault; never expose them in frontend or logs.                                |
| 9   | **Server Resource Overuse (Memory/CPU)**          | Large streams or repeated retries can strain backend resources.            | Medium | Medium     | Stream files efficiently, apply request limits, and monitor resource usage.                                                |
| 10  | **Inaccurate Speaker Diarization**                | Model may misidentify speakers in overlapping speech.                      | Medium | Medium     | Allow manual correction in UI; use confidence scores; consider improved diarization models later.                          |

---

## 9. Proposed Autosync Integration Workflow
When audio extraction is ready:

- Video -> Audio extraction.
- Upload the extracted audio to the Swagger API Endpoint.
- Forward Audio to AssemblyAI with Retry Support.
- Receive structured transcript with aligned timestamps.
- Map timestamps to video timeline.
- Generate synced transcript for Autosync.

---


## Time Estimate Chart

### Development Tasks & Estimates

| No | Task Name                                   | Estimate (Hours) | Dependencies | Notes                                      |
|----|--------------------------------------------|-------------------|-------------|-------------------------------------------|
| 1  | Setup HttpClient and API Key              | 0.5 hrs          | None        | Configure API key and headers            |
| 2  | Implement `UploadFileAsync` (audio upload)| 2.0 hrs          | Task 1      | Upload audio to AssemblyAI               |
| 3  | Implement robust retry support (policy, chunked upload with resume, exponential backoff, error handling, idempotency, logging, and tests) | 3.0 hrs | Task 2 | Includes defining retry strategy, implementing chunked upload with resume, adding backoff + jitter, handling transient vs non-retryable errors, ensuring idempotency, and writing unit/integration tests |
| 4  | Implement `CreateTranscriptAsync`         | 2.5 hrs          | Task 2      | Create transcription job                 |
| 5  | Implement `WaitForTranscriptAsync` (poll) | 2.0 hrs          | Task 3      | Poll until transcription completes       |
| 6  | Implement `MapToDto` (JSON mapping)       | 1.5 hrs          | Task 4      | Parse JSON and map to DTOs              |
| 7  | Unit Tests for Each Method                | 3 hrs          | Tasks 2–5   | Validate individual methods              |
| 8  | Integration Test with Demo Audio          | 2 hrs          | Task 6      | End-to-end workflow test                 |
| 9  | Documentation & Workflow Diagram          | 2 hrs          | All tasks   | Finalize docs and diagrams               |

**Total Estimated Time:** ~18.5 hrs

---

## 10. Conclusion
AssemblyAI transcription integrates seamlessly with C#, delivering accurate timestamps and confidence scores for every segment. The implementation ensures that the raw JSON response is efficiently mapped into clean, structured DTOs, making the data ready for downstream processing. This POC validates AssemblyAI as a reliable transcription engine and confirms its suitability for powering future Autosync functionality, enabling precise synchronization between audio and video timelines.

