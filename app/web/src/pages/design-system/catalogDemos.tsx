import { useState } from "react";
import { Button, Checkbox, CountFilterList, DataTable, Dialog, KeyValues, RadioGroup, SegmentedControl, Text } from "../../ds";
import { ConfirmDialog, FAIL_NEXT_CONFIRM } from "../shared";
import { LONG_TEXT } from "./catalogSamples";

export function DialogDemo() {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button onClick={() => setOpen(true)}>Open dialog</Button>
      <Dialog
        open={open}
        onOpenChange={setOpen}
        title="Close this batch"
        description="Closed is final."
        footer={
          <>
            <Button onClick={() => setOpen(false)}>Cancel</Button>
            <Button variant="primary" onClick={() => setOpen(false)}>
              Confirm
            </Button>
          </>
        }
      >
        <Text as="p">Dialog body.</Text>
      </Dialog>
    </>
  );
}

/** Confirm runs a pretend write, because the busy dialog refuses to close and needs its own end. */
export function ConfirmDemo({ danger }: { danger?: boolean }) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const confirm = () => {
    setBusy(true);
    setTimeout(() => {
      setBusy(false);
      setOpen(false);
    }, 1500);
  };
  return (
    <>
      <Button onClick={() => setOpen(true)}>Open confirmation</Button>
      <ConfirmDialog open={open} {...FAIL_NEXT_CONFIRM} danger={danger} busy={busy} onClose={() => setOpen(false)} onConfirm={confirm} />
    </>
  );
}

export function Choice({ kind, disabled, help }: { kind: "radio" | "segmented"; disabled?: boolean; help?: boolean }) {
  const [value, setValue] = useState("a");
  const Control = kind === "radio" ? RadioGroup : SegmentedControl;
  return (
    <Control
      legend={kind === "radio" ? "Result" : "Speed"}
      value={value}
      onChange={setValue}
      disabled={disabled}
      help={help && kind === "segmented" ? "Explains the whole control." : undefined}
      options={[
        { value: "a", label: "First", help: help && kind === "radio" ? "Explains this choice." : undefined },
        { value: "b", label: "Second", help: help && kind === "radio" ? "Explains this choice." : undefined },
      ]}
    />
  );
}

export function CountFilterDemo({ initial = "" }: { initial?: string }) {
  const [selected, setSelected] = useState(initial);
  return (
    <CountFilterList
      label="Count per reason"
      selected={selected}
      onSelect={(value) => setSelected(value === selected ? "" : value)}
      items={[
        { value: "api-timeout", title: "The API did not answer in time.", detail: "api-timeout", count: 41 },
        { value: "record-wait", title: LONG_TEXT, detail: "record-wait", count: 3 },
      ]}
    />
  );
}

export function DetailTableDemo() {
  return (
    <DataTable
      caption="Expandable example"
      columns={[{ key: "a", header: "Reason", render: (r: { a: string }) => r.a }]}
      rows={[{ a: "The API did not answer in time." }]}
      rowKey={(r) => r.a}
      detail={{ header: "Details", label: (r) => `details of ${r.a}`, render: () => <KeyValues items={[{ label: "Last HTTP status", value: "503" }]} /> }}
    />
  );
}

export function CheckDemo({ initial, disabled }: { initial: boolean; disabled?: boolean }) {
  const [checked, setChecked] = useState(initial);
  return <Checkbox label="RealTime Monitor" help="Dots move as hops arrive." checked={checked} disabled={disabled} onChange={setChecked} />;
}
