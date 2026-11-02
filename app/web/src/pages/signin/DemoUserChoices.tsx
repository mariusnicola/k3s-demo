import { Button, Panel, Stack, Text } from "../../ds";

interface DemoUser {
  user: string;
  role: string;
  access: string;
}

const DEMO_USERS: DemoUser[] = [
  { user: "olivia", role: "Operator", access: "Creates batches and moves them on farm-a and farm-b." },
  { user: "ian", role: "Inspector", access: "Passes or fails inspections on all farms." },
  { user: "ada", role: "Admin", access: "Everything, on all farms, plus the cluster view, logs and replay." },
  { user: "viewer", role: "Read only", access: "Sees farm-a. Cannot change anything." },
];

export function DemoUserChoices({ busy, choosing, onChoose }: { busy: boolean; choosing: string | null; onChoose: (user: string) => void }) {
  return (
    <Panel>
      <Stack gap={3}>
        {DEMO_USERS.map((demo) => (
          <Stack key={demo.user} gap={1}>
            <Button
              variant={demo.user === "olivia" ? "primary" : "secondary"}
              icon="user"
              loading={busy && choosing === demo.user}
              disabled={busy && choosing !== demo.user}
              onClick={() => onChoose(demo.user)}
            >
              Continue as {demo.user} ({demo.role})
            </Button>
            <Text size="xs" tone="secondary">
              {demo.access}
            </Text>
          </Stack>
        ))}
      </Stack>
    </Panel>
  );
}
