using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;

namespace MeetingAssistant;

/// <summary>
/// System tray application that orchestrates all services
/// </summary>
public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _contextMenu;
    
    private readonly WebSocketServer _webSocketServer;
    private readonly GeminiApiService _geminiService;
    private readonly WindowsCaptionService _captionService;

    private ToolStripMenuItem? _startStopMenuItem;
    private ToolStripMenuItem? _statusMenuItem;
    private ToolStripMenuItem? _ipAddressMenuItem;
    private ToolStripMenuItem? _apiStatusMenuItem;
    private bool _isListening;

    // Custom icons
    private Icon? _iconDefault;
    private Icon? _iconConnected;
    private Icon? _iconDisconnected;
    private Icon? _iconProcessing;

    // Hotkey support
    private HotkeyWindow? _hotkeyWindow;
    private const int HOTKEY_ID_START = 1;
    private const int HOTKEY_ID_STOP = 2;

    public TrayApplicationContext()
    {
        // Load configuration
        var config = LoadConfiguration();

        // Initialize services
        _webSocketServer = new WebSocketServer(config.WebSocketPort);
        _geminiService = new GeminiApiService(
            config.ApiKey,
            config.ApiUrl,
            config.Model,
            config.SystemPrompt
        );
        _captionService = new WindowsCaptionService(pollingIntervalMs: config.CaptionPollingMs);

        // Load custom icons
        LoadCustomIcons();

        // Wire up events
        SetupEventHandlers();

        // Create system tray icon
        _contextMenu = CreateContextMenu();
        _trayIcon = new NotifyIcon
        {
            Icon = _iconDisconnected ?? SystemIcons.Application,
            ContextMenuStrip = _contextMenu,
            Visible = true,
            Text = "Meeting Assistant - Stopped"
        };

        _trayIcon.DoubleClick += (s, e) => ToggleListening();

        // Setup global hotkeys
        SetupHotkeys();

        // Auto-start WebSocket server
        StartWebSocketServer();

        // Auto-start caption service in background (always-on mode)
        StartCaptionService();

        // Test API connection on startup (fire and forget)
        _ = TestApiConnection();

        ShowNotification("Meeting Assistant Started", "Caption service running. F9=Start Session, F10=Stop & Send", ToolTipIcon.Info);
    }

    private void SetupEventHandlers()
    {
        // WebSocket events
        _webSocketServer.Started += (s, e) => UpdateStatus("WebSocket server started");
        _webSocketServer.ClientConnected += (s, clientId) => 
        {
            UpdateStatus($"Android device connected ({_webSocketServer.ConnectedClientsCount} total)");
            ShowNotification("Device Connected", $"Android device joined", ToolTipIcon.Info);
        };
        _webSocketServer.ClientDisconnected += (s, clientId) => 
        {
            UpdateStatus($"Android device disconnected ({_webSocketServer.ConnectedClientsCount} remaining)");
        };

        // Speech recognition events
        // Windows Caption events (just for errors, accumulation is automatic)
        _captionService.ErrorOccurred += (s, error) =>
        {
            Console.WriteLine($"[App] Caption service error: {error}");
            ShowNotification("Caption Service Error", error, ToolTipIcon.Error);
        };

        // Gemini API events
        _geminiService.TokenReceived += (s, token) =>
        {
            // Stream tokens to Android in real-time
            _webSocketServer.Broadcast(token);
        };

        _geminiService.ResponseCompleted += (s, response) =>
        {
            Console.WriteLine($"[App] Response completed");
            UpdateApiStatus(true, "Connected");
            UpdateIcon(_isListening ? AppState.Listening : AppState.Stopped);
            
            // No separator needed - Android clears screen on "🎤 Captured:" message
        };

        _geminiService.ErrorOccurred += (s, error) =>
        {
            Console.WriteLine($"[App] AI error: {error}");
            UpdateApiStatus(false, $"Error: {error}");
            ShowNotification("AI Error", error, ToolTipIcon.Error);
            UpdateIcon(_isListening ? AppState.Listening : AppState.Stopped);
        };
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();

        // Status
        _statusMenuItem = new ToolStripMenuItem("Status: Stopped")
        {
            Enabled = false
        };
        menu.Items.Add(_statusMenuItem);
        
        _apiStatusMenuItem = new ToolStripMenuItem("🤖 AI Service: Testing...")
        {
            Enabled = false
        };
        menu.Items.Add(_apiStatusMenuItem);
        
        menu.Items.Add(new ToolStripSeparator());

        // Start/Stop with hotkey hints
        _startStopMenuItem = new ToolStripMenuItem("Start Listening (F9)", null, (s, e) => ToggleListening());
        menu.Items.Add(_startStopMenuItem);

        var stopMenuItem = new ToolStripMenuItem("Stop Listening (F10)", null, (s, e) => StopListening());
        menu.Items.Add(stopMenuItem);

        menu.Items.Add(new ToolStripSeparator());

        // IP Address - Prominent display
        var ipAddress = WebSocketServer.GetLocalIPAddress();
        _ipAddressMenuItem = new ToolStripMenuItem($"📡 IP: {ipAddress}")
        {
            Enabled = false,
            Font = new Font(menu.Font, FontStyle.Bold)
        };
        menu.Items.Add(_ipAddressMenuItem);

        var connectionInfo = new ToolStripMenuItem($"Server: ws://{ipAddress}:8080/display")
        {
            Enabled = false
        };
        menu.Items.Add(connectionInfo);

        var copyIpItem = new ToolStripMenuItem("Copy IP Address", null, (s, e) =>
        {
            Clipboard.SetText(ipAddress);
            ShowNotification("Copied", $"IP address copied: {ipAddress}", ToolTipIcon.Info);
        });
        menu.Items.Add(copyIpItem);

        menu.Items.Add(new ToolStripSeparator());

        // Connected Devices
        var devicesItem = new ToolStripMenuItem("📱 View Connected Devices", null, (s, e) => ShowConnectedDevices());
        menu.Items.Add(devicesItem);

        menu.Items.Add(new ToolStripSeparator());

        // Test buttons
        var testApiItem = new ToolStripMenuItem("Test AI Connection", null, async (s, e) =>
        {
            await TestApiConnection();
        });
        menu.Items.Add(testApiItem);

        var testItem = new ToolStripMenuItem("Send Test Message", null, async (s, e) =>
        {
            _webSocketServer.Broadcast("🧪 Test message from Meeting Assistant");
            await Task.Delay(500);
            _webSocketServer.Broadcast("This is a test of the real-time streaming system.");
        });
        menu.Items.Add(testItem);

        menu.Items.Add(new ToolStripSeparator());

        // Exit
        var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => Exit());
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ToggleListening()
    {
        if (_isListening)
        {
            StopListening();
        }
        else
        {
            StartListening();
        }
    }

    private void StartListening()
    {
        Console.WriteLine($"[App] StartListening called. Caption service running: {_captionService.IsRunning}");
        
        if (!_captionService.IsRunning)
        {
            Console.WriteLine("[App] Caption service NOT running - attempting to start...");
            var started = _captionService.Start();
            if (!started)
            {
                ShowNotification("Caption Service Error", "Failed to start Live Captions. Enable it manually (Win+Ctrl+L) and restart app.", ToolTipIcon.Error);
                return;
            }
            System.Threading.Thread.Sleep(100); // Give it a moment
        }

        // Start a new capture session
        Console.WriteLine("[App] Starting capture session...");
        _captionService.StartSession();
        _isListening = true;

        UpdateIcon(AppState.Listening);
        UpdateStatus("Listening to captions...");
        
        if (_startStopMenuItem != null)
            _startStopMenuItem.Text = "Stop & Send to AI (F10)";
        
        Console.WriteLine("[App] Session started successfully");
        ShowNotification("Session Started", "Capturing captions. Speak now. Press F10 to stop and send to AI.", ToolTipIcon.Info);
    }

    private void StopListening()
    {
        if (!_isListening)
            return;

        _isListening = false;

        UpdateIcon(AppState.Processing);
        UpdateStatus("Waiting for final caption...");

        if (_startStopMenuItem != null)
            _startStopMenuItem.Text = "Start Listening (F9)";

        // Enter drain state — waits for caption to stabilize, then calls back
        _captionService.BeginEndSession(async capturedText =>
        {
            UpdateStatus("Processing captured text...");

            if (string.IsNullOrWhiteSpace(capturedText))
            {
                ShowNotification("No Speech", "No audio was captured in this session.", ToolTipIcon.Warning);
                UpdateIcon(AppState.Stopped);
                UpdateStatus("Ready");
                return;
            }

            Console.WriteLine($"[App] Captured text ({capturedText.Length} chars): {capturedText.Substring(0, Math.Min(100, capturedText.Length))}...");

            // Broadcast the captured text with ASCII marker (emoji-safe)
            _webSocketServer.Broadcast($"[QUESTION]: {capturedText}\n\n");

            try
            {
                // Send to AI for response
                await _geminiService.SendQuestionAsync(capturedText);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[App] Error sending to AI: {ex.Message}");
                ShowNotification("AI Error", $"Failed to process: {ex.Message}", ToolTipIcon.Error);
                UpdateIcon(AppState.Stopped);
                UpdateStatus("Ready");
            }
        });
    }

    private void StartWebSocketServer()
    {
        try
        {
            _webSocketServer.Start();
            var ipAddress = WebSocketServer.GetLocalIPAddress();
            Console.WriteLine($"[App] Configure Android app to connect to: ws://{ipAddress}:8080/display");
        }
        catch (Exception ex)
        {
            ShowNotification("Server Error", $"Failed to start WebSocket server: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void StartCaptionService()
    {
        try
        {
            Console.WriteLine("[App] Attempting to start caption service...");
            var started = _captionService.Start();
            if (started)
            {
                Console.WriteLine("[App] ✓ Caption service started successfully and running in background");
                Console.WriteLine("[App] Ready to capture. Press F9 to start a session.");
                UpdateStatus("Ready - Press F9 to start");
            }
            else
            {
                Console.WriteLine("[App] ✗ Caption service failed to start");
                Console.WriteLine("[App] Please enable Live Captions manually: Win+Ctrl+L");
                ShowNotification("Caption Service", "Failed to start. Enable Live Captions (Win+Ctrl+L) and restart.", ToolTipIcon.Warning);
                UpdateStatus("Caption service failed");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[App] ✗ Caption service exception: {ex.Message}");
            Console.WriteLine($"[App] Stack trace: {ex.StackTrace}");
            ShowNotification("Caption Error", $"Failed to start caption service: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void UpdateIcon(AppState state)
    {
        _trayIcon.Icon = state switch
        {
            AppState.Stopped => _iconDisconnected ?? SystemIcons.Shield,
            AppState.Listening => _iconConnected ?? SystemIcons.Information,
            AppState.Processing => _iconProcessing ?? SystemIcons.Warning,
            _ => _iconDefault ?? SystemIcons.Application
        };

        _trayIcon.Text = $"Meeting Assistant - {state}";
    }

    private void UpdateStatus(string status)
    {
        if (_statusMenuItem != null)
        {
            _statusMenuItem.Text = $"Status: {status}";
        }
        Console.WriteLine($"[App] {status}");
    }

    private void UpdateApiStatus(bool isConnected, string message)
    {
        if (_apiStatusMenuItem != null)
        {
            var icon = isConnected ? "✅" : "❌";
            _apiStatusMenuItem.Text = $"{icon} AI Service: {message}";
        }
    }

    private async Task TestApiConnection()
    {
        UpdateApiStatus(false, "Testing...");
        Console.WriteLine("[App] Testing AI API connection...");
        
        try
        {
            var result = await _geminiService.TestConnectionAsync();
            
            if (result)
            {
                UpdateApiStatus(true, "Connected");
                Console.WriteLine("[App] AI API connection successful");
            }
            else
            {
                UpdateApiStatus(false, "Failed - Check API Key");
                Console.WriteLine("[App] AI API connection failed");
            }
        }
        catch (Exception ex)
        {
            UpdateApiStatus(false, $"Error: {ex.Message}");
            Console.WriteLine($"[App] AI API test error: {ex.Message}");
        }
    }

    private void ShowConnectedDevices()
    {
        var devices = _webSocketServer.GetConnectedDevices();
        
        if (devices.Count == 0)
        {
            MessageBox.Show(
                "No Android devices are currently connected.\n\n" +
                "To connect:\n" +
                "1. Open InterX app on your Android phone\n" +
                "2. Go to Settings\n" +
                "3. Enter server URL: ws://" + WebSocketServer.GetLocalIPAddress() + ":8080/display\n" +
                "4. Tap 'Save & Connect'",
                "Connected Devices",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }

        var message = $"📱 Connected Android Devices ({devices.Count}):\n\n";
        
        foreach (var device in devices)
        {
            var duration = $"{device.Duration.Hours:D2}:{device.Duration.Minutes:D2}:{device.Duration.Seconds:D2}";
            message += $"📱 {device.IPAddress}\n";
            message += $"   Connected: {device.ConnectedAt:HH:mm:ss}\n";
            message += $"   Duration: {duration}\n";
            message += $"   ID: {device.Id.Substring(0, Math.Min(8, device.Id.Length))}...\n\n";
        }

        MessageBox.Show(
            message,
            "Connected Devices",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information
        );
    }

    private void ShowNotification(string title, string message, ToolTipIcon icon)
    {
        _trayIcon.ShowBalloonTip(3000, title, message, icon);
    }

    private void Exit()
    {
        _captionService.Stop();
        _webSocketServer.Stop();
        _trayIcon.Visible = false;
        Application.Exit();
    }

    private Configuration LoadConfiguration()
    {
        try
        {
            var jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var json = File.ReadAllText(jsonPath);
            var config = JObject.Parse(json);

            return new Configuration
            {
                ApiKey = config["ApiKey"]?.ToString() ?? "",
                ApiUrl = config["ApiUrl"]?.ToString() ?? "https://api.cerebras.ai/v1/chat/completions",
                Model = config["Model"]?.ToString() ?? "llama3.1-8b",
                Provider = config["Provider"]?.ToString() ?? "cerebras",
                WebSocketPort = config["WebSocketPort"]?.Value<int>() ?? 8080,
                CaptionPollingMs = config["CaptionPollingMs"]?.Value<int>() ?? 100,
                SystemPrompt = config["SystemPrompt"]?.ToString() ?? "You are a helpful assistant."
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Config] Error loading configuration: {ex.Message}");
            throw;
        }
    }

    private void LoadCustomIcons()
    {
        try
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var resourcePath = Path.Combine(basePath, "Resources");

            // Try to load embedded resources first, then from Resources folder
            _iconDefault = TryLoadIcon("icon.ico", resourcePath);
            _iconConnected = TryLoadIcon("icon_connected.ico", resourcePath);
            _iconDisconnected = TryLoadIcon("icon_disconnected.ico", resourcePath);
            _iconProcessing = TryLoadIcon("icon_processing.ico", resourcePath);

            Console.WriteLine("[App] Custom icons loaded successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[App] Failed to load custom icons: {ex.Message}");
        }
    }

    private Icon? TryLoadIcon(string filename, string resourcePath)
    {
        try
        {
            // Try from Resources folder
            var iconPath = Path.Combine(resourcePath, filename);
            if (File.Exists(iconPath))
            {
                return new Icon(iconPath);
            }

            // Try from executable directory
            iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
            if (File.Exists(iconPath))
            {
                return new Icon(iconPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[App] Could not load icon {filename}: {ex.Message}");
        }
        return null;
    }

    private void SetupHotkeys()
    {
        try
        {
            _hotkeyWindow = new HotkeyWindow();
            _hotkeyWindow.HotkeyPressed += OnHotkeyPressed;

            // Register F9 for Start (0x78 = F9 key)
            _hotkeyWindow.RegisterHotkey(HOTKEY_ID_START, 0, 0x78);
            
            // Register F10 for Stop (0x79 = F10 key)
            _hotkeyWindow.RegisterHotkey(HOTKEY_ID_STOP, 0, 0x79);

            Console.WriteLine("[App] Global hotkeys registered: F9=Start, F10=Stop");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[App] Failed to register hotkeys: {ex.Message}");
        }
    }

    private void OnHotkeyPressed(int hotkeyId)
    {
        if (hotkeyId == HOTKEY_ID_START)
        {
            if (!_isListening)
            {
                StartListening();
            }
        }
        else if (hotkeyId == HOTKEY_ID_STOP)
        {
            if (_isListening)
            {
                StopListening();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hotkeyWindow?.UnregisterAllHotkeys();
            _hotkeyWindow?.DestroyHandle();
            _hotkeyWindow?.Dispose();
            
            _trayIcon?.Dispose();
            _contextMenu?.Dispose();
            
            _iconDefault?.Dispose();
            _iconConnected?.Dispose();
            _iconDisconnected?.Dispose();
            _iconProcessing?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Application configuration
/// </summary>
public class Configuration
{
    public string ApiKey { get; set; } = "";
    public string ApiUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string Provider { get; set; } = "";
    public int WebSocketPort { get; set; }
    public int CaptionPollingMs { get; set; }
    public string SystemPrompt { get; set; } = "";
}

/// <summary>
/// Application state for icon management
/// </summary>
public enum AppState
{
    Stopped,
    Listening,
    Processing
}

/// <summary>
/// Native window for handling global hotkeys
/// </summary>
public class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private readonly Dictionary<int, int> _registeredHotkeys = new();

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event Action<int>? HotkeyPressed;

    public HotkeyWindow()
    {
        // Create a simple message-only window
        CreateHandle(new CreateParams
        {
            Caption = "HotkeyWindow",
            Parent = new IntPtr(-3) // HWND_MESSAGE
        });
    }

    public void RegisterHotkey(int id, uint modifiers, uint key)
    {
        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Window handle not created.");
        }

        if (RegisterHotKey(Handle, id, modifiers, key))
        {
            _registeredHotkeys[id] = id;
            Console.WriteLine($"[Hotkey] Registered hotkey ID {id} (Key: 0x{key:X})");
        }
        else
        {
            Console.WriteLine($"[Hotkey] Failed to register hotkey ID {id}");
        }
    }

    public void UnregisterAllHotkeys()
    {
        foreach (var id in _registeredHotkeys.Keys.ToList())
        {
            UnregisterHotKey(Handle, id);
            _registeredHotkeys.Remove(id);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int hotkeyId = m.WParam.ToInt32();
            HotkeyPressed?.Invoke(hotkeyId);
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAllHotkeys();
        GC.SuppressFinalize(this);
    }
}
