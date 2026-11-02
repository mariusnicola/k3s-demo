import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { batch, hopSnapshot } from "../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../mocks/harness";
import { client } from "./client";
import type { ApiError } from "./problem";
import { setRetryWait } from "./retry";

const VALID = /^[A-Za-z0-9-]{1,64}$/;
const ADVANCE = "POST /api/batches/farm-a/b-1/advance";
const refused = (id: string) => ({ status: 412, body: { title: "Precondition failed", correlationId: id } });
const advance = () => client.advance("farm-a", "b-1", "Dose2", '"v1"').catch((e: unknown) => e) as Promise<ApiError>;
const sentHeader = (init?: RequestInit) => (init?.headers as Record<string, string>)["X-Correlation-Id"]!;

/** Refuses at the network, as a server that never answered would, and remembers what was sent. */
function unreachable() {
  const sent: string[] = [];
  vi.stubGlobal("fetch", (_: string, init?: RequestInit) => {
    sent.push(sentHeader(init));
    return Promise.reject(new TypeError("Failed to fetch"));
  });
  return sent;
}

beforeEach(() => useFakeSockets());
afterEach(() => {
  setRetryWait(undefined);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("REQ-BT-052 the ID the browser makes", () => {
  it("REQ-BT-052 is one the server accepts", async () => {
    const calls = installApi({ [ADVANCE]: { body: batch() } });
    await client.advance("farm-a", "b-1", "Dose2", '"v1"');
    expect(calls[0]!.headers["X-Correlation-Id"]).toMatch(VALID);
  });
});

describe("REQ-BT-052 the ID a failure shows", () => {
  it("REQ-BT-052 is the server's when it gave one: body first, then header", async () => {
    installApi({ [ADVANCE]: { ...refused("from-body"), headers: { "X-Correlation-Id": "from-header" } } });
    expect((await advance()).correlationId).toBe("from-body");
    installApi({ [ADVANCE]: { status: 412, headers: { "X-Correlation-Id": "from-header" } } });
    expect((await advance()).correlationId).toBe("from-header");
  });

  it("REQ-BT-052 is the one sent when the answer named none", async () => {
    const calls = installApi({ [ADVANCE]: { status: 412 } });
    expect((await advance()).correlationId).toBe(calls[0]!.headers["X-Correlation-Id"]);
  });

  it("REQ-BT-052 is the one sent when the request reached no server", async () => {
    const sent = unreachable();
    const error = await advance();
    expect(error.status).toBe(0);
    expect(error.correlationId).toBe(sent[0]);
  });

  it("REQ-BT-052 is the one every retry of a read carried", async () => {
    setRetryWait(() => Promise.resolve());
    const sent = unreachable();
    const error = await client.getBatch("farm-a", "b-1").catch((e: unknown) => e as ApiError);
    expect(new Set(sent).size).toBe(1);
    expect((error as ApiError).correlationId).toBe(sent[0]);
  });

  it("REQ-BT-053 reaches the screen when no server answered", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "GET /api/batches/farm-a/b-1": { etag: '"v1"', body: batch({ state: "Dose1" }) } });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    renderApp("/batches/farm-a/b-1");
    const move = await screen.findByRole("button", { name: /^Move to/ });
    const sent = unreachable();
    await userEvent.click(move);
    expect(await screen.findByText(sent[0]!)).toBeInTheDocument();
    expect(screen.getByText("The server did not answer.")).toBeInTheDocument();
    expect(screen.getByText("Check the connection, then try again.")).toBeInTheDocument();
  });
});

describe("REQ-BT-055 a list that could not be read", () => {
  it("REQ-BT-055 a reader's list says so in its own panel", async () => {
    installApi({ "GET /api/me": meRoute("viewer"), "GET /api/batches": { status: 500, body: {} } });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    renderApp("/dashboard");
    expect(await screen.findByRole("button", { name: "Try again" }, { timeout: 5000 })).toBeInTheDocument();
  });
});
