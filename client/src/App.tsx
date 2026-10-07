import { useState } from "react";
import { ChatWidget } from "./components/chat/ChatWidget";
import { FeatureSection } from "./components/landing/FeatureSection";
import { HeroSection } from "./components/landing/HeroSection";
import { SiteHeader } from "./components/landing/SiteHeader";
import "./App.css";

function App() {
  const [isChatOpen, setIsChatOpen] = useState(false);

  return (
    <main className="demo-page">
      <SiteHeader />
      <HeroSection onStartChat={() => setIsChatOpen(true)} />
      <FeatureSection />
      <ChatWidget isOpen={isChatOpen} onOpenChange={setIsChatOpen} />
    </main>
  );
}

export default App;
