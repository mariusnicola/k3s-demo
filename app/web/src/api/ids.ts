function hex(bytes: Uint8Array): string {
  return [...bytes].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

/** Not randomUUID: that exists only on https, and this app also runs on plain http. */
export function newCorrelationId(): string {
  return hex(crypto.getRandomValues(new Uint8Array(16)));
}

/** A version 4 UUID, for the same reason as above. */
export function newReportId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6]! & 0x0f) | 0x40;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const text = hex(bytes);
  return [text.slice(0, 8), text.slice(8, 12), text.slice(12, 16), text.slice(16, 20), text.slice(20)].join("-");
}
