import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { client } from "../api/client";
import { ApiError } from "../api/problem";
import { setRetryWait } from "../api/retry";
import { batch } from "../mocks/fixtures";
import { installApi, meRoute, renderApp, useFakeSockets } from "../mocks/harness";

const sentHeader = (init?: RequestInit) => (init?.headers as Record<string, string>)["X-Correlation-Id"]!;
const advance = () => client.advance("farm-a", "b-1", "Dose2", '"v1"').catch((e: unknown) => e) as Promise<ApiError>;

/** Every call fails at the network, and the IDs it carried are kept. */
function unreachable() {
  const sent: string[] = [];
  vi.stubGlobal("fetch", (_: string, init?: RequestInit) => {
    sent.push(sentHeader(init));
    return Promise.reject(new TypeError("Failed to fetch"));
  });
  return sent;
}

/** Headers arrive, then the body does not. */
function bodyLost(failure: unknown) {
  const sent: string[] = [];
  vi.stubGlobal("fetch", (_: string, init?: RequestInit) => {
    sent.push(sentHeader(init));
    return Promise.resolve({ ok: true, status: 200, headers: new Headers(), text: () => Promise.reject(failure) });
  });
  return sent;
}

beforeEach(() => {
  useFakeSockets();
  setRetryWait(() => Promise.resolve());
});
afterEach(() => {
  setRetryWait(undefined);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("REQ-BT-052 a request with no answer shows the ID it sent", () => {
  it("REQ-BT-052 restoring a session", async () => {
    const sent = unreachable();
    renderApp("/batches");
    expect(await screen.findByText("Cannot reach the server")).toBeInTheDocument();
    expect(await screen.findByText(sent.at(-1)!)).toBeInTheDocument();
  });

  it("REQ-BT-052 signing out", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    renderApp("/alerts");
    const leave = await screen.findByRole("button", { name: /Switch user/ });
    const sent = unreachable();
    await userEvent.click(leave);
    expect(await screen.findByText("Sign out did not finish")).toBeInTheDocument();
    expect(screen.getByText(/may still be signed in/)).toBeInTheDocument();
    expect(await screen.findByText(sent[0]!)).toBeInTheDocument();
  });
});

describe("REQ-BT-053 the server's words on session failures", () => {
  const SAID = { where: "Where from the server", impact: "Impact from the server", next: "Next from the server" };
  const refused = { status: 500, body: { ...SAID, title: "Failed", correlationId: "corr-session" } };

  const expectWords = async () => {
    expect(await screen.findByText(SAID.where)).toBeInTheDocument();
    expect(screen.getByText(SAID.impact)).toBeInTheDocument();
    expect(screen.getByText(SAID.next)).toBeInTheDocument();
    expect(screen.getByText("corr-session")).toBeInTheDocument();
  };

  it("REQ-BT-053 restoring a session shows them", async () => {
    installApi({ "GET /api/me": refused });
    renderApp("/alerts");
    await expectWords();
    expect(screen.queryByText("Try again, or go to sign in.")).toBeNull();
  });

  it("REQ-BT-053 signing out shows them", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "POST /api/auth/logout": refused });
    renderApp("/alerts");
    await userEvent.click(await screen.findByRole("button", { name: /Switch user/ }));
    await expectWords();
    expect(screen.queryByText("Sign out again.")).toBeNull();
  });

  const limited = (next?: string) => ({ status: 429, headers: { "Retry-After": "120" }, body: { title: "Too Many Requests", correlationId: "corr-session", ...(next ? { next } : {}) } });

  it("REQ-BT-050 a 429 on signing out shows its seconds beside the server's words", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "POST /api/auth/logout": limited("Wait, then send it again.") });
    renderApp("/alerts");
    await userEvent.click(await screen.findByRole("button", { name: /Switch user/ }));
    expect(await screen.findByText("Wait, then send it again. Try again in 120 s.")).toBeInTheDocument();
  });

  it("REQ-BT-050 a 429 with no words from the server shows its seconds beside the fixed next step", async () => {
    installApi({ "GET /api/me": meRoute("olivia"), "POST /api/auth/logout": limited() });
    renderApp("/alerts");
    await userEvent.click(await screen.findByRole("button", { name: /Switch user/ }));
    expect(await screen.findByText("Sign out again. Try again in 120 s.")).toBeInTheDocument();
  });

  it("REQ-BT-053 with no words from a server the fixed next step stands", async () => {
    installApi({ "GET /api/me": { status: 500, body: { title: "Failed" } } });
    renderApp("/alerts");
    expect(await screen.findByText("Try again, or go to sign in.")).toBeInTheDocument();
  });
});

describe("REQ-BT-050 a body lost after its headers", () => {
  it("REQ-BT-050 a write is an ApiError that says the result is unknown, with the ID sent", async () => {
    const sent = bodyLost(new TypeError("network error"));
    const error = await advance();
    expect(error).toBeInstanceOf(ApiError);
    expect(error.resultUnknown).toBe(true);
    expect(error.correlationId).toBe(sent[0]);
  });

  it("REQ-BT-050 a read is an ApiError with the ID sent, and no unknown result", async () => {
    const sent = bodyLost(new TypeError("network error"));
    const error = await client.getBatch("farm-a", "b-1").catch((e: unknown) => e as ApiError);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).resultUnknown).toBe(false);
    expect((error as ApiError).correlationId).toBe(sent[0]);
  });

  it("REQ-BT-050 a body cut short is the same failure", async () => {
    vi.stubGlobal("fetch", () => Promise.resolve({ ok: true, status: 200, headers: new Headers(), text: () => Promise.resolve(JSON.stringify(batch()).slice(0, 20)) }));
    const error = await advance();
    expect(error).toBeInstanceOf(ApiError);
    expect(error.resultUnknown).toBe(true);
  });

  it("REQ-BT-050 an abort stays an abort", async () => {
    bodyLost(new DOMException("Aborted", "AbortError"));
    expect(await client.getBatch("farm-a", "b-1").catch((e: unknown) => (e as Error).name)).toBe("AbortError");
  });
});
