import type { BrowserContext } from "@playwright/test";
import { BASE } from "./env";

interface DeclaredResponse {
  method: string;
  url: RegExp;
  status: number;
  seenUrl?: string;
  consoleUsed: boolean;
}

interface DeclaredFailure {
  method: string;
  url: RegExp;
  error: RegExp;
  seenUrl?: string;
  seenError?: string;
  consoleUsed: boolean;
}

type Window = "sockets" | "outage";

interface ConsoleEntry {
  type: string;
  text: string;
  url: string;
  windows: Window[];
}

interface FailedRequest {
  method: string;
  url: string;
  error: string;
  windows: Window[];
}

interface BadResponse {
  method: string;
  url: string;
  status: number;
}

const hide = (text: string) => text.replace(/token=[^&'\s"]+/g, "token=<hidden>");

/** A lost report is lost, and page code cannot stop the browser's line for it. This route and the statuses that refuse a report, nothing wider. */
const REPORTS_URL = `${BASE}/api/browser-reports`;
const REPORT_REFUSED = new Set([400, 403, 413, 429]);

const SOCKET_FAILED = /^WebSocket connection to '.*\/ws(\/hops)?(\?[^']*)?' failed/;

/**
 * Fails a test on any console error or warning, page error, failed request or 4xx or 5xx.
 * There is no allowlist: an expected failure is declared by the one test that causes it.
 * A browser writes "Failed to load resource" for every failed fetch and page code cannot
 * stop it, so a declared response consumes that one line and nothing else.
 */
export class ConsoleGuard {
  private readonly watched = new WeakSet<BrowserContext>();
  private readonly consoleEntries: ConsoleEntry[] = [];
  private readonly pageErrors: string[] = [];
  private readonly failures: FailedRequest[] = [];
  private readonly responses: BadResponse[] = [];
  private readonly declaredResponses: DeclaredResponse[] = [];
  private readonly declaredFailures: DeclaredFailure[] = [];
  private readonly open: Window[] = [];

  watch(context: BrowserContext): void {
    if (this.watched.has(context)) return;
    this.watched.add(context);
    context.on("console", (message) => {
      if (message.type() !== "error" && message.type() !== "warning") return;
      this.consoleEntries.push({ type: message.type(), text: hide(message.text()), url: message.location().url, windows: [...this.open] });
    });
    context.on("weberror", (error) => this.pageErrors.push(hide(String(error.error()?.stack ?? error.error()))));
    context.on("requestfailed", (request) => {
      this.failures.push({ method: request.method(), url: request.url(), error: request.failure()?.errorText ?? "unknown", windows: [...this.open] });
    });
    context.on("response", (response) => {
      if (response.status() >= 400) this.responses.push({ method: response.request().method(), url: response.url(), status: response.status() });
    });
  }

  /** Declares one 4xx or 5xx. Call once per response the test causes. */
  expectResponse(method: string, url: RegExp, status: number): void {
    this.declaredResponses.push({ method, url, status, consoleUsed: false });
  }

  /** Declares one request that fails at network level. Call once per request. */
  expectFailedRequest(method: string, url: RegExp, error: RegExp): void {
    this.declaredFailures.push({ method, url, error, consoleUsed: false });
  }

  /** While open, a reconnecting hop or monitor socket may log its failure. Nothing else passes. */
  expectSocketLoss(): () => void {
    return this.openWindow("sockets");
  }

  /** While open, same-origin requests and sockets may fail at network level. 4xx, 5xx, page errors and other console text still fail. */
  expectOutage(): () => void {
    return this.openWindow("outage");
  }

  private openWindow(kind: Window): () => void {
    this.open.push(kind);
    return () => {
      const at = this.open.indexOf(kind);
      if (at >= 0) this.open.splice(at, 1);
    };
  }

  verdict(): string[] {
    const problems: string[] = [];

    for (const response of this.responses) {
      if (response.method === "POST" && response.url === REPORTS_URL && REPORT_REFUSED.has(response.status)) continue;
      const declared = this.declaredResponses.find((d) => !d.seenUrl && d.method === response.method && d.status === response.status && d.url.test(response.url));
      if (declared) declared.seenUrl = response.url;
      else problems.push(`unexpected response ${response.status} ${response.method} ${hide(response.url)}`);
    }

    for (const failure of this.failures) {
      // The page aborts its own superseded reads, and a leaving page cancels the rest.
      if (failure.error === "net::ERR_ABORTED") continue;
      const declared = this.declaredFailures.find((d) => !d.seenUrl && d.method === failure.method && d.url.test(failure.url) && d.error.test(failure.error));
      if (declared) {
        declared.seenUrl = failure.url;
        declared.seenError = failure.error;
        continue;
      }
      if (failure.windows.includes("outage") && failure.url.startsWith(BASE) && failure.error.startsWith("net::ERR_")) continue;
      problems.push(`failed request ${failure.method} ${hide(failure.url)} ${failure.error}`);
    }

    for (const entry of this.consoleEntries) {
      if (entry.type === "error" && this.consumeConsole(entry)) continue;
      problems.push(`console.${entry.type}: ${entry.text}${entry.url ? ` at ${hide(entry.url)}` : ""}`);
    }

    for (const error of this.pageErrors) problems.push(`uncaught page error: ${error}`);

    for (const declared of this.declaredResponses) {
      if (!declared.seenUrl) problems.push(`declared response never happened: ${declared.status} ${declared.method} ${declared.url}`);
    }
    for (const declared of this.declaredFailures) {
      if (!declared.seenUrl) problems.push(`declared failed request never happened: ${declared.method} ${declared.url}`);
    }
    return problems;
  }

  private consumeConsole(entry: ConsoleEntry): boolean {
    const status = entry.text.match(/^Failed to load resource: the server responded with a status of (\d+)/);
    if (status) {
      if (entry.url === REPORTS_URL && REPORT_REFUSED.has(Number(status[1]))) return true;
      const declared = this.declaredResponses.find((d) => d.seenUrl === entry.url && d.status === Number(status[1]) && !d.consoleUsed);
      if (declared) declared.consoleUsed = true;
      return Boolean(declared);
    }
    const network = entry.text.match(/^Failed to load resource: (net::ERR_\w+)/);
    if (network) {
      const declared = this.declaredFailures.find((d) => d.seenUrl === entry.url && d.seenError === network[1] && !d.consoleUsed);
      if (declared) {
        declared.consoleUsed = true;
        return true;
      }
      return entry.windows.includes("outage") && entry.url.startsWith(BASE);
    }
    return SOCKET_FAILED.test(entry.text) && entry.windows.length > 0;
  }
}
