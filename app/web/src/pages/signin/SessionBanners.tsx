import { Banner, Button, ErrorDetail } from "../../ds";
import { sessionProblem } from "../shared";

export function SessionBanners({ logoutError, expired, onRetryLogout }: { logoutError: unknown; expired: boolean; onRetryLogout: () => void }) {
  return (
    <>
      {logoutError ? (
        <Banner tone="warning" title="Sign out did not finish" actions={<Button onClick={onRetryLogout}>Sign out again</Button>}>
          <ErrorDetail explanation={sessionProblem("The server did not confirm it, so this browser may still be signed in.", "Sign out again.", logoutError)} />
        </Banner>
      ) : null}
      {expired ? (
        <Banner tone="warning" title="Your session ended">
          Pick a user to continue where you left off.
        </Banner>
      ) : null}
    </>
  );
}
