import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { MemoryRouter, useNavigate } from "react-router-dom";
import { vi } from "vitest";
import { App } from "../App";
import { SessionProvider } from "../auth/session";
import { Button, ConnectionStatus, ErrorBoundary, Meter } from "../ds";
import { batch } from "../mocks/fixtures";
import { installApi, meRoute, renderApp, useFakeSockets, type Reply } from "../mocks/harness";

const ROUTE = "GET /api/batches/farm-a/b-1";

function Back() {
  const navigate = useNavigate();
  return <button onClick={() => navigate(-1)}>Go back</button>;
}

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

describe("try again shows it is trying", () => {
  it("REQ-BT-036 Try again is busy until the read ends", async () => {
    let release: () => void = () => undefined;
    let gate = Promise.resolve();
    installApi({
      "GET /api/me": meRoute("olivia"),
      "GET /api/batches": async () => {
        await gate;
        return { status: 503 };
      },
    });
    renderApp("/batches");
    const button = await screen.findByRole("button", { name: "Try again" });
    gate = new Promise<void>((resolve) => (release = resolve));
    await userEvent.click(button);
    expect(button).toHaveAttribute("aria-busy", "true");
    await act(async () => release());
    await waitFor(() => expect(screen.getByRole("button", { name: "Try again" })).not.toHaveAttribute("aria-busy"));
  });
});

describe("reads under load", () => {
  it("REQ-BT-036 changing page scrolls back to the top", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch()] }, [ROUTE]: { etag: '"v1"', body: batch() } });
    renderApp("/batches");
    const scroll = vi.mocked(window.scrollTo);
    scroll.mockClear();
    await userEvent.click(await screen.findByRole("link", { name: "Bovine vaccine" }));
    expect(scroll).toHaveBeenCalled();
  });

  it("REQ-BT-036 browser Back keeps the place in a long list", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch()] }, [ROUTE]: { etag: '"v1"', body: batch() } });
    render(
      <MemoryRouter initialEntries={["/batches", "/batches/farm-a/b-1"]} initialIndex={1}>
        <SessionProvider>
          <App />
          <Back />
        </SessionProvider>
      </MemoryRouter>,
    );
    await screen.findByRole("heading", { name: "Bovine vaccine" });
    const scroll = vi.mocked(window.scrollTo);
    scroll.mockClear();
    await userEvent.click(screen.getByRole("button", { name: "Go back" }));
    await screen.findByRole("heading", { name: "Batches" });
    await waitFor(() => expect(document.activeElement).toBe(document.getElementById("main")));
    expect(scroll).not.toHaveBeenCalled();
  });

  it("REQ-BT-035 changing a filter leaves focus on the filter", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch()] } });
    renderApp("/batches");
    const select = await screen.findByRole("combobox", { name: "Attention" });
    select.focus();
    await userEvent.selectOptions(select, "Blocked");
    await screen.findByText("No batches match these filters");
    expect(document.activeElement).toBe(select);
  });
});

describe("focus and stale addresses", () => {
  it("REQ-BT-035 a pressed control that vanishes hands focus to the main region", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [batch()] } });
    renderApp("/batches?attention=blocked");
    await userEvent.click(await screen.findByRole("button", { name: "Clear filters" }));
    await waitFor(() => expect(document.activeElement).toBe(document.getElementById("main")));
  });

  it("REQ-BT-016 a farm from another user's address is dropped, so no 403 follows", async () => {
    const calls = installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches": { body: [] } });
    renderApp("/batches?farm=farm-c");
    await userEvent.click(await screen.findByRole("button", { name: "New batch" }));
    expect(within(screen.getByRole("dialog")).getByRole("combobox", { name: /Farm/ })).toHaveValue("farm-a");
    expect(calls.some((c) => c.url.includes("farm-c"))).toBe(false);
  });
});

describe("session and faults", () => {
  it("REQ-BT-035 restoring a session keeps one main landmark", async () => {
    installApi({ "GET /api/me": () => new Promise<Reply>(() => undefined) });
    renderApp("/alerts");
    expect(await screen.findByText("Restoring your session")).toBeInTheDocument();
    expect(document.querySelectorAll("main")).toHaveLength(1);
  });

  it("REQ-BT-035 Try again on sign in hands focus to the main region", async () => {
    installApi({});
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("down")));
    renderApp("/signin");
    await userEvent.click(screen.getByRole("button", { name: /Continue as ian/ }));
    await userEvent.click(await screen.findByRole("button", { name: "Try again" }));
    await waitFor(() => expect(document.activeElement).toBe(document.getElementById("main")));
  });

  it("REQ-BT-036 a page that throws leaves a way out", () => {
    const quiet = vi.spyOn(console, "error").mockImplementation(() => undefined);
    const Broken = (): never => {
      throw new Error("boom");
    };
    render(
      <ErrorBoundary>
        <Broken />
      </ErrorBoundary>,
    );
    expect(screen.getByRole("alert")).toHaveTextContent("This page failed");
    expect(screen.getByRole("alert")).toHaveTextContent("A change made just before may have been saved. Check the batch list before repeating it.");
    expect(screen.getByRole("alert")).not.toHaveTextContent("Nothing was saved");
    expect(screen.getByRole("button", { name: "Reload page" })).toBeInTheDocument();
    quiet.mockRestore();
  });
});

describe("design system honesty", () => {
  it("REQ-BT-035 a button keeps focus when its gate turns on", async () => {
    function Gate() {
      const [reason, setReason] = useState<string | null>(null);
      return (
        <Button disabledReason={reason} onClick={() => setReason("Blocked now.")}>
          Press
        </Button>
      );
    }
    render(<Gate />);
    const button = screen.getByRole("button", { name: "Press" });
    await userEvent.click(button);
    expect(screen.getByText("Blocked now.")).toBeInTheDocument();
    expect(button.isConnected).toBe(true);
    expect(document.activeElement).toBe(button);
  });

  it("REQ-BT-030 a stale meter is not drawn as a live breach", () => {
    const { container } = render(<Meter label="CPU" value={9} max={4} unit="m" breach stale />);
    expect(container.querySelector(".ds-meter")).not.toHaveAttribute("data-breach");
    expect(container.querySelector(".ds-meter")).toHaveAttribute("data-stale");
  });

  it("REQ-BT-030 a stale open socket does not claim to be retrying", () => {
    render(<ConnectionStatus label="Monitor" state={{ status: "open", lastDataAt: 1000 }} now={500000} stale />);
    expect(screen.getByText(/Last data/)).not.toHaveTextContent("Retrying");
  });
});
