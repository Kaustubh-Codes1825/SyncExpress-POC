// store/VideoStore.ts
import { create } from "zustand";
import type { RefObject } from "react";

interface VideoState {
  currentTime: number;
  setCurrentTime: (time: number) => void;
  videoRef: RefObject<HTMLVideoElement | null> | null;
  setVideoRef: (ref: RefObject<HTMLVideoElement | null>) => void;
}

export const useVideoStore = create<VideoState>((set) => ({
  currentTime: 0,
  setCurrentTime: (time) => set({ currentTime: time }),
  videoRef: null,
  setVideoRef: (ref) => set({ videoRef: ref }),
}));
