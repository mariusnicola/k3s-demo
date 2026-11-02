import { screen } from "@testing-library/react";
import { vi } from "vitest";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../../mocks/harness";

beforeEach(() => useFakeSockets());

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("access", () => {
  it("REQ-BT-015 non admin gets their own dashboard, and the line feed stays shut", async () => {
    installApi({ "GET /api/me": meRoute("olivia") });
    renderApp("/dashboard");
    expect(await screen.findByRole("heading", { name: "Batches by step" })).toBeInTheDocument();
    expect(FakeSocket.all.filter((s) => s.url.endsWith("/line/ws"))).toEqual([]);
  });
});
