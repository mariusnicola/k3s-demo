/** Every address the suite uses. The browser and the tests share one origin. */
export const BASE = process.env.BT_BASE_URL ?? "http://<INGRESS_IP>";
export const API = `${BASE}/api`;
export const LINE = `${BASE}/line`;

export const USERS = ["olivia", "ian", "ada", "viewer"] as const;
export type User = (typeof USERS)[number];

export const WIDTHS = [360, 1440] as const;
export const HEIGHT = 900;
