using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace TailwindRemapper;

// WINCTRL's F0 commands configure the grid; F2 reports contain all 336 cells.
// Protocol reference: https://github.com/landre-cerp/WwDevicesDotnet/blob/main/Winctrl/README.md
internal static class McduDisplayProtocol
{
    public const int Columns = 24;
    public const int Rows = 14;
    private static readonly string[] Colours = ["Black", "Amber", "White", "Cyan", "Green", "Magenta", "Red", "Yellow", "Brown", "Gray", "Khaki"];
    private static readonly uint[] Palette = [0xFF000000, 0xFFFFA500, 0xFFFFFFFF, 0xFF00FFFF, 0xFF00FF3D,
        0xFFFF63FF, 0xFFFF0000, 0xFFFFFF00, 0xFF615C42, 0xFF777777, 0xFF79735E];

    public readonly record struct Cell(string Text, int Colour, bool Small);

    public static Cell[] Cells(McduFrame frame)
    {
        var cells = Enumerable.Repeat(new Cell(" ", 2, false), Columns * Rows).ToArray();
        if (frame.Brightness <= 0) return cells;
        for (var row = 0; row < Math.Min(Rows, frame.Rows.Length); row++)
        {
            foreach (var run in frame.Rows[row])
            {
                var characters = run.Text.EnumerateRunes().ToArray();
                var column = run.Column.ValueKind == JsonValueKind.Number ? run.Column.GetInt32()
                    : run.Column.GetString() == "right" ? Columns - characters.Length : (int)Math.Floor((Columns - characters.Length) / 2.0);
                var colour = Array.FindIndex(Colours, c => c.Equals(run.Color, StringComparison.OrdinalIgnoreCase));
                for (var i = 0; i < characters.Length; i++)
                {
                    if (column + i is < 0 or >= Columns) continue;
                    cells[row * Columns + column + i] = new Cell(Glyph(characters[i]), colour < 0 ? 2 : colour, run.Small);
                }
            }
        }
        return cells;
    }

    private static string Glyph(Rune rune) => rune.Value switch
    {
        '#' => "☐", '&' => "Δ", 0xA2 => "→", 0xA3 => "←", 0xA4 => "↑", 0xA5 => "↓",
        0xB0 => "°", >= 32 and <= 126 => rune.ToString(), _ => " "
    };

    public static byte[][] ScreenPackets(McduFrame frame)
    {
        var cells = Cells(frame);
        using var bytes = new MemoryStream();
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            var format = cell.Colour * 33 + (cell.Small ? 363 : 0) + (i == 0 ? 1 : i == cells.Length - 1 ? 2 : 0);
            bytes.WriteByte((byte)format);
            bytes.WriteByte((byte)(format >> 8));
            bytes.Write(Encoding.UTF8.GetBytes(cell.Text));
        }
        return Split(bytes.ToArray(), 0xF2, 63);
    }

    public static byte[] Command(int function, byte[]? data = null)
    {
        data ??= [];
        var command = new byte[17 + data.Length];
        command[0] = 0x32;
        command[1] = 0xBB;
        BinaryPrimitives.WriteInt32LittleEndian(command.AsSpan(4), function);
        BinaryPrimitives.WriteInt32LittleEndian(command.AsSpan(8), unchecked((int)Environment.TickCount64));
        BinaryPrimitives.WriteInt32LittleEndian(command.AsSpan(13), data.Length);
        data.CopyTo(command, 17);
        return command;
    }

    public static byte[][] CommandPackets(IEnumerable<byte> commands) => Split(commands.ToArray(), 0xF0, 56);

    private static byte[][] Split(byte[] bytes, byte report, int capacity)
    {
        var packets = new List<byte[]>();
        for (var offset = 0; offset < bytes.Length; offset += capacity)
        {
            var packet = new byte[64];
            packet[0] = report;
            var count = Math.Min(capacity, bytes.Length - offset);
            if (report == 0xF0)
            {
                packet[2] = (byte)(packets.Count + 1);
                packet[3] = (byte)count;
            }
            Array.Copy(bytes, offset, packet, report == 0xF0 ? 4 : 1, count);
            packets.Add(packet);
        }
        return packets.ToArray();
    }

    public static byte[][] Initialise(int x = 52, int y = 37)
    {
        var commands = new List<byte>();
        commands.AddRange(Command(0x11E));
        var grid = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(grid, checked((ushort)x));
        BinaryPrimitives.WriteUInt16LittleEndian(grid.AsSpan(2), checked((ushort)y));
        BinaryPrimitives.WriteUInt16LittleEndian(grid.AsSpan(4), Rows);
        BinaryPrimitives.WriteUInt16LittleEndian(grid.AsSpan(6), Columns);
        commands.AddRange(Command(0x118, grid));
        ulong formatId = 2;
        uint[][] features = [[5, 6], Palette, Palette, [0, 1, 2]];
        for (var feature = 0; feature < features.Length; feature++)
        {
            foreach (var value in features[feature])
            {
                var data = new byte[14];
                BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)(feature + 1));
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2), value);
                BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(6), formatId++);
                commands.AddRange(Command(0x119, data));
            }
        }
        commands.AddRange(Command(0x11A, [2]));
        commands.AddRange(Command(0x11C));
        commands.AddRange(Command(0x112, [255, 0, 0, 0]));
        commands.AddRange(Command(0x113, [255, 0, 0, 0]));
        commands.AddRange(Command(0x110, [0, 0, 0, 0, 0x80, 2, 0xE0, 1]));
        commands.AddRange(Command(0x103));
        return CommandPackets(commands);
    }

    public static byte[] Brightness(double value)
        => Illumination(1, (byte)Math.Clamp(Math.Round(value), 0, 255));

    private static byte[] Illumination(byte type, byte value)
    {
        var report = new byte[14];
        byte[] command = [2, 0x32, 0xBB, 0, 0, 3, 0x49, type, value];
        command.CopyTo(report, 0);
        return report;
    }

    public static IEnumerable<byte[]> AnnunciatorPackets(McduFrame frame, McduFrame? previous = null)
    {
        if (previous == null || frame.AnnunciatorBrightness != previous.AnnunciatorBrightness)
            yield return Illumination(2, (byte)Math.Clamp(frame.AnnunciatorBrightness, 0, 255));
        // FAIL, FM, MCDU, MENU, FM1, IND, RDY, unused line, FM2.
        for (var bit = 0; bit < 9; bit++)
        {
            var on = (frame.Annunciators & (1 << bit)) != 0;
            if (previous == null || on != ((previous.Annunciators & (1 << bit)) != 0))
                yield return Illumination((byte)(8 + bit), (byte)(on ? 1 : 0));
        }
    }

    public static McduFrame TestFrame(int side) => new()
    {
        Side = side, Brightness = 180, Annunciators = 383, AnnunciatorBrightness = 255,
        Rows = Enumerable.Range(0, Rows).Select(row => row switch
        {
            0 => new[] { new McduRun { Text = "TAILWIND MCDU", Column = JsonSerializer.SerializeToElement("center"), Color = "White" } },
            2 => new[] { new McduRun { Text = "SCREEN CONNECTED", Column = JsonSerializer.SerializeToElement(0), Color = "Green" } },
            4 => new[] { new McduRun { Text = "CYAN  SMALL TEXT", Column = JsonSerializer.SerializeToElement(0), Color = "Cyan", Small = true } },
            6 => new[] { new McduRun { Text = "AMBER", Column = JsonSerializer.SerializeToElement(0), Color = "Amber" } },
            8 => new[] { new McduRun { Text = "MAGENTA", Column = JsonSerializer.SerializeToElement(0), Color = "Magenta" } },
            10 => new[] { new McduRun { Text = "# ° & ¢ £ ¤ ¥", Column = JsonSerializer.SerializeToElement(0) } },
            13 => new[] { new McduRun { Text = "WAITING FOR ROBLOX", Column = JsonSerializer.SerializeToElement(0) } },
            _ => Array.Empty<McduRun>()
        }).ToArray()
    };
}
