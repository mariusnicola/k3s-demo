const PATHS = {
  check: "M4 12.5 9.5 18 20 6.5",
  copy: "M9 9h11v11H9V9Zm-4 6H4V4h11v1",
  close: "M6 6l12 12M18 6 6 18",
  warning: "M12 4 2.8 20h18.4L12 4Zm0 6v5m0 2.5v.5",
  blocked: "M8.2 3h7.6L21 8.2v7.6L15.8 21H8.2L3 15.8V8.2L8.2 3ZM12 7.5v6m0 2.5v.5",
  info: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18Zm0 8v5.5m0-9v.5",
  clock: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18Zm0 4.5V12l3.5 2",
  chevronRight: "m9 5 7 7-7 7",
  arrowLeft: "M20 12H5m6-7-7 7 7 7",
  refresh: "M20 8a8.5 8.5 0 0 0-15.5-1.5M4 3v4h4M4 16a8.5 8.5 0 0 0 15.5 1.5M20 21v-4h-4",
  plus: "M12 5v14M5 12h14",
  user: "M12 12a4.5 4.5 0 1 0 0-9 4.5 4.5 0 0 0 0 9Zm-8 9a8 8 0 0 1 16 0",
  sun: "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8Zm0-5v2m0 14v2M3 12h2m14 0h2M5.6 5.6 7 7m10 10 1.4 1.4M18.4 5.6 17 7M7 17l-1.4 1.4",
  moon: "M20 14.5A8.5 8.5 0 0 1 9.5 4a8.5 8.5 0 1 0 10.5 10.5Z",
  display: "M3 4h18v12H3zM8 20h8M12 16v4",
  lock: "M6 11h12v9H6zM8.5 11V7.5a3.5 3.5 0 0 1 7 0V11",
  flag: "M5 21V4h13l-3 4.5 3 4.5H5",
  chain: "M10 14a4 4 0 0 0 6 .5l3-3a4 4 0 0 0-5.7-5.7L11.5 7.5M14 10a4 4 0 0 0-6-.5l-3 3a4 4 0 0 0 5.7 5.7l1.8-1.7",
  plugOff: "M3 3l18 18M9 9v3a3 3 0 0 0 3 3h0M15 13V8m-6 0V3m6 5V3M6 8h12v4M12 15v6",
  play: "M7 4.5v15L19 12 7 4.5Z",
  stop: "M6 6h12v12H6z",
  list: "M4 6h16M4 12h16M4 18h16",
  bell: "M6 10a6 6 0 1 1 12 0c0 6 2.5 7 2.5 7h-17S6 16 6 10Zm4 10a2 2 0 0 0 4 0",
  pulse: "M3 12h4l3-8 4 16 3-8h4",
  grid: "M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z",
  exit: "M10 4H4v16h6M15 8l4 4-4 4M19 12H9",
  box: "M3.5 8 12 3.5 20.5 8v8L12 20.5 3.5 16V8Zm0 0L12 12.5 20.5 8M12 12.5v8",
} as const;

export type IconName = keyof typeof PATHS;
export const ICON_NAMES = Object.keys(PATHS) as IconName[];

/** Decorative unless `label` is given. */
export function Icon({ name, label }: { name: IconName; label?: string }) {
  return (
    <svg className="ds-icon" viewBox="0 0 24 24" role={label ? "img" : undefined} aria-label={label} aria-hidden={label ? undefined : true}>
      <path d={PATHS[name]} />
    </svg>
  );
}
