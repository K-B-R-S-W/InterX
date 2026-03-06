using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UIAutomationClient;

namespace MeetingAssistant;

/// <summary>
/// Service to capture text from Windows Live Captions
/// Runs continuously in background, tracks sessions to avoid capturing old text
/// Uses line-by-line baseline tracking to filter out old text reliably
/// </summary>
public class WindowsCaptionService
{
    private CUIAutomation? _automation;
    private IUIAutomationElement? _captionWindow;
    private IntPtr _captionWindowHandle = IntPtr.Zero;
    private System.Threading.Timer? _pollingTimer;
    private StringBuilder _sessionText = new StringBuilder();
    
    // Stores all lines seen before or during session to prevent duplicates
    private HashSet<string> _baselineLines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    
    // Track the line currently being built by Live Captions (not yet complete)
    private string _currentBuildingLine = string.Empty;

    // Drain state — active between F10 and final capture
    private bool _isDraining;
    private string _drainLastLine = string.Empty;
    private int _drainStableCount;
    private long _drainStartMs;
    private Func<string, Task>? _drainCallback;
    private SynchronizationContext? _drainSyncCtx;
    private const int DrainStabilityPolls = 2;  // 2 × 100ms = 200ms stable
    private const int DrainTimeoutMs = 800;

    private readonly int _pollingIntervalMs;
    private bool _isRunning;
    private bool _isSessionActive;

    public event EventHandler<string>? ErrorOccurred;

    public bool IsRunning => _isRunning;
    public bool IsSessionActive => _isSessionActive;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int SW_HIDE = 0;
    private const int SW_MINIMIZE = 6;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;

    public WindowsCaptionService(int pollingIntervalMs = 100)
    {
        _pollingIntervalMs = pollingIntervalMs;
    }

    /// <summary>
    /// Start monitoring Windows Live Captions - runs continuously in background
    /// Call this once when app launches
    /// </summary>
    public bool Start()
    {
        if (_isRunning)
        {
            Console.WriteLine("[Caption] Already running");
            return true;
        }

        try
        {
            Console.WriteLine("[Caption] Starting Windows Live Captions monitoring (always-on mode)...");
            
            _sessionText.Clear();
            _baselineLines.Clear();
            _isSessionActive = false;

            // Try to enable Live Captions programmatically
            EnableLiveCaptions();

            // Wait for caption window to fully initialize
            Console.WriteLine("[Caption] Waiting for caption window to initialize...");
            System.Threading.Thread.Sleep(500);

            // Initialize UI Automation
            _automation = new CUIAutomation();

            // Find the Live Captions window
            if (!FindCaptionWindow())
            {
                var error = "Windows Live Captions not found. Please enable it manually:\n" +
                           "Press Win + Ctrl + L to turn on Live Captions";
                Console.WriteLine($"[Caption] {error}");
                ErrorOccurred?.Invoke(this, error);
                return false;
            }

            // Try to hide the caption window off-screen (important for screen sharing!)
            HideCaptionWindow();

            // Start polling timer - runs continuously
            _pollingTimer = new System.Threading.Timer(PollCaptions, null, 0, _pollingIntervalMs);
            _isRunning = true;

            Console.WriteLine("[Caption] Monitoring started - running in background");
            Console.WriteLine($"[Caption] Polling interval: {_pollingIntervalMs}ms");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Caption] Failed to start: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Start a new capture session
    /// Snapshots ALL currently visible lines into baseline so they are ignored
    /// Call this when user presses F9
    /// </summary>
    public void StartSession()
    {
        if (!_isRunning)
        {
            Console.WriteLine("[Caption] ✗ Cannot start session - service not running!");
            return;
        }

        // Clear previous session data
        _sessionText.Clear();
        _baselineLines.Clear();
        _currentBuildingLine = string.Empty;

        // Snapshot every line currently visible in the caption window
        // These are OLD lines — we never want to capture them
        var currentLines = ReadCurrentLines();
        foreach (var line in currentLines)
        {
            _baselineLines.Add(line);
        }

        _isSessionActive = true;

        Console.WriteLine($"[Caption] ✓ Session started. Baseline has {_baselineLines.Count} old lines blocked.");
        Console.WriteLine("[Caption] Now capturing new lines only...");
    }

    /// <summary>
    /// Begin ending the session: enters drain state and waits for the caption
    /// window to stop changing (max 800ms) before finalizing capture.
    /// onCaptured is invoked on the UI thread once the text is ready.
    /// Call this when user presses F10.
    /// </summary>
    public void BeginEndSession(Func<string, Task> onCaptured)
    {
        if (!_isSessionActive)
        {
            Console.WriteLine("[Caption] No active session");
            _ = onCaptured(string.Empty);
            return;
        }

        _drainCallback = onCaptured;
        _drainSyncCtx = SynchronizationContext.Current;
        _drainLastLine = _currentBuildingLine;  // seed with what we already know
        _drainStableCount = 0;
        _drainStartMs = Environment.TickCount64;
        _isSessionActive = false;
        _isDraining = true;

        Console.WriteLine($"[Caption] Drain started. Seeded with: \"{_drainLastLine}\"");
    }

    /// <summary>
    /// Called by PollCaptions once the building line has stabilized or timed out.
    /// Performs final capture and fires the callback on the UI thread.
    /// </summary>
    private void FinalizeSession()
    {
        _isDraining = false;

        // Include the stabilized building line if it's new
        if (!string.IsNullOrEmpty(_drainLastLine))
        {
            if (_sessionText.Length > 0)
                _sessionText.Append(" ");
            _sessionText.Append(_drainLastLine);
            Console.WriteLine($"[Caption] Finalized with building line: \"{_drainLastLine}\"");
        }

        var result = _sessionText.ToString().Trim();
        _sessionText.Clear();
        _drainLastLine = string.Empty;
        _drainStableCount = 0;

        Console.WriteLine($"[Caption] Session finalized. Captured: \"{result}\"");

        var callback = _drainCallback;
        var syncCtx = _drainSyncCtx;
        _drainCallback = null;
        _drainSyncCtx = null;

        if (callback == null) return;

        void Invoke()
        {
            _ = callback(result).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Console.WriteLine($"[Caption] Callback error: {t.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);
        }

        if (syncCtx != null)
            syncCtx.Post(_ => Invoke(), null);
        else
            Invoke();
    }

    /// <summary>
    /// Completely stop the caption service
    /// Call this only when app is closing
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        try
        {
            _pollingTimer?.Dispose();
            _pollingTimer = null;
            _isRunning = false;
            _isSessionActive = false;
            _isDraining = false;
            _drainCallback = null;

            Console.WriteLine($"[Caption] Service stopped");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Caption] Error stopping: {ex.Message}");
        }
    }

    /// <summary>
    /// Read all current text lines from the Live Captions window
    /// Returns cleaned, non-empty lines split by newline
    /// </summary>
    private List<string> ReadCurrentLines()
    {
        var lines = new List<string>();

        try
        {
            if (_automation == null || _captionWindow == null)
                return lines;

            var textCondition = _automation.CreatePropertyCondition(
                UIA_PropertyIds.UIA_ControlTypePropertyId,
                UIA_ControlTypeIds.UIA_TextControlTypeId
            );

            var textElements = _captionWindow.FindAll(
                TreeScope.TreeScope_Descendants,
                textCondition
            );

            if (textElements == null || textElements.Length == 0)
                return lines;

            for (int i = 0; i < textElements.Length; i++)
            {
                var element = textElements.GetElement(i);
                if (element == null) continue;

                var rawText = element.CurrentName;
                if (string.IsNullOrWhiteSpace(rawText)) continue;

                // Split by newline — Live Captions sometimes returns multiple
                // lines as one element separated by \n
                var splitLines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in splitLines)
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        lines.Add(trimmed);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Caption] Error reading lines: {ex.Message}");
        }

        return lines;
    }

    /// <summary>
    /// Enable Windows Live Captions programmatically
    /// </summary>
    private void EnableLiveCaptions()
    {
        try
        {
            // Send Win+Ctrl+L hotkey to toggle Live Captions
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-WindowStyle Hidden -Command \"" +
                           "Add-Type -AssemblyName System.Windows.Forms; " +
                           "[System.Windows.Forms.SendKeys]::SendWait('^{LWIN}l')\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            
            using var process = Process.Start(psi);
            process?.WaitForExit(1000);
            
            Console.WriteLine("[Caption] Attempting to enable Live Captions...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Caption] Could not auto-enable captions: {ex.Message}");
        }
    }

    /// <summary>
    /// Try to hide or move caption window off-screen (so it won't show in screen shares)
    /// </summary>
    private void HideCaptionWindow()
    {
        try
        {
            Console.WriteLine("[Caption] Attempting to hide caption window...");
            
            if (_captionWindowHandle == IntPtr.Zero)
            {
                // Try multiple window names
                string[] windowNames = { "Live captions", "Live Captions", "Captions" };
                
                foreach (var name in windowNames)
                {
                    _captionWindowHandle = FindWindow(null, name);
                    if (_captionWindowHandle != IntPtr.Zero)
                    {
                        Console.WriteLine($"[Caption] Found window handle by name: '{name}'");
                        break;
                    }
                }
                
                // Try by class name if name search failed
                if (_captionWindowHandle == IntPtr.Zero)
                {
                    _captionWindowHandle = FindWindow("CatnipOverlaySurface", null);
                    if (_captionWindowHandle != IntPtr.Zero)
                        Console.WriteLine("[Caption] Found window handle by class: CatnipOverlaySurface");
                }
            }

            if (_captionWindowHandle != IntPtr.Zero)
            {
                bool moved = SetWindowPos(
                    _captionWindowHandle, IntPtr.Zero,
                    -32000, -32000, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER
                );

                Console.WriteLine(moved
                    ? "[Caption] ✓ Caption window moved off-screen (hidden from screen share)"
                    : "[Caption] ✗ Failed to move window off-screen");
            }
            else
            {
                Console.WriteLine("[Caption] ✗ Could not find caption window handle");
                Console.WriteLine("[Caption] Window will remain visible (may show in screen shares)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Caption] ✗ Error hiding window: {ex.Message}");
        }
    }

    /// <summary>
    /// Find the Windows Live Captions window using UI Automation
    /// </summary>
    private bool FindCaptionWindow()
    {
        try
        {
            if (_automation == null)
                return false;

            Console.WriteLine("[Caption] Searching for Live Captions window...");

            // Get the desktop root element
            var rootElement = _automation.GetRootElement();
            if (rootElement == null)
            {
                Console.WriteLine("[Caption] Could not get root element");
                return false;
            }

            // Try multiple window names/classes for different Windows versions
            string[] windowNames = { "Live captions", "Live Captions", "Captions" };
            string[] classNames = { "CatnipOverlaySurface", "Windows.UI.Composition.DesktopWindowContentBridge" };

            // Try finding by name
            foreach (var name in windowNames)
            {
                var condition = _automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_NamePropertyId, name);
                _captionWindow = rootElement.FindFirst(TreeScope.TreeScope_Descendants, condition);

                if (_captionWindow != null)
                {
                    Console.WriteLine($"[Caption] ✓ Found caption window by name: '{name}'");
                    return true;
                }
            }

            foreach (var className in classNames)
            {
                var condition = _automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_ClassNamePropertyId, className);
                _captionWindow = rootElement.FindFirst(TreeScope.TreeScope_Descendants, condition);

                if (_captionWindow != null)
                {
                    Console.WriteLine($"[Caption] ✓ Found caption window by class: '{className}'");
                    return true;
                }
            }

            Console.WriteLine("[Caption] ✗ Caption window not found");
            Console.WriteLine("[Caption] Make sure Live Captions is enabled (Win+Ctrl+L)");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Caption] ✗ Error finding window: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Poll the caption window for new lines not seen before
    /// Only runs logic when session is active
    /// </summary>
    private void PollCaptions(object? state)
    {
        if (!_isRunning || (!_isSessionActive && !_isDraining) || _automation == null)
            return;

        try
        {
            if (_captionWindow == null)
            {
                FindCaptionWindow();
                return;
            }

            var currentLines = ReadCurrentLines();
            if (currentLines.Count == 0)
                return;

            var buildingLine = currentLines[currentLines.Count - 1];

            if (_isSessionActive)
            {
                // Normal session: capture completed lines, track building line
                var completedLines = currentLines.Take(currentLines.Count - 1).ToList();
                foreach (var line in completedLines)
                {
                    if (!_baselineLines.Contains(line))
                    {
                        if (_sessionText.Length > 0)
                            _sessionText.Append(" ");

                        _sessionText.Append(line);
                        _baselineLines.Add(line);

                        Console.WriteLine($"[Caption] ✓ COMPLETE: \"{line}\"");
                        Console.WriteLine($"[Caption] Session so far: \"{_sessionText}\"");
                    }
                }
                _currentBuildingLine = buildingLine;
            }

            if (_isDraining)
            {
                // Drain mode: wait for the building line to stop changing
                if (buildingLine == _drainLastLine)
                {
                    _drainStableCount++;
                }
                else
                {
                    _drainStableCount = 0;
                    _drainLastLine = buildingLine;
                    Console.WriteLine($"[Caption] Drain: still changing → \"{buildingLine}\"");
                }

                bool stable = _drainStableCount >= DrainStabilityPolls;
                bool timedOut = (Environment.TickCount64 - _drainStartMs) >= DrainTimeoutMs;

                if (stable || timedOut)
                {
                    if (timedOut) Console.WriteLine("[Caption] Drain: 800ms timeout, finalizing");
                    else Console.WriteLine($"[Caption] Drain: stable ×{_drainStableCount}, finalizing");
                    FinalizeSession();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Caption] Error polling: {ex.Message}");
        }
    }
}
