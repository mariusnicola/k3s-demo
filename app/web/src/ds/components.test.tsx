import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { Button, ConnectionStatus, DataTable, Meter, Stat, StateRail, TextInput } from "./index";
import { formatCount } from "../logic/format";

describe("Button", () => {
  it("REQ-BT-034 disabled with reason stays focusable, shows why, ignores the press", async () => {
    const onClick = vi.fn();
    render(
      <Button disabledReason="Your user lacks batch.write." onClick={onClick}>
        Move
      </Button>,
    );
    const button = screen.getByRole("button", { name: "Move" });
    expect(button).toHaveAttribute("aria-disabled", "true");
    expect(button).toHaveAccessibleDescription("Your user lacks batch.write.");
    expect(screen.getByText("Your user lacks batch.write.")).toBeVisible();
    await userEvent.tab();
    expect(button).toHaveFocus();
    await userEvent.click(button);
    expect(onClick).not.toHaveBeenCalled();
  });

  it("REQ-BT-034 loading ignores a second press", async () => {
    const onClick = vi.fn();
    render(
      <Button loading onClick={onClick}>
        Save
      </Button>,
    );
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(onClick).not.toHaveBeenCalled();
    expect(screen.getByRole("button")).toHaveAttribute("aria-busy", "true");
  });
});

describe("TextInput", () => {
  it("REQ-BT-034 ties error text to the field", () => {
    render(<TextInput label="Product" required error="Enter the product name." />);
    const input = screen.getByRole("textbox", { name: /Product/ });
    expect(input).toHaveAttribute("aria-invalid", "true");
    expect(input).toHaveAccessibleDescription("Enter the product name.");
  });
});

describe("edge values", () => {
  it("REQ-BT-034 Stat shows zero and a huge number", () => {
    const { rerender } = render(
      <MemoryRouter>
        <Stat label="Blocked" value={0} tone="danger" />
      </MemoryRouter>,
    );
    expect(screen.getByText("0")).toBeInTheDocument();
    rerender(
      <MemoryRouter>
        <Stat label="Blocked" value={1234567890} tone="danger" />
      </MemoryRouter>,
    );
    expect(screen.getByText(new Intl.NumberFormat().format(1234567890))).toBeInTheDocument();
  });

  it("REQ-BT-046 Stat with no value says unknown, never zero", () => {
    render(
      <MemoryRouter>
        <Stat label="Rejected" value={null} tone="danger" detail="1.5 per minute" />
      </MemoryRouter>,
    );
    expect(screen.getByText("Unknown")).toBeInTheDocument();
    expect(screen.queryByText("0")).toBeNull();
    expect(screen.getByText("1.5 per minute")).toBeInTheDocument();
  });

  it("REQ-BT-034 Stat can show a worded value, large", () => {
    const { container } = render(
      <MemoryRouter>
        <Stat label="Checks healthy" text="27 of 27" hero />
      </MemoryRouter>,
    );
    expect(screen.getByText("27 of 27")).toBeInTheDocument();
    expect(container.querySelector(".ds-stat")).toHaveAttribute("data-hero", "true");
  });

  it("REQ-BT-034 Stat raises its tone only above zero", () => {
    const { container, rerender } = render(<Stat label="Blocked" value={0} tone="danger" />);
    expect(container.querySelector(".ds-stat")).not.toHaveAttribute("data-tone");
    rerender(<Stat label="Blocked" value={1} tone="danger" />);
    expect(container.querySelector(".ds-stat")).toHaveAttribute("data-tone", "danger");
  });

  it("REQ-BT-034 Meter over its maximum stays full and says so in text", () => {
    const { container } = render(<Meter label="CPU" value={4500} max={4000} unit="m" breach />);
    expect(container.querySelector(".ds-meter-fill")).toHaveAttribute("data-ratio", "100");
    expect(screen.getByText(/of .* m$/)).toBeInTheDocument();
  });

  it("REQ-BT-034 a reserved Meter is drawn apart from a live one and says what is left", () => {
    const { container } = render(<Meter label="CPU" value={3000} max={4000} unit="m" reserved />);
    expect(container.querySelector(".ds-meter")).toHaveAttribute("data-reserved");
    expect(screen.getByText(`${formatCount(3000)} of ${formatCount(4000)} m, ${formatCount(1000)} m free`)).toBeInTheDocument();
  });

  it("REQ-BT-034 a reserved Meter over its maximum says by how much", () => {
    render(<Meter label="CPU" value={4500} max={4000} unit="m" reserved breach />);
    expect(screen.getByText(`${formatCount(4500)} of ${formatCount(4000)} m, 500 m over`)).toBeInTheDocument();
  });

  it("REQ-BT-034 Meter with a zero maximum does not divide by zero", () => {
    const { container } = render(<Meter label="CPU" value={0} max={0} unit="m" />);
    expect(container.querySelector(".ds-meter-fill")).toHaveAttribute("data-ratio", "0");
  });

  it("REQ-BT-034 StateRail reads a rejected batch as text", () => {
    render(<StateRail state="Rejected" />);
    expect(screen.getByRole("img", { name: /rejected/i })).toBeInTheDocument();
  });

  it("REQ-BT-035 DataTable labels each cell for the stacked phone layout", () => {
    render(<DataTable caption="c" columns={[{ key: "a", header: "Product", render: (r: { a: string }) => r.a }]} rows={[{ a: "x" }]} rowKey={(r) => r.a} />);
    expect(screen.getByRole("cell", { name: "x" })).toHaveAttribute("data-label", "Product");
  });
});

describe("ConnectionStatus", () => {
  const now = Date.parse("2026-10-30T10:10:00Z");
  const last = now - 65_000;

  it("REQ-BT-030 lost shows the last data time and never the live word", () => {
    const { container } = render(<ConnectionStatus label="Monitor" state={{ status: "lost", lastDataAt: last }} now={now} onReconnect={() => undefined} />);
    expect(screen.getByText(/connection lost/)).toBeInTheDocument();
    expect(screen.getByText(/Last data .*1 min 5 s ago/)).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/: live/);
    expect(container.querySelector('[data-status="open"]')).toBeNull();
  });

  it("REQ-BT-030 lost before any data says so", () => {
    render(<ConnectionStatus label="Monitor" state={{ status: "lost", lastDataAt: null }} now={now} />);
    expect(screen.getByText(/No data received/)).toBeInTheDocument();
  });

  it("REQ-BT-030 an open socket with old data reads stale, not live", () => {
    const { container } = render(<ConnectionStatus label="Monitor" state={{ status: "open", lastDataAt: last }} now={now} stale />);
    expect(screen.getByText(/data stale/)).toBeInTheDocument();
    expect(container.querySelector('[data-status="open"]')).toBeNull();
  });

  it("REQ-BT-030 reconnect now is offered and works", async () => {
    const onReconnect = vi.fn();
    render(<ConnectionStatus label="Monitor" state={{ status: "lost", lastDataAt: last }} now={now} onReconnect={onReconnect} />);
    await userEvent.click(screen.getByRole("button", { name: "Reconnect now" }));
    expect(onReconnect).toHaveBeenCalled();
  });
});
