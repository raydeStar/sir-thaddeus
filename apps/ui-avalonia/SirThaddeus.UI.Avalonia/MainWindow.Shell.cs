using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_initialConnectAttempted)
        {
            return;
        }

        _initialConnectAttempted = true;
        TryStartGlobalPushToTalkHotkey();
        BeginVoiceHostLifecycleTransition(_backendSettings.VoiceHostEnabled);

        if (_uiSettings.AutoConnectOnLaunch && !AppStartupOptions.Current.SmokeTestMode)
        {
            await EnsureRuntimeConnectedAsync(
                allowStartRuntime: _uiSettings.AutoStartRuntime,
                appendTranscriptOnFailure: false);
        }

        UpdateRuntimeLaunchStatusText();
        UpdateHeaderConnectionControls();
        UpdateActionDrawerSummary();
        UpdateComposerState();
    }

    private void ViewTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked)
        {
            return;
        }

        SetActiveView(clicked);
    }

    private void SetActiveView(ToggleButton selected)
    {
        foreach (var tab in _viewTabs)
        {
            tab.IsChecked = ReferenceEquals(tab, selected);
        }

        for (var i = 0; i < _viewTabs.Length; i++)
        {
            _viewPanels[i].IsVisible = _viewTabs[i].IsChecked == true;
        }

        InputBar.IsVisible = ChatTabButton.IsChecked == true || BriefingTabButton.IsChecked == true;
        UpdateLandingEmptyStateVisibility();
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            Hide();
            return;
        }

        if (OperatingSystem.IsWindows() &&
            ShouldUseWindowScopedPttHotkey() &&
            IsConfiguredHotkeyDown(e, _backendSettings.ShutupChord))
        {
            e.Handled = true;
            _ = RequestVoiceCancelAsync("window cancel hotkey");
            return;
        }

        if (ShouldUseWindowScopedPttHotkey() &&
            IsConfiguredHotkeyDown(e, _backendSettings.PttChord))
        {
            e.Handled = true;

            if (IsVoiceResponseActive())
            {
                _ = RequestVoiceCancelAsync("window ptt interrupt hotkey");
                return;
            }

            if (!_pttHotkeyDown)
            {
                _pttHotkeyDown = true;
                _ = BeginPushToTalkAsync("hotkey");
            }

            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.D1:
                SetActiveView(ChatTabButton);
                e.Handled = true;
                return;
            case Key.D2:
                SetActiveView(BriefingTabButton);
                e.Handled = true;
                return;
            case Key.D3:
                SetActiveView(SettingsTabButton);
                SettingsTabControl.SelectedItem = PermissionsTabItem;
                e.Handled = true;
                return;
            case Key.D4:
                SetActiveView(SettingsTabButton);
                SettingsTabControl.SelectedItem = AuditTabItem;
                e.Handled = true;
                return;
            case Key.D5:
                SetActiveView(SettingsTabButton);
                SettingsTabControl.SelectedItem = MemoryTabItem;
                e.Handled = true;
                return;
            case Key.D6:
                SetActiveView(SettingsTabButton);
                SettingsTabControl.SelectedItem = ProfilesTabItem;
                e.Handled = true;
                return;
            case Key.D7:
                SetActiveView(SettingsTabButton);
                e.Handled = true;
                return;
        }
    }

    private void Window_KeyUp(object? sender, KeyEventArgs e)
    {
        if (ShouldUseWindowScopedPttHotkey() &&
            _pttHotkeyDown &&
            IsConfiguredHotkeyTriggerKey(e.Key, _backendSettings.PttChord))
        {
            _pttHotkeyDown = false;
            e.Handled = true;
            _ = EndPushToTalkAsync("hotkey");
        }
    }
}