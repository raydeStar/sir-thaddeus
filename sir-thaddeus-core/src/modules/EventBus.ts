export interface RuntimeEvent<TPayload = unknown> {
  type: string;
  payload: TPayload;
  moduleId?: string;
  occurredAt: Date;
}

export type EventHandler<TPayload = unknown> = (event: RuntimeEvent<TPayload>) => void | Promise<void>;
export type EventUnsubscribe = () => void;

export class EventBus {
  private readonly handlers = new Map<string, Set<EventHandler>>();
  private readonly wildcardHandlers = new Set<EventHandler>();

  subscribe<TPayload = unknown>(type: string, handler: EventHandler<TPayload>): EventUnsubscribe {
    const handlers = this.handlers.get(type) ?? new Set<EventHandler>();
    handlers.add(handler as EventHandler);
    this.handlers.set(type, handlers);

    return () => {
      handlers.delete(handler as EventHandler);
      if (handlers.size === 0) {
        this.handlers.delete(type);
      }
    };
  }

  subscribeAll(handler: EventHandler): EventUnsubscribe {
    this.wildcardHandlers.add(handler);
    return () => this.wildcardHandlers.delete(handler);
  }

  async publish<TPayload = unknown>(
    type: string,
    payload: TPayload,
    options: { moduleId?: string; occurredAt?: Date } = {}
  ): Promise<RuntimeEvent<TPayload>> {
    const event: RuntimeEvent<TPayload> = {
      type,
      payload,
      moduleId: options.moduleId,
      occurredAt: options.occurredAt ?? new Date()
    };

    const handlers = [
      ...(this.handlers.get(type) ?? []),
      ...this.wildcardHandlers
    ];

    await Promise.all(handlers.map((handler) => handler(event)));
    return event;
  }

  clear(): void {
    this.handlers.clear();
    this.wildcardHandlers.clear();
  }
}
