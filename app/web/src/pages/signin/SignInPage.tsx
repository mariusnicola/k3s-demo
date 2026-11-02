import { useState } from "react";
import { Navigate, useSearchParams } from "react-router-dom";
import { useSession } from "../../auth/session";
import { useAction } from "../../live/useAction";
import { CenteredPage, PageHeader, Stack, useDocumentTitle } from "../../ds";
import { ProblemBanner } from "../shared";
import { DemoUserChoices } from "./DemoUserChoices";
import { SessionBanners } from "./SessionBanners";

/** A browser reads a backslash as a slash and drops tabs and newlines, so these would turn the path into another site. */
const UNSAFE_PATH = /[\\\u0000-\u001f\u007f]/;

function safeNext(next: string | null): string {
  return next && next.startsWith("/") && !next.startsWith("//") && !UNSAFE_PATH.test(next) ? next : "/";
}

export function SignInPage() {
  const { status, signIn, expired, logoutError, retryLogout } = useSession();
  const [params] = useSearchParams();
  const action = useAction();
  const [choosing, setChoosing] = useState<string | null>(null);
  useDocumentTitle("Sign in");

  if (status === "signed-in") return <Navigate to={safeNext(params.get("next"))} replace />;

  const choose = (user: string) => {
    setChoosing(user);
    void action.run(() => signIn(user));
  };

  return (
    <CenteredPage>
      <Stack gap={4}>
        <PageHeader title="Sign in to Batch Tracker" subtitle="Demo environment. Pick a user to see the app with that user's permissions." />
        <SessionBanners logoutError={logoutError} expired={expired} onRetryLogout={retryLogout} />
        {action.error ? <ProblemBanner error={action.error} onRetry={() => choosing && choose(choosing)} /> : null}
        <DemoUserChoices busy={action.busy} choosing={choosing} onChoose={choose} />
      </Stack>
    </CenteredPage>
  );
}
