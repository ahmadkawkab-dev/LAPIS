import { useState } from "react";
import { Bookmark, ChevronRight, Grid2X2, LayoutList, SlidersHorizontal, X } from "lucide-react";
import { Button, IconButton } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { PreviewPage, PreviewScenario, PreviewSearch, PreviewState, PreviewTabs } from "../future/PreviewUI";
import { savedItems, type SavedItem } from "../future/fixtures";

type LibraryTab = "Everything" | "Videos" | "Shorts" | "Posts" | "Pictures" | "Albums" | "Collections";
const tabs: LibraryTab[] = ["Everything", "Videos", "Shorts", "Posts", "Pictures", "Albums", "Collections"];
const collections = ["Creative practice", "Places", "Recipes", "Inspiration"];

function MediaCard({ item, mode, onOpen }: { item: SavedItem; mode: "grid" | "list"; onOpen: () => void }) {
  return <button className={`wk-media-card wk-media-card--${mode}`} onClick={onOpen} aria-label={`View sample detail for ${item.title}`}>
    <span className={`wk-media-thumb wk-media-thumb--${item.type.toLowerCase()}`}><img src={item.image} alt={item.alt} />{item.duration && <span className="wk-duration">{item.duration}</span>}</span>
    <span className="wk-media-copy"><span className="wk-media-source">{item.source} · {item.type}</span><strong>{item.title}</strong><span>{item.creator}</span><small>Saved {item.savedAt}</small></span>
  </button>;
}

function CollectionCard({ name, onOpen }: { name: string; onOpen: () => void }) {
  const items = savedItems.filter((item) => item.collection === name);
  return <button className="wk-collection-card" onClick={onOpen}><span className="wk-collection-mosaic">{items.slice(0, 3).map((item) => <img key={item.id} src={item.image} alt="" />)}</span><strong>{name}</strong><span>{items.length} sample items <ChevronRight size={16} aria-hidden="true" /></span></button>;
}

export function LibraryPage({ navigate }: { navigate: (path: string) => void }) {
  const [tab, setTab] = useState<LibraryTab>("Everything");
  const [mode, setMode] = useState<"grid" | "list">("grid");
  const [query, setQuery] = useState("");
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [source, setSource] = useState("All sources");
  const [selected, setSelected] = useState<SavedItem | null>(() => savedItems.find((item) => item.id === new URLSearchParams(window.location.search).get("detail")) ?? null);
  const [collection, setCollection] = useState<string | null>(null);
  const scenario = new URLSearchParams(window.location.search).get("state");
  const visible = savedItems.filter((item) => {
    const category = tab === "Everything" || tab === "Collections" || tab === "Pictures" || tab === "Albums" ||
      tab === "Videos" && item.type === "Video" || tab === "Shorts" && ["Short", "Reel"].includes(item.type) || tab === "Posts" && ["Post", "Article"].includes(item.type);
    const term = query.toLocaleLowerCase();
    return category && (!collection || item.collection === collection) && (source === "All sources" || item.source === source) &&
      [item.title, item.creator, item.source, item.type, item.collection, ...item.tags].some((value) => value.toLocaleLowerCase().includes(term));
  });
  if (scenario === "loading" || scenario === "empty" || scenario === "error") return <PreviewPage title="Library" description="A considered home for the things you keep."><PreviewScenario area="library" /></PreviewPage>;
  return <PreviewPage title="Library" description="A considered home for the things you keep.">
    <PreviewTabs tabs={tabs} value={tab} onChange={(next) => { setTab(next); setCollection(null); setSelected(null); }} label="Library views" panelId="library-preview-panel" />
    <div id="library-preview-panel" role="tabpanel" aria-labelledby={`library-preview-panel-${tab.toLowerCase()}`}>
    {tab === "Pictures" || tab === "Albums" ? <div className="wk-library-branch"><Bookmark size={25} aria-hidden="true" /><div><h2>{tab} belongs here</h2><p>Pictures and albums are part of your Library. Explore the dedicated visual preview.</p></div><Button variant="secondary" onClick={() => navigate("/dev/pictures")}>Open Pictures preview</Button></div> : <>
      <div className="wk-future-toolbar"><PreviewSearch label="Search sample library" value={query} onChange={setQuery} placeholder="Search titles, creators, sources…" /><div className="wk-future-toolbar-actions"><Button variant="secondary" onClick={() => setFiltersOpen(true)}><SlidersHorizontal size={17} aria-hidden="true" /> Filters{source !== "All sources" ? " · 1" : ""}</Button><div className="wk-view-switch" aria-label="View mode"><IconButton label="Grid view" aria-pressed={mode === "grid"} onClick={() => setMode("grid")}><Grid2X2 size={18} aria-hidden="true" /></IconButton><IconButton label="List view" aria-pressed={mode === "list"} onClick={() => setMode("list")}><LayoutList size={18} aria-hidden="true" /></IconButton></div></div></div>
      {collection && <div className="wk-active-filter"><span>Collection: {collection}</span><button onClick={() => setCollection(null)} aria-label="Clear collection filter"><X size={17} /></button></div>}
      {tab === "Collections" ? <section className="wk-future-section"><div className="wk-section-heading"><h2>Collections</h2><span>Sample organization</span></div><div className="wk-collection-grid">{collections.map((name) => <CollectionCard key={name} name={name} onOpen={() => { setCollection(name); setTab("Everything"); }} />)}</div></section> : <section className="wk-future-section"><div className="wk-section-heading"><h2>{tab === "Everything" ? "Recently saved" : tab}</h2><span>{visible.length} sample items</span></div>{visible.length ? <div className={`wk-media-grid wk-media-grid--${mode}`}>{visible.map((item) => <MediaCard key={item.id} item={item} mode={mode} onOpen={() => setSelected(item)} />)}</div> : <PreviewState kind="empty" title="Nothing in this sample view">Try another source or search term.</PreviewState>}
        {tab === "Everything" && !query && !collection && <div className="wk-library-collections"><div className="wk-section-heading"><h2>Collections</h2><button onClick={() => setTab("Collections")}>See all <ChevronRight size={16} aria-hidden="true" /></button></div><div className="wk-collection-grid">{collections.slice(0, 3).map((name) => <CollectionCard key={name} name={name} onOpen={() => setCollection(name)} />)}</div></div>}</section>}
    </>}
    </div>
    {filtersOpen && <Dialog title="Filters" onClose={() => setFiltersOpen(false)} className="wk-preview-dialog wk-preview-dialog--filter"><section className="wk-filter-sheet"><div className="wk-sheet-heading"><span className="wk-future-eyebrow">Sample library filters</span><IconButton label="Close filters" onClick={() => setFiltersOpen(false)}><X size={18} /></IconButton></div><p>Preview filters apply to sample items only.</p><fieldset><legend>Source</legend>{["All sources", "Instagram", "YouTube", "Web"].map((value) => <label key={value}><input type="radio" name="preview-source" checked={source === value} onChange={() => setSource(value)} /> {value}</label>)}</fieldset><Button onClick={() => setFiltersOpen(false)}>Show sample items</Button></section></Dialog>}
    {selected && <Dialog title="Saved item sample detail" onClose={() => setSelected(null)} className="wk-preview-dialog wk-preview-dialog--detail"><aside className="wk-detail-dock"><div className="wk-sheet-heading"><span className="wk-future-eyebrow">Sample detail</span><IconButton label="Close detail" onClick={() => setSelected(null)}><X size={18} /></IconButton></div><img src={selected.image} alt={selected.alt} /><span className="wk-media-source">{selected.source} · {selected.type}</span><h2>{selected.title}</h2><p>{selected.creator}</p><dl><div><dt>Saved</dt><dd>{selected.savedAt}</dd></div><div><dt>Collection</dt><dd>{selected.collection}</dd></div><div><dt>Tags</dt><dd>{selected.tags.join(", ")}</dd></div><div><dt>Original link</dt><dd>Available when saved content is implemented</dd></div></dl><p className="wk-preview-note">This is fixture content. Saving, notes and external links are not available.</p></aside></Dialog>}
  </PreviewPage>;
}
