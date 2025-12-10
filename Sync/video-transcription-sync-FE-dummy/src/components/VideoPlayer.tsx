// import { useRef, useEffect, useState } from "react";
// import { useVideoStore } from "../store/VideoStore";

// const VideoPlayer = () => {
//   const videoRef = useRef<HTMLVideoElement | null>(null);
//   const setCurrentTime = useVideoStore((state) => state.setCurrentTime);
//   const setVideoRef = useVideoStore((state) => state.setVideoRef);
//   const [isPlaying, setIsPlaying] = useState(false);

//   // Save video ref in store
//   useEffect(() => {
//     setVideoRef(videoRef);
//   }, [setVideoRef]);

//   const togglePlayPause = () => {
//     const video = videoRef.current;
//     if (!video) return;

//     if (isPlaying) video.pause();
//     else video.play();

//     setIsPlaying((prev) => !prev);
//   };

//   useEffect(() => {
//     const video = videoRef.current;
//     if (!video) return;

//     const handleTimeUpdate = () => {
//       if (!video.paused) {
//         setCurrentTime(video.currentTime * 1000); // milliseconds
//       }
//     };

//     video.addEventListener("timeupdate", handleTimeUpdate);
//     return () => video.removeEventListener("timeupdate", handleTimeUpdate);
//   }, [setCurrentTime]);

//   useEffect(() => {
//     const handleKeyDown = (event: KeyboardEvent) => {
//       const target = document.activeElement?.tagName;
//       if (target === "INPUT" || target === "TEXTAREA") return;
//       if (event.key === "Enter" || event.code === "Space") {
//         event.preventDefault();
//         togglePlayPause();
//       }
//     };
//     document.addEventListener("keydown", handleKeyDown);
//     return () => document.removeEventListener("keydown", handleKeyDown);
//   }, [isPlaying]);

//     return (
//       <div className="p-4">
//         <video
//           ref={videoRef}
//           src="/video1.mp4"
//           controls
//           style={{ width: "100%", maxWidth: "800px" }}
//           onPlay={() => setIsPlaying(true)}
//           onPause={() => setIsPlaying(false)}
//         />
//       </div>
//     );
// };

// export default VideoPlayer;


import { useRef, useEffect, useState } from "react";
import { useVideoStore } from "../store/VideoStore";
import "../index.css"

const VideoPlayer = () => {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const setCurrentTime = useVideoStore((state) => state.setCurrentTime);
  const setVideoRef = useVideoStore((state) => state.setVideoRef);
  const [isPlaying, setIsPlaying] = useState(false);

  // Save video ref in store
  useEffect(() => {
    setVideoRef(videoRef);
  }, [setVideoRef]);

  const togglePlayPause = () => {
    const video = videoRef.current;
    if (!video) return;

    if (isPlaying) video.pause();
    else video.play();

    setIsPlaying((prev) => !prev);
  };

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;

    const handleTimeUpdate = () => {
      setCurrentTime(video.currentTime * 1000);
    };

    video.addEventListener("timeupdate", handleTimeUpdate);
    return () => video.removeEventListener("timeupdate", handleTimeUpdate);
  }, [setCurrentTime]);

  // Keep Enter as play/pause hotkey. Space is reserved for timestamp capture.
  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      const target = document.activeElement?.tagName;
      if (target === "INPUT" || target === "TEXTAREA") return;
      if (event.key === "Enter") {
        event.preventDefault();
        togglePlayPause();
      }
    };
    document.addEventListener("keydown", handleKeyDown);
    return () => document.removeEventListener("keydown", handleKeyDown);
  }, [isPlaying]);

  return (
    <div className="p-4">
      <video
        ref={videoRef}
        src="/video3.mp4"
        controls
        style={{ width: "100%", maxWidth: "800px" }}
        onPlay={() => setIsPlaying(true)}
        onPause={() => setIsPlaying(false)}
      />
      <div className="mt-2 text-sm text-gray-600">
        Hint: in Edit Mode, press <kbd className="px-2 py-1 bg-gray-200 rounded">Space</kbd> to capture timestamp and pause video. Use <kbd className="px-2 py-1 bg-gray-200 rounded">Enter</kbd> to toggle play/pause.
      </div>
    </div>
  );
};

export default VideoPlayer ;
