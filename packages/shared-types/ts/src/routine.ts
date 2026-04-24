/**
 * A repeatable user-invoked workflow. Mirrors {@link Routine} on the C# side.
 * Routines never fire on their own — the user opens one, checks items off,
 * optionally records a note, and completes it.
 */
export interface RoutineChecklistItem {
  id: string;
  text: string;
  sortOrder: number;
}

export interface Routine {
  id: string;
  name: string;
  description: string;
  checklistItems: RoutineChecklistItem[];
  promptTemplate?: string | null;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
  lastRunAt: string | null;
}

export interface RoutineRunItem {
  checklistItemId: string;
  text: string;
  isCompleted: boolean;
  completedAt: string | null;
}

export interface RoutineRun {
  id: string;
  routineId: string;
  startedAt: string;
  completedAt: string | null;
  items: RoutineRunItem[];
  userNote?: string | null;
  generatedSummary?: string | null;
}
