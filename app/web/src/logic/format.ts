const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "long" });
const time = new Intl.DateTimeFormat(undefined, { timeStyle: "medium" });
const count = new Intl.NumberFormat();

export function formatDateTime(iso: string | number | null | undefined): string {
  if (iso === null || iso === undefined || iso === "") return "Not recorded";
  const value = new Date(iso);
  return Number.isNaN(value.getTime()) ? "Not recorded" : dateTime.format(value);
}

export function formatTime(iso: string | number | null | undefined): string {
  if (iso === null || iso === undefined || iso === "") return "never";
  const value = new Date(iso);
  return Number.isNaN(value.getTime()) ? "never" : time.format(value);
}

export function newestTime(a: number | null, b: number | null): number | null {
  if (a === null) return b;
  return b === null ? a : Math.max(a, b);
}

/** While a read is on offer the page is only as new as its last read, whatever the socket pushed since. */
export function shownTime(missed: boolean, read: number | null, pushed: number | null): number | null {
  return missed && read !== null ? read : newestTime(read, pushed);
}

export function formatCount(value: number | null | undefined): string {
  return value === null || value === undefined ? "0" : count.format(value);
}

export function counted(value: number, noun: string): string {
  return `${count.format(value)} ${noun}${value === 1 ? "" : "s"}`;
}

export function formatAge(ms: number): string {
  const seconds = Math.max(0, Math.round(ms / 1000));
  if (seconds < 60) return `${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} min ${seconds % 60} s`;
  return `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

export function formatQuantity(value: number | null | undefined, unit: string): string {
  return value === null || value === undefined ? "Unknown" : `${count.format(value)} ${unit}`;
}

export function shortId(id: string): string {
  return id.length > 12 ? `${id.slice(0, 8)}...` : id;
}
