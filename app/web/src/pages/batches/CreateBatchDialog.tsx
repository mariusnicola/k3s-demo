import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { client } from "../../api/client";
import { useAction } from "../../live/useAction";
import { Button, Dialog, Select, Stack, TextInput } from "../../ds";
import { BusyNote, ProblemBanner } from "../shared";

function useCreateBatch(defaultFarm: string) {
  const navigate = useNavigate();
  const [product, setProduct] = useState("");
  const [farm, setFarm] = useState(defaultFarm);
  const [fieldError, setFieldError] = useState<string | null>(null);
  const action = useAction();

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const name = product.trim();
    if (!name) {
      setFieldError("Enter the product name.");
      return;
    }
    setFieldError(null);
    await action.run(async () => {
      const { data } = await client.createBatch(farm, name);
      navigate(`/batches/${encodeURIComponent(data.farmId)}/${encodeURIComponent(data.id)}`);
    });
  };
  return { product, setProduct, farm, setFarm, fieldError, action, submit };
}

function CreateFooter({ busy, onClose }: { busy: boolean; onClose: () => void }) {
  return (
    <>
      <Button disabled={busy} onClick={onClose}>
        Cancel
      </Button>
      <Button variant="primary" type="submit" form="create-batch" loading={busy}>
        Create batch
      </Button>
    </>
  );
}

export function CreateBatchDialog({ open, onClose, farms, defaultFarm }: { open: boolean; onClose: () => void; farms: string[]; defaultFarm: string }) {
  const { product, setProduct, farm, setFarm, fieldError, action, submit } = useCreateBatch(defaultFarm);

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !action.busy) onClose();
      }}
      title="New batch"
      description="The batch starts in Created. You can move it to Dose 1 from its page."
      footer={<CreateFooter busy={action.busy} onClose={onClose} />}
    >
      <form id="create-batch" onSubmit={(e) => void submit(e)} noValidate>
        <Stack gap={3}>
          <BusyNote busy={action.busy} />
          {action.error ? <ProblemBanner error={action.error} onDismiss={action.clearError} /> : null}
          <TextInput label="Product" required autoComplete="off" value={product} error={fieldError} onChange={(e) => setProduct(e.target.value)} />
          <Select label="Farm" required value={farm} onChange={(e) => setFarm(e.target.value)} options={farms.map((f) => ({ value: f, label: f }))} />
        </Stack>
      </form>
    </Dialog>
  );
}
