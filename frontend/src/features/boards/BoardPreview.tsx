import type {
  BoardPreviewConnectionDto,
  BoardPreviewNodeDto,
} from "../../api";
import { BrandMark } from "../../components/brand/BrandMark";

const viewport = { width: 320, height: 172, padding: 18 } as const;
const minimumNodeSize = { width: 7, height: 5 } as const;

type PreviewNodeLayout = BoardPreviewNodeDto & {
  renderX: number;
  renderY: number;
  renderWidth: number;
  renderHeight: number;
  centerX: number;
  centerY: number;
};

function layoutPreviewNodes(
  nodes: BoardPreviewNodeDto[],
): PreviewNodeLayout[] {
  if (nodes.length === 0) return [];

  let minX = Number.POSITIVE_INFINITY;
  let minY = Number.POSITIVE_INFINITY;
  let maxX = Number.NEGATIVE_INFINITY;
  let maxY = Number.NEGATIVE_INFINITY;

  for (const node of nodes) {
    const width = Math.max(node.width, 1);
    const height = Math.max(node.height, 1);
    minX = Math.min(minX, node.x);
    minY = Math.min(minY, node.y);
    maxX = Math.max(maxX, node.x + width);
    maxY = Math.max(maxY, node.y + height);
  }

  const contentWidth = Math.max(maxX - minX, 1);
  const contentHeight = Math.max(maxY - minY, 1);
  const availableWidth = viewport.width - viewport.padding * 2;
  const availableHeight = viewport.height - viewport.padding * 2;
  const scale = Math.min(
    availableWidth / contentWidth,
    availableHeight / contentHeight,
  );
  const offsetX =
    viewport.padding + (availableWidth - contentWidth * scale) / 2 - minX * scale;
  const offsetY =
    viewport.padding + (availableHeight - contentHeight * scale) / 2 - minY * scale;

  return nodes.map((node) => {
    const scaledWidth = Math.max(node.width * scale, minimumNodeSize.width);
    const scaledHeight = Math.max(node.height * scale, minimumNodeSize.height);
    const centerX = offsetX + (node.x + node.width / 2) * scale;
    const centerY = offsetY + (node.y + node.height / 2) * scale;

    return {
      ...node,
      renderX: centerX - scaledWidth / 2,
      renderY: centerY - scaledHeight / 2,
      renderWidth: scaledWidth,
      renderHeight: scaledHeight,
      centerX,
      centerY,
    };
  });
}

function connectionPath(source: PreviewNodeLayout, target: PreviewNodeLayout) {
  const midpointX = (source.centerX + target.centerX) / 2;
  const midpointY = (source.centerY + target.centerY) / 2;
  const distance = Math.hypot(
    target.centerX - source.centerX,
    target.centerY - source.centerY,
  );
  const curve = Math.min(18, distance * 0.12);
  return `M ${source.centerX.toFixed(2)} ${source.centerY.toFixed(2)} Q ${midpointX.toFixed(2)} ${(midpointY - curve).toFixed(2)} ${target.centerX.toFixed(2)} ${target.centerY.toFixed(2)}`;
}

export function BoardPreview({
  nodes,
  connections,
}: {
  nodes: BoardPreviewNodeDto[];
  connections: BoardPreviewConnectionDto[];
}) {
  if (nodes.length === 0) {
    return (
      <div className="wk-board-preview wk-board-preview--empty" aria-hidden="true">
        <BrandMark />
        <span>A quiet space, ready to grow.</span>
      </div>
    );
  }

  const layout = layoutPreviewNodes(nodes);
  const nodesById = new Map(layout.map((node) => [node.id, node]));

  return (
    <div className="wk-board-preview" aria-hidden="true">
      <svg
        viewBox={`0 0 ${viewport.width} ${viewport.height}`}
        preserveAspectRatio="xMidYMid meet"
      >
        <g className="wk-board-preview-edges">
          {connections.map((connection) => {
            const source = nodesById.get(connection.sourceId);
            const target = nodesById.get(connection.targetId);
            if (!source || !target) return null;
            return (
              <path
                key={`${connection.sourceId}-${connection.targetId}-${connection.type}`}
                d={connectionPath(source, target)}
                data-kind={connection.type === 1 ? "prerequisite" : "related"}
              />
            );
          })}
        </g>
        <g className="wk-board-preview-nodes">
          {layout.map((node) => (
            <rect
              key={node.id}
              x={node.renderX}
              y={node.renderY}
              width={node.renderWidth}
              height={node.renderHeight}
              rx={Math.min(7, node.renderHeight * 0.18)}
              fill={node.color}
              data-kind={node.type === 1 ? "task-list" : "note"}
            />
          ))}
        </g>
      </svg>
    </div>
  );
}
