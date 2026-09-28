import { useSyncExternalStore } from "react";
import { Maximize, Minus, Plus } from "lucide-react";
import { Button, IconButton } from "../../../components/ui/Button";
import { boardZoom } from "../boardZoom";
import type { BoardViewportController } from "../hooks/useBoardViewport";

export function ZoomControls({ controller }: { controller: BoardViewportController }) {
  const { zoom } = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  return (
    <div className="board-zoom-controls" role="group" aria-label="Board zoom">
      <IconButton label="Zoom out" disabled={zoom <= boardZoom.min + 0.001} onClick={controller.zoomOut}><Minus size={18} aria-hidden="true" /></IconButton>
      <Button variant="quiet" size="compact" aria-label={`Reset zoom (${Math.round(zoom * 100)}%)`} onClick={controller.reset}>{Math.round(zoom * 100)}%</Button>
      <IconButton label="Zoom in" disabled={zoom >= boardZoom.max - 0.001} onClick={controller.zoomIn}><Plus size={18} aria-hidden="true" /></IconButton>
      <IconButton label="Fit to content" onClick={controller.fit}><Maximize size={18} aria-hidden="true" /></IconButton>
      <span className="wk-sr-only" role="status">Board zoom {Math.round(zoom * 100)} percent</span>
    </div>
  );
}
