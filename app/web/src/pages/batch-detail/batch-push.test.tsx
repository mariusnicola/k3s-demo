import { act, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { batch, hopSnapshot } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets, type ApiCall } from "../../mocks/harness";

const gets = (calls: ApiCall[], part: string) => calls.filter((c) => c.method === "GET" && c.url.includes(part));
const wait = (ms: number) => act(() => new Promise<void>((resolve) => setTimeout(resolve, ms)));

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

function hopSocket() {
  const socket = FakeSocket.find("/ws/hops");
  socket.open();
  return socket;
}

describe("batch page", () => {
  const PATH = "/batches/farm-a/b-1";
  const page = (user: "olivia" | "ada", extra = {}) => {
    const calls = installApi({ "GET /api/me": meRoute(user), "GET /api/batches/farm-a/b-1": { etag: '"v1"', body: batch() }, ...extra });
    renderApp(PATH);
    return calls;
  };

  it("REQ-BT-032 a frame for this batch moves the page, and its etag is the one sent next", async () => {
    const calls = page("olivia", { "POST /api/batches/farm-a/b-1/advance": { etag: '"v3"', body: batch({ version: 3, state: "Dose2" }) } });
    await screen.findByRole("heading", { name: "Bovine vaccine" });
    const socket = hopSocket();
    act(() => socket.send(hopSnapshot()));
    act(() => socket.send({ type: "batch", etag: '"v2"', batch: batch({ version: 2, state: "Created" }) }));
    await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 1" }));
    const post = calls.find((c) => c.method === "POST")!;
    expect(post.headers["If-Match"]).toBe('"v2"');
    expect(gets(calls, "/batches/farm-a/b-1")).toHaveLength(1);
  });

  it("REQ-BT-032 an older frame never undoes the page", async () => {
    page("olivia");
    await screen.findByRole("heading", { name: "Bovine vaccine" });
    const socket = hopSocket();
    act(() => socket.send(hopSnapshot()));
    act(() => socket.send({ type: "batch", etag: '"v5"', batch: batch({ version: 5, state: "Shipped" }) }));
    expect(await screen.findByRole("button", { name: "Move to Closed" })).toBeInTheDocument();
    act(() => socket.send({ type: "batch", etag: '"v4"', batch: batch({ version: 4, state: "Dose1" }) }));
    await wait(350);
    expect(screen.getByRole("button", { name: "Move to Closed" })).toBeInTheDocument();
  });

  it("REQ-BT-032 a write shows its result with no GET after it", async () => {
    const calls = page("olivia", { "POST /api/batches/farm-a/b-1/advance": { etag: '"v2"', body: batch({ version: 2, state: "Dose2" }) } });
    await userEvent.click(await screen.findByRole("button", { name: "Move to Dose 2" }));
    expect(await screen.findByText("Moved to Dose 2.")).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Record inspection" })).toBeInTheDocument();
    expect(gets(calls, "/batches/farm-a/b-1")).toHaveLength(1);
  });

  it("REQ-BT-032 a row pushed out of the kept live rows does not go back to its first read", async () => {
    page("olivia");
    await screen.findByRole("heading", { name: "Bovine vaccine" });
    const socket = hopSocket();
    act(() => socket.send(hopSnapshot()));
    act(() => socket.send({ type: "batch", etag: '"v5"', batch: batch({ version: 5, state: "Shipped" }) }));
    await screen.findByRole("button", { name: "Move to Closed" });
    await wait(300);
    act(() => {
      for (let i = 0; i < 1100; i += 1) socket.send({ type: "batch", etag: '"v1"', batch: batch({ id: `other-${i}` }) });
    });
    await wait(300);
    expect(screen.getByRole("button", { name: "Move to Closed" })).toBeInTheDocument();
  });

  it("REQ-BT-032 frames for another batch change nothing", async () => {
    page("olivia");
    await screen.findByRole("heading", { name: "Bovine vaccine" });
    const socket = hopSocket();
    act(() => socket.send(hopSnapshot()));
    act(() => socket.send({ type: "batch", etag: '"v9"', batch: batch({ id: "b-2", version: 9, state: "Closed" }) }));
    await wait(350);
    expect(screen.getByRole("button", { name: "Move to Dose 2" })).toBeInTheDocument();
  });
});
