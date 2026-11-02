import { fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { LineChart } from "./Chart";

const ms = (seconds: number) => `${Math.round(seconds * 1000)} ms`;
const points = [
  { at: "a", value: 0.012 },
  { at: "b", value: null },
  { at: "c", value: 0.04 },
];
const series = [{ label: "Queue", points }];
const at = (time: string) => time;
const chart = () => screen.getByRole("img", { name: /^Stage/ });
const axis = (container: HTMLElement) => [...container.querySelectorAll(".ds-chart-axis")].map((t) => t.textContent);

describe("LineChart values", () => {
  it("REQ-BT-026 the axis shows small values through the formatter, each mark its own", () => {
    const { container } = render(<LineChart caption="Stage" series={series} formatAt={at} formatValue={ms} />);
    expect(axis(container)).toEqual(expect.arrayContaining(["0 ms", "17 ms", "33 ms", "50 ms"]));
  });

  it("REQ-BT-026 the tooltip uses the formatter, and an unknown bucket stays Unknown", () => {
    render(<LineChart caption="Stage" series={series} formatAt={at} formatValue={ms} />);
    fireEvent.keyDown(chart(), { key: "End" });
    expect(screen.getByRole("tooltip")).toHaveTextContent("40 ms Queue");
    fireEvent.keyDown(chart(), { key: "ArrowLeft" });
    expect(screen.getByRole("tooltip")).toHaveTextContent("Unknown Queue");
  });

  it("REQ-BT-026 the tooltip is the description of the chart while a bucket is read", () => {
    render(<LineChart caption="Stage" series={series} formatAt={at} formatValue={ms} />);
    expect(chart()).not.toHaveAccessibleDescription();
    fireEvent.keyDown(chart(), { key: "End" });
    expect(chart()).toHaveAccessibleDescription(/40 ms Queue/);
  });

  it("REQ-BT-026 each Show values button names its chart", () => {
    render(
      <>
        <LineChart caption="Stage one" series={series} formatAt={at} />
        <LineChart caption="Stage two" series={series} formatAt={at} />
      </>,
    );
    expect(screen.getByRole("button", { name: "Show values, Stage one" })).toHaveTextContent("Show values");
    expect(screen.getByRole("button", { name: "Show values, Stage two" })).toBeInTheDocument();
  });

  it("REQ-BT-026 the values table uses the formatter, and an unknown bucket stays Unknown", async () => {
    render(<LineChart caption="Stage" series={series} formatAt={at} formatValue={ms} />);
    await userEvent.click(screen.getByRole("button", { name: "Show values, Stage" }));
    const rows = within(screen.getByRole("table", { name: "Stage values" })).getAllByRole("row");
    expect(rows[1]).toHaveTextContent("12 ms");
    expect(rows[2]).toHaveTextContent("Unknown");
  });

  it("REQ-BT-026 with no formatter a chart reads whole counts, as before", () => {
    const { container } = render(<LineChart caption="Stage" series={[{ label: "Closed", points: [{ at: "a", value: 900 }] }]} formatAt={at} />);
    expect(axis(container)).toEqual(expect.arrayContaining(["0", "333", "667", "1,000"]));
  });
});

describe("LineChart buckets", () => {
  const buckets = ["a", "b", "c", "d"];

  it("REQ-BT-026 charts given one list have one count, however many points each holds", () => {
    render(
      <>
        <LineChart caption="Stage one" series={series} formatAt={at} buckets={buckets} />
        <LineChart caption="Stage two" series={[{ label: "Pending", points: [{ at: "d", value: 3 }] }]} formatAt={at} buckets={buckets} />
      </>,
    );
    expect(screen.getByRole("img", { name: /^Stage one, 4 buckets/ })).toBeInTheDocument();
    expect(screen.getByRole("img", { name: /^Stage two, 4 buckets/ })).toBeInTheDocument();
  });

  it("REQ-BT-026 a time the series lacks reads Unknown, and its value stays at its own time", () => {
    render(<LineChart caption="Stage" series={[{ label: "Pending", points: [{ at: "d", value: 3 }] }]} formatAt={at} buckets={buckets} />);
    fireEvent.keyDown(chart(), { key: "Home" });
    expect(screen.getByRole("tooltip")).toHaveTextContent("Unknown Pending");
    fireEvent.keyDown(chart(), { key: "End" });
    expect(screen.getByRole("tooltip")).toHaveTextContent("3 Pending");
  });

  it("REQ-BT-026 both ends of the axis are the shared list's", () => {
    const { container } = render(<LineChart caption="Stage" series={[{ label: "Pending", points: [{ at: "b", value: 3 }] }]} formatAt={at} buckets={buckets} />);
    expect(axis(container)).toEqual(expect.arrayContaining(["a", "d"]));
  });
});
