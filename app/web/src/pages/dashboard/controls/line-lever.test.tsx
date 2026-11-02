import { act, fireEvent, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { LINE_STOPPED, lineSnapshot } from "../../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type Handler, type Reply } from "../../../mocks/harness";

const running = (rate: number) => ({ ...LINE_STOPPED, running: true, rate });

function show(status: typeof LINE_STOPPED, extra: Record<string, Reply | Handler> = {}) {
  const calls = installApi({ "GET /api/me": meRoute("ada"), ...extra });
  FakeSocket.greet("/line/ws", lineSnapshot({ status }));
  renderApp("/dashboard");
  return calls;
}

const lever = () => screen.findByRole("slider", { name: "Requested rate" });
const push = (status: typeof LINE_STOPPED) => act(() => FakeSocket.find("/line/ws").send({ ...lineSnapshot({ status }), type: "line" }));
const drag = (slider: HTMLElement, ...values: string[]) => {
  for (const value of values) fireEvent.input(slider, { target: { value } });
  fireEvent.change(slider);
};
const posts = (calls: ReturnType<typeof show>) => calls.filter((c) => c.method === "POST" && c.url.endsWith("/line")).map((c) => (c.body as unknown as { rate: number }).rate);

beforeEach(() => useFakeSockets());

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("Line rate lever", () => {
  it("REQ-BT-031 the lever runs from 0 to 35 in steps of 0.2", async () => {
    show(running(3));
    const slider = await lever();
    expect(slider).toHaveAttribute("min", "0");
    expect(slider).toHaveAttribute("max", "35");
    expect(slider).toHaveAttribute("step", "0.2");
    expect(slider).toHaveValue("3");
    expect(screen.getByText("3 batches per second")).toBeVisible();
  });

  it("REQ-BT-031 with the line running, a drag sends once, when it ends", async () => {
    const calls = show(running(3), { "POST /line": { status: 202 } });
    const slider = await lever();
    fireEvent.input(slider, { target: { value: "4" } });
    fireEvent.input(slider, { target: { value: "7.4" } });
    expect(screen.getByText("7.4 batches per second")).toBeVisible();
    expect(posts(calls)).toEqual([]);
    fireEvent.change(slider);
    await act(async () => {});
    expect(posts(calls)).toEqual([7.4]);
  });

  it("REQ-BT-031 with the line running there is no button to press for the rate", async () => {
    show(running(3));
    await lever();
    expect(screen.queryByRole("button", { name: "Change rate" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Start line" })).toBeNull();
  });

  it("REQ-BT-031 with the line stopped a drag starts nothing, and Start line sends the lever's value", async () => {
    const calls = show(LINE_STOPPED, { "POST /line": { status: 202 } });
    drag(await lever(), "12.6");
    await act(async () => {});
    expect(posts(calls)).toEqual([]);
    await userEvent.click(screen.getByRole("button", { name: "Start line" }));
    expect(posts(calls)).toEqual([12.6]);
  });

  it("REQ-BT-031 the lever at 0 sends 0, which the line reads as idle", async () => {
    const calls = show(running(3), {
      "POST /line": () => {
        push(running(0));
        return { status: 202 };
      },
    });
    drag(await lever(), "0");
    expect(await screen.findByText("Idle: rate is 0")).toBeInTheDocument();
    expect(posts(calls)).toEqual([0]);
  });

  it("REQ-BT-031 a failed call puts the lever back at the real rate and says so", async () => {
    show(running(3), { "POST /line": { status: 500, body: {} } });
    const slider = await lever();
    drag(slider, "20");
    expect(await screen.findByText("The new rate was not confirmed. The lever is back at the rate the line reports, 3 per second.")).toBeInTheDocument();
    expect(slider).toHaveValue("3");
    expect(screen.getByText("3 batches per second")).toBeVisible();
  });

  it("REQ-BT-031 a rate the server refuses shows the server's words and puts the lever back", async () => {
    const calls = show(running(3), { "POST /line": { status: 400, body: { title: "Bad Request", status: 400, detail: "rate must be 0 to 35." } } });
    const slider = await lever();
    drag(slider, "20");
    expect(await screen.findByText(/rate must be 0 to 35\./)).toBeInTheDocument();
    expect(screen.getByText(/new rate was not confirmed/)).toBeInTheDocument();
    expect(slider).toHaveValue("3");
    expect(posts(calls)).toEqual([20]);
  });

  it("REQ-BT-031 the next move clears the failure note", async () => {
    show(running(3), { "POST /line": { status: 500, body: {} } });
    const slider = await lever();
    drag(slider, "20");
    await screen.findByText(/not confirmed/);
    fireEvent.input(slider, { target: { value: "4" } });
    expect(screen.queryByText(/not confirmed/)).toBeNull();
  });

  it("REQ-BT-031 a frame during a drag does not move the lever", async () => {
    show(running(3));
    const slider = await lever();
    fireEvent.input(slider, { target: { value: "9" } });
    push(running(3));
    expect(slider).toHaveValue("9");
  });

  it("REQ-BT-031 after an accepted change the lever follows the line again", async () => {
    show(running(3), { "POST /line": { status: 202 } });
    const slider = await lever();
    drag(slider, "9");
    await act(async () => {});
    push(running(9));
    push(running(11));
    expect(slider).toHaveValue("11");
  });

  it("REQ-BT-031 a drag begun after an accepted change is not moved by the frame that confirms it", async () => {
    show(running(3), { "POST /line": { status: 202 } });
    const slider = await lever();
    drag(slider, "9");
    await act(async () => {});
    fireEvent.input(slider, { target: { value: "12" } });
    push(running(9));
    expect(slider).toHaveValue("12");
  });

  it("REQ-BT-031 one batch a second reads in the singular", async () => {
    show(running(1));
    await lever();
    expect(screen.getByText("1 batch per second")).toBeVisible();
  });

  it("REQ-BT-031 a release while a call is out waits, then sends only the latest", async () => {
    let finish = () => {};
    const gate = new Promise<void>((resolve) => (finish = resolve));
    const calls = show(running(3), {
      "POST /line": async () => {
        await gate;
        return { status: 202 };
      },
    });
    const slider = await lever();
    drag(slider, "5");
    drag(slider, "6");
    drag(slider, "7");
    await act(async () => {});
    expect(posts(calls)).toEqual([5]);
    await act(async () => finish());
    expect(posts(calls)).toEqual([5, 7]);
  });

  it("REQ-BT-031 a rate above the lever keeps its true figure in the words and the state", async () => {
    show(running(100));
    const slider = await lever();
    expect(slider).toHaveValue("35");
    expect(screen.getByText("100 batches per second")).toBeVisible();
    expect(screen.getByText("Running, 100 per second requested")).toBeInTheDocument();
  });

  it("REQ-BT-031 while a stop is out the lever cannot send", async () => {
    let finish = () => {};
    const gate = new Promise<void>((resolve) => (finish = resolve));
    show(running(3), {
      "DELETE /line": async () => {
        await gate;
        return { body: LINE_STOPPED };
      },
    });
    const slider = await lever();
    await userEvent.click(screen.getByRole("button", { name: "Stop line" }));
    expect(slider).toBeDisabled();
    await act(async () => finish());
  });
});
