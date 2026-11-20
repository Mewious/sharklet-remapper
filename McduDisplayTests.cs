using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace TailwindRemapper;

internal static class McduDisplayTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string reason)
        {
            if (!condition) throw new Exception($"MCDU display: {reason}");
            checks++;
        }
        var test = McduDisplayProtocol.TestFrame(1);
        Check(McduDisplay.IsMcduController(Guid.Parse("bb364098-0000-0000-0000-504944564944")), "captain MCDU product ID");
        Check(McduDisplay.IsMcduProduct(0xBB3E) && McduDisplay.IsMcduProduct(0xBB3A), "first-officer and observer MCDU product IDs");
        Check(!McduDisplay.IsMcduProduct(0xBB37) && !McduDisplay.IsMcduProduct(0xB930), "do not write to Boeing CDU or throttle");
        var cells = McduDisplayProtocol.Cells(test);
        Check(cells.Length == 336 && cells[5].Text == "T", "centered title on 24 x 14 grid");
        Check(cells[2 * 24].Colour == 4, "green text");
        Check(cells[4 * 24].Small && cells[4 * 24].Colour == 3, "small cyan text");
        Check(cells[10 * 24].Text == "☐" && cells[10 * 24 + 4].Text == "Δ" && cells[10 * 24 + 6].Text == "→", "Tailwind custom glyphs map to firmware font glyphs");
        Check(McduDisplayProtocol.Cells(test with { Brightness = 0 }).All(c => c.Text == " "), "power-off blanks every cell");
        var rows = Enumerable.Range(0, 14).Select(_ => Array.Empty<McduRun>()).ToArray();
        rows[0] = [new() { Text = "AB", Column = JsonSerializer.SerializeToElement("right") },
            new() { Text = "XY", Column = JsonSerializer.SerializeToElement(-1) },
            new() { Text = "C", Column = JsonSerializer.SerializeToElement(23), Color = "Amber" }];
        cells = McduDisplayProtocol.Cells(test with { Rows = rows });
        Check(cells[0].Text == "Y" && cells[22].Text == "A" && cells[23].Text == "C", "right alignment, clipping and overlapping runs");
        var packets = McduDisplayProtocol.ScreenPackets(test);
        Check(packets.All(p => p.Length == 64 && p[0] == 0xF2), "F2 reports padded to 64 bytes");
        var payload = packets.SelectMany(p => p.Skip(1)).ToArray();
        Check(payload[0] == 67 && payload[1] == 0, "first cell marker and white format in device byte order");
        var offset = 0;
        for (var i = 0; i < 336; i++)
        {
            var format = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));
            offset += 2;
            var result = Rune.DecodeFromUtf8(payload.AsSpan(offset), out _, out var consumed);
            Check(result == System.Buffers.OperationStatus.Done, "UTF-8 stays valid across HID packet boundaries");
            offset += consumed;
            if (i == 335) Check(format == 68, "last cell marker plus white format");
        }
        Check(payload.Skip(offset).All(b => b == 0), "last report zero padded");
        var data = Enumerable.Range(0, 120).Select(i => (byte)i).ToArray();
        var command = McduDisplayProtocol.Command(0x107, data);
        Check(command[0] == 0x32 && command[1] == 0xBB && command[12] == 0, "MCDU command prefix, no response requested");
        Check(BinaryPrimitives.ReadInt32LittleEndian(command.AsSpan(4)) == 0x107
            && BinaryPrimitives.ReadInt32LittleEndian(command.AsSpan(13)) == data.Length, "F0 little-endian header");
        var chunks = McduDisplayProtocol.CommandPackets(command);
        Check(chunks.Length == 3 && chunks[0][2] == 1 && chunks[2][2] == 3, "F0 packet sequence");
        Check(chunks.SelectMany(p => p.Skip(4).Take(p[3])).SequenceEqual(command), "F0 commands survive packet boundaries");
        var init = McduDisplayProtocol.Initialise().SelectMany(p => p.Skip(4).Take(p[3])).ToArray();
        var functions = new List<int>();
        var featureIds = new List<int>();
        for (offset = 0; offset < init.Length;)
        {
            var function = BinaryPrimitives.ReadInt32LittleEndian(init.AsSpan(offset + 4));
            var size = BinaryPrimitives.ReadInt32LittleEndian(init.AsSpan(offset + 13));
            functions.Add(function);
            if (function == 0x119) featureIds.Add(BinaryPrimitives.ReadUInt16LittleEndian(init.AsSpan(offset + 17)));
            offset += 17 + size;
        }
        Check(functions[0] == 0x11E && functions.Contains(0x118) && functions.Contains(0x11A) && functions.Contains(0x11C), "format table initialization");
        Check(featureIds.Count(f => f == 1) == 2 && featureIds.Count(f => f == 2) == 11
            && featureIds.Count(f => f == 3) == 11 && featureIds.Count(f => f == 4) == 3, "726 format combinations match F2 strides");
        Check(!functions.Any(f => f is 0x106 or 0x107 or 0x108), "format initialization never flashes or clears fonts");
        Check(McduDisplayProtocol.Brightness(-10)[8] == 0 && McduDisplayProtocol.Brightness(300)[8] == 255, "brightness bounded");
        Check(McduDisplayProtocol.Brightness(180).Length == 14, "brightness report uses its own 14-byte length");
        var lamps = McduDisplayProtocol.AnnunciatorPackets(test).ToArray();
        Check(lamps.Length == 10 && lamps.All(p => p.Length == 14 && p[0] == 2 && p[1] == 0x32 && p[2] == 0xBB
            && p[5] == 3 && p[6] == 0x49), "annunciators use 14-byte MCDU illumination reports");
        Check(lamps[0][7] == 2 && lamps[0][8] == 255, "LED brightness uses selector 02, separate from the screen");
        for (var bit = 0; bit < 9; bit++)
        {
            var one = McduDisplayProtocol.AnnunciatorPackets(test with { Annunciators = 1 << bit }, test with { Annunciators = 0 }).Single();
            Check(one[7] == 8 + bit && one[8] == 1, "each LED bit maps to selectors 08 through 10");
        }
        Check(lamps[8][7] == 0x0F && lamps[8][8] == 0, "cockpit lamp test leaves the unused line LED off");
        Check(!McduDisplayProtocol.AnnunciatorPackets(test, test).Any(), "unchanged lamps produce no USB writes");
        var dimmed = McduDisplayProtocol.AnnunciatorPackets(test with { AnnunciatorBrightness = 89 }, test).Single();
        Check(dimmed[7] == 2 && dimmed[8] == 89, "dimming updates only LED brightness");
        Check(McduDisplayProtocol.AnnunciatorPackets(new McduFrame()).All(p => p[8] == 0), "shutdown clears lamp states and brightness");

        var path = Path.Combine(Path.GetTempPath(), $"tailwind-font-test-{Guid.NewGuid():N}.wwf");
        try
        {
            object Font(int id) => new { headConfig = new { id, matrixW = 23, matrixH = 29, glyphSize = 91, charSize = 1, version = 0, isWriteFlash = 1 }, pixelUint8Array = new int[91] };
            File.WriteAllText(path, JsonSerializer.Serialize(new { largeFontConfig = Font(5), smallFontConfig = Font(6) }));
            var uploads = McduDisplay.ReadFonts(path, 52, 37);
            Check(uploads.Count == 4 && uploads[0][17 + 24] == 0 && uploads[2][17 + 24] == 0, "font upload forces RAM even when source requests flash");
            var rejected = false;
            try { McduDisplay.ReadFonts(path, 200, 200); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "reject font and grid outside framebuffer");
        }
        finally { File.Delete(path); }
        return checks;
    }
}
