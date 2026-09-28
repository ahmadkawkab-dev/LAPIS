import { useId, useState, type CSSProperties, type PointerEvent } from "react";
import { collaboratorInitials } from "../../features/boards/collaboratorIdentity";
import { identityLabel, type Identity } from "../../features/profile/identity";
export { identityLabel } from "../../features/profile/identity";

export function Avatar({
  identity,
  size = "medium",
  className = "",
  style,
  tooltip = false,
}: {
  identity: Identity;
  size?: "small" | "medium" | "large";
  className?: string;
  style?: CSSProperties;
  tooltip?: boolean;
}) {
  const label = identityLabel(identity);
  const fallback = collaboratorInitials(label);
  const id = useId();
  const [failedUrl, setFailedUrl] = useState<string | null>(null);
  const [showTooltip, setShowTooltip] = useState(false);
  const [position, setPosition] = useState<CSSProperties>({});
  const image = identity.profileImageUrl && failedUrl !== identity.profileImageUrl;

  function open(target: HTMLElement) {
    const rect = target.getBoundingClientRect();
    const width = Math.min(280, Math.max(120, label.length * 8 + 24));
    const left = Math.max(8, Math.min(window.innerWidth - width - 8, rect.left + rect.width / 2 - width / 2));
    const top = rect.bottom + 8 + 36 <= window.innerHeight ? rect.bottom + 8 : Math.max(8, rect.top - 36);
    setPosition({ left, top });
    setShowTooltip(true);
  }

  return (
    <span
      className={`wk-avatar wk-avatar--${size} ${className}`.trim()}
      style={style}
      role={tooltip ? undefined : "img"}
      aria-label={label}
      aria-describedby={tooltip && showTooltip ? id : undefined}
      tabIndex={tooltip ? 0 : undefined}
      onPointerEnter={(event: PointerEvent<HTMLSpanElement>) => tooltip && open(event.currentTarget)}
      onPointerLeave={() => setShowTooltip(false)}
      onFocus={(event) => tooltip && open(event.currentTarget)}
      onBlur={() => setShowTooltip(false)}
      onKeyDown={(event) => {
        if (event.key === "Escape") setShowTooltip(false);
      }}
    >
      <span className="wk-avatar-content">
        {image ? <img src={identity.profileImageUrl!} alt="" loading="lazy" onError={() => setFailedUrl(identity.profileImageUrl!)} /> : fallback}
      </span>
      {tooltip && showTooltip && <span className="wk-avatar-tooltip" id={id} role="tooltip" style={position}>{label}</span>}
    </span>
  );
}
