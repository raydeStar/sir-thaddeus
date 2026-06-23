export class EventBus {
    handlers = new Map();
    wildcardHandlers = new Set();
    subscribe(type, handler) {
        const handlers = this.handlers.get(type) ?? new Set();
        handlers.add(handler);
        this.handlers.set(type, handlers);
        return () => {
            handlers.delete(handler);
            if (handlers.size === 0) {
                this.handlers.delete(type);
            }
        };
    }
    subscribeAll(handler) {
        this.wildcardHandlers.add(handler);
        return () => this.wildcardHandlers.delete(handler);
    }
    async publish(type, payload, options = {}) {
        const event = {
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
    clear() {
        this.handlers.clear();
        this.wildcardHandlers.clear();
    }
}
