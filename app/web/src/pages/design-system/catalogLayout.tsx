import { AppLink, Button, Cluster, Grid, PageHeader, Panel, Stack, Stat, StatusBadge, Text, TokenSwatches } from "../../ds";
import type { CatalogItem } from "./catalogItem";
import { LONG_TEXT, TONES } from "./catalogSamples";

function arrangementItems(): CatalogItem[] {
  return [
    { name: "Stack", states: [{ label: "default", render: () => <Stack gap={2}><Text>One</Text><Text>Two</Text></Stack> }] },
    { name: "Cluster", states: [{ label: "default", render: () => <Cluster><StatusBadge>One</StatusBadge><StatusBadge>Two</StatusBadge></Cluster> }] },
    { name: "Grid", states: [{ label: "default", render: () => <Grid min="11rem"><Stat label="Open" value={3} /><Stat label="Closed" value={9} /></Grid> }] },
    {
      name: "Panel",
      states: [
        { label: "title and actions", render: () => <Panel title="Batch list" description="Updated just now" actions={<Button>Refresh</Button>}><Text>Body</Text></Panel> },
        { label: "bare", render: () => <Panel><Text>Body</Text></Panel> },
      ],
    },
    {
      name: "PageHeader",
      states: [
        { label: "default", render: () => <PageHeader title="Batches" subtitle="Every batch you can reach." /> },
        { label: "with back and actions", render: () => <PageHeader title={LONG_TEXT} back={{ to: "/", label: "All batches" }} actions={<Button>Refresh</Button>} /> },
      ],
    },
    { name: "AppLink", states: [{ label: "default", render: () => <AppLink to="/">All batches</AppLink> }] },
  ];
}

function textItems(): CatalogItem[] {
  return [
    {
      name: "Text",
      states: [
        {
          label: "tones and sizes",
          render: () => (
            <Stack gap={1}>
              <Text tone="primary" size="md">Primary, medium</Text>
              <Text tone="secondary">Secondary</Text>
              <Text tone="muted" size="xs">Muted, small</Text>
              <Text tone="danger">Danger</Text>
              <Text mono>Monospace 0123456789</Text>
            </Stack>
          ),
        },
      ],
    },
  ];
}

function frameItems(): CatalogItem[] {
  return [
    { name: "AppShell", states: [{ label: "every page sits inside it, shown around this catalog", render: () => <Text tone="secondary">Top bar, skip link and focus on route change.</Text> }] },
    { name: "CenteredPage", states: [{ label: "used by sign in", render: () => <Text tone="secondary">Centered column with no navigation.</Text> }] },
    { name: "CatalogEntry", states: [{ label: "wraps each block on this page", render: () => <Text tone="secondary">This block.</Text> }] },
    { name: "CatalogState", states: [{ label: "wraps each row on this page", render: () => <Text tone="secondary">This row.</Text> }] },
  ];
}

function tokenItems(): CatalogItem[] {
  return [
    {
      name: "TokenSwatches",
      states: [
        { label: "semantic surfaces", render: () => <TokenSwatches names={["--bg-canvas", "--bg-surface", "--bg-raised", "--bg-sunken", "--bg-selected", "--bg-chrome"]} /> },
        { label: "semantic text", render: () => <TokenSwatches names={["--text-primary", "--text-secondary", "--text-muted", "--text-link"]} /> },
        { label: "tones", render: () => <TokenSwatches names={TONES.flatMap((t) => [`--tone-${t}-bg`, `--tone-${t}-fg`])} /> },
        { label: "actions and data", render: () => <TokenSwatches names={["--accent", "--danger", "--data-ok", "--data-fail", "--border-control", "--border-focus"]} /> },
      ],
    },
  ];
}

export function layoutItems(): CatalogItem[] {
  return [...arrangementItems(), ...textItems(), ...frameItems(), ...tokenItems()];
}
