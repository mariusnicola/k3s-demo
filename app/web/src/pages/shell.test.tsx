import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { batch } from "../mocks/fixtures";
import { installApi, meRoute, renderApp, useFakeSockets } from "../mocks/harness";

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

function page(user: "olivia", value: ReturnType<typeof batch>) {
  installApi({ "GET /api/me": meRoute(user), "GET /api/batches/farm-a/b-1": { etag: '"v1"', body: value } });
  renderApp("/batches/farm-a/b-1");
}

describe("shell", () => {
  it("REQ-BT-035 a skip link and named navigation exist", async () => {
    page("olivia", batch());
    await screen.findByRole("heading", { name: "Bovine vaccine" });
    expect(screen.getByRole("link", { name: "Skip to main content" })).toHaveAttribute("href", "#main");
    expect(screen.getByRole("navigation", { name: "Main" })).toBeInTheDocument();
  });

  it("REQ-BT-035 focus moves to main on route change", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [] } });
    renderApp("/batches");
    await userEvent.click(await screen.findByRole("link", { name: "Alerts" }));
    expect(await screen.findByRole("heading", { name: "Alerts" })).toBeInTheDocument();
    expect(screen.getByRole("main")).toHaveFocus();
  });

  it("REQ-BT-036 unknown address has a way back", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    renderApp("/nowhere");
    expect(await screen.findByRole("heading", { name: "Page not found" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "all batches" })).toBeInTheDocument();
    expect(document.title).toBe("Page not found - Batch Tracker");
  });

  it("REQ-BT-033 theme control changes the document theme", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    renderApp("/nowhere");
    await userEvent.click(await screen.findByRole("button", { name: /Theme: system/ }));
    expect(document.documentElement.dataset.theme).toBe("light");
    await userEvent.click(screen.getByRole("button", { name: /Theme: light/ }));
    expect(document.documentElement.dataset.theme).toBe("dark");
  });
});
