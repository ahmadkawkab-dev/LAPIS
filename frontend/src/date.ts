export function dateInZone(now: Date, zone: string): string {
  const parts = new Intl.DateTimeFormat("en-US", { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit" }).formatToParts(now);
  const value = (kind: string) => parts.find((part) => part.type === kind)!.value;
  return `${value("year")}-${value("month")}-${value("day")}`;
}

export function addDays(date: string, days: number): string {
  const value = new Date(`${date}T12:00:00Z`);
  value.setUTCDate(value.getUTCDate() + days);
  return value.toISOString().slice(0, 10);
}
