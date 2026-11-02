import { useState } from "react";
import type { Hop } from "../../../api/types";
import type { HopFlow } from "../../../live/useHopFlow";
import { type Feed } from "../../../live/useLiveSocket";
import { Button, DataTable, EmptyState, LiveNote, Panel, Text, useMediaQuery, type Column } from "../../../ds";
import { formatTime } from "../../../logic/format";
import { recentHopColumns } from "./recentHopColumns";

function NoHops({ frozen, buffered, lost }: { frozen: boolean; buffered: number; lost: boolean }) {
  return lost ? (
    <EmptyState icon="clock" title="Hops unknown">
      The hop stream is lost. Hops appear here when it returns.
    </EmptyState>
  ) : frozen ? (
    <EmptyState icon="pulse" title="List frozen with no hops">
      Press Resume list to see new hops.
    </EmptyState>
  ) : buffered > 0 ? (
    <EmptyState icon="pulse" title="No hops in this view">
      They appear here as they arrive or as playback reaches them.
    </EmptyState>
  ) : (
    <EmptyState icon="pulse" title="No hops yet">
      Start the production line, or move a batch, and hops appear here.
    </EmptyState>
  );
}

function HopRows({ rows, columns, held, onHold }: { rows: Hop[]; columns: Column<Hop>[]; held: boolean; onHold: (holding: boolean) => void }) {
  return (
    <>
      <DataTable tiles="labelled" caption="Recent hops" columns={columns} rows={rows} rowKey={(h) => `${h.traceId}-${h.hop}-${h.state}-${h.at}`} onHold={onHold} />
      {held ? (
        <Text size="xs" tone="secondary" as="p">
          List held while the pointer or focus is inside it.
        </Text>
      ) : null}
    </>
  );
}

function listNotes(frozenAt: number | null, lost: boolean, shown: number, total: number): string {
  return [
    frozenAt !== null ? `List frozen at ${formatTime(frozenAt)}. New hops are not shown.` : null,
    lost ? "The hop stream is lost, so this list stops here until it returns." : null,
    total > shown ? `Showing the newest ${shown} of ${total} hops.` : null,
  ]
    .filter((note) => note !== null)
    .join(" ");
}

function FreezeButton({ frozen, onClick }: { frozen: boolean; onClick: () => void }) {
  return (
    <Button icon={frozen ? "play" : "stop"} onClick={onClick}>
      {frozen ? "Resume list" : "Freeze list"}
    </Button>
  );
}

export function RecentHops({ flow, feed }: { flow: HopFlow; feed: Feed }) {
  const [freeze, setFreeze] = useState<{ rows: Hop[]; at: number } | null>(null);
  const frozen = freeze?.rows ?? null;
  // While the operator is in the table its rows must stay, or a running line takes the link or button away.
  const [held, setHeld] = useState<Hop[] | null>(null);
  const compact = useMediaQuery("(max-width: 720px)");
  const lost = feed.state.status === "lost";
  const source = frozen ?? held ?? flow.snapshot.recent;
  const shown = compact ? 5 : 12;
  const matching = flow.follow ? source.filter((h) => h.traceId === flow.follow) : source;
  const rows = matching.slice(0, shown);
  const notes = listNotes(freeze?.at ?? null, lost && rows.length > 0, shown, matching.length);
  const follow = (traceId: string) => {
    setHeld(null);
    flow.setFollow(traceId, source);
  };
  const onHold = (holding: boolean) => setHeld(holding && !flow.follow ? (current) => current ?? rows : null);
  return (
    <Panel
      title="Recent hops"
      description="Text form of the diagram. Press a trace to follow only that one."
      actions={
        <FreezeButton frozen={freeze !== null} onClick={() => setFreeze(freeze ? null : { rows: flow.snapshot.recent, at: Date.now() })} />
      }
    >
      <LiveNote>{notes || null}</LiveNote>
      {rows.length === 0 ? <NoHops frozen={Boolean(frozen)} buffered={flow.snapshot.buffered} lost={lost} /> : <HopRows rows={rows} columns={recentHopColumns(follow)} held={Boolean(held && !frozen)} onHold={onHold} />}
    </Panel>
  );
}
