// src/components/RenderTranscript.tsx
import { useEffect, useRef, useState } from "react";
import { useVideoStore } from "../store/VideoStore";

interface TranscriptRow {
  sentence: string;
  start: number; // milliseconds
  confidence: number;
  speaker: string;
  timestamps?: { at: number; note?: string }[];
}

const RenderTranscript = () => {
  const [transcript, setTranscript] = useState<TranscriptRow[]>([]);
  const [editMode, setEditMode] = useState(false);
  const [editingIndex, setEditingIndex] = useState<number | null>(null); // inline edit index
  const [editingText, setEditingText] = useState("");
  const currentTime = useVideoStore((state) => state.currentTime); // ms
  const videoRef = useVideoStore((state) => state.videoRef);

  // -- Edit-mode manual highlight (when editMode === true we use this)
  const [editHighlightIndex, setEditHighlightIndex] = useState<number | null>(null);

  // refs for stable transcript access and timers
  const transcriptRef = useRef<TranscriptRow[]>([]);
  useEffect(() => {
    transcriptRef.current = transcript;
  }, [transcript]);

  // last captured flash + resume affordance
  const [lastCapturedIndex, setLastCapturedIndex] = useState<number | null>(null);
  const [showResumeIndex, setShowResumeIndex] = useState<number | null>(null);
  const captureFlashTimer = useRef<number | null>(null);

  // play-range control: when playing a single sentence in edit mode we pause at endTimeSec
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

  // activeIndex (view mode) - only used when editMode === false
  const activeIndex = transcript.findIndex((item, idx) => {
    const start = item.start ?? 0;
    const nextStart = transcript[idx + 1]?.start ?? Infinity;
    return currentTime >= start && currentTime < nextStart;
  });

  // helper: compute end time (seconds) for sentence index (next.start or video.duration)
  const computeSentenceEndSeconds = (idx: number) => {
    if (!transcript[idx]) return videoRef?.current?.duration ?? 0;
    const nextStartMs = transcript[idx + 1]?.start ?? null;
    if (nextStartMs != null) return nextStartMs / 1000;
    return videoRef?.current?.duration ?? (transcript[idx].start / 1000 + 5); // fallback
  };

  // play only a single sentence (seek to start, play, and enforce a pause when reaching sentence end).
  const playSingleSentence = (idx: number) => {
    const line = transcript[idx];
    if (!line || !videoRef?.current) return;
    const startSec = line.start / 1000;
    const endSec = computeSentenceEndSeconds(idx);
    playRangeEndRef.current = endSec;
    videoRef.current.currentTime = startSec;
    videoRef.current.play().catch(() => {});
  };

  // generic play from timestamp (used for timestamp chips)
  const handlePlayFromTimestamp = (ms: number) => {
    if (!videoRef?.current) return;
    playRangeEndRef.current = null; // not restricting range when playing arbitrary timestamp
    videoRef.current.currentTime = ms / 1000;
    videoRef.current.play().catch(() => {});
  };

  // when clicking a sentence:
  // - in Edit Mode: highlight the clicked sentence and play only that sentence
  // - in View Mode: seek & play normally (and auto-highlighting will follow)
  const handleClick = (idx: number) => {
    if (editMode) {
      setEditHighlightIndex(idx);
      playSingleSentence(idx);
    } else {
      // view behavior
      const line = transcript[idx];
      if (!line || !videoRef?.current) return;
      playRangeEndRef.current = null;
      videoRef.current.currentTime = line.start / 1000;
      videoRef.current.play().catch(() => {});
    }
  };

  // inline edit lifecycle
  const startEditing = (idx: number) => {
    setEditingIndex(idx);
    setEditingText(transcript[idx].sentence);
    try { videoRef?.current?.pause(); } catch {}
  };
  const saveEdit = () => {
    if (editingIndex == null) return;
    setTranscript((prev) => {
      const copy = prev.map((r) => ({ ...r }));
      copy[editingIndex] = { ...copy[editingIndex], sentence: editingText };
      return copy;
    });
    setEditingIndex(null);
    setEditingText("");
  };
  const cancelEdit = () => {
    setEditingIndex(null);
    setEditingText("");
  };

  const deleteLine = (idx: number) => {
    const item = transcript[idx];
    if (!item) return;
    const ok = window.confirm("Delete this line? This action cannot be undone.");
    if (!ok) return;
    setTranscript((prev) => {
      const copy = prev.slice();
      copy.splice(idx, 1);
      return copy;
    });
    if (editingIndex === idx) {
      setEditingIndex(null);
      setEditingText("");
    } else if (editingIndex != null && idx < editingIndex) {
      setEditingIndex((prev) => (prev != null ? prev - 1 : prev));
    }
    // if we deleted the highlighted sentence, move highlight forward safely
    setEditHighlightIndex((cur) => {
      if (cur == null) return cur;
      if (idx === cur) return null;
      if (idx < cur) return cur - 1;
      return cur;
    });
  };

  // Spacebar handler in Edit Mode:
  // - append timestamp to the highlighted sentence,
  // - pause video,
  // - advance highlight to next sentence and start playing that sentence.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const tag = document.activeElement?.tagName;
      const activeEl = document.activeElement as HTMLElement | null;
      if (tag === "INPUT" || tag === "TEXTAREA" || activeEl?.isContentEditable) return;

      if (e.code === "Space" || e.key === " ") {
        if (!editMode) return; // only active in edit mode
        e.preventDefault();

        const video = videoRef?.current;
        if (!video) {
          console.warn("No video reference available to capture timestamp.");
          return;
        }

        // current highlighted sentence; if none, fall back to compute active from playhead
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

        // attach timestamp to that line
        setTranscript((prev) => {
          const copy = prev.map((r) => ({ ...r, timestamps: Array.isArray(r.timestamps) ? [...r.timestamps] : [] }));
          const line = copy[idx];
          if (!line) return prev;
          line.timestamps = [...(line.timestamps || []), { at: ms, note: "user-captured" }];
          return copy;
        });

        // pause video
        try { video.pause(); } catch (err) { console.warn("Failed to pause video after capture:", err); }

        // flash + resume affordance (flash for UI feedback)
        setLastCapturedIndex(idx);
        setShowResumeIndex(idx);
        if (captureFlashTimer.current) {
          window.clearTimeout(captureFlashTimer.current);
        }
        captureFlashTimer.current = window.setTimeout(() => {
          setLastCapturedIndex(null);
          captureFlashTimer.current = null;
        }, 900);

        // advance highlight to next sentence (if exists) and play it
        const nextIdx = idx + 1;
        if (nextIdx < transcriptRef.current.length) {
          setEditHighlightIndex(nextIdx);
          // short delay to ensure pause has taken effect visually
          setTimeout(() => playSingleSentence(nextIdx), 120);
        } else {
          // no next sentence — clear highlight
          setEditHighlightIndex(null);
        }
      }
    };

    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [editMode, videoRef, editHighlightIndex]);

  // Enforce playRangeEndRef: pause the video when currentTime reaches end of single-sentence play range
  useEffect(() => {
    const video = videoRef?.current;
    if (!video) return;
    const onTimeUpdate = () => {
      const end = playRangeEndRef.current;
      if (end != null && video.currentTime >= end - 0.02) { // tiny epsilon
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
    // Behavior:
    // - If in edit mode and this line is highlighted, play only this sentence.
    if (editMode && editHighlightIndex === idx) {
      // play single sentence from last captured timestamp if present else from sentence start
      const startSec = (lastTs ?? line.start) / 1000;
      const endSec = computeSentenceEndSeconds(idx);
      playRangeEndRef.current = endSec;
      videoRef.current.currentTime = startSec;
      videoRef.current.play().catch(() => {});
    } else {
      // generic resume: go to last ts and play
      playRangeEndRef.current = null;
      videoRef.current.currentTime = (lastTs ?? line.start) / 1000;
      videoRef.current.play().catch(() => {});
    }
    setShowResumeIndex(null);
    setLastCapturedIndex(null);
  };

  // auto-focus editing input
  const editInputRef = useRef<HTMLInputElement | null>(null);
  useEffect(() => {
    if (editingIndex != null) setTimeout(() => editInputRef.current?.focus(), 50);
  }, [editingIndex]);

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
            <input type="checkbox" checked={editMode} onChange={(e) => { setEditMode(e.target.checked); setEditingIndex(null); setEditHighlightIndex(null); }} />
            <span className="text-sm">Edit Mode</span>
          </label>
        </div>

        <div className="text-sm text-gray-600">
          {editMode ? "Edit Mode: click a sentence to play only that sentence. Press Space to capture timestamp, advance to next." : "View Mode: click a sentence to play (auto-highlighting enabled)."}
        </div>
      </div>

      <div className="p-4 max-h-[70vh] overflow-y-auto space-y-2">
        {transcript.map((item, idx) => {
          // highlight logic: in edit mode use editHighlightIndex, otherwise use activeIndex from currentTime
          const isHighlighted = editMode ? (editHighlightIndex === idx) : (activeIndex === idx);
          const startSeconds = (item.start / 1000).toFixed(3);
          const isEditingThis = editingIndex === idx;
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
                {/* Show start time only in view mode; in edit mode hide start time (per request) */}
                {!editMode ? (
                  <span className="text-sm font-mono">{startSeconds}s</span>
                ) : (
                  <span className="text-sm text-gray-400 italic">Edit Mode: start hidden</span>
                )}

                <div className="flex-1 px-2">
                  {isEditingThis ? (
                    <input
                      ref={editInputRef}
                      value={editingText}
                      onChange={(e) => setEditingText(e.target.value)}
                      onMouseDown={(e) => e.stopPropagation()}
                      onClick={(e) => e.stopPropagation()}
                      className="w-full p-1 border rounded-md"
                    />
                  ) : (
                    <span>{item.sentence}</span>
                  )}
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
                            onClick={(e) => {
                              e.stopPropagation();
                              handlePlayFromTimestamp(ts.at);
                            }}
                            className="underline"
                          >
                            {(ts.at / 1000).toFixed(3)}s
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
                    // when not in edit mode we still show a Resume button if available
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
