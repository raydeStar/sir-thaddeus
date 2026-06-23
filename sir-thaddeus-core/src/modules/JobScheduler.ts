import { EventBus } from "./EventBus.js";
import { JobName, ModuleId } from "./ModuleManifest.js";
import { PermissionManager } from "./PermissionManager.js";

export interface JobContext {
  moduleId: ModuleId;
  jobName: JobName;
  eventBus: EventBus;
}

export type JobHandler<TResult = unknown> = (context: JobContext) => TResult | Promise<TResult>;

export interface JobRegistration<TResult = unknown> {
  moduleId: ModuleId;
  name: JobName;
  handler: JobHandler<TResult>;
}

export interface JobSchedule {
  name: JobName;
  runAt?: Date;
  intervalMs?: number;
}

export class JobScheduler {
  private readonly jobs = new Map<JobName, JobRegistration>();
  private readonly timers = new Map<JobName, ReturnType<typeof setTimeout>>();

  constructor(
    private readonly permissions: PermissionManager,
    private readonly eventBus: EventBus
  ) {}

  register<TResult = unknown>(registration: JobRegistration<TResult>): void {
    if (this.jobs.has(registration.name)) {
      throw new Error(`Job '${registration.name}' is already registered.`);
    }

    this.jobs.set(registration.name, registration as JobRegistration);
  }

  unregister(name: JobName): boolean {
    this.cancel(name);
    return this.jobs.delete(name);
  }

  list(): Omit<JobRegistration, "handler">[] {
    return [...this.jobs.values()].map(({ moduleId, name }) => ({ moduleId, name }));
  }

  schedule(schedule: JobSchedule): void {
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

  cancel(name: JobName): boolean {
    const timer = this.timers.get(name);
    if (!timer) {
      return false;
    }

    clearTimeout(timer);
    this.timers.delete(name);
    return true;
  }

  async run<TResult = unknown>(name: JobName): Promise<TResult> {
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
      return result as TResult;
    } catch (error) {
      await this.eventBus.publish(
        "job.failed",
        { name, error: error instanceof Error ? error.message : String(error) },
        { moduleId: registration.moduleId }
      );
      throw error;
    }
  }

  private requireJob(name: JobName): JobRegistration {
    const registration = this.jobs.get(name);
    if (!registration) {
      throw new Error(`Job '${name}' is not registered.`);
    }

    return registration;
  }
}
