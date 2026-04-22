// AUTO-GENERATED-CANDIDATE — hand-mirrored from packages/shared-schemas/runtime-event.schema.json.

export interface RuntimeEvent<T = unknown> {
  /** Dotted namespace, e.g. runtime.state */
  type: string;
  /** ULID */
  id: string;
  /** ISO-8601 */
  timestamp: string;
  correlationId?: string;
  payload: T;
}
