import { useId, type ButtonHTMLAttributes, type MouseEvent, type ReactNode } from "react";
import { Icon, type IconName } from "../Icon";
import { Tooltip } from "./Overlay";

type Variant = "primary" | "secondary" | "danger" | "ghost";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  icon?: IconName;
  loading?: boolean;
  /** Keeps the control focusable and shows why it cannot be used. */
  disabledReason?: string | null;
  children: ReactNode;
  "data-force"?: "hover" | "active" | "focus";
}

function ButtonMark({ loading, icon }: { loading?: boolean; icon?: IconName }) {
  if (loading) return <span className="ds-spinner" aria-hidden="true" />;
  return icon ? <Icon name={icon} /> : null;
}

function ButtonReason({ id, reason }: { id: string; reason: string }) {
  return (
    <span id={id} className="ds-button-reason">
      <Icon name="lock" />
      <span>{reason}</span>
    </span>
  );
}

export function Button({ variant = "secondary", icon, loading, disabledReason, children, onClick, type, disabled, ...rest }: ButtonProps) {
  const reasonId = useId();
  const blocked = Boolean(disabledReason) || disabled;
  const inert = blocked || loading;
  const press = (event: MouseEvent<HTMLButtonElement>) => {
    if (inert) event.preventDefault();
    else onClick?.(event);
  };
  // One root in both cases: swapping it when a gate changes would destroy the focused button.
  return (
    <span className="ds-button-slot" data-reason={disabledReason ? "" : undefined}>
      <button
        {...rest}
        type={type ?? "button"}
        className="ds-button"
        data-variant={variant}
        data-loading={loading || undefined}
        aria-disabled={inert || undefined}
        aria-busy={loading || undefined}
        aria-describedby={disabledReason ? reasonId : undefined}
        onClick={press}
      >
        <ButtonMark loading={loading} icon={icon} />
        <span>{children}</span>
      </button>
      {disabledReason ? <ButtonReason id={reasonId} reason={disabledReason} /> : null}
    </span>
  );
}

export function IconButton({ icon, label, onClick, disabled }: { icon: IconName; label: string; onClick?: () => void; disabled?: boolean }) {
  return (
    <Tooltip content={label}>
      <button
        type="button"
        className="ds-icon-button"
        aria-label={label}
        aria-disabled={disabled || undefined}
        onClick={() => {
          if (!disabled) onClick?.();
        }}
      >
        <Icon name={icon} />
      </button>
    </Tooltip>
  );
}
