using System.Collections.ObjectModel;
using System.Windows.Input;
using SirThaddeus.AuditLog;
using SirThaddeus.Core;
using SirThaddeus.PermissionBroker;
using SirThaddeus.ToolRunner;

namespace SirThaddeus.DesktopRuntime.ViewModels;

/// <summary>
/// ViewModel for the overlay pill and drawer.
/// </summary>
public sealed class OverlayViewModel : ViewModelBase, IDisposable
{
    private readonly RuntimeController _controller;
    private readonly IAuditLogger _auditLogger;
    private readonly IPermissionBroker _permissionBroker;
    private readonly IToolRunner _toolRunner;
    private readonly Action _requestShutdown;
    
    private bool _isDrawerOpen;
    private string _stateLabel = "";
    private string _stateIcon = "";
    private bool _disposed;
    private int _simulatedStateIndex = 1;
    private string _permissionDemoStatus = "Ready";
    private int _activeTokenCount;

    public OverlayViewModel(
        RuntimeController controller, 
        IAuditLogger auditLogger,
        IPermissionBroker permissionBroker,
        IToolRunner toolRunner,
        Action requestShutdown)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _permissionBroker = permissionBroker ?? throw new ArgumentNullException(nameof(permissionBroker));
        _toolRunner = toolRunner ?? throw new ArgumentNullException(nameof(toolRunner));
        _requestShutdown = requestShutdown ?? throw new ArgumentNullException(nameof(requestShutdown));

        // Initialize commands
        ToggleDrawerCommand = new RelayCommand(ToggleDrawer);
        StopAllCommand = new RelayCommand(ExecuteStopAll, () => !_controller.IsStopped);
        PauseServiceJobsCommand = new RelayCommand(PauseServiceJobs, () => false); // Stubbed for V0
        RefreshAuditFeedCommand = new RelayCommand(RefreshAuditFeed);
        SimulateStateCommand = new RelayCommand(SimulateNextState, () => !_controller.IsStopped);
        DemoPermissionCommand = new AsyncRelayCommand(DemoPermissionFlow);

        // Subscribe to state changes
        _controller.StateChanged += OnStateChanged;
        
        // Initialize state
        UpdateStateDisplay();
        UpdateActiveTokenCount();
        RefreshAuditFeed();
    }

    /// <summary>
    /// Whether we're running in debug mode.
    /// </summary>
    public bool IsDebugMode
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// Whether the drawer is currently open.
    /// </summary>
    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        set => SetProperty(ref _isDrawerOpen, value);
    }

    /// <summary>
    /// The current state display label.
    /// </summary>
    public string StateLabel
    {
        get => _stateLabel;
        private set => SetProperty(ref _stateLabel, value);
    }

    /// <summary>
    /// Icon hint for the current state.
    /// </summary>
    public string StateIcon
    {
        get => _stateIcon;
        private set => SetProperty(ref _stateIcon, value);
    }

    /// <summary>
    /// The most recent audit events.
    /// </summary>
    public ObservableCollection<AuditEventViewModel> AuditFeed { get; } = new();

    /// <summary>
    /// Command to toggle the drawer open/closed.
    /// </summary>
    public ICommand ToggleDrawerCommand { get; }

    /// <summary>
    /// Command to execute STOP ALL.
    /// </summary>
    public ICommand StopAllCommand { get; }

    /// <summary>
    /// Command to pause service jobs (stubbed for V0).
    /// </summary>
    public ICommand PauseServiceJobsCommand { get; }

    /// <summary>
    /// Command to refresh the audit feed.
    /// </summary>
    public ICommand RefreshAuditFeedCommand { get; }

    /// <summary>
    /// Debug command to simulate state changes.
    /// </summary>
    public ICommand SimulateStateCommand { get; }

    /// <summary>
    /// Command to demo the permission + tool execution flow.
    /// </summary>
    public ICommand DemoPermissionCommand { get; }

    /// <summary>
    /// Status message from the permission demo.
    /// </summary>
    public string PermissionDemoStatus
    {
        get => _permissionDemoStatus;
        private set => SetProperty(ref _permissionDemoStatus, value);
    }

    /// <summary>
    /// Number of currently active permission tokens.
    /// </summary>
    public int ActiveTokenCount
    {
        get => _activeTokenCount;
        private set => SetProperty(ref _activeTokenCount, value);
    }

    private void ToggleDrawer()
    {
        IsDrawerOpen = !IsDrawerOpen;
        if (IsDrawerOpen)
        {
            RefreshAuditFeed();
        }
    }

    private void ExecuteStopAll()
    {
        _controller.StopAll();
        RefreshAuditFeed();
        _requestShutdown();
    }

    private void PauseServiceJobs()
    {
        // Stubbed for V0 - no service connection yet
    }

    private void RefreshAuditFeed()
    {
        var events = _auditLogger.ReadTail(10);
        
        AuditFeed.Clear();
        foreach (var evt in events.Reverse()) // Show newest first
        {
            AuditFeed.Add(new AuditEventViewModel(evt));
        }
    }

    private void SimulateNextState()
    {
        var states = Enum.GetValues<AssistantState>();
        var nextState = states[_simulatedStateIndex % states.Length];
        _simulatedStateIndex++;
        
        // Skip Off state in simulation
        if (nextState == AssistantState.Off)
        {
            nextState = states[_simulatedStateIndex % states.Length];
            _simulatedStateIndex++;
        }

        _controller.SetState(nextState, "Debug simulation");
        RefreshAuditFeed();
    }

    private void OnStateChanged(object? sender, StateChangedEventArgs e)
    {
        // Ensure we're on the UI thread
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            UpdateStateDisplay();
            RefreshAuditFeed();
        });
    }

    private void UpdateStateDisplay()
    {
        var state = _controller.CurrentState;
        StateLabel = state.ToDisplayLabel();
        StateIcon = state.ToIconHint();
    }

    private void UpdateActiveTokenCount()
    {
        ActiveTokenCount = _permissionBroker.ActiveTokenCount;
    }

    /// <summary>
    /// Demonstrates the full permission + tool execution flow:
    /// 1. Request permission (issue token)
    /// 2. Execute tool with token
    /// 3. Token expires or is revoked
    /// </summary>
    private async Task DemoPermissionFlow()
    {
        try
        {
            PermissionDemoStatus = "Requesting permission...";

            // Step 1: Request a permission token for screen reading
            var request = new PermissionRequest
            {
                Capability = Capability.ScreenRead,
                Purpose = "Demo: Capture screen for analysis",
                Scope = PermissionScope.ActiveWindow,
                Duration = TimeSpan.FromSeconds(10)
            };

            var token = _permissionBroker.IssueToken(request);
            UpdateActiveTokenCount();
            PermissionDemoStatus = $"Token issued (expires in 10s): {token.Id[..12]}...";
            RefreshAuditFeed();

            // Step 2: Execute a tool using the token
            await Task.Delay(500); // Brief visual pause
            
            var toolCall = new ToolCall
            {
                Id = ToolCall.GenerateId(),
                Name = "screen_capture",
                Purpose = "Demo screen capture",
                RequiredCapability = Capability.ScreenRead,
                Arguments = new Dictionary<string, object>
                {
                    ["target"] = "active_window"
                }
            };

            PermissionDemoStatus = "Executing tool with token...";
            var result = await _toolRunner.ExecuteAsync(toolCall, token.Id);

            if (result.Success)
            {
                PermissionDemoStatus = $"Tool executed successfully (took {result.DurationMs}ms)";
            }
            else
            {
                PermissionDemoStatus = $"Tool failed: {result.Error}";
            }

            RefreshAuditFeed();
            UpdateActiveTokenCount();

            // Brief pause to show the result
            await Task.Delay(2000);

            // Step 3: Demonstrate token still works (within expiry)
            PermissionDemoStatus = "Executing again with same token...";
            var result2 = await _toolRunner.ExecuteAsync(
                toolCall with { Id = ToolCall.GenerateId() },
                token.Id);
            
            PermissionDemoStatus = result2.Success 
                ? "Second execution succeeded (token still valid)" 
                : $"Second execution failed: {result2.Error}";
            
            RefreshAuditFeed();
            UpdateActiveTokenCount();
        }
        catch (Exception ex)
        {
            PermissionDemoStatus = $"Error: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _controller.StateChanged -= OnStateChanged;
    }
}
