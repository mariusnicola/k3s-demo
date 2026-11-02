import { useEffect, useRef, type ReactNode, type RefObject } from "react";
import { Link, NavLink, useLocation, useNavigationType } from "react-router-dom";
import { Icon, type IconName } from "../Icon";

type Gap = 1 | 2 | 3 | 4 | 5 | 6;

export function Stack({ gap = 4, children }: { gap?: Gap; children: ReactNode }) {
  return (
    <div className="ds-stack" data-gap={gap}>
      {children}
    </div>
  );
}

export function Cluster({ gap = 3, justify, align, children }: { gap?: Gap; justify?: "between" | "end"; align?: "start" | "center" | "end"; children: ReactNode }) {
  return (
    <div className="ds-cluster" data-gap={gap} data-justify={justify} data-align={align}>
      {children}
    </div>
  );
}

export function Grid({ min = "20rem", children }: { min?: "9rem" | "11rem" | "20rem" | "28rem"; children: ReactNode }) {
  return (
    <div className="ds-grid" data-min={min}>
      {children}
    </div>
  );
}

/** Main content with a narrow column beside it, which drops below it when the page is narrow. */
export function SplitLayout({ aside, children }: { aside: ReactNode; children: ReactNode }) {
  return (
    <div className="ds-split">
      <div className="ds-split-main">{children}</div>
      <div className="ds-split-aside">{aside}</div>
    </div>
  );
}

export interface PanelProps {
  title?: string;
  description?: ReactNode;
  actions?: ReactNode;
  /** Sits on the page ground with no card, for the map. */
  ground?: boolean;
  children: ReactNode;
}

export function Panel({ title, description, actions, ground, children }: PanelProps) {
  return (
    <section className="ds-panel" data-ground={ground || undefined} aria-label={title}>
      {title ? (
        <header className="ds-panel-header">
          <div>
            <h2 className="ds-panel-title">{title}</h2>
            {description ? <p className="ds-panel-description">{description}</p> : null}
          </div>
          {actions ? <div className="ds-panel-actions">{actions}</div> : null}
        </header>
      ) : null}
      <div className="ds-panel-body">{children}</div>
    </section>
  );
}

export function PageHeader({ title, subtitle, back, actions }: { title: string; subtitle?: ReactNode; back?: { to: string; label: string }; actions?: ReactNode }) {
  return (
    <header className="ds-page-header">
      <div className="ds-page-heading">
        {back ? (
          <Link to={back.to} className="ds-back-link">
            <Icon name="arrowLeft" />
            <span>{back.label}</span>
          </Link>
        ) : null}
        <h1 className="ds-page-title">{title}</h1>
        {subtitle ? <p className="ds-page-subtitle">{subtitle}</p> : null}
      </div>
      {actions ? <div className="ds-page-actions">{actions}</div> : null}
    </header>
  );
}

/** `label` names the link for assistive tech when its text alone, such as a bare count, would not. */
/** A longer name stands beside the words, not over them, so what a speech user reads out still names the control. */
export function Named({ name, children }: { name: string; children: ReactNode }) {
  return (
    <>
      <span className="ds-sr-only">{name}</span>
      <span aria-hidden="true">{children}</span>
    </>
  );
}

export function AppLink({ to, label, children }: { to: string; label?: string; children: ReactNode }) {
  return (
    <Link to={to} className="ds-link">
      {label ? <Named name={label}>{children}</Named> : children}
    </Link>
  );
}

type TextTone = "primary" | "secondary" | "muted" | "danger";

export function Text({ tone = "primary", size = "sm", mono, as: Tag = "span", children }: { tone?: TextTone; size?: "xs" | "sm" | "md"; mono?: boolean; as?: "span" | "p" | "h3"; children: ReactNode }) {
  return (
    <Tag className="ds-text" data-tone={tone} data-size={size} data-mono={mono || undefined}>
      {children}
    </Tag>
  );
}

export interface NavItem {
  to: string;
  label: string;
  icon: IconName;
  end?: boolean;
}

export interface AppShellProps {
  nav: NavItem[];
  account: ReactNode;
  children: ReactNode;
}

/** Controls that vanish on press (Dismiss, Reload batch) would drop keyboard focus to the top of the document. */
function useFocusFallback(main: RefObject<HTMLElement | null>) {
  useEffect(() => {
    const root = main.current;
    if (!root) return;
    let last: Element | null = null;
    const track = (event: FocusEvent) => {
      last = event.target as Element;
    };
    const observer = new MutationObserver(() => {
      if (last && !last.isConnected && document.activeElement === document.body) {
        last = null;
        root.focus({ preventScroll: true });
      }
    });
    observer.observe(root, { childList: true, subtree: true });
    document.addEventListener("focusin", track);
    return () => {
      observer.disconnect();
      document.removeEventListener("focusin", track);
    };
  }, [main]);
}

function useRouteFocus(main: RefObject<HTMLElement | null>) {
  const location = useLocation();
  const navigation = useNavigationType();
  const first = useRef(true);

  useEffect(() => {
    if (first.current) {
      first.current = false;
      return;
    }
    main.current?.focus();
    // Back and forward keep the place the browser restores.
    if (navigation !== "POP") window.scrollTo({ top: 0 });
    // Only the path: a filter change replaces the address and must not move focus.
  }, [location.pathname]);
}

function ShellBar({ nav, account }: Pick<AppShellProps, "nav" | "account">) {
  return (
    <header className="ds-shell-bar">
      <Link to="/" className="ds-brand">
        <Icon name="box" />
        <span>Batch Tracker</span>
      </Link>
      <nav className="ds-shell-nav" aria-label="Main">
        {nav.map((item) => (
          <NavLink key={item.to} to={item.to} end={item.end} className="ds-nav-link">
            <Icon name={item.icon} />
            <span>{item.label}</span>
          </NavLink>
        ))}
      </nav>
      <div className="ds-shell-account">{account}</div>
    </header>
  );
}

export function AppShell({ nav, account, children }: AppShellProps) {
  const main = useRef<HTMLElement>(null);
  useRouteFocus(main);
  useFocusFallback(main);

  return (
    <div className="ds-shell">
      <a className="ds-skip-link" href="#main">
        Skip to main content
      </a>
      <ShellBar nav={nav} account={account} />
      <main id="main" ref={main} tabIndex={-1} className="ds-main">
        {children}
      </main>
    </div>
  );
}

export function CatalogEntry({ name, children }: { name: string; children: ReactNode }) {
  return (
    <Panel title={name}>
      <div className="ds-catalog-entry" data-component={name}>
        {children}
      </div>
    </Panel>
  );
}

/** `inert` is for samples whose buttons would act on the real session or page. */
export function CatalogState({ label, children, inert }: { label: string; children: ReactNode; inert?: boolean }) {
  return (
    <div className="ds-catalog-state" data-state-label={label}>
      <span className="ds-catalog-state-label">{label}</span>
      <div inert={inert}>{children}</div>
    </div>
  );
}

export function TokenSwatches({ names }: { names: string[] }) {
  return (
    <div className="ds-catalog-swatches">
      {names.map((name) => (
        <span key={name} className="ds-swatch">
          <span className="ds-swatch-chip" style={{ background: `var(${name})` }} />
          {name}
        </span>
      ))}
    </div>
  );
}

/** Centered page with no navigation, used before anyone is signed in. */
export function CenteredPage({ children }: { children: ReactNode }) {
  const main = useRef<HTMLElement>(null);
  useFocusFallback(main);
  return (
    <main id="main" ref={main} tabIndex={-1} className="ds-centered-page">
      <div className="ds-centered-inner">{children}</div>
    </main>
  );
}
