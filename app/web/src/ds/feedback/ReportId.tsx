import { createContext, useContext } from "react";
import { logsForReport } from "../../logic/monitor/logs";
import { AppLink } from "../layout/Layout";
import { CopyButton } from "./CopyButton";

/** Context, not a prop: the ID also shows in places a page never renders itself, as the crash page and every connection status. */
export const ReportLogsLink = createContext(false);

/** Only someone Logs would let in gets the link. */
export function ReportId({ id }: { id: string }) {
  const logsLink = useContext(ReportLogsLink);
  return (
    <span className="ds-correlation">
      <code className="ds-id">{id}</code>
      <CopyButton value={id} label="Copy report ID" />
      {logsLink ? (
        <AppLink to={logsForReport(id)} label={`Open report ${id} in Logs`}>
          Open report in Logs
        </AppLink>
      ) : null}
    </span>
  );
}
