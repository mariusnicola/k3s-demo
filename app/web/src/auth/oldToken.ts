const OLD_TOKEN_KEY = "bt.token";

/** The session is an HttpOnly cookie now, so a token kept by an older page must not stay readable. */
export function clearOldToken(): void {
  try {
    sessionStorage.removeItem(OLD_TOKEN_KEY);
  } catch {
    /* A blocked store holds nothing to remove. */
  }
}
