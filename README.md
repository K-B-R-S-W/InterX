# Meeting Assistant - Windows Application

Invisible AI meeting assistant that captures speech, sends to Cerebras AI (Llama 3.1 8B), and displays responses on your Android device in real-time.

## 🚀 Features

- **Invisible Speech Recognition** - Captures meeting audio in the background (no UI)
- **AI-Powered Responses** - Uses Cerebras Llama 3.1 8B for ultra-fast, accurate answers
- **Real-Time Streaming** - Words appear on your Android device as they're generated
- **System Tray Only** - Completely invisible during meetings
- **Local Network** - Ultra-low latency (2-8ms) via WiFi
- **Simple Setup** - Just run and connect your Android device

## 📋 Requirements

- **Windows 11** (Windows 10 may work with limitations)
- **.NET 8.0 Runtime** (or SDK for development)
- **Cerebras API Key** ([Get one free](https://cloud.cerebras.ai/))
- **Android device** on the same WiFi network
- **Microphone access** for speech recognition

## 🔧 Setup

### 1. Get Cerebras API Key

1. Go to [Cerebras Cloud](https://cloud.cerebras.ai/)
2. Sign up for a free account
3. Create a new API key
4. Copy the key

### 2. Configure Application

Edit `appsettings.json`:

```json
{
  "ApiKey": "YOUR_API_KEY_HERE",
  ...
}
```

### 3. Build the Application

```powershell
# Using Visual Studio: Just press F5
# Or using command line:
dotnet build -c Release
```

### 4. Run the Application

```powershell
cd bin\Release\net8.0-windows
.\MeetingAssistant.exe
```

### 5. Configure Windows Firewall

Allow incoming connections on port 8080:

```powershell
# Run as Administrator
netsh advfirewall firewall add rule name="Meeting Assistant" dir=in action=allow protocol=TCP localport=8080
```

### 6. Find Your IP Address

The application will display your IP address in the system tray menu:
- Right-click the tray icon
- Look for "Server: ws://192.168.x.x:8080/display"
- Use this address in your Android app

## 🎮 Usage

### Starting

1. **Launch Application** - Double-click MeetingAssistant.exe
2. **System Tray Icon** appears (bottom-right, near clock)
3. **Right-click icon** → "Start Listening"
4. **Connect Android device** to the displayed IP address

### System Tray Menu

- **Start/Stop Listening** - Toggle speech recognition
- **Connection Info** - View server address
- **Copy IP Address** - Copy IP to clipboard
- **Send Test Message** - Test the connection
- **Exit** - Close application

### Status Icons

- 🛡️ **Shield** - Stopped (not listening)
- ℹ️ **Info** - Listening (active)
- ⚠️ **Warning** - Processing (sending to AI)

## 📁 File Structure

```
MeetingAssistant/
├── Program.cs                      # Entry point
├── TrayApplicationContext.cs       # Main orchestrator & UI
├── SpeechRecognitionService.cs     # Speech capture
├── GeminiApiService.cs             # AI integration
├── WebSocketServer.cs              # Network server
├── DisplayWebSocketBehavior.cs     # Connection handler
├── appsettings.json                # Configuration
└── MeetingAssistant.csproj         # Project file
```

## ⚙️ Configuration Options

### appsettings.json

| Setting | Description | Default |
|---------|-------------|---------|
| `ApiKey` | Your Cerebras API key | Required |
| `ApiUrl` | Cerebras API endpoint | Auto-configured |
| `Model` | AI model to use | llama3.1-8b |
| `Provider` | AI provider name | cerebras |
| `WebSocketPort` | Server port | 8080 |
| `SpeechLanguage` | Recognition language | en-US |
| `MinSpeechConfidence` | Confidence threshold (0-1) | 0.6 |
| `DebounceMs` | Delay between recognitions | 1500ms |
| `SystemPrompt` | AI instruction prompt | Default |

### Adjusting Settings

**Lower latency (may reduce accuracy):**
```json
{
  "MinSpeechConfidence": 0.5,
  "DebounceMs": 1000
}
```

**Higher accuracy (may increase latency):**
```json
{
  "MinSpeechConfidence": 0.8,
  "DebounceMs": 2000
}
```

## 🐛 Troubleshooting

### Speech Recognition Not Working

1. **Check microphone access:**
   - Settings → Privacy → Microphone
   - Enable for desktop apps

2. **Check installed languages:**
   - Settings → Time & Language → Speech
   - Install language pack if needed

3. **Test speech recognition:**
   - Open Windows Speech Recognition
   - Verify it works there first

### Android Can't Connect

1. **Check firewall:**
   ```powershell
   netsh advfirewall firewall show rule name="Meeting Assistant"
   ```

2. **Verify same WiFi network:**
   - PC and phone must be on same network
   - Corporate networks may block device-to-device communication

3. **Test connectivity:**
   ```powershell
   # On PC, check if port is listening:
   netstat -an | findstr :8080
   ```

### API Errors

1. **Check API key** in appsettings.json
2. **Verify internet connection**
3. **Check API quota** at [Cerebras Cloud](https://cloud.cerebras.ai/)

## 🔒 Privacy & Security

- **Local Processing**: Speech recognition runs on your PC
- **API Calls**: Only recognized text sent to Gemini (not audio)
- **No Recording**: Audio is not saved or stored
- **Local Network**: Communication stays on your WiFi (not internet)
- **No Logs**: No conversation history is saved

## 📊 Performance

- **Latency Breakdown:**
  - Speech Recognition: 500-800ms
  - Cerebras API: 300-800ms (faster than Gemini!)
  - Network (WiFi): 2-8ms
  - Android Display: 100-200ms
  - **Total: ~0.9-1.8 seconds** ⚡⚡

## 🛠️ Development

### Building from Source

```powershell
# Clone/download the project
cd MeetingAssistant

# Restore dependencies
dotnet restore

# Build
dotnet build -c Release

# Run
dotnet run -c Release
```

### Dependencies

- **.NET 8.0** (Windows desktop)
- **WebSocketSharp-netstandard** 1.0.1
- **Newtonsoft.Json** 13.0.3
- **System.Speech** (Windows built-in)

## 📝 License

This is a personal project. Use at your own discretion.

## ⚠️ Legal Notice

Recording conversations without consent may be illegal in your jurisdiction. Ensure compliance with:
- Local recording laws
- Company policies
- Meeting platform terms of service
- Participant consent requirements

**Use responsibly and ethically.**

**Need help?** Check the console output when running for detailed logs.

