import { useCallback, useEffect, useState } from "react";

export type ThemeChoice = "system" | "light" | "dark";
const KEY = "bt.theme";

function readChoice(): ThemeChoice {
  try {
    const value = localStorage.getItem(KEY);
    return value === "light" || value === "dark" ? value : "system";
  } catch {
    return "system";
  }
}

function systemDark(): boolean {
  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false;
}

export function applyTheme(choice: ThemeChoice = readChoice()): void {
  const dark = choice === "dark" || (choice === "system" && systemDark());
  document.documentElement.dataset.theme = dark ? "dark" : "light";
}

const ORDER: ThemeChoice[] = ["system", "light", "dark"];

export function useTheme(): { choice: ThemeChoice; next: () => void } {
  const [choice, setChoice] = useState<ThemeChoice>(readChoice);

  useEffect(() => {
    applyTheme(choice);
    if (choice !== "system") return;
    const query = window.matchMedia?.("(prefers-color-scheme: dark)");
    const listener = () => applyTheme("system");
    query?.addEventListener("change", listener);
    return () => query?.removeEventListener("change", listener);
  }, [choice]);

  const next = useCallback(() => {
    setChoice((current) => {
      const value = ORDER[(ORDER.indexOf(current) + 1) % ORDER.length]!;
      try {
        localStorage.setItem(KEY, value);
      } catch {
        /* Choice still holds for this tab. */
      }
      return value;
    });
  }, []);

  return { choice, next };
}

export function useReducedMotion(): boolean {
  const query = "(prefers-reduced-motion: reduce)";
  const [reduced, setReduced] = useState(() => window.matchMedia?.(query).matches ?? false);
  useEffect(() => {
    const list = window.matchMedia?.(query);
    const listener = () => setReduced(list.matches);
    list?.addEventListener("change", listener);
    return () => list?.removeEventListener("change", listener);
  }, []);
  return reduced;
}

export function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(() => window.matchMedia?.(query).matches ?? false);
  useEffect(() => {
    const list = window.matchMedia?.(query);
    setMatches(list?.matches ?? false);
    const listener = () => setMatches(list.matches);
    list?.addEventListener("change", listener);
    return () => list?.removeEventListener("change", listener);
  }, [query]);
  return matches;
}

export function useDocumentTitle(title: string): void {
  useEffect(() => {
    document.title = `${title} - Batch Tracker`;
  }, [title]);
}
