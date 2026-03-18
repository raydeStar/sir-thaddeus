# Non-Transferable WPF Functionality

While migrating from the former monolithic WPF architecture to the decoupled Avalonia/Headless API architecture, certain functionalities from the removed `SirThaddeus.DesktopRuntime` cannot be perfectly transferred 1:1. These will require manual attention, platform-specific workarounds, or new libraries in Avalonia.

## 1. Rich Markdown Rendering (FlowDocument)
**WPF Implementation:** The old app used `FlowDocumentScrollViewer` and a `MarkdownToFlowDocumentConverter` to render rich markdown (bolding, lists, code blocks, syntax highlighting) directly in the chat ledger.
**Avalonia Status:** 
- Avalonia does not have a built-in `FlowDocument` equivalent out of the box. 
- The current Avalonia scaffold uses a plain `TextBox` or text blocks for the chat transcript, losing the rich formatting.
**Remediation Required:** You will need to bring in a 3rd-party Markdown renderer for Avalonia (such as `Markdown.Avalonia`) or implement an HTML-based WebView renderer to restore rich message formatting.

## 2. Global System Hotkeys (Push to Talk)
**WPF Implementation:** Relied on `RegisterHotKey` (Win32 API) via `HwndSource` to bind `Ctrl+Alt+M` system-wide, even when the app was in the background.
**Avalonia Status:** 
- Global hotkeys are OS-specific. The current Avalonia code has a Windows-specific fallback hook, but it lacks native support for macOS/Linux.
- `PttHoldButton` works fine while focused, but true system-wide capture requires platform-specific bridging.
**Remediation Required:** Write platform-specific implementations for `TryStartGlobalPushToTalkHotkey()` using macOS `NSEvent` or Linux `X11` hooks if cross-platform background capture is intended.

## 3. Direct Audio Playback & Microphone Capture Hooks (NAudio)
**WPF Implementation:** The legacy app used NAudio natively, bound directly to Windows WASAPI loopback.
**Avalonia Status:**
- NAudio is Windows-only. The Avalonia client currently includes `NAudioMicrophoneCaptureService`, which limits the cross-platform goal.
**Remediation Required:** Swap NAudio for a cross-platform audio capturing library (like `PortAudio` or `OpenTK.Audio`) for the microphone/Push-to-Talk logic on Linux and Mac.

## 4. Win32 System Tray & Window Blur State (Glassmorphism)
**WPF Implementation:** Leveraged `WindowChrome` and specific Win32 API calls (`DwmEnableBlurBehindWindow`) for deep glass/mica integration and a native system tray icon with a WinForms `NotifyIcon`.
**Avalonia Status:**
- Avalonia provides basic TrayIcon support natively (`<TrayIcon>`), but native context menus and double-click behaviors can vary slightly by OS.
- Acrylic/Mica effects rely on Avalonia's `TransparencyLevelHint="Mica"` instead of the Win32 hooks.
**Remediation Required:** Test the window transparency settings on your specific Linux/Mac environments to ensure the styling behaves as expected.

## 5. Drag-and-Drop File Attachments
**WPF Implementation:** Handled via `AllowDrop="True"` and processing `DataObject.GetData(DataFormats.FileDrop)`.
**Avalonia Status:**
- The Avalonia client has an "Attach" button wired to a file picker (`Avalonia.Platform.Storage`), but drag-and-drop requires setting up Avalonia's `DragDrop.AllowDrop` attached properties and handling `DragDrop.DropEvent`.
**Remediation Required:** Explicitly wire up Avalonia drag-and-drop events on the `ChatView` target.
