import { useCallback, useEffect, useState } from "react";
import { BookOpen, Images, NotebookPen } from "lucide-react";
import { Wordmark } from "../../components/brand/Wordmark";
import { ThemeControl } from "../../components/ui/ThemeControl";
import { LibraryPage } from "../library/LibraryPage";
import { JournalPage } from "../journal/JournalPage";
import { PicturesPage } from "../library/pictures/PicturesPage";

const links = [
  { path: "/dev/library", label: "Library", icon: BookOpen },
  { path: "/dev/journal", label: "Journal", icon: NotebookPen },
  { path: "/dev/pictures", label: "Pictures", icon: Images },
];

export function FuturePreviewApp({ path, navigate }: { path: string; navigate: (path: string) => void }) {
  useEffect(() => {
    const forced = new URLSearchParams(window.location.search).get("theme");
    if (forced !== "light" && forced !== "dark") return;
    const previous = document.documentElement.dataset.theme;
    const previousMotion = document.documentElement.style.getPropertyValue("--motion-fast");
    document.documentElement.dataset.theme = forced;
    document.documentElement.style.colorScheme = forced;
    document.documentElement.style.setProperty("--motion-fast", "0ms");
    return () => {
      if (previous) document.documentElement.dataset.theme = previous;
      document.documentElement.style.colorScheme = previous ?? "";
      if (previousMotion) document.documentElement.style.setProperty("--motion-fast", previousMotion);
      else document.documentElement.style.removeProperty("--motion-fast");
    };
  }, [path]);
  const page = path === "/dev/journal" ? <JournalPage /> : path === "/dev/pictures" ? <PicturesPage /> : <LibraryPage navigate={navigate} />;
  return <div className="wk-app-shell wk-preview-app"><a className="wk-skip-link" href="#wk-main-content">Skip to content</a>
    <aside className="wk-sidebar wk-preview-sidebar"><div className="wk-sidebar-header"><span className="wk-sidebar-brand"><Wordmark /></span></div><nav className="wk-sidebar-nav" aria-label="Design preview navigation"><p className="wk-preview-nav-caption">Future surfaces</p>{links.map(({ path: target, label, icon: Icon }) => <button key={target} className={`wk-shell-link${path === target ? " wk-shell-link--active" : ""}`} aria-current={path === target ? "page" : undefined} onClick={() => navigate(target)}><Icon size={19} aria-hidden="true" /><span>{label}</span></button>)}</nav><div className="wk-preview-sidebar-foot"><ThemeControl /><p>Development preview<br />Sample content only</p></div></aside>
    <header className="wk-mobile-header"><span className="wk-mobile-brand"><Wordmark /></span><span className="wk-preview-badge">Preview</span></header>
    <main tabIndex={-1} className="wk-shell-main" id="wk-main-content"><div className="wk-feature-stage">{page}</div></main>
    <nav className="wk-mobile-nav wk-preview-mobile-nav" aria-label="Design preview navigation">{links.map(({ path: target, label, icon: Icon }) => <button key={target} className={`wk-mobile-nav-item${path === target ? " wk-mobile-nav-item--active" : ""}`} aria-current={path === target ? "page" : undefined} onClick={() => navigate(target)}><Icon size={20} aria-hidden="true" /><span>{label}</span></button>)}</nav>
  </div>;
}

export function FuturePreviewEntry() {
  const [path, setPath] = useState(window.location.pathname);
  const navigate = useCallback((next: string) => {
    window.history.pushState(null, "", next);
    setPath(next);
  }, []);
  useEffect(() => {
    const onPopState = () => setPath(window.location.pathname);
    window.addEventListener("popstate", onPopState);
    return () => window.removeEventListener("popstate", onPopState);
  }, []);
  return <FuturePreviewApp path={path} navigate={navigate} />;
}
