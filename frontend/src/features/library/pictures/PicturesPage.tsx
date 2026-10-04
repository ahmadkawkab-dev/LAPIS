import { useState } from "react";
import { Check, ChevronLeft, ChevronRight, Heart, X } from "lucide-react";
import { Button, IconButton } from "../../../components/ui/Button";
import { Dialog } from "../../../components/ui/Dialog";
import { PreviewPage, PreviewScenario, PreviewSearch, PreviewState, PreviewTabs } from "../../future/PreviewUI";
import { pictures, type Picture } from "../../future/fixtures";

type View = "All" | "Albums" | "Favorites";
const views: View[] = ["All", "Albums", "Favorites"];

function PictureTile({ picture, selected, selecting, onClick }: { picture: Picture; selected: boolean; selecting: boolean; onClick: () => void }) {
  return <button className={`wk-picture-tile${selected ? " wk-picture-tile--selected" : ""}`} onClick={onClick} aria-label={`${selecting ? selected ? "Deselect" : "Select" : "Open"} sample picture: ${picture.alt}`} aria-pressed={selecting ? selected : undefined}><img src={picture.src} alt={picture.alt} />{selecting && <span className="wk-picture-check" aria-hidden="true">{selected && <Check size={17} aria-hidden="true" />}</span>}</button>;
}

function AlbumCard({ name, onOpen }: { name: string; onOpen: () => void }) {
  const images = pictures.filter((picture) => picture.album === name);
  return <button className="wk-album-card" onClick={onOpen}><span className="wk-album-mosaic">{images.slice(0, 4).map((picture) => <img key={picture.id} src={picture.src} alt="" />)}</span><strong>{name}</strong><small>{images.length} sample pictures</small></button>;
}

export function PicturesPage() {
  const [view, setView] = useState<View>("All");
  const [density, setDensity] = useState<"comfortable" | "compact">("comfortable");
  const [query, setQuery] = useState("");
  const [album, setAlbum] = useState<string | null>(null);
  const [selecting, setSelecting] = useState(false);
  const [selected, setSelected] = useState<string[]>([]);
  const [viewer, setViewer] = useState<Picture | null>(() => pictures.find((picture) => picture.id === new URLSearchParams(window.location.search).get("detail")) ?? null);
  const visible = pictures.filter((picture) => (view !== "Favorites" || picture.favorite) && (!album || picture.album === album) && `${picture.alt} ${picture.caption} ${picture.album}`.toLowerCase().includes(query.toLowerCase()));
  const toggleSelected = (id: string) => setSelected((current) => current.includes(id) ? current.filter((value) => value !== id) : [...current, id]);
  const showViewer = (index: number) => setViewer(visible[(index + visible.length) % visible.length]);
  const scenario = new URLSearchParams(window.location.search).get("state");
  if (scenario === "loading" || scenario === "empty" || scenario === "error") return <PreviewPage title="Pictures" description="A visual home for the images you keep close."><PreviewScenario area="pictures" /></PreviewPage>;
  return <PreviewPage title="Pictures" description="A visual home for the images you keep close.">
    <div className="wk-library-breadcrumb">Library <ChevronRight size={15} aria-hidden="true" /> Pictures</div>
    <PreviewTabs tabs={views} value={view} onChange={(next) => { setView(next); setAlbum(null); setSelected([]); setSelecting(false); }} label="Pictures views" panelId="pictures-preview-panel" />
    <div id="pictures-preview-panel" role="tabpanel" aria-labelledby={`pictures-preview-panel-${view.toLowerCase()}`}>
    <div className="wk-future-toolbar"><PreviewSearch label="Search sample pictures" value={query} onChange={setQuery} placeholder="Search pictures and albums…" /><div className="wk-future-toolbar-actions"><div className="wk-density-switch" aria-label="Picture density"><button aria-pressed={density === "comfortable"} onClick={() => setDensity("comfortable")}>Comfortable</button><button aria-pressed={density === "compact"} onClick={() => setDensity("compact")}>Compact</button></div><Button variant="secondary" onClick={() => { setSelecting(!selecting); setSelected([]); }}>{selecting ? "Done" : "Select"}</Button></div></div>
    {selecting && <div className="wk-selection-bar" role="status"><strong>{selected.length} selected</strong><span>Selection preview</span><div><Button variant="quiet" disabled>Add to album</Button><Button variant="quiet" disabled>Favorite</Button><Button variant="quiet" disabled>Delete</Button></div></div>}
    {view === "Albums" && !album ? <section className="wk-future-section"><div className="wk-section-heading"><h2>Albums</h2><span>Curated sample groups</span></div><div className="wk-album-grid">{["Lebanon", "Little things"].map((name) => <AlbumCard key={name} name={name} onOpen={() => { setAlbum(name); setView("All"); }} />)}</div></section> : <>{album && <div className="wk-active-filter"><span>Album: {album}</span><button onClick={() => setAlbum(null)} aria-label="Clear album filter"><X size={17} /></button></div>}{["September 23", "September 20"].map((date) => { const group = visible.filter((picture) => picture.date === date); return group.length ? <section className="wk-future-section wk-picture-section" key={date}><div className="wk-section-heading"><h2>{date}</h2><span>{group.length} sample pictures</span></div><div className={`wk-picture-grid wk-picture-grid--${density}`}>{group.map((picture) => <PictureTile key={picture.id} picture={picture} selecting={selecting} selected={selected.includes(picture.id)} onClick={() => selecting ? toggleSelected(picture.id) : setViewer(picture)} />)}</div></section> : null; })}{visible.length === 0 && <PreviewState kind="empty" title="No sample pictures found">Try another search or view.</PreviewState>}</>}
    </div>
    {viewer && <Dialog title="Sample picture viewer" onClose={() => setViewer(null)} className="wk-preview-dialog wk-preview-dialog--viewer"><div className="wk-picture-viewer"><div className="wk-picture-viewer-bar"><span>Sample picture · {visible.indexOf(viewer) + 1} of {visible.length}</span><IconButton label="Close picture" onClick={() => setViewer(null)}><X size={20} /></IconButton></div><div className="wk-picture-viewer-main"><IconButton label="Previous picture" onClick={() => showViewer(visible.indexOf(viewer) - 1)}><ChevronLeft size={22} aria-hidden="true" /></IconButton><img src={viewer.src} alt={viewer.alt} /><IconButton label="Next picture" onClick={() => showViewer(visible.indexOf(viewer) + 1)}><ChevronRight size={22} aria-hidden="true" /></IconButton></div><aside className="wk-picture-viewer-meta"><h2>{viewer.caption}</h2><p>{viewer.date} · {viewer.album}</p><p>{viewer.dimensions}</p><span>{viewer.favorite && <Heart size={15} fill="currentColor" aria-hidden="true" />} {viewer.favorite ? "Favorite · " : ""}Sample metadata</span></aside></div></Dialog>}
  </PreviewPage>;
}
