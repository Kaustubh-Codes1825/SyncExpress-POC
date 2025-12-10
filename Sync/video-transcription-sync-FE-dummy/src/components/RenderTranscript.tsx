import { useEffect, useRef, useState } from "react";
import { useVideoStore } from "../store/VideoStore";

interface TranscriptRow {
  sentence: string;
  start: number; // milliseconds
  end: number;
  confidence: number;
  speaker: string;
  timestamps?: { at: number; note?: string }[];
}

const RenderTranscript = () => {
  const [transcript, setTranscript] = useState<TranscriptRow[]>([]);
  const [editMode, setEditMode] = useState(false);
  // edit mode manual highlight (null when none)
  const [editHighlightIndex, setEditHighlightIndex] = useState<number | null>(null);

  const currentTime = useVideoStore((state) => state.currentTime); // ms
  const videoRef = useVideoStore((state) => state.videoRef);

  // keep ref copy to avoid stale closures in key handlers
  const transcriptRef = useRef<TranscriptRow[]>([]);
  useEffect(() => {
    transcriptRef.current = transcript;
  }, [transcript]);

  // flash + resume affordances
  const [lastCapturedIndex, setLastCapturedIndex] = useState<number | null>(null);
  const [showResumeIndex, setShowResumeIndex] = useState<number | null>(null);
  const captureFlashTimer = useRef<number | null>(null);

  // restrict playback to a sentence end when playing a single sentence in edit mode
  const playRangeEndRef = useRef<number | null>(null);

  useEffect(() => {
    const loadTranscript = async () => {
      try {
        const res = await fetch("/transcript3.json");
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data: TranscriptRow[] = await res.json();
        const normalized = data.map((r) => ({ ...r, timestamps: Array.isArray(r.timestamps) ? r.timestamps : [] }));
        setTranscript(normalized);
      } catch (err) {
        console.error("Error loading transcript:", err);
        setTranscript([]);
      }
    };
    loadTranscript();
  }, []);

  // view-mode active index (based on currentTime)
  const activeIndex = transcript.findIndex((item, idx) => {
    const start = item.start ?? 0;
    const nextStart = transcript[idx + 1]?.start ?? Infinity;
    return currentTime >= start && currentTime < nextStart;
  });

  // helper: compute end seconds of sentence idx (next.start or video.duration)
  const computeSentenceEndSeconds = (idx: number) => {
    if (!transcript[idx]) return videoRef?.current?.duration ?? 0;
    const nextStartMs = transcript[idx + 1]?.start ?? null;
    if (nextStartMs != null) return nextStartMs / 1000;
    return videoRef?.current?.duration ?? (transcript[idx].start / 1000 + 5);
  };

  // play single sentence: sets playRangeEndRef so we pause at end
  const playSingleSentence = (idx: number) => {
    const line = transcript[idx];
    if (!line || !videoRef?.current) return;
    const startSec = line.start / 1000;
    const endSec = computeSentenceEndSeconds(idx);
    playRangeEndRef.current = endSec;
    videoRef.current.currentTime = startSec;
    videoRef.current.play().catch(() => {});
  };

  const handlePlayFromTimestamp = (ms: number) => {
    if (!videoRef?.current) return;
    playRangeEndRef.current = null;
    videoRef.current.currentTime = ms / 1000;
    videoRef.current.play().catch(() => {});
  };

  // click a sentence
  const handleClick = (idx: number) => {
    if (editMode) {
      setEditHighlightIndex(idx);
      playSingleSentence(idx);
    } else {
      const line = transcript[idx];
      if (!line || !videoRef?.current) return;
      playRangeEndRef.current = null;
      videoRef.current.currentTime = line.start / 1000;
      videoRef.current.play().catch(() => {});
    }
  };

  // remove a single timestamp entry (lineIdx, tsIdx)
  const removeTimestamp = (lineIdx: number, tsIdx: number) => {
    setTranscript((prev) => {
      const copy = prev.map((r) => ({ ...r, timestamps: Array.isArray(r.timestamps) ? [...r.timestamps] : [] }));
      const line = copy[lineIdx];
      if (!line || !Array.isArray(line.timestamps) || tsIdx < 0 || tsIdx >= line.timestamps.length) return prev;
      line.timestamps.splice(tsIdx, 1);
      return copy;
    });
  };


  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const tag = document.activeElement?.tagName;
      const activeEl = document.activeElement as HTMLElement | null;
      if (tag === "INPUT" || tag === "TEXTAREA" || activeEl?.isContentEditable) return;

      if (e.code === "Space" || e.key === " ") {
        if (!editMode) return;
        e.preventDefault();

        const video = videoRef?.current;
        if (!video) {
          console.warn("No video reference available to capture timestamp.");
          return;
        }

        // decide which sentence to attach to: prefer manual highlight, else find by playhead
        const idx = editHighlightIndex ?? transcriptRef.current.findIndex((item, i) => {
          const s = item.start ?? 0;
          const next = transcriptRef.current[i + 1]?.start ?? Infinity;
          return (video.currentTime * 1000) >= s && (video.currentTime * 1000) < next;
        });

        if (idx === -1 || idx == null) {
          console.warn("No active/highlighted transcript line to attach timestamp to.");
          return;
        }

        const ms = Math.round(video.currentTime * 1000);

        // append timestamp to that line
        setTranscript((prev) => {
          const copy = prev.map((r) => ({ ...r, timestamps: Array.isArray(r.timestamps) ? [...r.timestamps] : [] }));
          const line = copy[idx];
          if (!line) return prev;
          line.timestamps = [...(line.timestamps || []), { at: ms, note: "user-captured" }];
          return copy;
        });

        // pause video
        try { video.pause(); } catch (err) { console.warn("Failed to pause video after capture:", err); }

        // flash + resume affordance
        setLastCapturedIndex(idx);
        setShowResumeIndex(idx);
        if (captureFlashTimer.current) window.clearTimeout(captureFlashTimer.current);
        captureFlashTimer.current = window.setTimeout(() => {
          setLastCapturedIndex(null);
          captureFlashTimer.current = null;
        }, 900);

        // advance highlight to next sentence (if exists) and play it
        const nextIdx = idx + 1;
        if (nextIdx < transcriptRef.current.length) {
          setEditHighlightIndex(nextIdx);
          setTimeout(() => playSingleSentence(nextIdx), 120);
        } else {
          setEditHighlightIndex(null);
        }
      }
    };

    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [editMode, videoRef, editHighlightIndex]);

  // enforce playRangeEndRef: pause when reach end of sentence
  useEffect(() => {
    const video = videoRef?.current;
    if (!video) return;
    const onTimeUpdate = () => {
      const end = playRangeEndRef.current;
      if (end != null && video.currentTime >= end - 0.02) {
        try { video.pause(); } catch {}
        playRangeEndRef.current = null;
      }
    };
    video.addEventListener("timeupdate", onTimeUpdate);
    return () => video.removeEventListener("timeupdate", onTimeUpdate);
  }, [videoRef, transcript]);

  const handleResume = (idx: number) => {
    if (!videoRef?.current) return;
    const line = transcript[idx];
    if (!line) return;
    const lastTs = line.timestamps && line.timestamps.length ? line.timestamps[line.timestamps.length - 1].at : line.start;
    if (editMode && editHighlightIndex === idx) {
      const startSec = (lastTs ?? line.start) / 1000;
      const endSec = computeSentenceEndSeconds(idx);
      playRangeEndRef.current = endSec;
      videoRef.current.currentTime = startSec;
      videoRef.current.play().catch(() => {});
    } else {
      playRangeEndRef.current = null;
      videoRef.current.currentTime = (lastTs ?? line.start) / 1000;
      videoRef.current.play().catch(() => {});
    }
    setShowResumeIndex(null);
    setLastCapturedIndex(null);
  };

  const getConfidenceColor = (confidence: number) => {
    if (confidence < 50) return "bg-red-200 text-red-800";
    if (confidence <= 80) return "bg-yellow-200 text-yellow-800";
    return "bg-green-200 text-green-800";
  };

  return (
    <div className="p-4">
      <div className="flex items-center justify-between mb-3">
        <div>
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              checked={editMode}
              onChange={(e) => { setEditMode(e.target.checked); setEditHighlightIndex(null); }}
            />
            <span className="text-sm">Edit Mode</span>
          </label>
        </div>

        <div className="text-sm text-gray-600">
          {editMode
            ? "Edit Mode: click a sentence to play only that sentence. Press Space to capture timestamp; it will pause and advance to the next sentence."
            : "View Mode: click a sentence to play (auto-highlighting enabled)."}
        </div>
      </div>

      <div className="p-4 max-h-[70vh] overflow-y-auto space-y-2">
        {transcript.map((item, idx) => {
          // highlight logic: use manual editHighlightIndex in editMode, otherwise activeIndex
          const isHighlighted = editMode ? (editHighlightIndex === idx) : (activeIndex === idx);
          const startSeconds = (item.start / 1000).toFixed(3);
          const endSeconds = (item.end/1000).toFixed(3);
          const justCaptured = lastCapturedIndex === idx;
          const showResume = showResumeIndex === idx;

          return (
            <div
              key={idx}
              onClick={() => handleClick(idx)}
              className={`p-3 rounded-lg cursor-pointer transition ${isHighlighted ? "bg-yellow-200 text-black font-semibold" : "bg-gray-100 hover:bg-gray-200"} shadow-sm flex flex-col ${justCaptured ? "ring-4 ring-offset-2 ring-green-300" : ""}`}
              data-line-index={idx}
            >
              <div className="flex justify-between items-center gap-2 mb-1">
                
                {!editMode ? (
                  <span className="text-sm font-mono">{startSeconds}s</span>
                ) : (
                  <span className="text-sm text-red-400 italic">{startSeconds}s - {endSeconds}s</span>
                )}

                <div className="flex-1 px-2">
                  <span>{item.sentence}</span>
                </div>

                <div className={`text-sm font-medium px-2 py-0.5 rounded ${getConfidenceColor(item.confidence)}`}>{item.confidence}%</div>
              </div>

              <div className="flex items-center justify-between">
                <div className="text-gray-600 text-sm italic">Speaker: {item.speaker}</div>

                <div className="flex items-center gap-3">
                  <div className="flex gap-2 items-center">
                    {item.timestamps && item.timestamps.length > 0 ? (
                      item.timestamps.map((ts, tsi) => (
                        <div key={tsi} className="flex items-center gap-2 text-xs bg-gray-100 rounded-full px-2 py-1">
                          <button
                            onClick={(e) => { e.stopPropagation(); handlePlayFromTimestamp(ts.at); }}
                            className="underline"
                          >
                            {(ts.at / 1000).toFixed(3)}s
                          </button>

                          {/* cross icon to remove this timestamp */}
                          <button
                            onClick={(e) => { e.stopPropagation(); removeTimestamp(idx, tsi); }}
                            title="Remove timestamp"
                            className="ml-1 text-xs rounded-full w-5 h-5 flex items-center justify-center text-red-800 hover:bg-gray-200"
                          >
                            ×
                          </button>
                        </div>
                      ))
                    ) : (
                      <div className="text-xs text-gray-400 italic">No user timestamps</div>
                    )}
                  </div>

                  {editMode && (
                    <div className="flex gap-2">
                      <button
                        onClick={(e) => { e.stopPropagation(); playSingleSentence(idx); }}
                        className="px-2 py-1 bg-gray-200 rounded text-sm"
                      >
                        Listen
                      </button>

                      {/* Resume button after capture */}
                      {showResume && (
                        <button
                          onClick={(e) => { e.stopPropagation(); handleResume(idx); }}
                          className="px-2 py-1 bg-green-600 text-white rounded text-sm ml-2"
                        >
                          Resume
                        </button>
                      )}
                    </div>
                  )}

                  {!editMode && (
                    showResume && (
                      <button
                        onClick={(e) => { e.stopPropagation(); handleResume(idx); }}
                        className="px-2 py-1 bg-green-600 text-white rounded text-sm ml-2"
                      >
                        Resume
                      </button>
                    )
                  )}
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
};

export default RenderTranscript;
