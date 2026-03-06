using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MeetingAssistant;

/// <summary>
/// WebSocket server using System.Net.WebSockets over TcpListener.
/// Replaces WebSocketSharp which was silently dropping all outbound frames.
/// </summary>
public class WebSocketServer
{
    private TcpListener? _listener;
    private readonly int _port;
    private bool _isRunning;
    private CancellationTokenSource? _cts;

    private readonly ConcurrentDictionary<string, ConnectedClient> _clients = new();

    public event EventHandler? Started;
    public event EventHandler? Stopped;
    public event EventHandler<string>? ClientConnected;
    public event EventHandler<string>? ClientDisconnected;

    public bool IsRunning => _isRunning;
    public int ConnectedClientsCount => _clients.Count;

    public WebSocketServer(int port = 8080)
    {
        _port = port;
    }

    public void Start()
    {
        if (_isRunning) return;

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _isRunning = true;

            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

            var localIP = GetLocalIPAddress();
            Console.WriteLine($"[WebSocketServer] Started on port {_port}");
            Console.WriteLine($"[WebSocketServer] Listening on ALL network interfaces (0.0.0.0:{_port})");
            Console.WriteLine($"[WebSocketServer] Local endpoint: ws://{localIP}:{_port}/display");
            Console.WriteLine($"[WebSocketServer] Also accessible via: ws://localhost:{_port}/display");
            Started?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebSocketServer] Failed to start: {ex.Message}");
            throw;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(tcpClient, ct), CancellationToken.None);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Console.WriteLine($"[WebSocketServer] Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken ct)
    {
        string clientId = Guid.NewGuid().ToString("N");
        string clientIP = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "Unknown";
        System.Net.WebSockets.WebSocket? ws = null;

        try
        {
            tcpClient.NoDelay = true;
            var stream = tcpClient.GetStream();

            // Read HTTP upgrade request
            var requestBuilder = new StringBuilder();
            var buf = new byte[4096];
            while (!requestBuilder.ToString().Contains("\r\n\r\n"))
            {
                int n = await stream.ReadAsync(buf, 0, buf.Length, ct);
                if (n == 0) return;
                requestBuilder.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            var request = requestBuilder.ToString();

            if (!request.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n"), ct);
                return;
            }

            var keyMatch = Regex.Match(request, @"Sec-WebSocket-Key:\s*(.+)\r\n", RegexOptions.IgnoreCase);
            if (!keyMatch.Success) return;

            var acceptKey = Convert.ToBase64String(
                SHA1.HashData(Encoding.UTF8.GetBytes(keyMatch.Groups[1].Value.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

            var upgradeResponse =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(upgradeResponse), ct);

            ws = System.Net.WebSockets.WebSocket.CreateFromStream(
                stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);

            var client = new ConnectedClient(clientId, clientIP, ws);
            _clients[clientId] = client;

            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine("           ✅ CLIENT CONNECTED");
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine($"  Session ID: {clientId}");
            Console.WriteLine($"  Client IP:  {clientIP}");
            Console.WriteLine($"  Time:       {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            Console.WriteLine($"  Active:     {_clients.Count} client(s)");
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine();

            ClientConnected?.Invoke(this, clientId);

            // Confirm connection
            await SendToClientAsync(client, "🔔 Connection established! Ready to receive AI responses.", CancellationToken.None);
            Console.WriteLine($"[DEBUG] ✅ Connection confirmation sent to {clientIP}");

            // Read loop – keeps connection alive and detects disconnects
            var readBuf = new byte[4096];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(readBuf), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }
                }
                catch { break; }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebSocket] {clientId[..8]} error: {ex.GetType().Name} - {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            try { ws?.Dispose(); } catch { }
            tcpClient.Close();

            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine("           ❌ CLIENT DISCONNECTED");
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine($"  Session ID: {clientId}");
            Console.WriteLine($"  Client IP:  {clientIP}");
            Console.WriteLine($"  Remaining:  {_clients.Count} client(s)");
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine();

            ClientDisconnected?.Invoke(this, clientId);
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;

        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _isRunning = false;
            Console.WriteLine("[WebSocketServer] Stopped");
            Stopped?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebSocketServer] Error stopping: {ex.Message}");
        }
    }

    public void Broadcast(string message)
    {
        Console.WriteLine($"[WebSocketServer] ====== BROADCAST ======");
        Console.WriteLine($"[WebSocketServer] Active Clients: {_clients.Count}");
        Console.WriteLine($"[WebSocketServer] Message: {message[..Math.Min(100, message.Length)]}");

        if (_clients.Count == 0)
        {
            Console.WriteLine("[WebSocketServer] ⚠️ No connected clients");
            Console.WriteLine($"[WebSocketServer] ========================");
            return;
        }

        int successCount = 0, failedCount = 0;
        var dead = new List<string>();

        foreach (var (id, client) in _clients)
        {
            Console.WriteLine($"[WebSocketServer] 📤 Sending to {id[..8]}... State: {client.WebSocket.State}");
            if (client.WebSocket.State != WebSocketState.Open)
            {
                dead.Add(id);
                failedCount++;
                continue;
            }
            // Fire-and-forget: do not block the caller (SSE stream reader) on the WS write.
            // SemaphoreSlim inside SendToClientAsync guarantees per-client ordering.
            _ = SendToClientAsync(client, message, CancellationToken.None);
            successCount++;
            Console.WriteLine($"  ✅ Queued ({message.Length} chars) to {client.ClientIP}");
        }

        foreach (var id in dead) _clients.TryRemove(id, out _);

        Console.WriteLine($"[WebSocketServer] ✅ SUCCESS: {successCount} | ❌ FAILED: {failedCount}");
        Console.WriteLine($"[WebSocketServer] ========================");
    }

    private static async Task SendToClientAsync(ConnectedClient client, string message, CancellationToken ct)
    {
        if (client.WebSocket.State != WebSocketState.Open) return;

        await client.SendLock.WaitAsync(ct);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await client.WebSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken: ct);
        }
        finally { client.SendLock.Release(); }
    }

    public List<ConnectedDeviceInfo> GetConnectedDevices()
    {
        return _clients.Values.Select(c => new ConnectedDeviceInfo
        {
            Id = c.Id,
            IPAddress = c.ClientIP,
            ConnectedAt = c.ConnectedAt,
            Duration = DateTime.Now - c.ConnectedAt,
            DeviceName = "Android Device"
        }).ToList();
    }

    public static string GetLocalIPAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }
}

internal class ConnectedClient
{
    public string Id { get; }
    public string ClientIP { get; }
    public System.Net.WebSockets.WebSocket WebSocket { get; }
    public DateTime ConnectedAt { get; }
    public SemaphoreSlim SendLock { get; } = new SemaphoreSlim(1, 1);

    public ConnectedClient(string id, string clientIP, System.Net.WebSockets.WebSocket webSocket)
    {
        Id = id;
        ClientIP = clientIP;
        WebSocket = webSocket;
        ConnectedAt = DateTime.Now;
    }
}

public class ConnectedDeviceInfo
{
    public string Id { get; set; } = "";
    public string IPAddress { get; set; } = "";
    public DateTime ConnectedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public string DeviceName { get; set; } = "";
}