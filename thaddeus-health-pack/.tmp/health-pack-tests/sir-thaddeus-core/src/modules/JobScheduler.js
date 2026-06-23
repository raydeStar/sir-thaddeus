export class JobScheduler {
    permissions;
    eventBus;
    jobs = new Map();
    timers = new Map();
    constructor(permissions, eventBus) {
        this.permissions = permissions;
        this.eventBus = eventBus;
    }
    register(registration) {
        if (this.jobs.has(registration.name)) {
            throw new Error(`Job '${registration.name}' is already registered.`);
        }
        this.jobs.set(registration.name, registration);
    }
    unregister(name) {
        this.cancel(name);
        return this.jobs.delete(name);
    }
    list() {
        return [...this.jobs.values()].map(({ moduleId, name }) => ({ moduleId, name }));
    }
    schedule(schedule) {
        const registration = this.requireJob(schedule.name);
        this.cancel(schedule.name);
        const delayMs = schedule.runAt
            ? Math.max(0, schedule.runAt.getTime() - Date.now())
            : 0;
        const run = async () => {
            await this.run(schedule.name);
            if (schedule.intervalMs && schedule.intervalMs > 0 && this.jobs.has(schedule.name)) {
                this.timers.set(schedule.name, setTimeout(run, schedule.intervalMs));
            }
        };
        this.timers.set(registration.name, setTimeout(run, delayMs));
    }
    cancel(name) {
        const timer = this.timers.get(name);
        if (!timer) {
            return false;
        }
        clearTimeout(timer);
        this.timers.delete(name);
        return true;
    }
    async run(name) {
        const registration = this.requireJob(name);
        const decision = this.permissions.canUseModule(registration.moduleId);
        if (!decision.allowed) {
            throw new Error(decision.reason ?? `Module '${registration.moduleId}' is not permitted.`);
        }
        await this.eventBus.publish("job.started", { name }, { moduleId: registration.moduleId });
        try {
            const result = await registration.handler({
                moduleId: registration.moduleId,
                jobName: name,
                eventBus: this.eventBus
            });
            await this.eventBus.publish("job.completed", { name, result }, { moduleId: registration.moduleId });
            return result;
        }
        catch (error) {
            await this.eventBus.publish("job.failed", { name, error: error instanceof Error ? error.message : String(error) }, { moduleId: registration.moduleId });
            throw error;
        }
    }
    requireJob(name) {
        const registration = this.jobs.get(name);
        if (!registration) {
            throw new Error(`Job '${name}' is not registered.`);
        }
        return registration;
    }
}
