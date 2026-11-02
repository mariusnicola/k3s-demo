import { screen, within } from "@testing-library/react";
import { vi } from "vitest";
import { serviceEntry } from "../../../mocks/fixtures";
import { useFakeSockets } from "../../../mocks/harness";
import { CORRELATION, INGRESS, LIVE, SERVED, found, ingress, open, report, trail } from "../../../mocks/trail";

const row = (upstreamStatus: string | null, status = 502, at = SERVED.at) => ({ ...SERVED, at, status, upstreamStatus });
const EARLIER = "2026-10-31T08:59:50.000Z";

async function reading(pattern: string | RegExp) {
  return within(await trail()).findByText(pattern);
}

beforeEach(() => useFakeSockets());
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("REQ-BT-056 what the upstream status adds", () => {
  it.each(["-", "-, -", "- : -", "-, - : -"])("REQ-BT-056 %s: no upstream answered, so it stopped at the ingress", async (upstream) => {
    open({ [INGRESS]: ingress([row(upstream)]) });
    expect(await reading("The request stopped at the ingress.")).toBeInTheDocument();
  });

  it.each(["502", "502, 200", "-, 502", "502 : 200", "- : 502", "-1", "--", ""])("REQ-BT-056 %j: arrived with both statuses, and no claim a service answered", async (upstream) => {
    open({ [INGRESS]: ingress([row(upstream)]) });
    expect(await reading(`A request with this ID arrived at the ingress, last with status 502 and upstream status ${upstream}.`)).toBeInTheDocument();
    expect(await trail()).not.toHaveTextContent(/stopped|reached a service|service answered/);
  });

  it("REQ-BT-056 one row with an upstream answer stops the claim, wherever it sits", async () => {
    open({ [INGRESS]: ingress([row("-"), row("502", 502, EARLIER)]) });
    expect(await reading("A request with this ID arrived at the ingress, last with status 502 and upstream status -.")).toBeInTheDocument();
    expect(await trail()).not.toHaveTextContent("stopped");
  });

  it("REQ-BT-056 a row with no upstream field says nothing, so the sentence stays as it was", async () => {
    open({ [INGRESS]: ingress([row(null, 503)]) });
    expect(await reading("A request with this ID arrived at the ingress, last with status 503.")).toBeInTheDocument();
  });

  it("REQ-BT-056 an older row with no field leaves every row unproven", async () => {
    open({ [INGRESS]: ingress([row("-"), row(null, 502, EARLIER)]) });
    expect(await reading(/^A request with this ID arrived/)).toBeInTheDocument();
    expect(await trail()).not.toHaveTextContent("stopped");
  });

  it("REQ-BT-056 a service row outweighs a row with no upstream", async () => {
    open({ [INGRESS]: ingress([row("-")]), [LIVE]: found(report(), serviceEntry({ correlationId: CORRELATION })) });
    expect(await reading("The request reached a service.")).toBeInTheDocument();
  });

  it("REQ-BT-056 the rows list what the upstream gave", async () => {
    open({ [INGRESS]: ingress([row("502, 200"), row(null, 200, EARLIER)]) });
    const table = await within(await trail()).findByRole("table", { name: "Ingress rows" });
    expect(table).toHaveTextContent("502, 200");
    expect(table).toHaveTextContent("Not recorded");
  });
});

describe("B-076 rows in the same second", () => {
  it("B-076 name no last row, and list the statuses instead", async () => {
    open({ [INGRESS]: ingress([row(null, 200), row(null, 503)]) });
    expect(await reading("A request with this ID arrived at the ingress, with statuses 200, 503 in its last second.")).toBeInTheDocument();
    expect(await trail()).not.toHaveTextContent("last with status");
  });

  it("B-076 keep each row's upstream status beside its own", async () => {
    open({ [INGRESS]: ingress([row("200", 200), row("-", 503)]) });
    expect(await reading("A request with this ID arrived at the ingress, with statuses 200 (upstream 200), 503 (upstream -) in its last second.")).toBeInTheDocument();
  });

  it("B-076 leave out rows from an earlier second", async () => {
    open({ [INGRESS]: ingress([row(null, 200), row(null, 503), row(null, 404, EARLIER)]) });
    expect(await reading("A request with this ID arrived at the ingress, with statuses 200, 503 in its last second.")).toBeInTheDocument();
  });

  it("B-076 one row in the last second can still be named", async () => {
    open({ [INGRESS]: ingress([row(null, 200), row(null, 503, EARLIER)]) });
    expect(await reading("A request with this ID arrived at the ingress, last with status 200.")).toBeInTheDocument();
  });

  it("B-076 all with no upstream answering still stopped", async () => {
    open({ [INGRESS]: ingress([row("-", 502), row("-", 504)]) });
    expect(await reading("The request stopped at the ingress.")).toBeInTheDocument();
  });

  it("B-076 reads the times, not the text, so two spellings of one second tie", async () => {
    open({ [INGRESS]: ingress([row(null, 200, "2026-10-31T08:59:59Z"), row(null, 503, "2026-10-31T08:59:59.000Z")]) });
    expect(await reading(/with statuses 200, 503 in its last second\.$/)).toBeInTheDocument();
    expect(screen.queryByText(/last with status/)).not.toBeInTheDocument();
  });
});
