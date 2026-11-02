import { useSearchParams } from "react-router-dom";
import { BATCH_STATES, type BatchState } from "../../api/types";
import { useSession } from "../../auth/session";
import { farmChoices, type AttentionFilter } from "../../logic/batch/batchRules";

export const ATTENTION_OPTIONS: { value: AttentionFilter; label: string }[] = [
  { value: "", label: "All batches" },
  { value: "blocked", label: "Blocked" },
  { value: "flagged", label: "Flagged" },
  { value: "inflight", label: "Event in flight" },
];

export function useBatchFilters() {
  const { me } = useSession();
  const [params, setParams] = useSearchParams();
  // A hand-edited address must not reach the API as a 400 with no way out.
  const stateParam = params.get("state") as BatchState | null;
  const state: BatchState | "" = stateParam && BATCH_STATES.includes(stateParam) ? stateParam : "";
  // After a user switch the address can name a farm this user lacks, which would end in a 403.
  const farms = farmChoices(me);
  const farmParam = params.get("farm") ?? "";
  const farm = farms.includes(farmParam) ? farmParam : "";
  const attentionParam = (params.get("attention") ?? "") as AttentionFilter;
  const attention = ATTENTION_OPTIONS.some((o) => o.value === attentionParam) ? attentionParam : "";

  const setFilter = (key: string, value: string) => {
    setParams(
      (current) => {
        const next = new URLSearchParams(current);
        if (value) next.set(key, value);
        else next.delete(key);
        return next;
      },
      { replace: true },
    );
  };
  const clearFilters = () => setParams({}, { replace: true });
  return { me, state, farms, farm, attention, setFilter, clearFilters };
}
