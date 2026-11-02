import { useMemo } from "react";
import { PageHeader, useDocumentTitle } from "../../ds";
import { CatalogView, buildCatalog } from "./catalog";

export function DesignSystemPage() {
  useDocumentTitle("Design system");
  const items = useMemo(() => buildCatalog(Date.now()), []);
  return (
    <>
      <PageHeader title="Design system" subtitle="Every component and its states. Change the theme from the menu to see both." />
      <CatalogView items={items} />
    </>
  );
}
