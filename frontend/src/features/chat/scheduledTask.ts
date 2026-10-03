import type { ScheduledTaskDraft, ScheduledTaskWrite } from "./types.ts";

export const emptyScheduledTaskDraft = (): ScheduledTaskDraft => ({
  title: "", description: "", localStart: "", localEnd: "",
  timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC",
  startOffsetMinutes: "", endOffsetMinutes: "",
});

type Choice = { offsetMinutes: number; utc: number };
export type LocalTimeChoices = { state: "valid" | "gap" | "invalid-zone" | "invalid-time"; choices: Choice[] };

function partsAt(formatter: Intl.DateTimeFormat, instant: number) {
  const parts = Object.fromEntries(formatter.formatToParts(new Date(instant))
    .filter(part => part.type !== "literal").map(part => [part.type, Number(part.value)]));
  return `${String(parts.year).padStart(4, "0")}-${String(parts.month).padStart(2, "0")}-${String(parts.day).padStart(2, "0")}T${String(parts.hour).padStart(2, "0")}:${String(parts.minute).padStart(2, "0")}`;
}

/** Resolve an offset-free minute against IANA zone rules without using the device zone. */
export function localTimeChoices(local: string, zone: string): LocalTimeChoices {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(local)) return { state: "invalid-time", choices: [] };
  const [year, month, day, hour, minute] = [local.slice(0, 4), local.slice(5, 7), local.slice(8, 10),
    local.slice(11, 13), local.slice(14, 16)].map(Number);
  const wallDate = new Date(0);
  wallDate.setUTCFullYear(year, month - 1, day);
  wallDate.setUTCHours(hour, minute, 0, 0);
  const wall = wallDate.getTime();
  if (!Number.isFinite(wall) || new Date(wall).toISOString().slice(0, 16) !== local)
    return { state: "invalid-time", choices: [] };
  let formatter: Intl.DateTimeFormat;
  try { formatter = new Intl.DateTimeFormat("en-US", { timeZone: zone, calendar: "gregory", numberingSystem: "latn",
    year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hourCycle: "h23" }); }
  catch (cause) { if (cause instanceof RangeError) return { state: "invalid-zone", choices: [] }; throw cause; }
  const offsets = new Set<number>();
  for (let hourOffset = -15; hourOffset <= 15; hourOffset++) {
    const instant = wall + hourOffset * 3_600_000;
    const represented = partsAt(formatter, instant);
    const localMs = Date.parse(`${represented}:00Z`);
    offsets.add(Math.round((localMs - instant) / 60_000));
  }
  const choices = [...offsets].map(offsetMinutes => ({ offsetMinutes, utc: wall - offsetMinutes * 60_000 }))
    .filter(choice => partsAt(formatter, choice.utc) === local)
    .sort((left, right) => left.utc - right.utc);
  return { state: choices.length ? "valid" : "gap", choices };
}

export function offsetLabel(minutes: number) {
  const sign = minutes < 0 ? "−" : "+";
  const absolute = Math.abs(minutes);
  return `UTC${sign}${String(Math.floor(absolute / 60)).padStart(2, "0")}:${String(absolute % 60).padStart(2, "0")}`;
}

export function scheduledTaskSubmission(draft: ScheduledTaskDraft): { request?: ScheduledTaskWrite; error?: string } {
  const title = draft.title.trim();
  if (!title || title.length > 200)
    return { error: "Add a title of up to 200 characters." };
  if (draft.description.length > 4000) return { error: "Keep the description under 4,000 characters." };
  if (!draft.timeZoneId || draft.timeZoneId.length > 100 || draft.timeZoneId !== draft.timeZoneId.trim())
    return { error: "Choose a valid IANA time zone." };
  if (!draft.localStart) return { error: "Choose a start date and time." };
  const start = localTimeChoices(draft.localStart, draft.timeZoneId);
  if (start.state === "invalid-zone") return { error: "Choose a valid IANA time zone." };
  if (start.state === "gap") return { error: "The start time does not exist in that time zone." };
  if (start.state !== "valid") return { error: "Choose a valid start date and time." };
  const chosenStart = selectedChoice(start, draft.startOffsetMinutes);
  if (!chosenStart) return { error: "Choose which occurrence of the start time you mean." };
  let chosenEnd: Choice | undefined;
  let ambiguousEnd = false;
  if (draft.localEnd) {
    const end = localTimeChoices(draft.localEnd, draft.timeZoneId);
    if (end.state === "gap") return { error: "The end time does not exist in that time zone." };
    if (end.state !== "valid") return { error: "Choose a valid end date and time." };
    chosenEnd = selectedChoice(end, draft.endOffsetMinutes);
    if (!chosenEnd) return { error: "Choose which occurrence of the end time you mean." };
    if (chosenEnd.utc <= chosenStart.utc) return { error: "The end must be after the start." };
    ambiguousEnd = end.choices.length > 1;
  }
  return { request: { title, description: draft.description.trim() || null, localStart: draft.localStart,
    localEnd: draft.localEnd || null, timeZoneId: draft.timeZoneId,
    startOffsetMinutes: start.choices.length > 1 ? chosenStart.offsetMinutes : null,
    endOffsetMinutes: chosenEnd && ambiguousEnd ? chosenEnd.offsetMinutes : null } };
}

function selectedChoice(result: LocalTimeChoices, selected: string): Choice | undefined {
  if (result.choices.length === 1) return result.choices[0];
  return result.choices.find(choice => String(choice.offsetMinutes) === selected);
}

export function scheduledTaskLabel(task: { startsAtUtc: string; endsAtUtc: string | null;
  timeZoneId: string; originalOffsetMinutes: number }, viewerZone?: string) {
  const formatter = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short", ...(viewerZone ? { timeZone: viewerZone } : {}) });
  const start = new Date(task.startsAtUtc);
  const end = task.endsAtUtc ? new Date(task.endsAtUtc) : null;
  return { when: end ? formatter.formatRange(start, end) : formatter.format(start),
    authored: `${task.timeZoneId} (${offsetLabel(task.originalOffsetMinutes)})` };
}
