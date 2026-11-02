import { useState } from "react";
import { Button } from "../controls/Button";

async function copyText(value: string): Promise<boolean> {
  try {
    if (navigator.clipboard) {
      await navigator.clipboard.writeText(value);
      return true;
    }
    // Plain http has no clipboard API, and this app also runs there.
    const focused = document.activeElement as HTMLElement | null;
    const area = document.createElement("textarea");
    area.value = value;
    area.readOnly = true;
    area.className = "ds-sr-only";
    document.body.append(area);
    area.select();
    const copied = document.execCommand("copy");
    area.remove();
    focused?.focus();
    return copied;
  } catch {
    return false;
  }
}

export function CopyButton({ value, label }: { value: string; label: string }) {
  const [result, setResult] = useState<"copied" | "failed" | null>(null);
  return (
    <>
      <Button variant="ghost" icon="copy" aria-label={label} onClick={() => void copyText(value).then((copied) => setResult(copied ? "copied" : "failed"))}>
        Copy
      </Button>
      <span role="status" className="ds-copy-result">
        {result === "copied" ? "Copied" : result === "failed" ? "Could not copy. Select the ID." : null}
      </span>
    </>
  );
}
