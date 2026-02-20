using System;
using SirThaddeus.Core;
using SirThaddeus.Voice;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Unified runtime state encompassing both core assistant state and voice state.
/// This serves as the single source of truth for the UI header.
/// </summary>
public enum RuntimeState
{
    Idle,
    Listening,
    Transcribing,
    Thinking,
    Speaking,
    ReadingScreen,
    BrowserControl,
    ServiceWorking,
    Faulted,
    Stopped
}

public static class RuntimeStateExtensions
{
    public static string ToDisplayLabel(this RuntimeState state) => state switch
    {
        RuntimeState.Stopped => "Stopped",
        RuntimeState.Idle => "Connected", // Updated for plain language
        RuntimeState.Listening => "Listening",
        RuntimeState.Transcribing => "Transcribing",
        RuntimeState.Thinking => "Thinking",
        RuntimeState.Speaking => "Speaking",
        RuntimeState.ReadingScreen => "Reading Screen",
        RuntimeState.BrowserControl => "Using browser", // Updated for plain language
        RuntimeState.ServiceWorking => "Working in background", // Updated for plain language
        RuntimeState.Faulted => "Error",
        _ => state.ToString()
    };

    public static string ToIconHint(this RuntimeState state) => state switch
    {
        RuntimeState.Stopped => "power_off",
        RuntimeState.Idle => "check_circle",
        RuntimeState.Listening => "mic",
        RuntimeState.Transcribing => "waveform",
        RuntimeState.Thinking => "hourglass",
        RuntimeState.Speaking => "speaker",
        RuntimeState.ReadingScreen => "visibility",
        RuntimeState.BrowserControl => "mouse",
        RuntimeState.ServiceWorking => "cloud",
        RuntimeState.Faulted => "error",
        _ => "help"
    };
}

/// <summary>
/// Single observable state store for the unified RuntimeState.
/// </summary>
public sealed class RuntimeStateStore : IDisposable
{
    private readonly RuntimeController _controller;
    private readonly IVoiceStateSource? _voiceStateSource;
    private bool _disposed;
    
    private RuntimeState _currentState = RuntimeState.Idle;

    public event EventHandler<RuntimeState>? StateChanged;

    public RuntimeStateStore(RuntimeController controller, IVoiceStateSource? voiceStateSource = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _voiceStateSource = voiceStateSource;

        _controller.StateChanged += OnControllerStateChanged;
        if (_voiceStateSource is not null)
        {
            _voiceStateSource.StateChanged += OnVoiceStateChanged;
        }

        UpdateState();
    }

    public RuntimeState CurrentState
    {
        get
        {
            lock (this)
            {
                return _currentState;
            }
        }
    }

    private void OnControllerStateChanged(object? sender, StateChangedEventArgs e)
    {
        UpdateState();
    }

    private void OnVoiceStateChanged(object? sender, VoiceStateChangedEventArgs e)
    {
        UpdateState();
    }

    private void UpdateState()
    {
        RuntimeState newState;

        if (_controller.IsStopped || _controller.CurrentState == AssistantState.Off)
        {
            newState = RuntimeState.Stopped;
        }
        else if (_voiceStateSource is not null && _voiceStateSource.CurrentState != VoiceState.Idle)
        {
            newState = _voiceStateSource.CurrentState switch
            {
                VoiceState.Listening => RuntimeState.Listening,
                VoiceState.Transcribing => RuntimeState.Transcribing,
                VoiceState.Thinking => RuntimeState.Thinking,
                VoiceState.Speaking => RuntimeState.Speaking,
                VoiceState.Faulted => RuntimeState.Faulted,
                _ => RuntimeState.Idle
            };
        }
        else
        {
            newState = _controller.CurrentState switch
            {
                AssistantState.Listening => RuntimeState.Listening,
                AssistantState.Thinking => RuntimeState.Thinking,
                AssistantState.ReadingScreen => RuntimeState.ReadingScreen,
                AssistantState.BrowserControl => RuntimeState.BrowserControl,
                AssistantState.ServiceWorking => RuntimeState.ServiceWorking,
                _ => RuntimeState.Idle
            };
        }

        bool changed = false;
        lock (this)
        {
            if (_currentState != newState)
            {
                _currentState = newState;
                changed = true;
            }
        }

        if (changed)
        {
            StateChanged?.Invoke(this, newState);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _controller.StateChanged -= OnControllerStateChanged;
        if (_voiceStateSource is not null)
        {
            _voiceStateSource.StateChanged -= OnVoiceStateChanged;
        }
    }
}