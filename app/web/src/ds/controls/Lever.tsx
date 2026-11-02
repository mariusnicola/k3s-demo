import { useEffect, useId, useRef, type CSSProperties } from "react";
import { Icon } from "../Icon";

export interface LeverProps {
  label: string;
  /** The value in words, shown beside the lever and read out in place of the bare number. */
  valueText: string;
  value: number;
  min: number;
  max: number;
  step: number;
  /** Every how many units the scale prints a long tick with a number. */
  major: number;
  /** Every how many units the scale prints a short tick. */
  minor: number;
  help?: string;
  error?: string | null;
  disabled?: boolean;
  /** Each change while it moves, for showing only. */
  onMove: (value: number) => void;
  /** Once per drag or key press, when it ends. */
  onRelease: (value: number) => void;
}

function Scale({ min, max, major, minor }: Pick<LeverProps, "min" | "max" | "major" | "minor">) {
  const marks = Array.from({ length: Math.round((max - min) / minor) + 1 }, (_, i) => min + i * minor);
  return (
    <div className="ds-lever-scale" aria-hidden="true">
      {marks.map((mark) => {
        const long = (mark - min) % major === 0;
        return (
          <span key={mark} className="ds-lever-tick" data-major={long || undefined}>
            {long ? <span className="ds-lever-number">{mark}</span> : null}
          </span>
        );
      })}
    </div>
  );
}

/**
 * The browser's own `change` fires when a drag ends, on mouse and touch alike, and once for
 * every repeat of a held key. Only the key repeats need holding back.
 */
function useRelease(input: React.RefObject<HTMLInputElement | null>, onRelease: (value: number) => void) {
  const latest = useRef(onRelease);
  latest.current = onRelease;
  const keyDown = useRef(false);
  const moved = useRef(false);

  useEffect(() => {
    const element = input.current!;
    const changed = () => {
      if (keyDown.current) moved.current = true;
      else latest.current(Number(element.value));
    };
    element.addEventListener("change", changed);
    return () => element.removeEventListener("change", changed);
  }, [input]);

  const keyEnded = () => {
    keyDown.current = false;
    if (moved.current) latest.current(Number(input.current!.value));
    moved.current = false;
  };
  return { onKeyDown: () => (keyDown.current = true), onKeyUp: keyEnded, onBlur: keyEnded };
}

function Head({ id, label, valueText }: { id: string; label: string; valueText: string }) {
  return (
    <div className="ds-lever-head">
      <label htmlFor={id} className="ds-field-label">
        {label}
      </label>
      <output htmlFor={id} className="ds-lever-value" aria-live="off">
        {valueText}
      </output>
    </div>
  );
}

function Notes({ id, help, error }: { id: string; help?: string; error?: string | null }) {
  return (
    <>
      {help ? (
        <p id={`${id}-help`} className="ds-field-help">
          {help}
        </p>
      ) : null}
      {error ? (
        <p id={`${id}-error`} className="ds-field-error" role="alert">
          <Icon name="warning" />
          <span>{error}</span>
        </p>
      ) : null}
    </>
  );
}

export function Lever({ label, valueText, value, min, max, step, major, minor, help, error, disabled, onMove, onRelease }: LeverProps) {
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  const release = useRelease(input, onRelease);
  const at = (Math.min(max, Math.max(min, value)) - min) / (max - min);
  const described = [help ? `${id}-help` : null, error ? `${id}-error` : null].filter(Boolean).join(" ") || undefined;
  return (
    <div className="ds-lever" data-disabled={disabled || undefined} style={{ "--lever-at": at } as CSSProperties}>
      <Head id={id} label={label} valueText={valueText} />
      <input
        ref={input}
        id={id}
        className="ds-lever-input"
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={disabled}
        aria-valuetext={valueText}
        aria-describedby={described}
        onChange={(e) => onMove(Number(e.target.value))}
        {...release}
      />
      <Scale min={min} max={max} major={major} minor={minor} />
      <Notes id={id} help={help} error={error} />
    </div>
  );
}
