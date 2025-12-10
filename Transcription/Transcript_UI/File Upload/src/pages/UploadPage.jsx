// src/UploadPage.jsx
import React, { useState, useRef } from "react";
import axios from "axios";

const API_URL = "https://localhost:7001/api/Transcription";

export default function UploadPage() {
  const [file, setFile] = useState(null);
  const [uploading, setUploading] = useState(false);
  const [progress, setProgress] = useState(0);
  const [error, setError] = useState("");
  const [result, setResult] = useState(null);
  const [rawJson, setRawJson] = useState("");
  const fileRef = useRef(null);

  // handle file selection
  const handleFile = (e) => {
    setFile(e.target.files?.[0] ?? null);
    setProgress(0);
    setError("");
    setResult(null);
    setRawJson("");
  };

  // upload form data
  const doUpload = async () => {
    if (!file) return;
    setUploading(true);
    setError("");
    setResult(null);
    setRawJson("");
    setProgress(0);

    const fd = new FormData();
    fd.append("file", file);

    try {
      const res = await axios.post(API_URL, fd, {
        headers: { "Content-Type": "multipart/form-data" },
        onUploadProgress: (evt) => {
          if (!evt.total) return;
          setProgress(Math.round((evt.loaded * 100) / evt.total));
        },
        timeout: 10 * 60 * 1000,
      });

      console.log("raw server:", res.data);
      setRawJson(JSON.stringify(res.data, null, 2));
      setResult(normalizeResponse(res.data));
    } catch (err) {
      const msg = err.response?.data || err.message || "Upload failed.";
      setError(typeof msg === "string" ? msg : JSON.stringify(msg));
    } finally {
      setUploading(false);
    }
  };

  const retry = () => doUpload();
  const reset = () => {
    setFile(null);
    setResult(null);
    setError("");
    setProgress(0);
    setRawJson("");
    if (fileRef.current) fileRef.current.value = "";
  };

  return (
    <div style={{ maxWidth: 820, margin: 24, fontFamily: "Inter, Roboto, Arial, sans-serif" }}>
      <h2 style={{ marginBottom: 8 }}>Transcription Upload</h2>

      <div style={{ padding: 14, border: "1px solid #eee", borderRadius: 8, background: "#fff" }}>
        <input
          ref={fileRef}
          type="file"
          accept="audio/*,video/*"
          onChange={handleFile}
          disabled={uploading}
        />

        <div style={{ marginTop: 12 }}>
          <button onClick={doUpload} disabled={!file || uploading} style={btn}>
            {uploading ? "Uploading..." : "Upload"}
          </button>
          <button onClick={reset} disabled={uploading} style={btnAlt}>
            Reset
          </button>
        </div>

        {uploading && (
          <div style={{ marginTop: 14 }}>
            <div style={{ marginBottom: 6 }}>{progress}%</div>
            <input type="range" min="0" max="100" value={progress} readOnly style={{ width: "100%" }} />
          </div>
        )}

        {!uploading && error && (
          <div style={{ marginTop: 14 }}>
            <div style={errorBox}>{error}</div>
            <div style={{ marginTop: 8 }}>
              <button onClick={retry} style={{ ...btn, background: "#ff9800" }}>
                Retry
              </button>
            </div>
          </div>
        )}

        {result && (
          <div style={{ marginTop: 18 }}>
            <h3>Full Text</h3>
            <div style={{ background: "#fafafa", padding: 10, borderRadius: 6, whiteSpace: "pre-wrap" }}>
              {result.fullText}
            </div>

            <h3 style={{ marginTop: 12 }}>Sentences</h3>
            <div style={{ maxHeight: 420, overflowY: "auto", marginTop: 8 }}>
              {result.sentences.length === 0 && (
                <div style={{ color: "#666", padding: 8 }}>No sentence segments found.</div>
              )}
              {result.sentences.map((s, i) => (
                <div key={i} style={{ display: "flex", gap: 12, padding: "10px 0", borderBottom: "1px solid #f1f1f1" }}>
                  <div style={{ width: 140, fontSize: 12, color: "#555" }}>
                    <div><strong>{s.speaker || "—"}</strong></div>
                    <div>{s.startTime ?? ""} – {s.endTime ?? ""}</div>
                    <div style={{ marginTop: 6 }}>{s.confidencePercent ?? ""}</div>
                  </div>
                  <div style={{ flex: 1 }}>{s.text}</div>
                </div>
              ))}
            </div>
          </div>
        )}

        {rawJson && (
          <div style={{ marginTop: 14 }}>
            <h4>Raw JSON (debug)</h4>
            <pre style={{ background: "#0b0b0b", color: "#eaf2ff", padding: 10, borderRadius: 6, maxHeight: 260, overflow: "auto" }}>
              {rawJson}
            </pre>
          </div>
        )}
      </div>
    </div>
  );
}

/* ---------- helpers ---------- */

// normalize different backend shapes into { fullText, sentences[] }
function normalizeResponse(data) {
  if (!data) return { fullText: "", sentences: [] };

  const fullText = data.fullText ?? data.FullText ?? data.text ?? "";

  // already-mapped sentences
  const sentencesRaw = data.sentences ?? data.Sentences;
  if (Array.isArray(sentencesRaw) && sentencesRaw.length > 0) {
    return {
      fullText,
      sentences: sentencesRaw.map(s => ({
        speaker: s.speaker ?? s.Speaker ?? s.speaker_label ?? "",
        text: s.text ?? s.Text ?? s.transcript ?? "",
        startTime: s.startTime ?? s.StartTime ?? toMMSS(s.start ?? s.startMs),
        endTime: s.endTime ?? s.EndTime ?? toMMSS(s.end ?? s.endMs),
        confidencePercent: s.confidencePercent ?? s.ConfidencePercent ?? formatConf(s.confidence)
      }))
    };
  }

  // utterances
  if (Array.isArray(data.utterances) && data.utterances.length > 0) {
    return {
      fullText,
      sentences: data.utterances.map(u => ({
        speaker: u.speaker ?? u.Speaker ?? "",
        text: u.text ?? u.Text ?? "",
        startTime: toMMSS(u.start ?? u.startMs ?? null),
        endTime: toMMSS(u.end ?? u.endMs ?? null),
        confidencePercent: formatConf(u.confidence ?? u.Confidence)
      }))
    };
  }

  // words array grouping
  if (Array.isArray(data.words) && data.words.length > 0) {
    const words = data.words;
    const getSpeaker = (w) => w.speaker ?? w.Speaker ?? w.speaker_label ?? (w.speaker_tag != null ? String(w.speaker_tag) : "");

    const anySpeaker = words.some(w => Boolean(getSpeaker(w)));
    if (anySpeaker) {
      const out = [];
      let curSpeaker = getSpeaker(words[0]);
      let sb = [];
      let segStart = words[0].start ?? words[0].startMs ?? null;
      let segEnd = words[0].end ?? words[0].endMs ?? null;
      let maxConf = words[0].confidence ?? null;

      const flush = () => {
        if (sb.length === 0) return;
        out.push({
          speaker: curSpeaker,
          text: sb.join(" ").trim(),
          startTime: toMMSS(segStart),
          endTime: toMMSS(segEnd),
          confidencePercent: formatConf(maxConf)
        });
        sb = []; segStart = segEnd = null; maxConf = null;
      };

      for (const w of words) {
        const sp = getSpeaker(w);
        if (sp !== curSpeaker) { flush(); curSpeaker = sp; }
        sb.push(w.text ?? w.word ?? "");
        segStart = segStart ?? (w.start ?? w.startMs ?? null);
        segEnd = w.end ?? w.endMs ?? segEnd;
        if (w.confidence != null) maxConf = maxConf == null ? w.confidence : Math.max(maxConf, w.confidence);
      }
      flush();
      return { fullText, sentences: out };
    } else {
      // chunk by time (6s)
      const chunkMs = 6000;
      const chunks = [];
      let curIdx = null;
      let sb = [];
      let segStart = null, segEnd = null;

      for (const w of words) {
        const start = w.start ?? w.startMs ?? 0;
        const idx = Math.floor(start / chunkMs);
        if (curIdx === null) curIdx = idx;
        if (idx !== curIdx) {
          chunks.push({ text: sb.join(" ").trim(), start: segStart, end: segEnd });
          sb = []; segStart = segEnd = null; curIdx = idx;
        }
        sb.push(w.text ?? w.word ?? "");
        segStart = segStart ?? start;
        segEnd = w.end ?? w.endMs ?? segEnd;
      }
      if (sb.length) chunks.push({ text: sb.join(" ").trim(), start: segStart, end: segEnd });

      return {
        fullText,
        sentences: chunks.map(c => ({
          speaker: "",
          text: c.text,
          startTime: toMMSS(c.start),
          endTime: toMMSS(c.end),
          confidencePercent: null
        }))
      };
    }
  }

  // segments/results fallback
  const segs = data.segments ?? data.Segments ?? data.results ?? data.Results;
  if (Array.isArray(segs) && segs.length > 0) {
    return {
      fullText,
      sentences: segs.map(s => ({
        speaker: s.speaker ?? s.Speaker ?? "",
        text: s.text ?? s.Text ?? "",
        startTime: toMMSS(s.start ?? s.startMs ?? null),
        endTime: toMMSS(s.end ?? s.endMs ?? null),
        confidencePercent: formatConf(s.confidence ?? s.Confidence)
      }))
    };
  }

  return { fullText, sentences: [] };
}

function toMMSS(ms) {
  if (ms == null) return "";
  const total = Math.floor(ms / 1000);
  const m = String(Math.floor(total / 60)).padStart(2, "0");
  const s = String(total % 60).padStart(2, "0");
  return `${m}:${s}`;
}

function formatConf(v) {
  if (v == null) return null;
  if (typeof v === "string") return v;
  return `${(v * 100).toFixed(1)}%`;
}

const btn = { marginTop: 12, padding: "8px 12px", background: "#1976d2", color: "#fff", border: "none", borderRadius: 6, cursor: "pointer" };
const btnAlt = { marginLeft: 8, marginTop: 12, padding: "8px 12px", background: "#eee", border: "1px solid #ddd", borderRadius: 6, cursor: "pointer" };
const errorBox = { background: "#fff3f3", padding: 10, borderRadius: 6, border: "1px solid #ffc7c7", color: "#a33" };
