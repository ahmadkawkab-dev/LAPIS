import { Search, X } from "lucide-react";
import { useId, type ReactNode } from "react";
import { Button } from "../../components/ui/Button";

export function PreviewPage({ title, description, children }: { title: string; description: string; children: ReactNode }) {
  return <div className="wk-future-page">
    <header className="wk-future-header">
      <div><p className="wk-future-eyebrow">Design preview · sample content</p><h1>{title}</h1><p>{description}</p></div>
      <span className="wk-preview-badge">Preview only</span>
    </header>
    {children}
  </div>;
}

export function PreviewTabs<T extends string>({ tabs, value, onChange, label, panelId }: {
  tabs: readonly T[]; value: T; onChange: (value: T) => void; label: string; panelId: string;
}) {
  return <div className="wk-future-tabs" role="tablist" aria-label={label}>
    {tabs.map((tab) => <button key={tab} type="button" role="tab" aria-selected={tab === value}
      id={`${panelId}-${tab.toLowerCase()}`} aria-controls={panelId}
      tabIndex={tab === value ? 0 : -1} onClick={() => onChange(tab)}
      onKeyDown={(event) => {
        const next = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
        if (next) { event.preventDefault(); const index = (tabs.indexOf(value) + next + tabs.length) % tabs.length; onChange(tabs[index]); event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>("[role=tab]")[index]?.focus(); }
      }}>{tab}</button>)}
  </div>;
}

export function PreviewSearch({ label, value, onChange, placeholder }: { label: string; value: string; onChange: (value: string) => void; placeholder: string }) {
  const id = useId();
  return <div className="wk-preview-search">
    <label htmlFor={id}>{label}</label>
    <div><Search size={18} aria-hidden="true" /><input id={id} type="search" value={value} placeholder={placeholder} onChange={(event) => onChange(event.target.value)} />
      {value && <button aria-label={`Clear ${label.toLowerCase()}`} onClick={() => onChange("")}><X size={17} /></button>}</div>
  </div>;
}

export function SoonPage({ area }: { area: "Library" | "Journal" | "Pictures" }) {
  const copy = {
    Library: ["Your library will live here.", "Save videos, posts, articles and things you want to return to without losing them in different apps."],
    Journal: ["A quieter place for your days.", "Journal will give you a private space for daily writing, memories and reflection."],
    Pictures: ["Your visual library will live here.", "Keep the pictures you want to remember, organize and return to."],
  }[area];
  return <div className="wk-soon-page"><div className="wk-soon-content"><p className="wk-future-eyebrow">{area === "Pictures" ? "Library / Pictures" : area}</p><h1>{copy[0]}</h1><p>{copy[1]}</p><span className="wk-soon-label">Coming soon</span></div></div>;
}

export function PreviewState({ kind, title, children }: { kind: "empty" | "error"; title: string; children: ReactNode }) {
  return <div className={`wk-preview-state wk-preview-state--${kind}`}><h3>{title}</h3><p>{children}</p>{kind === "error" && <Button variant="secondary" disabled>Try again</Button>}</div>;
}

export function PreviewScenario({ area }: { area: "library" | "journal" | "pictures" }) {
  const state = new URLSearchParams(window.location.search).get("state");
  if (state === "error") return <PreviewState kind="error" title={`We couldn't load your ${area}.`}>A future request error would appear here. Retry becomes available when the feature has a data source.</PreviewState>;
  if (state === "empty") return <PreviewState kind="empty" title={area === "journal" ? "Your first page begins here" : `Your ${area} will live here.`}>This is the planned empty state, with no sample content presented as yours.</PreviewState>;
  if (state !== "loading") return null;
  return <div className={`wk-preview-skeleton wk-preview-skeleton--${area}`} role="status" aria-label={`Loading ${area} preview`}>
    {Array.from({ length: area === "pictures" ? 12 : 4 }, (_, index) => <div className="wk-skeleton-unit" key={index}><span /><span /><span /></div>)}
  </div>;
}
