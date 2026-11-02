import "@testing-library/jest-dom/vitest";
import { afterEach, vi } from "vitest";
import { cleanup, configure, prettyDOM } from "@testing-library/react";

afterEach(() => {
  cleanup();
  sessionStorage.clear();
  localStorage.clear();
});

// jsdom recomputes style per call and a role query calls it for every element and ancestor, which makes one
// query on a full page take over a second. Nothing changed means the answer cannot change, so it is kept until the DOM moves.
const computeStyle = window.getComputedStyle.bind(window);
let styles = new WeakMap<Element, CSSStyleDeclaration>();
const changes = new MutationObserver(() => {
  styles = new WeakMap();
});
changes.observe(document, { subtree: true, childList: true, attributes: true });
window.getComputedStyle = (element, pseudo) => {
  if (pseudo) return computeStyle(element, pseudo);
  if (changes.takeRecords().length > 0) styles = new WeakMap();
  const known = styles.get(element);
  if (known) return known;
  const style = computeStyle(element);
  styles.set(element, style);
  return style;
};

// jsdom parses its default stylesheet on the first style read, so that one-time parse would land inside some test's first find.
window.getComputedStyle(document.body);

// The library prints the page into every failed poll of a find, and nearly all of those errors are never read.
configure({
  getElementError(message, container) {
    const error = new Error(message ?? "");
    error.name = "TestingLibraryElementError";
    let text: string | undefined;
    const read = () => (text ??= [message, prettyDOM(container)].filter(Boolean).join("\n\n"));
    Object.defineProperty(error, "message", { configurable: true, get: read });
    return error;
  },
});

window.scrollTo = vi.fn();

window.matchMedia ??= vi.fn().mockImplementation((query: string) => ({
  matches: false,
  media: query,
  addEventListener: () => undefined,
  removeEventListener: () => undefined,
}));

window.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

Element.prototype.scrollIntoView ??= () => undefined;
Element.prototype.hasPointerCapture ??= () => false;
