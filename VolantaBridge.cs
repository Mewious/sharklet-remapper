using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TailwindRemapper;

// Reads the "[Tailwind:Volanta:1]" lines the game prints to the Roblox client log and passes them
// to the Volanta desktop app over its local plugin socket, as Volanta's own sim plugins do.
// Only the aircraft title and model are sent, so the aircraft picked in Volanta is kept.
public sealed class VolantaBridge : IDisposable
{
    private const string Prefix = "[Tailwind:Volanta:1]";
    private const string SimAbbreviation = "fg";
    private const string SimVersion = "Tailwind";
    private const int PollMilliseconds = 250;
    private const int DiscoverMilliseconds = 1000;
    private const int ReconnectMilliseconds = 3000;
    private const int StaleMilliseconds = 10000;
    private const int MaximumLogs = 4;
    private const int ReadBudget = 262144;
    private static readonly string[] PositionFields =
    [
        "latitude", "longitude", "altitudeAmsl", "altitudeAgl", "onGround", "pitch", "bank", "headingTrue",
        "groundSpeed", "verticalSpeed", "gForce", "windHeading", "windSpeed", "fuelKg", "paused",
        "enginesRunning", "parkingBrake", "autopilotEngaged", "fps"
    ];

    private readonly Uri volantaUrl;
    private readonly string logDirectory;
    private readonly Dictionary<string, Cursor> logs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private CancellationTokenSource? cancellation;
    private ClientWebSocket? socket;
    private JsonObject? aircraft;
    private JsonNode? lastPosition;
    private bool receiving;

    public event Action<string>? StatusChanged;
    public bool IsRunning => cancellation != null;
    public bool IsConnected => socket?.State == WebSocketState.Open;
    public bool IsReceiving => receiving;

    public VolantaBridge(string? volantaUrl = null, string? logDirectory = null)
    {
        this.volantaUrl = new Uri(volantaUrl ?? "ws://127.0.0.1:6745/plugin");
        this.logDirectory = logDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
    }

    public void Start()
    {
        if (cancellation != null) return;
        cancellation = new CancellationTokenSource();
        receiving = false;
        aircraft = null;
        lastPosition = null;
        logs.Clear();
        Discover(initial: true);
        _ = ConnectLoop(cancellation.Token);
        _ = ReadLoop(cancellation.Token);
        Report("Waiting for Volanta and a Tailwind flight.");
    }

    public void Stop()
    {
        if (cancellation == null) return;
        cancellation.Cancel();
        cancellation.Dispose();
        cancellation = null;
        socket?.Abort();
        socket?.Dispose();
        socket = null;
        Report("Volanta tracking is off.");
    }

    public void Dispose() => Stop();

    private void Report(string text) => StatusChanged?.Invoke(text);

    private async Task ConnectLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var client = new ClientWebSocket();
            try
            {
                await client.ConnectAsync(volantaUrl, token);
                socket = client;
                Report(receiving ? "Connected to Volanta. Tracking this flight." : "Connected to Volanta. Waiting for a Tailwind flight.");
                await SendAircraft(token);
                await ReceiveLoop(client, token);
                if (!token.IsCancellationRequested) Report("Lost Volanta; retrying.");
            }
            catch (Exception) when (!token.IsCancellationRequested) { }
            catch (Exception) { return; }
            finally
            {
                if (socket == client) socket = null;
                client.Dispose();
            }
            try { await Task.Delay(ReconnectMilliseconds, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoop(ClientWebSocket client, CancellationToken token)
    {
        var buffer = new byte[16384];
        var message = new MemoryStream();
        while (client.State == WebSocketState.Open)
        {
            var result = await client.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            await Answer(text, token);
        }
    }

    // Volanta asks for the aircraft when a flight starts; other requests go unanswered, as they do
    // for its own FlightGear support.
    private async Task Answer(string text, CancellationToken token)
    {
        JsonNode? request;
        try { request = JsonNode.Parse(text); }
        catch (Exception) { return; }
        if (request?["name"]?.GetValue<string>() != "GET_AIRCRAFT_DATA") return;
        await Send(new JsonObject
        {
            ["type"] = "MESSAGE",
            ["name"] = "GET_AIRCRAFT_DATA",
            ["seq_id"] = request["seq_id"]?.DeepClone(),
            ["data"] = aircraft?.DeepClone() ?? new JsonObject()
        }, token);
        await SendAircraft(token);
    }

    private async Task ReadLoop(CancellationToken token)
    {
        var nextDiscovery = Environment.TickCount64 + DiscoverMilliseconds;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (Environment.TickCount64 >= nextDiscovery)
                {
                    Discover(initial: false);
                    nextDiscovery = Environment.TickCount64 + DiscoverMilliseconds;
                }
                foreach (var (file, cursor) in logs)
                    foreach (var line in ReadLog(file, cursor))
                        await Receive(line, token);
                await Task.Delay(PollMilliseconds, token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Report($"Volanta bridge: {ex.Message}"); }
        }
    }

    // Logs already there at start are read from their end, so an old session is never replayed;
    // logs that appear later are read from the start.
    private void Discover(bool initial)
    {
        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(logDirectory).GetFiles("*.log")
                .Where(f => (f.Name.Contains("_Player_") || f.Name.Contains("_Studio_")) && !f.Name.Contains("_Server_"))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaximumLogs)
                .ToArray();
        }
        catch (Exception) { return; }
        var keep = files.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in logs.Keys.Where(f => !keep.Contains(f)).ToList()) logs.Remove(file);
        foreach (var file in files)
            if (!logs.ContainsKey(file.FullName)) logs[file.FullName] = new Cursor { Position = initial ? file.Length : 0 };
    }

    private static List<string> ReadLog(string file, Cursor cursor)
    {
        var found = new List<string>();
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < cursor.Position)
            {
                cursor.Position = 0;
                cursor.Partial = "";
            }
            if (stream.Length == cursor.Position) return found;
            var buffer = new byte[(int)Math.Min(stream.Length - cursor.Position, ReadBudget)];
            stream.Position = cursor.Position;
            stream.ReadExactly(buffer);
            cursor.Position += buffer.Length;
            var lines = (cursor.Partial + Encoding.UTF8.GetString(buffer)).Split('\n');
            cursor.Partial = lines[^1];
            foreach (var line in lines[..^1])
            {
                var start = line.IndexOf(Prefix, StringComparison.Ordinal);
                if (start >= 0) found.Add(line[(start + Prefix.Length)..].Trim());
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return found;
    }

    private async Task Receive(string text, CancellationToken token)
    {
        JsonNode? data;
        try { data = JsonNode.Parse(text); }
        catch (Exception) { return; }
        if (data?["timestamp"] is not JsonValue timestamp || !timestamp.TryGetValue(out double sent)) return;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sent > StaleMilliseconds) return;
        if (!receiving)
        {
            receiving = true;
            Report(IsConnected ? "Connected to Volanta. Tracking this flight." : "Receiving Tailwind flight data. Waiting for Volanta.");
        }
        var kind = data["kind"]?.GetValue<string>();
        if (kind == "position") await SendPosition(data, token);
        else if (kind == "landing") await SendLanding(data, token);
    }

    private async Task SendAircraft(CancellationToken token)
    {
        if (aircraft != null) await Send(new JsonObject { ["type"] = "STREAM", ["name"] = "AIRCRAFT_UPDATE", ["data"] = aircraft.DeepClone() }, token);
    }

    private async Task SendPosition(JsonNode data, CancellationToken token)
    {
        var title = data["title"]?.ToString() ?? "";
        var model = data["model"]?.ToString() ?? "";
        lastPosition = data;
        if (aircraft == null || aircraft["title"]?.ToString() != title || aircraft["model"]?.ToString() != model)
        {
            aircraft = new JsonObject { ["title"] = title, ["model"] = model };
            await SendAircraft(token);
        }
        var position = new JsonObject();
        foreach (var field in PositionFields) position[field] = data[field]?.DeepClone();
        position["gravity"] = data["gForce"]?.DeepClone();
        position["transponder"] = null;
        position["slew"] = false;
        position["inReplayMode"] = false;
        position["timeAcceleration"] = 1;
        position["simAbbreviation"] = SimAbbreviation;
        position["simVersion"] = SimVersion;
        position["simZuluTime"] = DateTime.UtcNow.ToString("o");
        await Send(new JsonObject { ["type"] = "STREAM", ["name"] = "POSITION_UPDATE", ["data"] = position }, token);
    }

    private async Task SendLanding(JsonNode data, CancellationToken token)
    {
        Report($"Landing: {data["landingRate"]} fpm, {data["gForce"]} g.");
        var landing = new JsonObject();
        foreach (var field in new[] { "landingRate", "gForce", "pitch", "roll", "groundSpeed", "latitude", "longitude", "heading", "altitudeAmsl" })
            landing[field] = data[field]?.DeepClone();
        landing["windHeading"] = lastPosition?["windHeading"]?.DeepClone() ?? 0;
        landing["windSpeed"] = lastPosition?["windSpeed"]?.DeepClone() ?? 0;
        landing["altitudeAgl"] = 0;
        landing["isHidden"] = false;
        landing["id"] = "";
        landing["time"] = DateTime.UtcNow.ToString("o");
        await Send(new JsonObject { ["type"] = "STREAM", ["name"] = "EVENT_LANDING", ["data"] = landing }, token);
    }

    private async Task Send(JsonObject message, CancellationToken token)
    {
        var client = socket;
        if (client?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await sendLock.WaitAsync(token);
        try { await client.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
        catch (WebSocketException) { }
        finally { sendLock.Release(); }
    }

    private sealed class Cursor
    {
        public long Position;
        public string Partial = "";
    }
}
