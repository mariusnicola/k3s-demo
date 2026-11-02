import { type MonitorReport } from "../../../api/monitorTypes";
import { formatDateTime } from "../../../logic/format";
import { Grid } from "../../../ds";
import { CapsPanel } from "./CapsPanel";
import { monitorPlaceholder } from "./MissingSection";
import { NodesPanel } from "./NodesPanel";
import { PodsPanel } from "./PodsPanel";

export function ClusterPanels({ report, fresh, lost }: { report: MonitorReport | null; fresh: boolean; lost: boolean }) {
  const staleNote = report && !fresh ? `Last known values from ${formatDateTime(report.checkedAt)}.` : undefined;
  const placeholder = monitorPlaceholder(lost);
  return (
    <Grid min="28rem">
      <NodesPanel report={report} fresh={fresh} staleNote={staleNote} placeholder={placeholder} />
      <CapsPanel report={report} fresh={fresh} staleNote={staleNote} placeholder={placeholder} />
      <PodsPanel report={report} fresh={fresh} staleNote={staleNote} placeholder={placeholder} />
    </Grid>
  );
}
