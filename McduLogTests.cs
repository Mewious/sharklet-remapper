using System.Text;
using System.Text.Json;

namespace TailwindRemapper;

internal static class McduLogTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool passed, string message)
        {
            if (!passed) throw new Exception($"MCDU log: {message}");
            checks++;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rows = Enumerable.Range(0, 14).Select(_ => Array.Empty<McduRun>()).ToArray();
        rows[0] = [new McduRun { Text = "FLIGHT °¢", Column = JsonSerializer.SerializeToElement("center"), Color = "Cyan", Small = true }];
        var frame = new McduFrame { Session = "test-session", Sequence = 1, Side = 1, Brightness = 128, Rows = rows, Timestamp = now };
        string Line(McduFrame value) => "2026-10-27T10:00:00Z [FLog::Output] " + McduLogReader.Prefix + JsonSerializer.Serialize(value);
        var valid = Line(frame);
        Check(McduLogReader.TryParse(valid, now, out var parsed) && parsed!.Session == frame.Session
            && parsed.Sequence == 1 && parsed.Side == 1 && parsed.Brightness == 128 && parsed.Timestamp == now, "valid snapshot preserves metadata");
        Check(parsed!.Rows[0][0].Text == "FLIGHT °¢" && parsed.Rows[0][0].Column.GetString() == "center" && parsed.Rows[0][0].Small, "Unicode and small text survive JSON");
        Check(!McduLogReader.TryParse(valid.Replace("MCDU:1", "MCDU:2"), now, out _), "unknown protocol version rejected");
        Check(!McduLogReader.TryParse(Line(frame with { Timestamp = now - 10001 }), now, out _), "stale snapshot rejected");
        Check(!McduLogReader.TryParse(Line(frame with { Timestamp = now + 2001 }), now, out _), "future snapshot rejected");
        Check(!McduLogReader.TryParse(Line(frame with { Side = 3 }), now, out _), "invalid unit rejected");
        Check(!McduLogReader.TryParse(Line(frame with { Brightness = 256 }), now, out _), "invalid brightness rejected");
        Check(McduLogReader.TryParse(Line(frame with { Annunciators = 383, AnnunciatorBrightness = 89 }), now, out var lit)
            && lit!.Annunciators == 383 && lit.AnnunciatorBrightness == 89, "annunciator states and brightness survive the log transport");
        Check(!McduLogReader.TryParse(Line(frame with { Annunciators = 512 }), now, out _)
            && !McduLogReader.TryParse(Line(frame with { Annunciators = -1 }), now, out _), "invalid LED mask rejected");
        Check(!McduLogReader.TryParse(Line(frame with { AnnunciatorBrightness = 256 }), now, out _)
            && !McduLogReader.TryParse(Line(frame with { AnnunciatorBrightness = -1 }), now, out _), "invalid LED brightness rejected");
        Check(parsed!.Annunciators == 0 && parsed.AnnunciatorBrightness == 0, "older screen-only frames leave lamps off");
        Check(!McduLogReader.TryParse(Line(frame with { Rows = [] }), now, out _), "incomplete grid rejected");
        Check(!McduLogReader.TryParse(McduLogReader.Prefix + "{broken}", now, out _), "bad JSON ignored");
        Check(!McduLogReader.TryParse(new string('x', McduLogReader.MaximumLineLength + 1), now, out _), "oversized log line ignored");
        var badRows = rows.ToArray();
        badRows[0] = [new McduRun { Text = "INVALID", Column = JsonSerializer.SerializeToElement(true) }];
        Check(!McduLogReader.TryParse(Line(frame with { Rows = badRows }), now, out _), "invalid column rejected without throwing");
        Check(McduLogReader.IsClientLog("version_Player_a_last.log") && McduLogReader.IsClientLog("version_Studio_b.log")
            && !McduLogReader.IsClientLog("version_Server_c.log") && !McduLogReader.IsClientLog("other.log"), "client logs selected without server logs");

        var denseRows = Enumerable.Range(0, 14).Select(row => new[] {
            new McduRun { Text = $"INIT PERF PROG STATUS {row}", Column = JsonSerializer.SerializeToElement(0), Color = "Green" },
            new McduRun { Text = "|°¢", Column = JsonSerializer.SerializeToElement("right"), Color = "Cyan", Small = true }
        }).ToArray();
        var dense = frame with { Rows = denseRows };
        var json = JsonSerializer.Serialize(dense);
        var chunks = Enumerable.Range(0, (json.Length + 599) / 600)
            .Select(part => $"{McduLogReader.ChunkPrefix}{frame.Session}|1|{part + 1}|{(json.Length + 599) / 600}|{json.Substring(part * 600, Math.Min(600, json.Length - part * 600))}").ToArray();
        var assembly = new McduLogReader.ChunkAssembly();
        Check(chunks.Length > 2 && chunks.All(line => Encoding.UTF8.GetByteCount(line) < 800), "dense pages use short log lines");
        for (var part = 0; part < chunks.Length - 1; part++)
            Check(assembly.Accept(chunks[part], now) == null, "incomplete page must not reach the display");
        var complete = assembly.Accept(chunks[^1], now);
        Check(complete != null && McduLogReader.TryParse(complete, now, out var denseParsed)
            && denseParsed!.Rows[13][1].Text == "|°¢", "large page reassembles with alignment, Unicode and delimiters intact");
        assembly.Accept(chunks[0], now);
        Check(assembly.Accept(chunks[^1], now) == null, "missing chunks cannot show a partial page");
        assembly.Accept(chunks[0], now);
        Check(assembly.Accept(chunks[1], now + 10001) == null, "abandoned assembly expires");
        assembly.Accept(chunks[0], now);
        Check(assembly.Accept(chunks[1].Replace("|1|2|", "|2|2|"), now) == null, "sequences cannot mix");

        var directory = Path.Combine(Path.GetTempPath(), "TailwindMcduLogTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var player = Path.Combine(directory, "version_Player_a_last.log");
            var studio = Path.Combine(directory, "version_Studio_b_last.log");
            var server = Path.Combine(directory, "version_Server_c.log");
            File.WriteAllText(player, valid + "\n", Encoding.UTF8);
            File.WriteAllText(studio, "unrelated Studio\n", Encoding.UTF8);
            File.WriteAllText(server, "server\n", Encoding.UTF8);
            using var reader = new McduLogReader(1, directory);
            Check(reader.Poll(now) == null, "startup does not replay existing flight data");
            File.AppendAllText(player, Line(frame with { Sequence = 2, Side = 2 }) + "\n", Encoding.UTF8);
            Check(reader.Poll(now) == null, "other unit ignored");
            var next = Line(frame with { Sequence = 3 });
            File.AppendAllText(player, next[..(next.Length / 2)], Encoding.UTF8);
            Check(reader.Poll(now) == null, "partial log line waits for newline");
            File.AppendAllText(player, next[(next.Length / 2)..] + "\r\n", Encoding.UTF8);
            Check(reader.Poll(now)?.Sequence == 3 && reader.CurrentLogPath == player, "completed line received even with unrelated Studio log");
            Check(reader.Poll(now) == null, "poll does not replay an already consumed line");
            File.AppendAllText(player, next + "\n", Encoding.UTF8);
            Check(reader.Poll(now) == null, "duplicate sequence rejected");
            File.AppendAllText(player, Line(frame with { Sequence = 4 }) + "\n" + Line(frame with { Sequence = 5 }) + "\n", Encoding.UTF8);
            Check(reader.Poll(now)?.Sequence == 5, "a burst yields the latest complete frame");
            File.AppendAllText(server, Line(frame with { Sequence = 99 }) + "\n", Encoding.UTF8);
            Check(reader.Poll(now) == null, "server log cannot take over the screen");
            File.WriteAllText(player, Line(frame with { Sequence = 6 }) + "\n", Encoding.UTF8);
            Check(reader.Poll(now)?.Sequence == 6, "truncated log is reread from its new beginning");
            File.WriteAllText(player, Line(frame with { Sequence = 7 }) + "\n", Encoding.UTF8);
            Check(reader.Poll(now)?.Sequence == 7, "replacement of the same length does not hide the new frame");
            var rotated = Path.Combine(directory, "version_Player_new_last.log");
            File.WriteAllText(rotated, Line(frame with { Session = "new-session", Sequence = 1, Timestamp = now + 1001 }) + "\n", Encoding.UTF8);
            Check(reader.Poll(now + 1001)?.Session == "new-session" && reader.CurrentLogPath == rotated, "new client log discovered after rotation");
            File.AppendAllText(rotated, new string('x', McduLogReader.MaximumLineLength + 100) + "\n" +
                Line(frame with { Session = "new-session", Sequence = 2, Timestamp = now + 1002 }) + "\n", Encoding.UTF8);
            Check(reader.Poll(now + 1002)?.Sequence == 2, "oversized line discarded without losing the following frame");
            File.AppendAllText(rotated, new string('x', 300000) + "\n" +
                Line(frame with { Session = "new-session", Sequence = 3, Timestamp = now + 1003 }) + "\n", Encoding.UTF8);
            Check(reader.Poll(now + 1003)?.Sequence == 3, "large unrelated backlog is skipped within the read budget");
            var unicodeFrame = frame with { Session = "new-session", Sequence = 4, Timestamp = now + 1004 };
            var unicodeLine = Line(unicodeFrame).Replace("\\u00B0", "°").Replace("\\u00A2", "¢");
            var encoded = Encoding.UTF8.GetBytes(unicodeLine + "\n");
            var split = Array.IndexOf(encoded, (byte)0xc2) + 1;
            using (var append = new FileStream(rotated, FileMode.Append)) append.Write(encoded, 0, split);
            Check(reader.Poll(now + 1004) == null, "split UTF-8 character waits for remainder");
            using (var append = new FileStream(rotated, FileMode.Append)) append.Write(encoded, split, encoded.Length - split);
            Check(reader.Poll(now + 1004)?.Rows[0][0].Text == "FLIGHT °¢", "split UTF-8 character decoded correctly");
            var chunkLog = Path.Combine(directory, "version_Player_chunks.log");
            File.WriteAllText(chunkLog, string.Join("\n", chunks) + "\n", Encoding.UTF8);
            using var chunkReader = new McduLogReader(1, directory);
            File.AppendAllText(chunkLog, string.Join("\n", chunks) + "\n", Encoding.UTF8);
            Check(chunkReader.Poll(now)?.Rows[13][0].Text == "INIT PERF PROG STATUS 13", "tailer routes complete chunked pages to the display");
            reader.Dispose();
            try { reader.Poll(); throw new Exception("Disposed reader was polled."); }
            catch (ObjectDisposedException) { checks++; }
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
        return checks;
    }
}
