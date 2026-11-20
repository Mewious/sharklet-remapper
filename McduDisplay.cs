using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using HidSharp;

namespace TailwindRemapper;

internal sealed record McduUsbDevice(string Path, string Label);

internal sealed class McduDisplay : IDisposable
{
    private readonly HidStream stream;
    private readonly Mutex ownership;
    private byte[][]? previousScreen;
    private int previousBrightness = -1;
    private McduFrame? previousAnnunciators;

    public static string DefaultFontPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "SimAppPro", "resources", "app.asar.unpacked", "config", "GameExtendDisplay", "MCDU", "EditSpace", "WWT_MCDU", "WWT_MCDU.wwf");

    // The USB PID includes the configured seat; the command prefix stays 0xBB32.
    public static bool IsMcduProduct(int product) => product is 0xBB32 or 0xBB36 or 0xBB3A or 0xBB3E;

    public static bool IsMcduController(Guid productGuid)
    {
        var identifier = productGuid.ToByteArray();
        return BinaryPrimitives.ReadUInt16LittleEndian(identifier) == 0x4098
            && IsMcduProduct(BinaryPrimitives.ReadUInt16LittleEndian(identifier.AsSpan(2)));
    }

    public static List<McduUsbDevice> FindDevices() => DeviceList.Local.GetHidDevices(0x4098)
        .Where(d => IsMcduProduct(d.ProductID) && d.GetMaxOutputReportLength() >= 64)
        .Select(d => new McduUsbDevice(d.DevicePath, $"{d.GetProductName()} / {d.GetSerialNumber()}"))
        .ToList();

    public static void CheckSimAppProClosed()
    {
        foreach (var name in new[] { "SimAppPro", "SimLogic", "WWTStream" })
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes) process.Dispose();
            if (processes.Length > 0)
                throw new InvalidOperationException($"Close SimAppPro and its SimLogic / WWTStream helper before starting USB display output. {name} is running and can overwrite the screen.");
        }
    }

    public McduDisplay(string devicePath, string fontPath, int x, int y, CancellationToken cancellation = default)
    {
        CheckSimAppProClosed();
        // Validate all font data before touching the hardware. Fonts are loaded into RAM only.
        var fonts = ReadFonts(fontPath, x, y);
        ownership = new Mutex(false, "Local\\TailwindRemapperMcduDisplay");
        bool acquired;
        try { acquired = ownership.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            ownership.Dispose();
            throw new InvalidOperationException("Another Tailwind MCDU bridge is already using the display.");
        }
        HidStream? opened = null;
        try
        {
            var device = DeviceList.Local.GetHidDevices(0x4098).SingleOrDefault(d => IsMcduProduct(d.ProductID) && d.DevicePath == devicePath)
                ?? throw new IOException("MCDU-32 disconnected. Reconnect it and refresh devices.");
            opened = device.Open();
            opened.WriteTimeout = 1000;
            stream = opened;
            stream.Write(McduDisplayProtocol.Brightness(0));
            foreach (var command in fonts)
            {
                cancellation.ThrowIfCancellationRequested();
                WritePackets(McduDisplayProtocol.CommandPackets(command));
                Thread.Sleep(10);
            }
            cancellation.ThrowIfCancellationRequested();
            WritePackets(McduDisplayProtocol.Initialise(x, y));
        }
        catch
        {
            opened?.Dispose();
            ownership.ReleaseMutex();
            ownership.Dispose();
            throw;
        }
    }

    internal static List<byte[]> ReadFonts(string path, int x, int y)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Select the MCDU .wwf font from your SimAppPro installation.", path);
        if (file.Length > 2_000_000) throw new InvalidDataException("The MCDU font file is too large.");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var commands = new List<byte[]>();
        foreach (var (key, id) in new[] { ("largeFontConfig", 5), ("smallFontConfig", 6) })
        {
            var font = json.RootElement.GetProperty(key);
            var head = font.GetProperty("headConfig");
            var width = head.GetProperty("matrixW").GetInt32();
            var height = head.GetProperty("matrixH").GetInt32();
            var glyphSize = head.GetProperty("glyphSize").GetInt32();
            var count = head.GetProperty("charSize").GetInt32();
            var pixels = font.GetProperty("pixelUint8Array").EnumerateArray().Select(n => n.GetByte()).ToArray();
            if (head.GetProperty("id").GetInt32() != id || width is < 1 or > 26 || height is < 1 or > 31
                || count is < 1 or > 512 || glyphSize != (width + 7) / 8 * height + 4 || pixels.Length != count * glyphSize
                || x < 0 || y < 0 || x + width * 24 > 640 || y + height * 14 > 480)
                throw new InvalidDataException("The .wwf font must contain MCDU font slots 5 and 6 and fit the 24 x 14 screen at the selected offsets.");
            var header = new byte[25];
            BinaryPrimitives.WriteInt32LittleEndian(header, id);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)height);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), glyphSize);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), count);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), head.GetProperty("version").GetInt32());
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), pixels.Length + header.Length);
            header[24] = 0; // Never write flash, regardless of the source file's setting.
            commands.Add(McduDisplayProtocol.Command(0x106, header));
            for (var offset = 0; offset < pixels.Length; offset += 512)
            {
                var size = Math.Min(512, pixels.Length - offset);
                var data = new byte[12 + size];
                BinaryPrimitives.WriteInt32LittleEndian(data, id);
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), offset);
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), size);
                Array.Copy(pixels, offset, data, 12, size);
                commands.Add(McduDisplayProtocol.Command(0x107, data));
            }
        }
        return commands;
    }

    private void WritePackets(IEnumerable<byte[]> packets)
    {
        foreach (var packet in packets) stream.Write(packet);
    }

    public void Send(McduFrame frame)
    {
        WritePackets(McduDisplayProtocol.AnnunciatorPackets(frame, previousAnnunciators));
        previousAnnunciators = frame;
        var packets = McduDisplayProtocol.ScreenPackets(frame);
        if (previousScreen == null || !packets.SelectMany(p => p).SequenceEqual(previousScreen.SelectMany(p => p)))
        {
            WritePackets(packets);
            previousScreen = packets;
        }
        var brightness = (int)Math.Clamp(Math.Round(frame.Brightness), 0, 255);
        if (brightness != previousBrightness)
        {
            stream.Write(McduDisplayProtocol.Brightness(brightness));
            previousBrightness = brightness;
        }
    }

    public void Dispose()
    {
        try
        {
            WritePackets(McduDisplayProtocol.AnnunciatorPackets(new McduFrame()));
            stream.Write(McduDisplayProtocol.Brightness(0));
        }
        catch (IOException) { }
        catch (TimeoutException) { }
        finally
        {
            stream.Dispose();
            ownership.ReleaseMutex();
            ownership.Dispose();
        }
    }
}
