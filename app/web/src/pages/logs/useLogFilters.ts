import { useSearchParams } from "react-router-dom";
import type { LogFilters } from "../../api/types";
import { useSession } from "../../auth/session";
import { farmChoices } from "../../logic/batch/batchRules";
import { readLogFilters } from "../../logic/monitor/logs";

export function useLogFilters() {
  const { me } = useSession();
  const [params, setParams] = useSearchParams();
  const farms = farmChoices(me);
  const filters = readLogFilters(params, farms);
  const setFilter = (key: keyof LogFilters, value: string) => {
    if (filters[key] === value) return;
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
  return { farms, filters, setFilter, clearFilters };
}
