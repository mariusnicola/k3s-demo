import { expect } from "@playwright/test";
import { API, BASE, LINE, type User } from "./env";

export type State = "Created" | "Dose1" | "Dose2" | "Inspected" | "Shipped" | "Closed" | "Rejected";
export type Farm = "farm-a" | "farm-b" | "farm-c";

export interface Step {
  state: State;
  by: string;
  traceId: string;
  recordId: string;
  hash: string;
}

export interface Batch {
  id: string;
  farmId: Farm;
  product: string;
  state: State;
  failNext: boolean;
  steps: Step[];
  pending: { eventId: string; status: string; error: string | null } | null;
}

export interface Alert {
  type: string;
  batchId: string;
  farmId: Farm;
  product: string;
  detail: string | null;
}

export interface LineStatus {
  running: boolean;
  rate: number;
  started: number;
  closed: number;
  rejected: number;
  errors: number;
}

type BatchRef = Pick<Batch, "farmId" | "id">;

const sessions = new Map<string, string>();

/** Sign-in answers with an HttpOnly cookie and no token, so a script sends what a browser would. */
async function session(user: User): Promise<string> {
  const known = sessions.get(user);
  if (known) return known;
  const response = await fetch(`${API}/auth/dev-login`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ user }) });
  expect(response.status, `dev-login ${user}`).toBe(200);
  const value = sessionCookie(response);
  sessions.set(user, value);
  return value;
}

async function call(user: User, method: string, url: string, body?: unknown, ifMatch?: string): Promise<Response> {
  const headers: Record<string, string> = { cookie: await session(user), origin: BASE };
  if (body !== undefined) headers["content-type"] = "application/json";
  if (ifMatch) headers["if-match"] = ifMatch;
  return fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
}

export function sessionCookie(response: Response): string {
  const cookie = response.headers.getSetCookie().find((value) => value.startsWith("bt_session="));
  if (!cookie) throw new Error("sign-in set no bt_session cookie");
  return cookie.split(";")[0]!;
}

async function ok<T>(response: Response, what: string): Promise<T> {
  const text = await response.text();
  expect(response.ok, `${what}: ${response.status} ${text.slice(0, 300)}`).toBe(true);
  return JSON.parse(text) as T;
}

let serial = 0;

/** Unique in every run, so a repeat run never meets its own rows. */
export function productName(label: string): string {
  serial += 1;
  return `e2e ${label} ${Date.now().toString(36)}${serial}`;
}

export const api = {
  async create(user: User, farmId: Farm, product: string): Promise<Batch> {
    return ok<Batch>(await call(user, "POST", `${API}/batches`, { farmId, product }), "create");
  },

  async read(user: User, batch: BatchRef): Promise<{ batch: Batch; etag: string }> {
    const response = await call(user, "GET", `${API}/batches/${batch.farmId}/${batch.id}`);
    const etag = response.headers.get("etag") ?? "";
    return { batch: await ok<Batch>(response, "read"), etag };
  },

  /** Waits for the real Worker to write the record, and returns the batch with its fresh ETag. */
  async settled(batch: BatchRef): Promise<{ batch: Batch; etag: string }> {
    await expect
      .poll(async () => (await api.read("ada", batch)).batch.pending?.status ?? "none", { timeout: 30_000, message: `event of ${batch.id} never settled` })
      .toBe("none");
    return api.read("ada", batch);
  },

  async advance(user: User, batch: BatchRef, to: State): Promise<void> {
    const { etag } = await api.settled(batch);
    await ok(await call(user, "POST", `${API}/batches/${batch.farmId}/${batch.id}/advance`, { to }, etag), `advance to ${to}`);
  },

  async inspect(batch: BatchRef, passed: boolean, reason: string | null = null): Promise<void> {
    const { etag } = await api.settled(batch);
    await ok(await call("ian", "POST", `${API}/batches/${batch.farmId}/${batch.id}/inspect`, { passed, reason }, etag), "inspect");
  },

  async failNext(batch: BatchRef): Promise<void> {
    await ok(await call("ada", "POST", `${API}/batches/${batch.farmId}/${batch.id}/fail-next`), "fail-next");
  },

  async replay(batch: BatchRef): Promise<void> {
    await ok(await call("ada", "POST", `${API}/batches/${batch.farmId}/${batch.id}/replay`), "replay");
  },

  async alerts(user: User): Promise<Alert[]> {
    return ok<Alert[]>(await call(user, "GET", `${API}/alerts`), "alerts");
  },

  async list(user: User, limit = 200): Promise<Batch[]> {
    return ok<Batch[]>(await call(user, "GET", `${API}/batches?limit=${limit}`), "list");
  },

  async dead(batch: BatchRef): Promise<void> {
    await expect
      .poll(async () => (await api.read("ada", batch)).batch.pending?.status ?? "none", { timeout: 45_000, message: `event of ${batch.id} never dead-lettered` })
      .toBe("dead");
  },

  /** Creates a batch and walks it by API. Stops settled, with its record written. */
  async batchAt(state: State, farmId: Farm = "farm-b", label = "batch"): Promise<Batch> {
    const batch = await api.create("olivia", farmId, productName(label));
    const path: State[] = ["Dose1", "Dose2", "Inspected", "Shipped", "Closed"];
    if (state === "Rejected") {
      await api.advance("olivia", batch, "Dose1");
      await api.advance("olivia", batch, "Dose2");
      await api.inspect(batch, false, "failed by the e2e walk");
    } else if (state !== "Created") {
      for (const to of path.slice(0, path.indexOf(state) + 1)) {
        if (to === "Inspected") await api.inspect(batch, true);
        else await api.advance("olivia", batch, to);
      }
    }
    return (await api.settled(batch)).batch;
  },
};

export const line = {
  async status(): Promise<LineStatus> {
    return ok<LineStatus>(await call("ada", "GET", LINE), "line status");
  },
  async start(rate: number): Promise<LineStatus> {
    return ok<LineStatus>(await call("ada", "POST", LINE, { rate }), "line start");
  },
  async stop(): Promise<LineStatus> {
    return ok<LineStatus>(await call("ada", "DELETE", LINE), "line stop");
  },
  /** Every started walk has ended, so the next test meets a quiet system. */
  async quiet(): Promise<void> {
    await expect
      .poll(
        async () => {
          const s = await line.status();
          return s.started - s.closed - s.rejected - s.errors;
        },
        { timeout: 90_000, message: "line walks never finished" },
      )
      .toBe(0);
  },
};
