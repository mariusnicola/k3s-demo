import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { USERS, hopSnapshot, lineSnapshot } from "../../mocks/fixtures";
import { FakeSocket, installApi, meRoute, renderApp, useFakeSockets } from "../../mocks/harness";

type Role = "olivia" | "ian" | "viewer" | "ada";

const SIGNED_OUT = { status: 401, body: { title: "Unauthorized" } };
const MENUS: [Role, string[]][] = [
  ["olivia", ["Dashboard", "Batches", "Alerts"]],
  ["ian", ["Dashboard", "Batches", "Alerts"]],
  ["viewer", ["Dashboard", "Batches", "Alerts"]],
  ["ada", ["Dashboard", "Batches", "Alerts", "Logs"]],
];
const ROLES = MENUS.map(([user]) => user);
/** Only the admin dashboard has no role line of its own. */
const LANDED: Record<Role, string> = {
  olivia: "Your work on farm-a and farm-b.",
  ian: "Inspection across all farms.",
  viewer: "Batches on farm-a. This view is read only.",
  ada: "Live view of the running system, with controls to load it and break it.",
};

function signedIn(user: Role) {
  installApi({ "GET /api/me": meRoute(user), "GET /api/batches": { body: [] } });
  FakeSocket.greet("/ws/hops", hopSnapshot());
  FakeSocket.greet("/line/ws", lineSnapshot());
}

beforeEach(() => useFakeSockets());
afterEach(() => void vi.unstubAllGlobals());

describe("REQ-BT-055 Dashboard leads the menu and is where every role lands", () => {
  it.each(MENUS)("REQ-BT-055 %s has the menu in this order, Dashboard first", async (user, menu) => {
    signedIn(user);
    renderApp("/dashboard");
    await screen.findByRole("heading", { level: 1, name: "Dashboard" });
    const links = within(screen.getByRole("navigation", { name: "Main" })).getAllByRole("link");
    expect(links.map((link) => link.textContent)).toEqual(menu);
  });

  it.each(ROLES)("REQ-BT-055 no menu offers the design system to %s, and its address still opens", async (user) => {
    signedIn(user);
    renderApp("/design-system");
    expect(await screen.findByRole("heading", { level: 1, name: "Design system" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Design system" })).toBeNull();
  });

  it.each(ROLES)("REQ-BT-055 the root address lands %s on their dashboard", async (user) => {
    signedIn(user);
    renderApp("/");
    expect(await screen.findByText(LANDED[user])).toBeInTheDocument();
  });

  it.each(ROLES)("REQ-BT-055 signing in lands %s on their dashboard", async (user) => {
    let session = false;
    installApi({
      "POST /api/auth/dev-login": () => {
        session = true;
        return { body: USERS[user] };
      },
      "GET /api/me": () => (session ? meRoute(user) : SIGNED_OUT),
      "GET /api/batches": { body: [] },
    });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    FakeSocket.greet("/line/ws", lineSnapshot());
    renderApp("/signin");
    await userEvent.click(await screen.findByRole("button", { name: new RegExp(`Continue as ${user}`) }));
    expect(await screen.findByText(LANDED[user])).toBeInTheDocument();
  });

  it.each(ROLES)("REQ-BT-055 a session coming back to the sign-in page lands %s on their dashboard", async (user) => {
    signedIn(user);
    renderApp("/signin");
    expect(await screen.findByText(LANDED[user])).toBeInTheDocument();
  });

  it("REQ-BT-055 an expired session still returns to the address it was on", async () => {
    let session = false;
    installApi({
      "POST /api/auth/dev-login": () => {
        session = true;
        return { body: USERS.olivia };
      },
      "GET /api/me": () => (session ? meRoute("olivia") : SIGNED_OUT),
      "GET /api/batches": { body: [] },
    });
    FakeSocket.greet("/ws/hops", hopSnapshot());
    renderApp("/alerts");
    await userEvent.click(await screen.findByRole("button", { name: /Continue as olivia/ }));
    expect(await screen.findByRole("heading", { level: 1, name: "Alerts" })).toBeInTheDocument();
  });

  it("REQ-BT-055 Batches keeps a menu entry and an address of its own", async () => {
    signedIn("olivia");
    renderApp("/dashboard");
    await screen.findByRole("heading", { level: 1, name: "Dashboard" });
    expect(within(screen.getByRole("navigation", { name: "Main" })).getByRole("link", { name: "Batches" })).toHaveAttribute("href", "/batches");
    await userEvent.click(within(screen.getByRole("navigation", { name: "Main" })).getByRole("link", { name: "Batches" }));
    expect(await screen.findByRole("heading", { level: 1, name: "Batches" })).toBeInTheDocument();
  });
});
