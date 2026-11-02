import { ApiError, STATUS_NETWORK } from "../../api/problem";
import { ConnectionStatus } from "../../ds";
import { EventBanner } from "../EventBanner";
import { ProblemBanner, ProblemState } from "../shared";
import type { CatalogItem } from "./catalogItem";
import { noop, sampleBatch, socket } from "./catalogSamples";

function problemBannerItems(): CatalogItem[] {
  return [
    {
      name: "ProblemBanner",
      states: [
        { label: "400 with Dismiss", inert: true, render: () => <ProblemBanner error={new ApiError(400, "Bad request", null)} onDismiss={noop} /> },
        { label: "401", inert: true, render: () => <ProblemBanner error={new ApiError(401, "Unauthorized", null)} /> },
        { label: "403", inert: true, render: () => <ProblemBanner error={new ApiError(403, "Forbidden", null)} /> },
        { label: "404", inert: true, render: () => <ProblemBanner error={new ApiError(404, "Not found", null)} /> },
        { label: "409", inert: true, render: () => <ProblemBanner error={new ApiError(409, "Conflict", null)} onReload={noop} /> },
        { label: "412", inert: true, render: () => <ProblemBanner error={new ApiError(412, "Precondition failed", null)} onReload={noop} /> },
        { label: "428", inert: true, render: () => <ProblemBanner error={new ApiError(428, "Precondition required", null)} onReload={noop} /> },
        { label: "500", inert: true, render: () => <ProblemBanner error={new ApiError(500, "Server error", null)} onRetry={noop} /> },
        { label: "network", inert: true, render: () => <ProblemBanner error={new ApiError(STATUS_NETWORK, "", null)} onRetry={noop} /> },
      ],
    },
    {
      name: "ProblemState",
      states: [
        { label: "404", inert: true, render: () => <ProblemState error={new ApiError(404, "Not found", null)} /> },
        { label: "network", inert: true, render: () => <ProblemState error={new ApiError(STATUS_NETWORK, "", null)} onRetry={noop} /> },
      ],
    },
  ];
}

function eventItems(now: number): CatalogItem[] {
  return [
    {
      name: "EventBanner",
      states: [
        { label: "in flight", inert: true, render: () => <EventBanner batch={sampleBatch(now, "sent")} now={now} onReplay={noop} replayReason={null} replaying={false} /> },
        { label: "delayed", inert: true, render: () => <EventBanner batch={sampleBatch(now - 300_000, "sent")} now={now} onReplay={noop} replayReason={null} replaying={false} /> },
        { label: "blocked", inert: true, render: () => <EventBanner batch={sampleBatch(now, "dead")} now={now} onReplay={noop} replayReason={null} replaying={false} /> },
        { label: "blocked, replay running", inert: true, render: () => <EventBanner batch={sampleBatch(now, "dead")} now={now} onReplay={noop} replayReason={null} replaying /> },
        { label: "blocked, viewer cannot replay", inert: true, render: () => <EventBanner batch={sampleBatch(now, "dead")} now={now} onReplay={noop} replayReason="Replay needs the Admin role." replaying={false} /> },
      ],
    },
  ];
}

function connectionItems(now: number): CatalogItem[] {
  return [
    {
      name: "ConnectionStatus",
      states: [
        { label: "connecting", render: () => <ConnectionStatus label="Hop stream" state={socket("connecting", null)} now={now} /> },
        { label: "open, no data", render: () => <ConnectionStatus label="Hop stream" state={socket("open", null)} now={now} /> },
        { label: "open", render: () => <ConnectionStatus label="Hop stream" state={socket("open", now - 2000)} now={now} /> },
        { label: "stale", render: () => <ConnectionStatus label="Monitor" state={socket("open", now - 200000)} now={now} stale onReconnect={noop} /> },
        { label: "lost", render: () => <ConnectionStatus label="Hop stream" state={socket("lost", now - 65000)} now={now} onReconnect={noop} /> },
        { label: "lost, never connected", render: () => <ConnectionStatus label="Monitor" state={socket("lost", null)} now={now} onReconnect={noop} /> },
      ],
    },
  ];
}

export function problemItems(now: number): CatalogItem[] {
  return [...problemBannerItems(), ...eventItems(now), ...connectionItems(now)];
}
