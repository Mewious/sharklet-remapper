using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TailwindRemapper;

public sealed record McduRun
{
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("column")] public JsonElement Column { get; init; }
    [JsonPropertyName("color")] public string Color { get; init; } = "White";
    [JsonPropertyName("small")] public bool Small { get; init; }
}

public sealed record McduFrame
{
    [JsonPropertyName("session")] public string Session { get; init; } = "";
    [JsonPropertyName("sequence")] public long Sequence { get; init; }
    [JsonPropertyName("side")] public int Side { get; init; }
    [JsonPropertyName("brightness")] public double Brightness { get; init; }
    [JsonPropertyName("annunciators")] public int Annunciators { get; init; }
    [JsonPropertyName("annunciatorBrightness")] public int AnnunciatorBrightness { get; init; }
    [JsonPropertyName("rows")] public McduRun[][] Rows { get; init; } = [];
    [JsonPropertyName("timestamp")] public long Timestamp { get; init; }
}

// No network or Roblox process access: only tagged screen snapshots from local client logs.
public sealed class McduLogReader : IDisposable
{
    public const string Prefix = "[Tailwind:MCDU:1]";
    public const string ChunkPrefix = "[Tailwind:MCDU:2]";
    public const int MaximumLineLength = 65536;
    public const int StaleMilliseconds = 10000;
    private const int ReadBudget = 262144;
    private const int MaximumLogs = 8;
    private readonly string directory;
    private readonly int side;
    private readonly Dictionary<string, Cursor> logs = new(StringComparer.OrdinalIgnoreCase);
    private long nextDiscovery;
    private long lastTimestamp;
    private string? lastSession;
    private long lastSequence;
    private bool disposed;
    public string? CurrentLogPath { get; private set; }

    public McduLogReader(int side, string? directory = null)
    {
        if (side is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(side));
        this.side = side;
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
        Discover(initial: true);
    }

    public McduFrame? Poll() => Poll(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    internal McduFrame? Poll(long now)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (now >= nextDiscovery)
        {
            Discover(initial: false);
            nextDiscovery = now + 1000;
        }

        McduFrame? latest = null;
        string? latestPath = null;
        foreach (var (path, cursor) in logs)
        {
            try
            {
                cursor.Read(line =>
                {
                    var complete = cursor.Chunks.Accept(line, now);
                    if (complete == null) return;
                    if (!TryParse(complete, now, out var frame) || frame!.Side != side) return;
                    if (frame.Timestamp < lastTimestamp) return;
                    if (frame.Session == lastSession && frame.Sequence <= lastSequence) return;
                    if (latest != null && (frame.Timestamp < latest.Timestamp
                        || frame.Session == latest.Session && frame.Sequence <= latest.Sequence)) return;
                    latest = frame;
                    latestPath = path;
                });
            }
            catch (IOException) { } // Roblox may rotate or briefly lock a log while it is being read.
            catch (UnauthorizedAccessException) { }
        }

        if (latest != null)
        {
            lastTimestamp = latest.Timestamp;
            lastSession = latest.Session;
            lastSequence = latest.Sequence;
            CurrentLogPath = latestPath;
        }
        return latest;
    }

    private void Discover(bool initial)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var candidates = new DirectoryInfo(directory).EnumerateFiles("*.log")
                .Where(file => IsClientLog(file.Name))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaximumLogs).ToList();
            var paths = candidates.Select(file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in logs.Keys.Where(path => !paths.Contains(path)).ToArray()) logs.Remove(path);
            foreach (var file in candidates)
                if (!logs.ContainsKey(file.FullName)) logs.Add(file.FullName, new Cursor(file.FullName, initial ? file.Length : 0));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static bool IsClientLog(string name) =>
        name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
        && (name.Contains("_Player_", StringComparison.OrdinalIgnoreCase)
            || name.Contains("_Studio_", StringComparison.OrdinalIgnoreCase))
        && !name.Contains("_Server_", StringComparison.OrdinalIgnoreCase);

    internal static bool TryParse(string line, long now, out McduFrame? frame)
    {
        frame = null;
        if (line.Length > MaximumLineLength) return false;
        var start = line.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<McduFrame>(line.AsSpan(start + Prefix.Length));
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.Session) || parsed.Session.Length > 64
                || parsed.Sequence <= 0 || parsed.Side is not (1 or 2)
                || !double.IsFinite(parsed.Brightness) || parsed.Brightness < 0 || parsed.Brightness > 255
                || parsed.Annunciators is < 0 or > 511 || parsed.AnnunciatorBrightness is < 0 or > 255
                || parsed.Timestamp < now - StaleMilliseconds || parsed.Timestamp > now + 2000
                || parsed.Rows == null || parsed.Rows.Length != 14) return false;
            foreach (var row in parsed.Rows)
            {
                if (row == null || row.Length > 96) return false;
                foreach (var run in row)
                {
                    if (run == null || run.Text == null || run.Text.Length > 256 || run.Text.Any(char.IsControl)
                        || run.Color is not ("White" or "Cyan" or "Green" or "Amber" or "Magenta" or "Red" or "Yellow" or "Black")) return false;
                    if (run.Column.ValueKind == JsonValueKind.String)
                    {
                        if (run.Column.GetString() is not ("center" or "right")) return false;
                    }
                    else if (!run.Column.TryGetInt32(out var column) || column < -256 || column > 256) return false;
                }
            }
            frame = parsed;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public void Dispose()
    {
        disposed = true;
        logs.Clear();
    }

    // One in-progress snapshot per log. Never display a partial or mixed frame.
    internal sealed class ChunkAssembly
    {
        private readonly StringBuilder payload = new();
        private string? session;
        private long sequence;
        private int nextPart;
        private int total;
        private long started;

        public string? Accept(string line, long now)
        {
            var start = line.IndexOf(ChunkPrefix, StringComparison.Ordinal);
            if (start < 0) return line;
            var fields = line[(start + ChunkPrefix.Length)..].Split('|', 5);
            if (fields.Length != 5 || fields[0].Length is < 1 or > 64
                || !long.TryParse(fields[1], out var id) || id <= 0
                || !int.TryParse(fields[2], out var part)
                || !int.TryParse(fields[3], out var count) || count is < 1 or > 128
                || part < 1 || part > count || fields[4].Length > 600) return null;
            if (part == 1)
            {
                payload.Clear();
                session = fields[0];
                sequence = id;
                total = count;
                nextPart = 1;
                started = now;
            }
            if (session != fields[0] || sequence != id || total != count || nextPart != part
                || now - started > StaleMilliseconds || payload.Length + fields[4].Length > MaximumLineLength)
            {
                nextPart = 0;
                return null;
            }
            payload.Append(fields[4]);
            nextPart++;
            if (part != total) return null;
            nextPart = 0;
            var result = Prefix + payload;
            // The chunk header must agree with the enclosed snapshot.
            if (!TryParse(result, now, out var frame) || frame!.Session != session || frame.Sequence != sequence) return null;
            return result;
        }
    }

    private sealed class Cursor(string path, long position)
    {
        public ChunkAssembly Chunks { get; } = new();
        private readonly StringBuilder partial = new();
        private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
        private readonly byte[] bytes = new byte[8192];
        private readonly char[] characters = new char[Encoding.UTF8.GetMaxCharCount(8192)];
        private readonly byte[] checkpoint = new byte[64];
        private int checkpointLength;
        private long lastWriteTicks;
        private bool skipLine;

        public void Read(Action<string> receive)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var writeTicks = File.GetLastWriteTimeUtc(path).Ticks;
            var replaced = stream.Length < position
                || stream.Length == position && lastWriteTicks != 0 && writeTicks != lastWriteTicks;
            if (!replaced && checkpointLength > 0)
            {
                stream.Position = position - checkpointLength;
                stream.ReadExactly(bytes.AsSpan(0, checkpointLength));
                replaced = !bytes.AsSpan(0, checkpointLength).SequenceEqual(checkpoint.AsSpan(0, checkpointLength));
            }
            if (replaced)
            {
                position = 0;
                partial.Clear();
                decoder.Reset();
                skipLine = false;
            }
            if (stream.Length - position > ReadBudget)
            {
                position = stream.Length - ReadBudget;
                partial.Clear();
                decoder.Reset();
                skipLine = true;
            }
            stream.Position = position;
            var remaining = ReadBudget;
            while (remaining > 0)
            {
                var count = stream.Read(bytes, 0, Math.Min(bytes.Length, remaining));
                if (count == 0) break;
                position += count;
                remaining -= count;
                var length = decoder.GetChars(bytes, 0, count, characters, 0, flush: false);
                for (var i = 0; i < length; i++)
                {
                    var character = characters[i];
                    if (character == '\n')
                    {
                        if (!skipLine) receive(partial.ToString().TrimEnd('\r'));
                        partial.Clear();
                        skipLine = false;
                    }
                    else if (!skipLine)
                    {
                        if (partial.Length >= MaximumLineLength)
                        {
                            partial.Clear();
                            skipLine = true;
                        }
                        else partial.Append(character);
                    }
                }
            }
            // Catch replacement/truncation even when the new file has already grown past the old cursor.
            checkpointLength = (int)Math.Min(position, checkpoint.Length);
            stream.Position = position - checkpointLength;
            stream.ReadExactly(checkpoint.AsSpan(0, checkpointLength));
            lastWriteTicks = writeTicks;
        }
    }
}
