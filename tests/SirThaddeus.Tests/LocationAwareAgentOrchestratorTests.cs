using Microsoft.Extensions.Time.Testing;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Config;
using SirThaddeus.DesktopRuntime.Services;

namespace SirThaddeus.Tests;

public sealed class LocationAwareAgentOrchestratorTests
{
    [Fact]
    public async Task NearMeWithoutManualLocation_AsksUserForLocation()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 2, 16, 12, 0, 0, TimeSpan.Zero));
        var inner = new FakeAgentOrchestrator();
        var settings = new AppSettings();
        var activeProfileId = "user-a";

        var orchestrator = CreateWrapper(
            inner,
            clock,
            () => settings,
            () => activeProfileId,
            saved =>
            {
                settings = saved;
            });

        var response = await orchestrator.ProcessAsync("are there any restaurants near me?");

        Assert.True(response.Success);
        Assert.Contains("Where are you located?", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(inner.ProcessedMessages);
    }

    [Fact]
    public async Task WhenAwaitingLocation_ProvidingLocation_SavesAndContinuesOriginalQuery()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 2, 16, 12, 0, 0, TimeSpan.Zero));
        var inner = new FakeAgentOrchestrator();
        var settings = new AppSettings();
        var saveCalls = 0;
        var activeProfileId = "user-a";

        var orchestrator = CreateWrapper(
            inner,
            clock,
            () => settings,
            () => activeProfileId,
            saved =>
            {
                saveCalls++;
                settings = saved;
            });

        var ask = await orchestrator.ProcessAsync("are there any restaurants near me?");
        Assert.Contains("Where are you located?", ask.Text, StringComparison.OrdinalIgnoreCase);

        var followUp = await orchestrator.ProcessAsync("Portland, OR");

        Assert.Equal(1, saveCalls);
        Assert.Equal("manual", settings.GetEffectiveUserLocation(activeProfileId).GetNormalizedMode());
        Assert.Equal("Portland, OR", settings.GetEffectiveUserLocation(activeProfileId).GetResolvedLabel());
        Assert.Single(inner.ProcessedMessages);
        Assert.Equal("are there any restaurants near me?", inner.ProcessedMessages[0]);
        Assert.Contains("processed", followUp.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StaleLocationQuery_AsksConfirmation_YesRefreshesTimestampAndContinues()
    {
        var now = new DateTimeOffset(2026, 2, 16, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var inner = new FakeAgentOrchestrator();
        var activeProfileId = "user-a";
        var settings = BuildManualLocationSettings("Seattle, WA", now.AddDays(-40), activeProfileId);
        var touchCalls = 0;

        var orchestrator = CreateWrapper(
            inner,
            clock,
            () => settings,
            () => activeProfileId,
            saved =>
            {
                settings = saved;
            },
            onTouch: () => touchCalls++);

        var confirm = await orchestrator.ProcessAsync("restaurants near me");
        Assert.Contains("Are you still at Seattle, WA", confirm.Text, StringComparison.OrdinalIgnoreCase);

        var continued = await orchestrator.ProcessAsync("yes");

        Assert.Equal(1, touchCalls);
        Assert.Single(inner.ProcessedMessages);
        Assert.Equal("restaurants near me", inner.ProcessedMessages[0]);
        Assert.Contains("processed", continued.Text, StringComparison.OrdinalIgnoreCase);

        var updatedAt = settings.GetEffectiveUserLocation(activeProfileId).GetResolvedUpdatedAt();
        Assert.True(DateTimeOffset.TryParse(updatedAt, out var parsed));
        Assert.True(parsed >= now.AddMinutes(-1));
    }

    [Fact]
    public async Task NearMeWithFreshLocation_PassesThroughWithoutPrompt()
    {
        var now = new DateTimeOffset(2026, 2, 16, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var inner = new FakeAgentOrchestrator();
        var activeProfileId = "user-a";
        var settings = BuildManualLocationSettings("Portland, OR", now.AddDays(-3), activeProfileId);

        var orchestrator = CreateWrapper(
            inner,
            clock,
            () => settings,
            () => activeProfileId,
            saved => settings = saved);

        var response = await orchestrator.ProcessAsync("restaurants near me");

        Assert.Single(inner.ProcessedMessages);
        Assert.Equal("restaurants near me", inner.ProcessedMessages[0]);
        Assert.Contains("processed", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FirstNearMeWithoutLocation_QueuesPopup_AndAsksLocationQuestion()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 2, 16, 12, 0, 0, TimeSpan.Zero));
        var inner = new FakeAgentOrchestrator();
        var settings = new AppSettings();
        var activeProfileId = "user-a";
        var promptCalls = 0;

        var orchestrator = CreateWrapper(
            inner,
            clock,
            () => settings,
            () => activeProfileId,
            saved => settings = saved,
            onPromptQueued: () => promptCalls++);

        var response = await orchestrator.ProcessAsync("hello there, any good restaurants near me?");

        Assert.Contains("Where are you located?", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, promptCalls);
        Assert.Empty(inner.ProcessedMessages);
    }

    [Fact]
    public async Task VettingSkip_DoesNotSaveLocation_AndNearMeStillAsksForLocation()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 2, 16, 12, 0, 0, TimeSpan.Zero));
        var inner = new FakeAgentOrchestrator();
        var settings = new AppSettings();
        var activeProfileId = "user-a";
        var saveCalls = 0;

        var orchestrator = CreateWrapper(
            inner,
            clock,
            () => settings,
            () => activeProfileId,
            saved =>
            {
                saveCalls++;
                settings = saved;
            });

        var firstTurn = await orchestrator.ProcessAsync("can you find a restaurant near me?");
        Assert.Contains("Where are you located?", firstTurn.Text, StringComparison.OrdinalIgnoreCase);

        var skip = await orchestrator.ProcessAsync("skip");

        Assert.Equal(0, saveCalls);
        Assert.Contains("No problem. I can still help.", skip.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(inner.ProcessedMessages);
        Assert.Null(settings.GetEffectiveUserLocation(activeProfileId).GetResolvedLabel());

        var retry = await orchestrator.ProcessAsync("can you find a restaurant near me?");
        Assert.Contains("Where are you located?", retry.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(inner.ProcessedMessages);
    }

    [Fact]
    public async Task SwitchingProfiles_AsksNearMePromptForEachProfile()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 2, 16, 12, 0, 0, TimeSpan.Zero));
        var inner = new FakeAgentOrchestrator();
        var settings = new AppSettings();
        var activeProfileId = "user-a";
        var promptCalls = 0;

        var orchestrator = CreateWrapper(
            inner,
            clock,
            () => settings,
            () => activeProfileId,
            saved => settings = saved,
            onPromptQueued: () => promptCalls++);

        var first = await orchestrator.ProcessAsync("hello, find coffee near me");
        Assert.Contains("Where are you located?", first.Text, StringComparison.OrdinalIgnoreCase);

        activeProfileId = "user-b";
        var second = await orchestrator.ProcessAsync("hello again, find lunch near me");
        Assert.Contains("Where are you located?", second.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, promptCalls);
    }

    [Fact]
    public async Task FirstTurnWithExplicitPlaceRequest_DoesNotBlockOnVetting()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 2, 16, 12, 0, 0, TimeSpan.Zero));
        var inner = new FakeAgentOrchestrator();
        var settings = new AppSettings();
        var activeProfileId = "user-a";
        var promptCalls = 0;
        const string message = "Use weather tools to provide a short weather outlook for Seattle, WA.";

        var orchestrator = CreateWrapper(
            inner,
            clock,
            () => settings,
            () => activeProfileId,
            saved => settings = saved,
            onPromptQueued: () => promptCalls++);

        var response = await orchestrator.ProcessAsync(message);

        Assert.Contains("processed", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(inner.ProcessedMessages);
        Assert.Equal(message, inner.ProcessedMessages[0]);
        Assert.Equal(0, promptCalls);
    }

    private static LocationAwareAgentOrchestrator CreateWrapper(
        FakeAgentOrchestrator inner,
        FakeTimeProvider clock,
        Func<AppSettings> getSettings,
        Func<string?> getActiveProfileId,
        Action<AppSettings> setSettings,
        Action? onTouch = null,
        Action? onPromptQueued = null)
    {
        return new LocationAwareAgentOrchestrator(
            inner,
            getSettings: () => getSettings(),
            getActiveProfileId: getActiveProfileId,
            saveManualLocation: value =>
            {
                var current = getSettings();
                var profileId = getActiveProfileId();
                var profileKey = AppSettings.NormalizeLocationProfileKey(profileId);
                var location = current.GetEffectiveUserLocation(profileId) with
                {
                    Mode = "manual",
                    Value = value,
                    UpdatedAt = clock.GetUtcNow().ToString("O"),
                    Enabled = true,
                    Label = value,
                    Timezone = "",
                    Latitude = null,
                    Longitude = null
                };

                var updated = current with
                {
                    UserProfile = current.UserProfile with
                    {
                        Location = location,
                        LocationsByProfile = new Dictionary<string, LocationSettings>(current.UserProfile.LocationsByProfile)
                        {
                            [profileKey] = location
                        }
                    },
                    Location = location
                };
                return updated;
            },
            touchManualLocationTimestamp: () =>
            {
                var current = getSettings();
                var profileId = getActiveProfileId();
                var profileKey = AppSettings.NormalizeLocationProfileKey(profileId);
                var existing = current.GetEffectiveUserLocation(profileId);
                var value = existing.GetResolvedLabel() ?? "";
                var location = existing with
                {
                    Mode = "manual",
                    Value = value,
                    UpdatedAt = clock.GetUtcNow().ToString("O"),
                    Enabled = true,
                    Label = value
                };

                var updated = current with
                {
                    UserProfile = current.UserProfile with
                    {
                        Location = location,
                        LocationsByProfile = new Dictionary<string, LocationSettings>(current.UserProfile.LocationsByProfile)
                        {
                            [profileKey] = location
                        }
                    },
                    Location = location
                };
                onTouch?.Invoke();
                return updated;
            },
            applySettings: updated =>
            {
                if (updated is not null)
                    setSettings(updated);
            },
            queueManualLocationPrompt: onPromptQueued,
            timeProvider: clock);
    }

    private static AppSettings BuildManualLocationSettings(string value, DateTimeOffset updatedAt, string? profileId = null)
    {
        var key = AppSettings.NormalizeLocationProfileKey(profileId);
        var location = new LocationSettings
        {
            Mode = "manual",
            Value = value,
            UpdatedAt = updatedAt.ToString("O"),
            Enabled = true,
            Label = value
        };

        return new AppSettings
        {
            UserProfile = new UserProfileSettings
            {
                Location = location,
                LocationsByProfile = new Dictionary<string, LocationSettings>
                {
                    [key] = location
                }
            },
            Location = location
        };
    }

    private sealed class FakeAgentOrchestrator : IAgentOrchestrator
    {
        public List<string> ProcessedMessages { get; } = [];

        public Task<AgentResponse> ProcessAsync(string userMessage, CancellationToken cancellationToken = default)
        {
            ProcessedMessages.Add(userMessage);
            return Task.FromResult(new AgentResponse
            {
                Text = $"processed: {userMessage}",
                Success = true
            });
        }

        public void ResetConversation()
        {
        }

        public void SeedDialogueState(DialogueState state)
        {
        }

        public DialogueContextSnapshot GetContextSnapshot() => new();

        public bool ContextLocked { get; set; }

        public Task<int> GetAvailableToolCountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
