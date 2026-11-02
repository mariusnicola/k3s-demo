import { TOP, alignTo, buildPlot } from "./plot";

const line = (...values: (number | null)[]) => [{ label: "s", points: values.map((value, i) => ({ at: `t${i}`, value })) }];

describe("buildPlot", () => {
  it.each([
    [0.04, 0.05],
    [0.003, 0.005],
    [0.2, 0.2],
    [0.9, 1],
    [7, 10],
    [120, 200],
  ])("REQ-BT-026 a largest value of %s scales to %s", (value, max) => {
    expect(buildPlot(line(0, value), 640).max).toBeCloseTo(max, 10);
  });

  it.each([[[0, 0]], [[null, null]], [[]]])("REQ-BT-026 with nothing above zero in %j the scale stays 1", (values) => {
    expect(buildPlot(line(...values), 640).max).toBe(1);
  });

  it("REQ-BT-026 a small value stands in the chart, not on its floor", () => {
    const plot = buildPlot(line(0, 0.05), 640);
    expect(plot.y(0.05)).toBe(TOP);
  });

  it("REQ-BT-026 a shared list of buckets sets the count and the times", () => {
    const plot = buildPlot(line(1), 640, ["a", "b", "c"]);
    expect(plot.count).toBe(3);
    expect(plot.at(2)).toBe("c");
  });
});

describe("alignTo", () => {
  const series = [{ label: "s", points: [{ at: "b", value: 2 }, { at: "d", value: 4 }] }];

  it("REQ-BT-026 each value lands at its own time, and a time it lacks is unknown", () => {
    expect(alignTo(series, ["a", "b", "c", "d"])[0]!.points).toEqual([
      { at: "a", value: null },
      { at: "b", value: 2 },
      { at: "c", value: null },
      { at: "d", value: 4 },
    ]);
  });

  it("REQ-BT-026 a value outside the list is left out, so no chart runs past the others", () => {
    expect(alignTo(series, ["a", "b"])[0]!.points.map((p) => p.value)).toEqual([null, 2]);
  });
});
