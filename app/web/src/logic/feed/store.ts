/** Every store the app can run on, with the monitor check that reaches each. */
const STORES = {
  mongo: { label: "MongoDB", check: "mongo" },
  cosmos: { label: "Cosmos DB", check: "cosmos" },
} as const;

type StoreKey = keyof typeof STORES;

const KEYS = Object.keys(STORES) as StoreKey[];

const isStore = (store: string | null | undefined): store is StoreKey => KEYS.includes(store as StoreKey);

/** The report names the store in use. Until it does the map names none, because either could be the one. */
export function storeLabel(store: string | null | undefined): string {
  return isStore(store) ? STORES[store].label : "Store";
}

export function storeCheck(store: string | null | undefined): string | null {
  return isStore(store) ? STORES[store].check : null;
}

/** Containers that run with no app on them, so they get neither links nor counts. With no store named, none can be called idle. */
export function idleStores(store: string | null | undefined): { label: string; check: string }[] {
  return isStore(store) ? KEYS.filter((key) => key !== store).map((key) => STORES[key]) : [];
}

export const storeKnown = isStore;
