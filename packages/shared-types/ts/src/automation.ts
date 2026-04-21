/**
 * A user-defined automation (Phase 7.2). Mirrors {@link Automation} on the C# side.
 */
export type AutomationScheduleKind = 'off' | 'cron' | 'one-shot';

export interface AutomationSchedule {
  kind: AutomationScheduleKind;
  cron?: string | null;
  runAt?: string | null;
  timezone?: string | null;
  nextRunAt?: string | null;
  lastFiredAt?: string | null;
}

export interface Automation {
  id: string;
  name: string;
  description: string;
  trigger: string;
  steps: string[];
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
  lastRunAt: string | null;
  allowedTools?: string[] | null;
  schedule?: AutomationSchedule | null;
}

export interface ToolCatalogEntry {
  name: string;
  description: string;
  group: string;
}

/**
 * Payload emitted on the <code>chat.automation.proposed</code> WebSocket
 * event when the assistant calls the virtual <code>propose_automation</code>
 * tool. The UI renders an inline editable confirmation card (name, steps,
 * schedule) with Create / Cancel buttons — clicking Create POSTs to
 * <code>/api/automations</code>.
 */
export interface AutomationProposal {
  proposalId: string;
  threadId: string;
  messageId: string;
  name: string;
  description?: string | null;
  steps: string[];
  schedule?: AutomationSchedule | null;
  proposedAt: string;
}
