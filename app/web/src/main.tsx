import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import "@fontsource-variable/hanken-grotesk";
import "@fontsource-variable/jetbrains-mono";
// Order is the cascade: a later file overrides an earlier one.
import "./ds/tokens.css";
import "./ds/component-tokens.css";
import "./ds/base.css";
import "./ds/icon.css";
import "./ds/layout/primitives.css";
import "./ds/feedback/operations.css";
import "./ds/layout/shell.css";
import "./ds/layout/page.css";
import "./ds/controls/button.css";
import "./ds/feedback/spinner.css";
import "./ds/controls/fields.css";
import "./ds/controls/lever.css";
import "./ds/map/part-tip.css";
import "./ds/data/status.css";
import "./ds/chart/chart.css";
import "./ds/data/table.css";
import "./ds/data/figures.css";
import "./ds/data/records.css";
import "./ds/data/units.css";
import "./ds/feedback/notices.css";
import "./ds/feedback/checklist.css";
import "./ds/feedback/error-detail.css";
import "./ds/feedback/skeleton.css";
import "./ds/feedback/connection.css";
import "./ds/controls/overlay.css";
import "./ds/map/map.css";
import "./ds/layout/catalog.css";
import { App } from "./App";
import { SessionProvider } from "./auth/session";
import { postBrowserReport } from "./api/client";
import { startBrowserReports } from "./api/reports";
import { applyTheme } from "./ds";

applyTheme();
startBrowserReports(postBrowserReport);

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <BrowserRouter>
      <SessionProvider>
        <App />
      </SessionProvider>
    </BrowserRouter>
  </StrictMode>,
);
