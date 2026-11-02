import { useId, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from "react";
import { Icon } from "../Icon";

interface FieldProps {
  label: string;
  help?: string;
  error?: string | null;
  required?: boolean;
}

function fieldIds(id: string, help?: string, error?: string | null) {
  const parts = [help ? `${id}-help` : null, error ? `${id}-error` : null].filter(Boolean);
  return parts.length > 0 ? parts.join(" ") : undefined;
}

function FieldFrame({ id, label, help, error, required, children }: FieldProps & { id: string; children: ReactNode }) {
  return (
    <div className="ds-field" data-invalid={error ? true : undefined}>
      <label htmlFor={id} className="ds-field-label">
        {label}
        {required ? <span className="ds-field-required"> (required)</span> : null}
      </label>
      {children}
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
    </div>
  );
}

export type TextInputProps = FieldProps & InputHTMLAttributes<HTMLInputElement>;

export function TextInput({ label, help, error, required, ...rest }: TextInputProps) {
  const id = useId();
  return (
    <FieldFrame id={id} label={label} help={help} error={error} required={required}>
      <input {...rest} id={id} className="ds-input" aria-required={required || undefined} aria-invalid={error ? true : undefined} aria-describedby={fieldIds(id, help, error)} />
    </FieldFrame>
  );
}

export type TextAreaProps = FieldProps & TextareaHTMLAttributes<HTMLTextAreaElement>;

export function TextArea({ label, help, error, required, ...rest }: TextAreaProps) {
  const id = useId();
  return (
    <FieldFrame id={id} label={label} help={help} error={error} required={required}>
      <textarea {...rest} id={id} className="ds-input ds-textarea" aria-required={required || undefined} aria-invalid={error ? true : undefined} aria-describedby={fieldIds(id, help, error)} />
    </FieldFrame>
  );
}

export interface SelectOption {
  value: string;
  label: string;
}

export type SelectProps = FieldProps & SelectHTMLAttributes<HTMLSelectElement> & { options: SelectOption[] };

export function Select({ label, help, error, required, options, ...rest }: SelectProps) {
  const id = useId();
  return (
    <FieldFrame id={id} label={label} help={help} error={error} required={required}>
      <select {...rest} id={id} className="ds-input ds-select" aria-required={required || undefined} aria-invalid={error ? true : undefined} aria-describedby={fieldIds(id, help, error)}>
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    </FieldFrame>
  );
}

export function Checkbox({ label, help, checked, onChange, disabled }: { label: string; help?: string; checked: boolean; onChange: (checked: boolean) => void; disabled?: boolean }) {
  const id = useId();
  return (
    <div className="ds-check">
      <input id={id} type="checkbox" className="ds-checkbox" checked={checked} disabled={disabled} aria-describedby={help ? `${id}-help` : undefined} onChange={(e) => onChange(e.target.checked)} />
      <label htmlFor={id} className="ds-check-label">
        {label}
      </label>
      {help ? (
        <p id={`${id}-help`} className="ds-field-help ds-check-help">
          {help}
        </p>
      ) : null}
    </div>
  );
}

export interface ChoiceOption<T extends string> {
  value: T;
  label: string;
  help?: string;
}

interface ChoiceProps<T extends string> {
  legend: string;
  options: ChoiceOption<T>[];
  value: T;
  onChange: (value: T) => void;
  disabled?: boolean;
}

export function RadioGroup<T extends string>({ legend, options, value, onChange, disabled }: ChoiceProps<T>) {
  const name = useId();
  return (
    <fieldset className="ds-fieldset" disabled={disabled}>
      <legend className="ds-field-label">{legend}</legend>
      {options.map((o) => (
        <label key={o.value} className="ds-radio">
          <input type="radio" name={name} value={o.value} checked={value === o.value} onChange={() => onChange(o.value)} />
          <span>
            <span className="ds-radio-label">{o.label}</span>
            {o.help ? <span className="ds-field-help ds-radio-help">{o.help}</span> : null}
          </span>
        </label>
      ))}
    </fieldset>
  );
}

/** Radios drawn as one bar, so arrow keys and screen readers work with no script. */
export function SegmentedControl<T extends string>({ legend, options, value, onChange, disabled, help }: ChoiceProps<T> & { help?: string }) {
  const name = useId();
  return (
    <fieldset className="ds-segmented" disabled={disabled} data-disabled={disabled || undefined}>
      <legend className="ds-field-label">{legend}</legend>
      <div className="ds-segmented-bar">
        {options.map((o) => (
          <label key={o.value} className="ds-segment">
            <input type="radio" name={name} value={o.value} checked={value === o.value} onChange={() => onChange(o.value)} />
            <span>{o.label}</span>
          </label>
        ))}
      </div>
      {help ? <p className="ds-field-help ds-segmented-help">{help}</p> : null}
    </fieldset>
  );
}
