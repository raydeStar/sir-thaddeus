export const healthPackManifest = {
    id: "com.thaddeus.health",
    name: "Health Pack",
    version: "0.1.0",
    description: "Adds biometrics, health snapshots, baselines, similar-day lookup, manual check-ins, and morning strategy briefs.",
    permissions: {
        externalAccounts: [
            {
                provider: "google-health",
                scopes: ["sleep.read", "heart_rate.read", "hrv.read", "activity.read"]
            },
            {
                provider: "fitbit",
                scopes: ["sleep.read", "heart_rate.read", "hrv.read", "activity.read"]
            }
        ],
        memory: {
            read: ["fitness_goals", "sleep_preferences", "daily_health_snapshots", "pattern_episodes"],
            write: [
                "daily_health_snapshots",
                "daily_health_baselines",
                "daily_strategy_briefs",
                "pattern_episodes",
                "manual_health_checkins"
            ]
        },
        notifications: ["morning_strategy_brief"]
    },
    tools: [
        "health.get_daily_snapshot",
        "health.refresh_daily_snapshot",
        "health.get_baselines",
        "health.get_morning_strategy_brief",
        "health.get_similar_past_days",
        "health.log_manual_checkin",
        "health.provider_status",
        "health.provider_config_schema",
        "health.secret_store_status",
        "health.set_provider_config",
        "health.clear_provider_config",
        "health.start_provider_auth",
        "health.complete_provider_auth",
        "health.disconnect_provider",
        "health.provider_audit_events",
        "health.sync_range",
        "health.backfill"
    ],
    jobs: ["health.morning_strategy_job"],
    hooks: ["on_module_installed", "on_morning"],
    settings: [
        {
            key: "health.provider",
            type: "string",
            label: "Health provider",
            defaultValue: "mock"
        }
    ],
    memoryNamespaces: [
        "daily_health_snapshots",
        "daily_health_baselines",
        "daily_strategy_briefs",
        "pattern_episodes",
        "manual_health_checkins"
    ]
};
