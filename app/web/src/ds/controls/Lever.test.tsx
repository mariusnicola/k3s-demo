import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { vi } from "vitest";
import { Lever } from "./Lever";

function Held({ start = 5, onRelease }: { start?: number; onRelease: (value: number) => void }) {
  const [value, setValue] = useState(start);
  return <Lever label="Rate" valueText={`${value} a second`} value={value} min={0} max={35} step={0.2} major={5} minor={1} onMove={setValue} onRelease={onRelease} />;
}

const show = (start?: number) => {
  const onRelease = vi.fn();
  render(<Held start={start} onRelease={onRelease} />);
  return { onRelease, lever: screen.getByRole("slider", { name: "Rate" }) };
};

describe("Lever", () => {
  it("REQ-BT-031 is a native range with its name, limits, step and the value in words", () => {
    const { lever } = show(7.4);
    expect(lever).toHaveAttribute("type", "range");
    expect(lever).toHaveAttribute("min", "0");
    expect(lever).toHaveAttribute("max", "35");
    expect(lever).toHaveAttribute("step", "0.2");
    expect(lever).toHaveValue("7.4");
    expect(lever).toHaveAttribute("aria-valuetext", "7.4 a second");
    expect(screen.getByText("7.4 a second")).toBeVisible();
  });

  it("REQ-BT-031 prints a scale of ticks and numbers, which a screen reader skips", () => {
    const { lever } = show();
    const scale = lever.closest(".ds-lever")!.querySelector(".ds-lever-scale")!;
    expect(scale).toHaveAttribute("aria-hidden", "true");
    expect(scale.querySelectorAll(".ds-lever-tick")).toHaveLength(36);
    expect(scale.querySelectorAll(".ds-lever-tick[data-major]")).toHaveLength(8);
    expect([...scale.querySelectorAll(".ds-lever-number")].map((n) => n.textContent)).toEqual(["0", "5", "10", "15", "20", "25", "30", "35"]);
  });

  it("REQ-BT-031 shows the value while it moves and releases once, at the end of the drag", () => {
    const { lever, onRelease } = show();
    for (const value of ["6", "7", "8.2"]) fireEvent.input(lever, { target: { value } });
    expect(screen.getByText("8.2 a second")).toBeVisible();
    expect(onRelease).not.toHaveBeenCalled();
    fireEvent.change(lever);
    expect(onRelease).toHaveBeenCalledTimes(1);
    expect(onRelease).toHaveBeenCalledWith(8.2);
  });

  it("REQ-BT-031 one key press releases once, a step up", () => {
    const { lever, onRelease } = show(5);
    fireEvent.keyDown(lever, { key: "ArrowRight" });
    fireEvent.input(lever, { target: { value: "5.2" } });
    fireEvent.change(lever);
    fireEvent.keyUp(lever, { key: "ArrowRight" });
    expect(onRelease.mock.calls).toEqual([[5.2]]);
  });

  it("REQ-BT-031 a key held down releases once, when it comes up", () => {
    const { lever, onRelease } = show(5);
    fireEvent.keyDown(lever, { key: "ArrowRight" });
    for (const value of ["5.2", "5.4", "5.6"]) {
      fireEvent.input(lever, { target: { value } });
      fireEvent.change(lever);
    }
    expect(onRelease).not.toHaveBeenCalled();
    fireEvent.keyUp(lever, { key: "ArrowRight" });
    expect(onRelease.mock.calls).toEqual([[5.6]]);
  });

  it("REQ-BT-031 leaving the lever with a key held still releases", () => {
    const { lever, onRelease } = show(5);
    fireEvent.keyDown(lever, { key: "ArrowLeft" });
    fireEvent.input(lever, { target: { value: "4.8" } });
    fireEvent.change(lever);
    fireEvent.blur(lever);
    expect(onRelease.mock.calls).toEqual([[4.8]]);
  });

  it("REQ-BT-031 a key that moved nothing releases nothing", () => {
    const { lever, onRelease } = show(35);
    fireEvent.keyDown(lever, { key: "ArrowRight" });
    fireEvent.keyUp(lever, { key: "ArrowRight" });
    expect(onRelease).not.toHaveBeenCalled();
  });

  it("REQ-BT-031 stops at its end", () => {
    const { lever, onRelease } = show(34);
    fireEvent.input(lever, { target: { value: "40" } });
    fireEvent.change(lever);
    expect(lever).toHaveValue("35");
    expect(onRelease.mock.calls).toEqual([[35]]);
  });

  it("REQ-BT-031 a value past the end keeps its true words and rests the handle at the end", () => {
    const { lever } = show(50);
    expect(lever).toHaveValue("35");
    expect(screen.getByText("50 a second")).toBeVisible();
    expect((lever.closest(".ds-lever") as HTMLElement).style.getPropertyValue("--lever-at")).toBe("1");
  });

  it("REQ-BT-031 says an error beside itself and joins it to the input", () => {
    render(<Lever label="Rate" valueText="1" value={1} min={0} max={35} step={0.2} major={5} minor={1} onMove={() => {}} onRelease={() => {}} error="Not confirmed" />);
    expect(screen.getByRole("alert")).toHaveTextContent("Not confirmed");
    expect(screen.getByRole("slider", { name: "Rate" })).toHaveAccessibleDescription("Not confirmed");
  });
});
