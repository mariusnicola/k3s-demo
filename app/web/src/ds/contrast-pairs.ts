export interface Pair {
  fg: string;
  bg: string;
  min: number;
}

const TEXT = 4.5;
const NON_TEXT = 3;

const surfaces = ["--bg-canvas", "--bg-surface", "--bg-raised", "--bg-sunken", "--bg-hover", "--bg-selected"];
const texts = ["--text-primary", "--text-secondary", "--text-muted", "--text-link"];
const tones = ["neutral", "info", "success", "warning", "danger"];

export const contrastPairs: Pair[] = [
  ...texts.flatMap((fg) => surfaces.map((bg) => ({ fg, bg, min: TEXT }))),
  ...["--accent", "--accent-hover", "--accent-active"].map((bg) => ({ fg: "--text-on-accent", bg, min: TEXT })),
  ...["--danger", "--danger-hover", "--danger-active"].map((bg) => ({ fg: "--text-on-danger", bg, min: TEXT })),
  ...tones.map((t) => ({ fg: `--tone-${t}-fg`, bg: `--tone-${t}-bg`, min: TEXT })),
  ...tones.flatMap((t) => ["--bg-surface", "--bg-raised"].map((bg) => ({ fg: `--tone-${t}-fg`, bg, min: TEXT }))),
  ...tones.map((t) => ({ fg: "--text-link", bg: `--tone-${t}-bg`, min: TEXT })),
  { fg: "--text-on-chrome", bg: "--bg-chrome", min: TEXT },
  { fg: "--text-on-chrome-muted", bg: "--bg-chrome", min: TEXT },
  { fg: "--focus-on-chrome", bg: "--bg-chrome", min: NON_TEXT },
  { fg: "--button-disabled-fg", bg: "--button-disabled-bg", min: TEXT },
  ...["--bg-canvas", "--bg-surface", "--bg-raised"].flatMap((bg) => [
    { fg: "--border-control", bg, min: NON_TEXT },
    { fg: "--border-focus", bg, min: NON_TEXT },
    { fg: "--data-ok", bg, min: NON_TEXT },
    { fg: "--data-fail", bg, min: NON_TEXT },
  ]),
  ...tones.map((t) => ({ fg: `--tone-${t}-border`, bg: "--bg-surface", min: NON_TEXT })),
  ...[1, 2, 3, 4].map((n) => ({ fg: `--series-${n}`, bg: "--bg-surface", min: NON_TEXT })),
  { fg: "--text-on-rail-active", bg: "--bg-rail-active", min: TEXT },
  { fg: "--map-text", bg: "--map-halo", min: TEXT },
  { fg: "--map-text-muted", bg: "--map-halo", min: TEXT },
  { fg: "--map-plate-text", bg: "--map-plate-top", min: TEXT },
  { fg: "--map-plate-note", bg: "--map-plate-top", min: TEXT },
  { fg: "--map-pod-text", bg: "--map-halo", min: TEXT },
  ...["--map-plate-top"].flatMap((bg) => ["--map-block-top", "--map-pod-top", "--map-route", "--map-dot", "--map-fail", "--map-warn"].map((fg) => ({ fg, bg, min: NON_TEXT }))),
];
