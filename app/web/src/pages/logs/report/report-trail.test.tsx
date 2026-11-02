import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { useFakeSockets } from "../../../mocks/harness";
import { CORRELATION, ID, INGRESS, LIVE, SERVED, found, ingress, open, reads, trail } from "../../../mocks/trail";

beforeEach(() => useFakeSockets());
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("REQ-BT-056 Logs follows a report ID", () => {
  it("REQ-BT-056 asks the service log by report, and the Line and ingress by the report's correlation ID", async () => {
    const calls = open({});
    await trail();
    await waitFor(() => expect(reads(calls, "/line/logs").at(-1)?.get("correlationId")).toBe(CORRELATION));
    expect(reads(calls, "/live/logs").at(-1)?.get("reportId")).toBe(ID);
    expect(reads(calls, "/line/logs").every((query) => !query.has("reportId"))).toBe(true);
    expect(reads(calls, "/line/logs")).toHaveLength(1);
    expect(reads(calls, "/ingress/logs").at(-1)?.get("correlationId")).toBe(CORRELATION);
  });

  it("REQ-BT-056 a plain correlation ID reads the ingress as well", async () => {
    const calls = open({ [LIVE]: found() }, `/logs?correlationId=${CORRELATION}`);
    await trail();
    expect(reads(calls, "/ingress/logs").at(-1)?.get("correlationId")).toBe(CORRELATION);
  });

  it("REQ-BT-056 with neither ID there is nothing to look up, so the ingress is not read", async () => {
    const calls = open({ [LIVE]: found() }, "/logs");
    await screen.findByRole("heading", { name: "Logs" });
    await screen.findByRole("textbox", { name: /Report ID/ });
    expect(reads(calls, "/ingress/logs")).toHaveLength(0);
    expect(screen.queryByRole("region", { name: "Ingress" })).not.toBeInTheDocument();
  });

  it("REQ-BT-056 lists the ingress rows beside the entries", async () => {
    open({ [INGRESS]: ingress([SERVED]) });
    const table = await within(await trail()).findByRole("table", { name: "Ingress rows" });
    expect(table).toHaveTextContent("/api/batches");
    expect(table).toHaveTextContent("200");
    expect(await screen.findByRole("table", { name: "Log entries" })).toHaveTextContent("GET /api/batches: Down");
  });

  it("REQ-BT-056 a hand-edited address holding no UUID is dropped, as any bad filter is", async () => {
    const calls = open({ [LIVE]: found() }, "/logs?reportId=not-a-uuid");
    await screen.findByRole("textbox", { name: /Report ID/ });
    await waitFor(() => expect(reads(calls, "/live/logs").length).toBeGreaterThan(0));
    expect(reads(calls, "/live/logs").every((query) => !query.has("reportId"))).toBe(true);
  });

  it("REQ-BT-056 the Report ID field filters, and Clear filters takes it off", async () => {
    const calls = open({ [LIVE]: found() }, "/logs");
    const field = await screen.findByRole("textbox", { name: /Report ID/ });
    await userEvent.type(field, `${ID}{Enter}`);
    await waitFor(() => expect(reads(calls, "/live/logs").at(-1)?.get("reportId")).toBe(ID));
    await userEvent.click(screen.getAllByRole("button", { name: "Clear filters" })[0]!);
    await waitFor(() => expect(reads(calls, "/live/logs").at(-1)?.has("reportId")).toBe(false));
  });

  it("REQ-BT-056 an open entry names the report, the page and the kind", async () => {
    open({});
    await userEvent.click(await screen.findByRole("button", { name: /details of browser-failure/ }));
    expect(await screen.findByText(ID)).toBeInTheDocument();
    expect(screen.getByText(/\/batches\/farm-a\/b1/)).toBeInTheDocument();
    expect(screen.getByText("request-failure")).toBeInTheDocument();
  });
});

describe("REQ-BT-056 the browser-failure reason", () => {
  it("REQ-BT-056 goes to the service log and never to the Line, which refuses it", async () => {
    const calls = open({}, "/logs?reason=browser-failure");
    expect(await screen.findByRole("table", { name: "Log entries" })).toHaveTextContent("GET /api/batches: Down");
    expect(reads(calls, "/live/logs").at(-1)?.get("reason")).toBe("browser-failure");
    expect(reads(calls, "/line/logs")).toHaveLength(0);
  });

  it("REQ-BT-056 can be picked from the reasons", async () => {
    const calls = open({}, "/logs");
    await userEvent.selectOptions(await screen.findByRole("combobox", { name: "Reason" }), "browser-failure");
    await waitFor(() => expect(reads(calls, "/live/logs").at(-1)?.get("reason")).toBe("browser-failure"));
  });

  it("REQ-BT-056 an open entry says what it did and what to do", async () => {
    open({});
    await userEvent.click(await screen.findByRole("button", { name: /details of browser-failure/ }));
    expect(await screen.findByText("A browser reported this failure. The report changes nothing on any batch.")).toBeInTheDocument();
    expect(screen.getByText("Open the report by its ID to see the request it came from.")).toBeInTheDocument();
  });

  it("REQ-BT-056 the Line's own reasons still stay off the service log", async () => {
    const calls = open({}, "/logs?reason=event-timeout");
    await screen.findByRole("heading", { name: "Logs" });
    await waitFor(() => expect(reads(calls, "/line/logs").at(-1)?.get("reason")).toBe("event-timeout"));
    expect(reads(calls, "/live/logs")).toHaveLength(0);
  });
});
