import { useState } from "react";
import { CalendarDays, ChevronLeft, ChevronRight, LockKeyhole, X } from "lucide-react";
import { IconButton } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { PreviewPage, PreviewScenario, PreviewSearch, PreviewState, PreviewTabs } from "../future/PreviewUI";

type View = "Today" | "Entries" | "Calendar";
const views: View[] = ["Today", "Entries", "Calendar"];
const entries = [
  { day: 22, date: "SEP 22", title: "A quiet afternoon", preview: "Spent some time thinking about all the small moments that made this week feel full.", body: "Spent some time thinking about all the small moments that made this week feel full. A long walk, an unhurried meal, and a conversation I want to remember.", mood: "Calm" },
  { day: 20, date: "SEP 20", title: "", preview: "Finally found a little time to sit with the ideas I had been carrying around.", body: "Finally found a little time to sit with the ideas I had been carrying around. It felt good to write them down without needing to make anything of them.", mood: "Reflective" },
  { day: 18, date: "SEP 18", title: "The first rain", preview: "The windows stayed open and the air changed all at once.", body: "The windows stayed open and the air changed all at once. I made tea and watched the street slow down.", mood: "Grateful" },
];

function EntryList({ onOpen, query = "" }: { onOpen: (index: number) => void; query?: string }) {
  const found = entries.map((entry, index) => ({ entry, index })).filter(({ entry }) => `${entry.title} ${entry.preview}`.toLowerCase().includes(query.toLowerCase()));
  return found.length ? <div className="wk-entry-list">{found.map(({ entry, index }) => <button key={entry.date} className="wk-entry-row" onClick={() => onOpen(index)}><span>{entry.date}</span><span><strong>{entry.title || "Untitled entry"}</strong><small>{entry.preview}</small></span><ChevronRight size={18} aria-hidden="true" /></button>)}</div> : <PreviewState kind="empty" title="No sample entries found">Try a different word.</PreviewState>;
}

function Calendar({ onOpen }: { onOpen: (index: number) => void }) {
  const [offset, setOffset] = useState(0);
  const [selectedDay, setSelectedDay] = useState(23);
  const month = new Date(2026, 8 + offset, 1);
  const monthLabel = new Intl.DateTimeFormat("en", { month: "long", year: "numeric" }).format(month);
  const pad = month.getDay();
  const count = new Date(month.getFullYear(), month.getMonth() + 1, 0).getDate();
  const active = offset === 0;
  const selectedEntryIndex = active ? entries.findIndex((entry) => entry.day === selectedDay) : -1;
  const changeMonth = (delta: number) => { setOffset((value) => value + delta); setSelectedDay(1); };
  return <div className="wk-calendar-layout">
    <section className="wk-calendar"><div className="wk-calendar-head"><h2>{monthLabel}</h2><div><IconButton label="Previous month" onClick={() => changeMonth(-1)}><ChevronLeft size={18} aria-hidden="true" /></IconButton><IconButton label="Next month" onClick={() => changeMonth(1)}><ChevronRight size={18} aria-hidden="true" /></IconButton></div></div>
      <div className="wk-calendar-grid" role="group" aria-label={monthLabel}>
        {["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"].map((day) => <span className="wk-calendar-weekday" key={day}>{day}</span>)}
        {Array.from({ length: pad }, (_, index) => <span key={`pad-${index}`} />)}
        {Array.from({ length: count }, (_, index) => {
          const day = index + 1;
          const hasEntry = active && entries.some((entry) => entry.day === day);
          return <button key={day} type="button" aria-pressed={selectedDay === day}
            aria-label={`${monthLabel} ${day}${hasEntry ? ", sample entry" : ", no sample entry"}`}
            onClick={() => setSelectedDay(day)} className={`${day === 23 && active ? "wk-calendar-today" : ""} ${hasEntry ? "wk-calendar-has-entry" : ""}`}>
            <span>{day}</span>{hasEntry && <span className="wk-calendar-dot" aria-hidden="true" />}
          </button>;
        })}
      </div>
    </section>
    <aside className="wk-calendar-context"><CalendarDays size={23} aria-hidden="true" /><h3>{monthLabel} {selectedDay}</h3>
      {selectedEntryIndex >= 0 ? <button className="wk-calendar-entry" onClick={() => onOpen(selectedEntryIndex)}><strong>{entries[selectedEntryIndex].title || "Untitled entry"}</strong><span>{entries[selectedEntryIndex].preview}</span><ChevronRight size={18} aria-hidden="true" /></button> : <p>No sample entry for this day. Dates with entries have a dot.</p>}
    </aside>
  </div>;
}

export function JournalPage() {
  const [view, setView] = useState<View>(() => {
    const requested = new URLSearchParams(window.location.search).get("view");
    return views.find((value) => value.toLowerCase() === requested?.toLowerCase()) ?? "Today";
  });
  const [prompt, setPrompt] = useState(true);
  const [query, setQuery] = useState("");
  const [entryIndex, setEntryIndex] = useState<number | null>(() => {
    const requested = Number(new URLSearchParams(window.location.search).get("entry"));
    return Number.isInteger(requested) && requested >= 0 && requested < entries.length && new URLSearchParams(window.location.search).has("entry") ? requested : null;
  });
  const chosen = entryIndex === null ? null : entries[entryIndex];
  const scenario = new URLSearchParams(window.location.search).get("state");
  if (scenario === "loading" || scenario === "empty" || scenario === "error") return <PreviewPage title="Journal" description="A quieter place for your days, thoughts and memories."><PreviewScenario area="journal" /></PreviewPage>;
  return <PreviewPage title="Journal" description="A quieter place for your days, thoughts and memories.">
    <PreviewTabs tabs={views} value={view} onChange={setView} label="Journal views" panelId="journal-preview-panel" />
    <div id="journal-preview-panel" role="tabpanel" aria-labelledby={`journal-preview-panel-${view.toLowerCase()}`}>
    {view === "Today" && <div className="wk-journal-today"><div className="wk-journal-date"><span>Wednesday</span><h2>September 23</h2><p><LockKeyhole size={15} aria-hidden="true" /> Planned as a private space</p></div><div className="wk-writing-shell"><span className="wk-future-eyebrow">Today’s page · visual preview</span><h3>How was today?</h3><p>Start writing when Journal becomes available. This preview shows the reading space, rhythm and optional entry details.</p><div className="wk-writing-lines" aria-hidden="true"><span /><span /><span /><span /></div><span className="wk-writing-footnote">Date and writing will take the lead. Title, mood and photos will be optional.</span></div>{prompt && <div className="wk-journal-prompt"><div><small>A thought to begin with</small><p>What do you want to remember from today?</p></div><IconButton label="Dismiss sample prompt" onClick={() => setPrompt(false)}><X size={17} /></IconButton></div>}<section className="wk-future-section"><div className="wk-section-heading"><h2>Recent entries</h2><button onClick={() => setView("Entries")}>All entries <ChevronRight size={16} aria-hidden="true" /></button></div><EntryList onOpen={setEntryIndex} /></section></div>}
    {view === "Entries" && <div className="wk-journal-entries"><PreviewSearch label="Search sample entries" value={query} onChange={setQuery} placeholder="Search your writing…" /><div className="wk-section-heading"><h2>September 2026</h2><span>Sample entries</span></div><EntryList query={query} onOpen={setEntryIndex} /></div>}
    {view === "Calendar" && <Calendar onOpen={setEntryIndex} />}
    </div>
    {chosen && <Dialog title="Sample journal entry" onClose={() => setEntryIndex(null)} className="wk-preview-dialog wk-preview-dialog--reader"><article className="wk-journal-reader"><div className="wk-sheet-heading"><span className="wk-future-eyebrow">Sample entry · {chosen.date}</span><IconButton label="Close entry" onClick={() => setEntryIndex(null)}><X size={18} /></IconButton></div><h2>{chosen.title || "September 20"}</h2><p>{chosen.body}</p><footer>{chosen.mood} · Sample content</footer></article></Dialog>}
  </PreviewPage>;
}
