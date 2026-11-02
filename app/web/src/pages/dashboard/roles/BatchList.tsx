import { useState } from "react";
import type { Batch } from "../../../api/types";
import { BATCH_LIST_LIMIT } from "../../../config";
import { useHopState } from "../../../live/hopFeed";
import { formatDateTime } from "../../../logic/format";
import { DataTable, EmptyState, LiveNote, Panel, Skeleton, Stack, Text, type Column } from "../../../ds";
import { ProblemBanner, ProblemState } from "../../shared";
import type { ListRead } from "./roleReads";

function footnoteTexts(notes: (string | null | undefined)[]) {
  return notes.filter(Boolean).map((text) => (
    <Text key={text} size="xs" tone="secondary" as="p">
      {text}
    </Text>
  ));
}

export function BatchList({ title, rows, read, columns, empty, note, doubtful }: { title: string; rows: Batch[] | undefined; read: ListRead; columns: Column<Batch>[]; empty: string; note?: string | null; doubtful?: boolean }) {
  const { list, capped } = read;
  const [held, setHeld] = useState<Batch[] | null>(null);
  const shown = held ?? rows;
  const { feed } = useHopState();
  const lost = feed.state.status === "lost";
  const footnotes = footnoteTexts([note, capped ? `Only the newest ${BATCH_LIST_LIMIT} batches were read, so older ones are not here.` : null, held ? "List held while the pointer or focus is inside it." : null]);
  return (
    <Panel title={title}>
      {list.error && shown ? <ProblemBanner error={list.error} onRetry={list.reload} /> : null}
      {list.error && !shown ? (
        <ProblemState error={list.error} onRetry={list.reload} />
      ) : !shown ? (
        <Skeleton rows={3} label={`Loading ${title.toLowerCase()}`} />
      ) : shown.length === 0 ? (
        <Stack gap={2}>
          <EmptyState icon={capped || doubtful ? "info" : "check"} title={capped ? `Nothing waiting in the newest ${BATCH_LIST_LIMIT} batches` : empty} />
          {footnotes}
        </Stack>
      ) : (
        <Stack gap={2}>
          <DataTable caption={title} columns={columns} rows={shown} rowKey={(b) => `${b.farmId}/${b.id}`} onHold={(holding) => setHeld(holding ? (current) => current ?? shown : null)} />
          {footnotes}
        </Stack>
      )}
      <LiveNote>{lost ? `This list may be out of date: live updates stopped at ${formatDateTime(feed.state.lastDataAt)}.` : null}</LiveNote>
    </Panel>
  );
}
