using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SirThaddeus.Config;
using SirThaddeus.UI.Avalonia.ViewModels;
using System.ComponentModel;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    public void ConfigureTrayUi(bool trayAvailable, bool minimizeToTrayEnabled)
    {
        _trayAvailable = trayAvailable;

        MinimizeToTrayCheckBox.IsEnabled = trayAvailable;

        var desired = trayAvailable ? _uiSettings.MinimizeToTray : false;
        if (Application.Current is App app)
        {
            app.MinimizeToTrayEnabled = desired;
        }

        MinimizeToTrayCheckBox.IsChecked = desired;
        TraySupportText.Text = trayAvailable
            ? "Tray is available. You can keep Sir Thaddeus running in the system tray."
            : "Tray is not available on this platform. Closing exits the app.";

        PersistUiSettings();
    }

    private void LlmPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            _backendSettings.LlmBaseUrl = url;
            _backendSettings.LlmModel = string.Empty;
        }
    }

    private void BackendSettings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SettingsViewModel.SelectedInputDevice), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(SettingsViewModel.SelectedOutputDevice), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(SettingsViewModel.InputGain), StringComparison.Ordinal))
        {
            ApplyAudioPreferences();
        }

        if (string.Equals(e.PropertyName, nameof(SettingsViewModel.VoiceHostEnabled), StringComparison.Ordinal))
        {
            BeginVoiceHostLifecycleTransition(_backendSettings.VoiceHostEnabled);
        }

        if (string.Equals(e.PropertyName, nameof(SettingsViewModel.VoiceHostBaseUrl), StringComparison.Ordinal) &&
            _backendSettings.VoiceHostEnabled)
        {
            BeginVoiceHostLifecycleTransition(enabled: true, restartManagedProcess: true);
        }

        if (string.Equals(e.PropertyName, nameof(SettingsViewModel.PttChord), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(SettingsViewModel.ShutupChord), StringComparison.Ordinal))
        {
            TryStartGlobalPushToTalkHotkey();
            SetPushToTalkReadyState();
        }

        if (!ReferenceEquals(SettingsTabControl.SelectedItem, AudioTabItem))
        {
            if (string.Equals(e.PropertyName, nameof(SettingsViewModel.VoiceHostEnabled), StringComparison.Ordinal) &&
                !_backendSettings.VoiceHostEnabled)
            {
                _backendSettings.StopVoiceHostHealthPolling();
            }

            return;
        }

        if (string.Equals(e.PropertyName, nameof(SettingsViewModel.VoiceHostEnabled), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(SettingsViewModel.VoiceHostBaseUrl), StringComparison.Ordinal))
        {
            if (_backendSettings.VoiceHostEnabled)
            {
                _backendSettings.StartVoiceHostHealthPolling();
                _ = _backendSettings.RefreshVoiceHostHealthAsync();
            }
            else
            {
                _backendSettings.StopVoiceHostHealthPolling();
            }
        }
    }

    private void ApplyUiSettingsToControls()
    {
        RuntimeUrlBox.Text = _uiSettings.RuntimeUrl;
        SendOnEnterCheckBox.IsChecked = _uiSettings.SendOnEnter;
        AutoSwitchPermissionsCheckBox.IsChecked = _uiSettings.AutoSwitchToPermissions;
        AutoConnectCheckBox.IsChecked = _uiSettings.AutoConnectOnLaunch;
        AutoStartRuntimeCheckBox.IsChecked = _uiSettings.AutoStartRuntime;
    }

    private void PersistUiSettings()
    {
        _uiSettingsStore.Save(_uiSettings);
    }

    private async Task SaveBackendSettingsAsync(
        string connectedStatus,
        string localStatus,
        string localHealthStatus,
        string syncFailureStatus,
        bool appendTranscript)
    {
        var snapshot = _backendSettings.BuildPersistableSnapshot();
        AppSettings localPersisted;

        try
        {
            SettingsManager.Save(snapshot);
            localPersisted = SettingsManager.Load();

            if (_runtimeApiClient is null)
            {
                _backendSettings.ApplySavedSnapshot(localPersisted, localStatus);
                _backendSettings.ResetSearchHealthState(
                    "Not connected",
                    localHealthStatus);
                if (appendTranscript)
                {
                    AppendTranscript("[system] " + localStatus);
                }

                return;
            }

            try
            {
                var persisted = await _runtimeApiClient.SaveSettingsAsync(localPersisted, CancellationToken.None);
                SettingsManager.Save(persisted);
                var syncedPersisted = SettingsManager.Load();
                _backendSettings.ApplySavedSnapshot(syncedPersisted, connectedStatus);
                await RefreshSearchStatusAsync();
                if (appendTranscript)
                {
                    AppendTranscript("[system] " + connectedStatus);
                }
            }
            catch (Exception ex)
            {
                _backendSettings.ApplySavedSnapshot(localPersisted, syncFailureStatus);
                _backendSettings.ResetSearchHealthState("Unavailable", syncFailureStatus);
                if (appendTranscript)
                {
                    AppendTranscript("[error] Runtime settings sync failed: " + ex.Message);
                    AppendTranscript("[system] " + syncFailureStatus);
                }
            }
        }
        catch (Exception ex)
        {
            _backendSettings.SetStatus("Settings save failed: " + ex.Message);
            if (appendTranscript)
            {
                AppendTranscript("[error] Settings save failed: " + ex.Message);
            }
        }
    }

    private async void SaveSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        await SaveBackendSettingsAsync(
            connectedStatus: "Settings saved and applied to the connected runtime.",
            localStatus: "Settings saved locally. Connect or restart the runtime to apply them.",
            localHealthStatus: "Settings saved locally. Connect the runtime to inspect live web-search and MCP health.",
            syncFailureStatus: "Settings saved locally. Runtime sync failed; reconnect to apply them.",
            appendTranscript: true);
    }

    private void ReloadSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        _backendSettings.Reload();
        AppendTranscript("[system] Settings reloaded from disk.");
    }

    private async void RefreshPrimaryModelsButton_Click(object? sender, RoutedEventArgs e)
    {
        await _backendSettings.RefreshPrimaryModelsAsync();
    }

    private async void RefreshGatekeeperModelsButton_Click(object? sender, RoutedEventArgs e)
    {
        await _backendSettings.RefreshGatekeeperModelsAsync();
    }

    private async void RefreshVoiceHostHealthButton_Click(object? sender, RoutedEventArgs e)
    {
        await _backendSettings.RefreshVoiceHostHealthAsync();
    }

    private void RefreshAudioDevicesButton_Click(object? sender, RoutedEventArgs e)
    {
        _backendSettings.RefreshAudioDevices();
        ApplyAudioPreferences();
    }

    private void RefreshTtsVoicesButton_Click(object? sender, RoutedEventArgs e)
    {
        _backendSettings.RefreshVoiceCatalogs("TTS voices refreshed.");
    }

    private async void AddAllowedFileRootButton_Click(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            _backendSettings.SetStatus("Folder picker is unavailable on this platform.");
            AppendTranscript("[error] Folder picker is unavailable on this platform.");
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an allowed folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.TryGetLocalPath() is not { Length: > 0 } path)
        {
            return;
        }

        var previousCount = _backendSettings.AllowedFileRoots.Count;
        _backendSettings.AddAllowedFileRoot(path);
        if (_backendSettings.AllowedFileRoots.Count == previousCount)
        {
            return;
        }

        await SaveBackendSettingsAsync(
            connectedStatus: "File access settings saved and applied to the connected runtime.",
            localStatus: "File access settings saved locally.",
            localHealthStatus: "File access settings saved locally. Connect the runtime to inspect live web-search and MCP health.",
            syncFailureStatus: "File access settings saved locally. Runtime sync failed; reconnect to apply them.",
            appendTranscript: false);
    }

    private async void RemoveAllowedFileRootButton_Click(object? sender, RoutedEventArgs e)
    {
        if (AllowedFileRootsList.SelectedItem is string path)
        {
            var previousCount = _backendSettings.AllowedFileRoots.Count;
            _backendSettings.RemoveAllowedFileRoot(path);
            if (_backendSettings.AllowedFileRoots.Count == previousCount)
            {
                return;
            }

            await SaveBackendSettingsAsync(
                connectedStatus: "File access settings saved and applied to the connected runtime.",
                localStatus: "File access settings saved locally.",
                localHealthStatus: "File access settings saved locally. Connect the runtime to inspect live web-search and MCP health.",
                syncFailureStatus: "File access settings saved locally. Runtime sync failed; reconnect to apply them.",
                appendTranscript: false);
        }
    }

    private async void DisableAllFileAccessCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (!_backendSettings.IsDirty)
        {
            return;
        }

        await SaveBackendSettingsAsync(
            connectedStatus: "File access settings saved and applied to the connected runtime.",
            localStatus: "File access settings saved locally.",
            localHealthStatus: "File access settings saved locally. Connect the runtime to inspect live web-search and MCP health.",
            syncFailureStatus: "File access settings saved locally. Runtime sync failed; reconnect to apply them.",
            appendTranscript: false);
    }

    private void BeginVoiceHostLifecycleTransition(bool enabled, bool restartManagedProcess = false)
    {
        _voiceHostLifecycleCancellation?.Cancel();
        _voiceHostLifecycleCancellation?.Dispose();
        _voiceHostLifecycleCancellation = null;

        if (!enabled)
        {
            _voiceHostLauncher.StopManagedVoiceHost();
            return;
        }

        if (restartManagedProcess)
        {
            _voiceHostLauncher.StopManagedVoiceHost();
        }

        var cts = new CancellationTokenSource();
        _voiceHostLifecycleCancellation = cts;
        _ = StartManagedVoiceHostAsync(cts.Token);
    }

    private async Task StartManagedVoiceHostAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = _backendSettings.BuildPersistableSnapshot();
            var baseUrl = snapshot.Voice.GetVoiceHostBaseUrl();
            _backendSettings.SetVoiceHostStatus("Starting...", $"Starting VoiceHost at {baseUrl}...");

            var result = await _voiceHostLauncher.EnsureRunningAsync(snapshot.Voice, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (result.Status is VoiceHostLaunchStatus.Started or VoiceHostLaunchStatus.AlreadyRunning)
            {
                _backendSettings.SetVoiceHostStatus("Checking...", result.Message);
                await _backendSettings.RefreshVoiceHostHealthAsync(cancellationToken);
                return;
            }

            _backendSettings.SetVoiceHostStatus("Failed", result.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _backendSettings.SetVoiceHostStatus("Error", ex.Message);
        }
    }

    private void SettingsTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabControl)
        {
            return;
        }

        if (tabControl.SelectedItem is not TabItem selectedTab ||
            !selectedTab.IsEnabled ||
            selectedTab.Classes.Contains("navGroupHeader"))
        {
            var fallbackTab = _lastValidSettingsTabItem ?? GeneralTabItem;
            if (!ReferenceEquals(tabControl.SelectedItem, fallbackTab))
            {
                tabControl.SelectedItem = fallbackTab;
            }

            return;
        }

        _lastValidSettingsTabItem = selectedTab;

        if (ReferenceEquals(tabControl.SelectedItem, LlmsTabItem))
        {
            _backendSettings.StopVoiceHostHealthPolling();
            _ = _backendSettings.OnLlmsTabActivatedAsync();
        }
        else if (ReferenceEquals(tabControl.SelectedItem, AudioTabItem))
        {
            _backendSettings.RefreshVoiceCatalogs();
            ApplyAudioPreferences();
            _backendSettings.StartVoiceHostHealthPolling();
            _ = _backendSettings.RefreshVoiceHostHealthAsync();
        }
        else if (ReferenceEquals(tabControl.SelectedItem, SearchTabItem))
        {
            _backendSettings.StopVoiceHostHealthPolling();
            _ = RefreshSearchStatusAsync();
        }
        else
        {
            _backendSettings.StopVoiceHostHealthPolling();

            if (ReferenceEquals(tabControl.SelectedItem, AuditTabItem))
            {
                _ = RefreshAuditAsync();
            }
            else if (ReferenceEquals(tabControl.SelectedItem, MemoryTabItem))
            {
                _ = RefreshMemoryAsync();
            }
            else if (ReferenceEquals(tabControl.SelectedItem, ProfilesTabItem))
            {
                _ = RefreshProfilesAsync();
            }
        }
    }

    private void ApplyAudioPreferences()
    {
        _microphoneCaptureService.DeviceNumber = _backendSettings.SelectedInputDevice?.DeviceNumber ?? -1;
        _microphoneCaptureService.InputGain = _backendSettings.InputGain;
        _ttsPlaybackService.OutputDeviceNumber = _backendSettings.SelectedOutputDevice?.DeviceNumber ?? -1;
    }

    private void SendOnEnterCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        _uiSettings = _uiSettings with { SendOnEnter = SendOnEnterCheckBox.IsChecked == true };
        PersistUiSettings();
    }

    private void AutoSwitchPermissionsCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        _uiSettings = _uiSettings with { AutoSwitchToPermissions = AutoSwitchPermissionsCheckBox.IsChecked == true };
        PersistUiSettings();
    }

    private void AutoConnectCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        _uiSettings = _uiSettings with { AutoConnectOnLaunch = AutoConnectCheckBox.IsChecked == true };
        PersistUiSettings();
    }

    private void AutoStartRuntimeCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        _uiSettings = _uiSettings with { AutoStartRuntime = AutoStartRuntimeCheckBox.IsChecked == true };
        PersistUiSettings();
    }

    private void MinimizeToTrayCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (!_trayAvailable)
        {
            MinimizeToTrayCheckBox.IsChecked = false;
            return;
        }

        var enabled = MinimizeToTrayCheckBox.IsChecked == true;
        _uiSettings = _uiSettings with { MinimizeToTray = enabled };
        if (Application.Current is App app)
        {
            app.MinimizeToTrayEnabled = enabled;
        }

        PersistUiSettings();
    }
}