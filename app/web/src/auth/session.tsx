import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { client, setUnauthorizedHandler } from "../api/client";
import { clearOldToken } from "./oldToken";
import { ApiError } from "../api/problem";
import type { Me } from "../api/types";

export type SessionStatus = "restoring" | "restore-failed" | "signed-out" | "signed-in";

interface Session {
  status: SessionStatus;
  me: Me | null;
  /** True when the session ended on its own, so sign in can say why. */
  expired: boolean;
  /** The cookie may still be set, so the browser may still be signed in. */
  logoutError: unknown;
  /** A sign-out is on its way, so the browser still sends the cookie. */
  logoutPending: boolean;
  /** Counts finished sign-ins, because one can land before the page's own session check and leave no status to compare. */
  signIns: number;
  restoreError: unknown;
  retryLogout: () => void;
  signIn: (user: string) => Promise<void>;
  signOut: () => void;
  retryRestore: () => void;
}

const SessionContext = createContext<Session | null>(null);

function useSessionState() {
  const [status, setStatus] = useState<SessionStatus>("restoring");
  const [me, setMe] = useState<Me | null>(null);
  const [expired, setExpired] = useState(false);
  const [logoutError, setLogoutError] = useState<unknown>(null);
  const [logoutPending, setLogoutPending] = useState(false);
  const [restoreError, setRestoreError] = useState<unknown>(null);
  const [signIns, setSignIns] = useState(0);
  // A sign-in moves the run on, so a session check sent before it cannot speak for the session after it.
  const restoreRun = useRef(0);
  // A second 401 must not overwrite why the first ended the session.
  const signedIn = useRef(false);
  return { status, setStatus, me, setMe, expired, setExpired, logoutError, setLogoutError, logoutPending, setLogoutPending, restoreError, setRestoreError, signIns, setSignIns, restoreRun, signedIn };
}

type SessionState = ReturnType<typeof useSessionState>;

function useRestore({ setStatus, setMe, setRestoreError, restoreRun, signedIn }: SessionState): () => void {
  return useCallback(() => {
    const run = (restoreRun.current += 1);
    setStatus("restoring");
    client
      .restoreSession()
      .then(({ data }) => {
        if (run !== restoreRun.current) return;
        signedIn.current = data.me !== null;
        setMe(data.me);
        setStatus(data.me ? "signed-in" : "signed-out");
      })
      .catch((error: unknown) => {
        if (run !== restoreRun.current) return;
        setRestoreError(error);
        // A client over its limit can still sign in, so a 429 is no failure to retry from.
        setStatus(error instanceof ApiError && error.status === 429 ? "signed-out" : "restore-failed");
      });
  }, []);
}

function useExpiryWatch({ setStatus, setMe, setExpired, signedIn }: SessionState, restore: () => void): void {
  useEffect(() => {
    clearOldToken();
    setUnauthorizedHandler(() => {
      if (!signedIn.current) return;
      signedIn.current = false;
      setExpired(true);
      setMe(null);
      setStatus("signed-out");
    });
    restore();
    return () => setUnauthorizedHandler(() => undefined);
  }, [restore]);
}

function useLogout({ setLogoutError, setLogoutPending }: SessionState): () => void {
  return useCallback(() => {
    setLogoutError(null);
    setLogoutPending(true);
    // A 401 means the server already holds no session, so nothing is left to say.
    client
      .logout()
      .catch((error: unknown) => setLogoutError(error instanceof ApiError && error.status === 401 ? null : error))
      .finally(() => setLogoutPending(false));
  }, []);
}

function useSignActions(session: SessionState) {
  const { setStatus, setMe, setExpired, setLogoutError, setSignIns, restoreRun, signedIn } = session;
  const logout = useLogout(session);
  const signIn = useCallback(async (user: string) => {
    const { data } = await client.devLogin(user);
    restoreRun.current += 1;
    signedIn.current = true;
    setMe(data);
    setExpired(false);
    setLogoutError(null);
    setSignIns((count) => count + 1);
    setStatus("signed-in");
  }, []);

  const signOut = useCallback(() => {
    signedIn.current = false;
    logout();
    setMe(null);
    setExpired(false);
    setStatus("signed-out");
  }, [logout]);

  return { signIn, logout, signOut };
}

export function SessionProvider({ children }: { children: ReactNode }) {
  const session = useSessionState();
  const { status, me, expired, logoutError, logoutPending, signIns, restoreError } = session;
  const restore = useRestore(session);
  useExpiryWatch(session, restore);
  const { signIn, logout, signOut } = useSignActions(session);

  const value = useMemo(() => ({ status, me, expired, logoutError, logoutPending, signIns, restoreError, retryLogout: logout, signIn, signOut, retryRestore: restore }), [status, me, expired, logoutError, logoutPending, signIns, restoreError, logout, signIn, signOut, restore]);
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): Session {
  const session = useContext(SessionContext);
  if (!session) throw new Error("useSession outside SessionProvider");
  return session;
}
