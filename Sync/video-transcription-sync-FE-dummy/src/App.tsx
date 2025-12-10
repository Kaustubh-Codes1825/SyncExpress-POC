import RenderTranscript from "./components/RenderTranscript";
import VideoPlayer from "./components/VideoPlayer";
import "./index.css";

const App = () => {
  return (
    <div className="w-full h-screen grid grid-cols-2 gap-0 overflow-hidden">
  {/* LEFT SIDE — VIDEO occupies 50% width */}
  <div className="border-r overflow-hidden flex flex-col">
    <VideoPlayer />
  </div>

  {/* RIGHT SIDE — TRANSCRIPT occupies 50% width */}
  <div className="overflow-y-auto">
    <RenderTranscript />
  </div>
</div>

  );
};

export default App;
