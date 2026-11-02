import { Suspense, lazy } from "react";
import { Navigate, Outlet, Route, Routes, useLocation } from "react-router-dom";
import { describeProblem } from "./api/problem";
import { useSession } from "./auth/session";
import { can } from "./logic/batch/batchRules";
import { HopProvider } from "./live/hopFeed";
import { MonitorProvider } from "./live/monitorFeed";
import { ClusterOperations } from "./pages/operations/ClusterOperations";
import { RetryStatus } from "./pages/RetryStatus";
import { AppLink, AppShell, Banner, Button, EmptyState, ErrorBoundary, ErrorDetail, ErrorState, IconButton, ReportLogsLink, PageHeader, Panel, Spinner, Text, useDocumentTitle, useTheme, type NavItem, type ThemeChoice } from "./ds";
import { AlertsPage } from "./pages/alerts/AlertsPage";
import { BatchDetailPage } from "./pages/batch-detail/BatchDetailPage";
import { BatchesPage } from "./pages/batches/BatchesPage";
import { SignInPage } from "./pages/signin/SignInPage";
import { sessionProblem, useCanOpenLogs } from "./pages/shared";

const DashboardPage = lazy(() => import("./pages/dashboard/DashboardPage").then((m) => ({ default: m.DashboardPage })));
const LogsPage = lazy(() => import("./pages/logs/LogsPage").then((m) => ({ default: m.LogsPage })));
const DesignSystemPage = lazy(() => import("./pages/design-system/DesignSystemPage").then((m) => ({ default: m.DesignSystemPage })));

const NAV: NavItem[] = [
  { to: "/dashboard", label: "Dashboard", icon: "pulse" },
  { to: "/batches", label: "Batches", icon: "list" },
  { to: "/alerts", label: "Alerts", icon: "bell" },
  { to: "/logs", label: "Logs", icon: "warning" },
];

const THEME_ICON = { system: "display", light: "sun", dark: "moon" } as const;
const THEME_LABEL: Record<ThemeChoice, string> = { system: "system", light: "light", dark: "dark" };

function Account() {
  const { me, signOut } = useSession();
  const theme = useTheme();
  return (
    <>
      <IconButton icon={THEME_ICON[theme.choice]} label={`Theme: ${THEME_LABEL[theme.choice]}. Press to change.`} onClick={theme.next} />
      {me ? (
        <>
          <span>{me.user}</span>
          <IconButton icon="exit" label={`Switch user, signed in as ${me.user}`} onClick={signOut} />
        </>
      ) : (
        <AppLink to="/signin">Sign in</AppLink>
      )}
    </>
  );
}

/** A page that sends the user elsewhere says why in the router state. */
function RedirectNotice() {
  const { state } = useLocation();
  const notice = typeof state?.notice === "string" ? state.notice : null;
  return notice ? <Banner tone="info" title={notice} /> : null;
}

function Shell() {
  const { pathname } = useLocation();
  const { me } = useSession();
  const nav = NAV.filter((item) => item.to !== "/logs" || can(me, "ops.admin"));
  return (
    <AppShell nav={nav} account={<Account />}>
      <RetryStatus />
      <RedirectNotice />
      <ErrorBoundary key={pathname}>
        <Suspense fallback={<Spinner label="Loading page" />}>
          <Outlet />
        </Suspense>
      </ErrorBoundary>
    </AppShell>
  );
}

function RequireSession() {
  const { status, restoreError, retryRestore, signOut } = useSession();
  const location = useLocation();
  if (status === "restoring") {
    return <Spinner label="Restoring your session" />;
  }
  if (status === "restore-failed") {
    return (
      <ErrorState
        title={describeProblem(restoreError).title}
        action={
          <>
            <Button variant="primary" icon="refresh" onClick={retryRestore}>
              Try again
            </Button>
            <Button onClick={signOut}>Go to sign in</Button>
          </>
        }
      >
        <ErrorDetail explanation={sessionProblem("Your session could not be checked.", "Try again, or go to sign in.", restoreError)} />
      </ErrorState>
    );
  }
  if (status === "signed-out") {
    const next = `${location.pathname}${location.search}`;
    return <Navigate to={`/signin?next=${encodeURIComponent(next)}`} replace />;
  }
  return <Outlet />;
}

function NotFoundPage() {
  useDocumentTitle("Page not found");
  return (
    <>
      <PageHeader title="Page not found" />
      <Panel>
        <EmptyState icon="info" title="Nothing lives at this address">
          <Text>
            Go to <AppLink to="/batches">all batches</AppLink>.
          </Text>
        </EmptyState>
      </Panel>
    </>
  );
}

export function App() {
  const logsLink = useCanOpenLogs();
  return (
    <ReportLogsLink.Provider value={logsLink}>
    <MonitorProvider>
      <ClusterOperations />
      <HopProvider>
      <Routes>
      <Route path="/signin" element={<SignInPage />} />
      <Route element={<Shell />}>
        <Route path="/design-system" element={<DesignSystemPage />} />
        <Route element={<RequireSession />}>
          <Route path="/" element={<Navigate to="/dashboard" replace />} />
          <Route path="/batches" element={<BatchesPage />} />
          <Route path="/batches/:farmId/:id" element={<BatchDetailPage />} />
          <Route path="/alerts" element={<AlertsPage />} />
          <Route path="/dashboard" element={<DashboardPage />} />
          <Route path="/logs" element={<LogsPage />} />
          <Route path="*" element={<NotFoundPage />} />
        </Route>
      </Route>
      </Routes>
      </HopProvider>
    </MonitorProvider>
    </ReportLogsLink.Provider>
  );
}
