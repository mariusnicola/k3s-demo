import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { OperationToasts, type OperationItem } from "../index";

const css = readFileSync(resolve(__dirname, "operations.css"), "utf8") + readFileSync(resolve(__dirname, "checklist.css"), "utf8");

/** Declarations of the first rule with exactly this selector. */
function rule(selector: string): string {
  const start = css.indexOf(`${selector} {`);
  expect(start, selector).toBeGreaterThanOrEqual(0);
  return css.slice(start, css.indexOf("}", start));
}

const STEPS = Array.from({ length: 30 }, (_, i) => ({ name: `Step ${i}`, mark: "pending" as const, note: "Not started" }));
const ITEM: OperationItem = { id: "a", state: "running", title: "Deploy the app", detail: null, elapsed: "1 min", steps: STEPS, eta: "About 3 min left" };

describe("REQ-BT-054 cluster work is a toast over the page", () => {
  it("REQ-BT-054 it floats over the page, top right, and never takes room in it", () => {
    const toast = rule(".ds-operations:has(.ds-operation)");
    expect(toast).toContain("position: fixed");
    expect(toast).toMatch(/inset-block-start: /);
    expect(toast).toMatch(/inset-inline-end: /);
    expect(toast).not.toMatch(/inset-block-end: /);
    expect(toast).toContain("pointer-events: none");
    expect(css).not.toContain("position: sticky");
  });

  it("REQ-BT-054 it has a max width and a max height, and scrolls inside past them", () => {
    const toast = rule(".ds-operations:has(.ds-operation)");
    expect(toast).toMatch(/width: min\(24rem, /);
    expect(toast).toContain("max-height: 70dvh");
    expect(toast).toContain("overflow-y: auto");
  });

  it("REQ-BT-054 a long list scrolls inside the toast, and a keyboard can scroll it", () => {
    expect(rule(".ds-operation .ds-checklist")).toContain("overflow-y: auto");
    render(<OperationToasts items={[ITEM]} />);
    expect(screen.getByRole("list", { name: "Steps of Deploy the app" })).toHaveAttribute("tabindex", "0");
  });

  it("REQ-BT-054 a toast changes nothing in the page under it", () => {
    expect(css).not.toContain(".ds-shell .ds-main");
    expect(css).not.toContain("scroll-padding");
  });

  it("REQ-BT-054 failure details stay collapsed, with the checklist, the estimate and Seen in view", () => {
    const explanation = { what: "Rollout failed", where: "Helm", next: "Read the log" } as never;
    render(<OperationToasts items={[{ ...ITEM, id: "b", state: "failed", explanation, onSeen: () => undefined }]} />);
    const details = document.querySelector(".ds-operation details")!;
    expect(details).not.toHaveAttribute("open");
    expect(details).toHaveTextContent("Rollout failed");
    expect(details.querySelector("summary")).toHaveTextContent("Details");
    expect(details.querySelector(".ds-checklist")).toBeNull();
    expect(screen.getByRole("list", { name: "Steps of Deploy the app" })).toBeInTheDocument();
    expect(screen.getByText("About 3 min left")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Seen: Deploy the app" })).toBeInTheDocument();
  });

  it("REQ-BT-054 the catalog sample stays in the page", () => {
    render(<OperationToasts inline items={[ITEM]} />);
    expect(document.querySelector(".ds-operations")).toHaveAttribute("data-inline", "true");
    expect(rule(".ds-operations[data-inline]:has(.ds-operation)")).toContain("position: static");
  });
});
