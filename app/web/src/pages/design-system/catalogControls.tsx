import type { ComponentProps } from "react";
import { Button, Cluster, ICON_NAMES, Icon, IconButton, Select, Tabs, Text, TextArea, TextInput, Tooltip } from "../../ds";
import type { CatalogItem } from "./catalogItem";
import { CheckDemo, Choice, ConfirmDemo, DialogDemo } from "./catalogDemos";
import { LONG_TEXT } from "./catalogSamples";

function ButtonVariants({ variant }: { variant: ComponentProps<typeof Button>["variant"] }) {
  return (
    <Cluster>
      <Button variant={variant}>Default</Button>
      <Button variant={variant} data-force="hover">
        Hover
      </Button>
      <Button variant={variant} data-force="active">
        Active
      </Button>
      <Button variant={variant} data-force="focus">
        Focus
      </Button>
    </Cluster>
  );
}

function buttonItems(): CatalogItem[] {
  return [
    {
      name: "Button",
      states: [
        ...(["primary", "secondary", "danger", "ghost"] as const).map((variant) => ({ label: variant, render: () => <ButtonVariants variant={variant} /> })),
        { label: "with icon", render: () => <Button icon="plus">New batch</Button> },
        { label: "loading", render: () => <Button loading>Saving</Button> },
        { label: "disabled", render: () => <Button disabled>Unavailable</Button> },
        { label: "disabled with reason", render: () => <Button disabledReason="Your user lacks batch.write.">Move to Dose 2</Button> },
        { label: "long text", render: () => <Button>{LONG_TEXT}</Button> },
      ],
    },
    {
      name: "IconButton",
      states: [
        { label: "default", render: () => <IconButton icon="refresh" label="Refresh" /> },
        { label: "disabled", render: () => <IconButton icon="refresh" label="Refresh" disabled /> },
      ],
    },
  ];
}

function iconItems(): CatalogItem[] {
  return [
    {
      name: "Icon",
      states: [
        {
          label: "all names",
          render: () => (
            <Cluster>
              {ICON_NAMES.map((name) => (
                <Icon key={name} name={name} label={name} />
              ))}
            </Cluster>
          ),
        },
      ],
    },
  ];
}

function dialogItems(): CatalogItem[] {
  return [
    {
      name: "Dialog",
      states: [{ label: "closed, opens on press", render: () => <DialogDemo /> }],
    },
    {
      name: "ConfirmDialog",
      states: [
        { label: "default, opens on press, confirm shows busy", render: () => <ConfirmDemo /> },
        { label: "danger, opens on press, confirm shows busy", render: () => <ConfirmDemo danger /> },
      ],
    },
  ];
}

function tabItems(): CatalogItem[] {
  return [
    {
      name: "Tabs",
      states: [
        {
          label: "first selected",
          render: () => (
            <Tabs
              label="Example"
              items={[
                { id: "a", label: "Timeline", content: <Text>First panel</Text> },
                { id: "b", label: "Records", content: <Text>Second panel</Text> },
              ]}
            />
          ),
        },
      ],
    },
  ];
}

function tooltipItems(): CatalogItem[] {
  return [
    {
      name: "Tooltip",
      states: [
        {
          label: "on hover or focus",
          render: () => (
            <Tooltip content="Full value appears here">
              <Button>Hover or focus me</Button>
            </Tooltip>
          ),
        },
      ],
    },
  ];
}

function textFieldItems(): CatalogItem[] {
  return [
    {
      name: "TextInput",
      states: [
        { label: "default", render: () => <TextInput label="Product" /> },
        { label: "required with help", render: () => <TextInput label="Product" required help="The name on the label." /> },
        { label: "error", render: () => <TextInput label="Product" required error="Enter the product name." /> },
        { label: "disabled", render: () => <TextInput label="Product" disabled defaultValue="Locked" /> },
      ],
    },
    {
      name: "TextArea",
      states: [
        { label: "default", render: () => <TextArea label="Reason" /> },
        { label: "error", render: () => <TextArea label="Reason" required error="A failed inspection needs a reason." /> },
        { label: "disabled", render: () => <TextArea label="Reason" disabled defaultValue="Locked" /> },
      ],
    },
    {
      name: "Select",
      states: [
        { label: "default", render: () => <Select label="Farm" options={[{ value: "a", label: "farm-a" }]} /> },
        { label: "error", render: () => <Select label="Farm" error="Choose a farm." options={[{ value: "a", label: "farm-a" }]} /> },
        { label: "disabled", render: () => <Select label="Farm" disabled options={[{ value: "a", label: "farm-a" }]} /> },
      ],
    },
  ];
}

function choiceItems(): CatalogItem[] {
  return [
    {
      name: "Checkbox",
      states: [
        { label: "checked", render: () => <CheckDemo initial /> },
        { label: "unchecked", render: () => <CheckDemo initial={false} /> },
        { label: "disabled", render: () => <CheckDemo initial disabled /> },
      ],
    },
    {
      name: "RadioGroup",
      states: [
        { label: "default", render: () => <Choice kind="radio" /> },
        { label: "with help", render: () => <Choice kind="radio" help /> },
        { label: "disabled", render: () => <Choice kind="radio" disabled /> },
      ],
    },
    {
      name: "SegmentedControl",
      states: [
        { label: "default", render: () => <Choice kind="segmented" /> },
        { label: "with help", render: () => <Choice kind="segmented" help /> },
        { label: "disabled", render: () => <Choice kind="segmented" disabled /> },
      ],
    },
  ];
}

export function controlItems(): CatalogItem[] {
  return [...buttonItems(), ...iconItems(), ...dialogItems(), ...tabItems(), ...tooltipItems(), ...textFieldItems(), ...choiceItems()];
}
