import { CatalogEntry, CatalogState } from "../../ds";
import { controlItems } from "./catalogControls";
import { dataItems } from "./catalogData";
import { feedbackItems } from "./catalogFeedback";
import type { CatalogItem } from "./catalogItem";
import { layoutItems } from "./catalogLayout";
import { problemItems } from "./catalogProblems";
import { statusItems } from "./catalogStatus";

export function buildCatalog(now: number): CatalogItem[] {
  return [...controlItems(), ...layoutItems(), ...feedbackItems(), ...problemItems(now), ...statusItems(), ...dataItems()];
}

export function CatalogView({ items }: { items: CatalogItem[] }) {
  return (
    <>
      {items.map((item) => (
        <CatalogEntry key={item.name} name={item.name}>
          {item.states.map((state) => (
            <CatalogState key={state.label} label={state.label} inert={state.inert}>
              {state.render()}
            </CatalogState>
          ))}
        </CatalogEntry>
      ))}
    </>
  );
}
