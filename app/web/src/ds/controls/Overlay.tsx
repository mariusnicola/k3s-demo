import * as RadixDialog from "@radix-ui/react-dialog";
import * as RadixTabs from "@radix-ui/react-tabs";
import * as RadixTooltip from "@radix-ui/react-tooltip";
import { useRef, type ReactElement, type ReactNode } from "react";
import { Icon } from "../Icon";

export interface DialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: string;
  children: ReactNode;
  footer: ReactNode;
}

/** First field if there is one, else the first footer button: Cancel, so Enter never confirms something destructive. */
function firstUseful(root: HTMLElement): HTMLElement | null {
  return root.querySelector<HTMLElement>(".ds-dialog-body input, .ds-dialog-body textarea, .ds-dialog-body select") ?? root.querySelector<HTMLElement>(".ds-dialog-footer button");
}

function useDialogFocus() {
  // Radix only restores focus to its own Trigger, and these dialogs open from page state.
  const returnTo = useRef<HTMLElement | null>(null);

  const onOpenAutoFocus = (event: Event) => {
    returnTo.current = document.activeElement as HTMLElement | null;
    const target = firstUseful(event.currentTarget as HTMLElement);
    if (!target) return;
    event.preventDefault();
    target.focus();
  };

  const onCloseAutoFocus = (event: Event) => {
    event.preventDefault();
    const target = returnTo.current?.isConnected ? returnTo.current : document.getElementById("main");
    target?.focus();
  };

  return { onOpenAutoFocus, onCloseAutoFocus };
}

export function Dialog({ open, onOpenChange, title, description, children, footer }: DialogProps) {
  const focus = useDialogFocus();
  return (
    <RadixDialog.Root open={open} onOpenChange={onOpenChange}>
      <RadixDialog.Portal>
        <RadixDialog.Overlay className="ds-dialog-scrim" />
        <RadixDialog.Content className="ds-dialog" {...focus}>
          <header className="ds-dialog-header">
            <RadixDialog.Title className="ds-dialog-title">{title}</RadixDialog.Title>
            <RadixDialog.Close className="ds-icon-button" aria-label="Close">
              <Icon name="close" />
            </RadixDialog.Close>
          </header>
          {description ? <RadixDialog.Description className="ds-dialog-description">{description}</RadixDialog.Description> : <RadixDialog.Description className="ds-sr-only">{title}</RadixDialog.Description>}
          <div className="ds-dialog-body">{children}</div>
          <footer className="ds-dialog-footer">{footer}</footer>
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}

export interface TabItem {
  id: string;
  label: string;
  content: ReactNode;
}

export function Tabs({ label, items, defaultValue }: { label: string; items: TabItem[]; defaultValue?: string }) {
  return (
    <RadixTabs.Root className="ds-tabs" defaultValue={defaultValue ?? items[0]?.id}>
      <RadixTabs.List className="ds-tab-list" aria-label={label}>
        {items.map((item) => (
          <RadixTabs.Trigger key={item.id} className="ds-tab" value={item.id}>
            {item.label}
          </RadixTabs.Trigger>
        ))}
      </RadixTabs.List>
      {items.map((item) => (
        <RadixTabs.Content key={item.id} className="ds-tab-panel" value={item.id}>
          {item.content}
        </RadixTabs.Content>
      ))}
    </RadixTabs.Root>
  );
}

export function Tooltip({ content, children }: { content: string; children: ReactElement }) {
  return (
    <RadixTooltip.Provider delayDuration={300}>
      <RadixTooltip.Root>
        <RadixTooltip.Trigger asChild>{children}</RadixTooltip.Trigger>
        <RadixTooltip.Portal>
          <RadixTooltip.Content className="ds-tooltip" sideOffset={6}>
            {content}
          </RadixTooltip.Content>
        </RadixTooltip.Portal>
      </RadixTooltip.Root>
    </RadixTooltip.Provider>
  );
}
