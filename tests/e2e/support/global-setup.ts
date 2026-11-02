import { sessionCookie } from "./api";
import { API, LINE } from "./env";

const DRAIN_MS = 120_000;

async function json<T>(url: string, cookie: string): Promise<T> {
  const response = await fetch(url, { headers: { cookie } });
  if (!response.ok) throw new Error(`${url} answered ${response.status}`);
  return (await response.json()) as T;
}

/** Results mean nothing on a loaded system, so a run starts only when it is quiet. This reads and never changes anything. */
export default async function quietSystem(): Promise<void> {
  const login = await fetch(`${API}/auth/dev-login`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ user: "ada" }) });
  const cookie = sessionCookie(login);
  const line = await json<{ running: boolean; rate: number }>(LINE, cookie);
  if (line.running) throw new Error(`Line is running at rate ${line.rate}. Stop it before this suite, which does not stop what it did not start.`);
  const deadline = Date.now() + DRAIN_MS;
  for (;;) {
    const { pending } = await json<{ pending: number }>(`${API}/summary`, cookie);
    if (pending === 0) return;
    if (Date.now() > deadline) throw new Error(`${pending} events still pending after ${DRAIN_MS / 1000} s.`);
    await new Promise((resolve) => setTimeout(resolve, 2_000));
  }
}
