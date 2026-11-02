import { useSession } from "../../auth/session";
import { roleOf } from "../../logic/batch/roleDashboard";
import { useDocumentTitle } from "../../ds";
import { AdminDashboard } from "./AdminDashboard";
import { RoleDashboard } from "./roles/RoleDashboard";

export function DashboardPage() {
  const { me } = useSession();
  useDocumentTitle("Dashboard");
  const role = roleOf(me);
  return role === "admin" ? <AdminDashboard /> : <RoleDashboard role={role} />;
}
