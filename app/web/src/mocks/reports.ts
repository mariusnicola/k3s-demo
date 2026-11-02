import type { ApiCall } from "./harness";

export const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
export const REPORTS = "POST /api/browser-reports";
export const ACCEPTED = { status: 202, body: { id: "x" } };

export const posted = (calls: ApiCall[]) => calls.filter((call) => call.url.endsWith("/api/browser-reports"));
export const settle = () => new Promise((resolve) => setTimeout(resolve));
