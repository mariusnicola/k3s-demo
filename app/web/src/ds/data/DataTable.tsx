import { useId, useRef, useState, type FocusEvent, type ReactNode } from "react";
import { Button } from "../controls/Button";

export interface Column<T> {
  key: string;
  header: string;
  render: (row: T) => ReactNode;
  align?: "end";
}

export interface DataTableProps<T> {
  /** Adds a show and hide control per row, with a full width row for the content. */
  detail?: { header: string; label: (row: T) => string; render: (row: T) => ReactNode };
  /** True while the pointer or focus is inside, so a caller can stop live rows moving under it. */
  onHold?: (holding: boolean) => void;
  /** Rows as a grid of cards, still a table to a screen reader. `compact` drops the field labels, for values that name themselves. */
  tiles?: "labelled" | "compact";
  caption: string;
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => string;
}

/** Keyed by row, so an open row stays open when live data rebuilds the list. */
function useOpenRows() {
  const [open, setOpen] = useState<ReadonlySet<string>>(new Set());
  const toggle = (key: string) =>
    setOpen((current) => {
      const next = new Set(current);
      if (!next.delete(key)) next.add(key);
      return next;
    });
  return { open, toggle };
}

function useHoldEvents(onHold: DataTableProps<unknown>["onHold"]) {
  const inside = useRef({ focus: false, pointer: false });
  const mark = (key: "focus" | "pointer", value: boolean) => {
    inside.current[key] = value;
    onHold?.(inside.current.focus || inside.current.pointer);
  };
  return {
    onFocus: () => mark("focus", true),
    onBlur: (event: FocusEvent<HTMLDivElement>) => {
      // Switching window blurs with no target, yet focus comes back here on return.
      if (document.hasFocus() && !event.currentTarget.contains(event.relatedTarget)) mark("focus", false);
    },
    onPointerEnter: () => mark("pointer", true),
    onPointerLeave: () => mark("pointer", false),
  };
}

function TableHead<T>({ columns, detail }: Pick<DataTableProps<T>, "columns" | "detail">) {
  return (
    <thead role="rowgroup">
      <tr role="row">
        {columns.map((c) => (
          <th key={c.key} scope="col" role="columnheader" data-align={c.align}>
            {c.header}
          </th>
        ))}
        {detail ? (
          <th scope="col" role="columnheader">
            {detail.header}
          </th>
        ) : null}
      </tr>
    </thead>
  );
}

interface TableRowProps<T> extends Pick<DataTableProps<T>, "columns" | "detail"> {
  row: T;
  expanded: boolean;
  detailId: string;
  onToggle: () => void;
}

function TableRow<T>({ row, columns, detail, expanded, detailId, onToggle }: TableRowProps<T>) {
  return (
    <>
      <tr role="row">
        {columns.map((c) => (
          <td key={c.key} role="cell" data-label={c.header} data-align={c.align}>
            {c.render(row)}
          </td>
        ))}
        {detail ? (
          <td role="cell" data-label={detail.header}>
            <Button variant="ghost" aria-expanded={expanded} aria-controls={expanded ? detailId : undefined} aria-label={`${expanded ? "Hide" : "Show"} ${detail.label(row)}`} onClick={onToggle}>
              {expanded ? "Hide" : "Show"}
            </Button>
          </td>
        ) : null}
      </tr>
      {detail && expanded ? (
        <tr id={detailId} role="row" className="ds-table-detail">
          <td role="cell" colSpan={columns.length + 1}>
            {detail.render(row)}
          </td>
        </tr>
      ) : null}
    </>
  );
}

export function DataTable<T>({ caption, columns, rows, rowKey, detail, onHold, tiles }: DataTableProps<T>) {
  const baseId = useId();
  const { open, toggle } = useOpenRows();
  const holdEvents = useHoldEvents(onHold);
  return (
    <div className="ds-table-wrap" {...holdEvents}>
      <table className="ds-table" role="table" data-layout={tiles ? "tiles" : undefined} data-density={tiles}>
        <caption className="ds-sr-only">{caption}</caption>
        <TableHead columns={columns} detail={detail} />
        <tbody role="rowgroup">
          {rows.map((row, index) => {
            const key = rowKey(row);
            return <TableRow key={key} row={row} columns={columns} detail={detail} expanded={open.has(key)} detailId={`${baseId}-${index}`} onToggle={() => toggle(key)} />;
          })}
        </tbody>
      </table>
    </div>
  );
}
