/**
 * A user-defined automation (Phase 7.2). Mirrors {@link Automation} on the C# side.
 */
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
}
