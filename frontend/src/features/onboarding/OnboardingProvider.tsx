import { createContext, lazy, Suspense, useContext, useEffect, useState, type ReactNode } from "react";
import { onboardingApi } from "../../api";
import { TourController, TOUR_VERSION } from "./tour";

const WuknaTour = lazy(() => import("./WuknaTour"));
const TourContext = createContext<TourController | null>(null);
export function useReplayTour() { return useContext(TourContext)?.start; }

export function OnboardingProvider({ ready, path, children }: {
  ready: boolean; path: string; children: ReactNode;
}) {
  const [controller] = useState(() => new TourController(onboardingApi, (name, step) => {
    // A local integration hook only: no analytics service, user identifiers or network traffic.
    window.dispatchEvent(new CustomEvent("wukna:tour-event", { detail: { name, version: TOUR_VERSION, step: step + 1 } }));
  }));
  const [started, setStarted] = useState(false);
  useEffect(() => {
    if (!ready || started) return;
    setStarted(true);
    void controller.load();
  }, [ready, started, controller]);
  return <TourContext.Provider value={controller}>
    {children}
    <Suspense fallback={null}><WuknaTour controller={controller} path={path} ready={ready} /></Suspense>
  </TourContext.Provider>;
}
