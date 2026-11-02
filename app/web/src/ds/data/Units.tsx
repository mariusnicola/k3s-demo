import { Icon, type IconName } from "../Icon";

export interface Unit {
  id: string;
  name: string;
  note: string;
  tone: "success" | "warning" | "danger" | "neutral";
  icon: IconName;
  /** A second fact about the unit, with its own icon. */
  detail?: { words: string; icon: IconName };
}

/** One element per running thing, so a count can be checked by eye. Icon and words carry the state, never colour alone. */
export function UnitGrid({ caption, units }: { caption: string; units: Unit[] }) {
  return (
    <ul className="ds-units" aria-label={caption}>
      {units.map((unit) => (
        <li key={unit.id} className="ds-unit" data-tone={unit.tone} title={unit.id}>
          <span className="ds-unit-mark" data-icon={unit.icon}>
            <Icon name={unit.icon} />
          </span>
          <span className="ds-unit-name">{unit.name}</span>
          <span className="ds-unit-note">{unit.note}</span>
          {unit.detail ? (
            <span className="ds-unit-detail" data-detail-icon={unit.detail.icon}>
              <Icon name={unit.detail.icon} />
              <span>{unit.detail.words}</span>
            </span>
          ) : null}
        </li>
      ))}
    </ul>
  );
}
