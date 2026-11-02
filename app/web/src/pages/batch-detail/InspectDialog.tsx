import { useState } from "react";
import { Button, Dialog, RadioGroup, TextArea } from "../../ds";
import { BusyNote } from "../shared";

function InspectFooter({ busy, failing, onClose, onSubmit }: { busy: boolean; failing: boolean; onClose: () => void; onSubmit: () => void }) {
  return (
    <>
      <Button disabled={busy} onClick={onClose}>
        Cancel
      </Button>
      <Button variant={failing ? "danger" : "primary"} loading={busy} onClick={onSubmit}>
        {failing ? "Reject batch" : "Record pass"}
      </Button>
    </>
  );
}

function ResultChoice({ result, onChange }: { result: "pass" | "fail"; onChange: (value: "pass" | "fail") => void }) {
  return (
    <RadioGroup
      legend="Result"
      value={result}
      onChange={onChange}
      options={[
        { value: "pass", label: "Pass", help: "The batch moves to Inspected." },
        { value: "fail", label: "Fail", help: "The batch moves to Rejected." },
      ]}
    />
  );
}

export function InspectDialog({ open, busy, reason, onReason, onClose, onSubmit }: { open: boolean; busy: boolean; reason: string; onReason: (value: string) => void; onClose: () => void; onSubmit: (passed: boolean, reason: string | null) => void }) {
  const [result, setResult] = useState<"pass" | "fail">(reason ? "fail" : "pass");
  const [reasonError, setReasonError] = useState<string | null>(null);

  const submit = () => {
    if (result === "fail" && !reason.trim()) {
      setReasonError("A failed inspection needs a reason. It goes on the compliance record.");
      return;
    }
    setReasonError(null);
    onSubmit(result === "pass", result === "fail" ? reason.trim() : null);
  };

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) onClose();
      }}
      title="Record inspection"
      description="A failed inspection rejects the batch. Rejected is final."
      footer={<InspectFooter busy={busy} failing={result === "fail"} onClose={onClose} onSubmit={submit} />}
    >
      <ResultChoice result={result} onChange={setResult} />
      <BusyNote busy={busy} />
      {result === "fail" ? <TextArea label="Reason" required value={reason} error={reasonError} onChange={(e) => onReason(e.target.value)} /> : null}
    </Dialog>
  );
}
