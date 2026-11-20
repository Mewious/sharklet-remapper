namespace TailwindRemapper;

// MCDU-32 HID report bits, exposed in the same order as DirectInput buttons.
// The two blank keys are deliberately omitted from the virtual controller packet.
public sealed class McduInput : IDisposable
{
    public static readonly string[] Keys =
    [
        "L1", "L2", "L3", "L4", "L5", "L6", "R1", "R2", "R3", "R4", "R5", "R6",
        "DIR", "PROG", "PERF", "INIT", "DATA", "", "BRT", "FPLN", "RADNAV", "FUELPRED", "SECFPLN", "ATCCOMM",
        "MENU", "DIM", "AIRPORT", "", "LEFT", "UP", "RIGHT", "DOWN",
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "DOT", "0", "PLUSMINUS",
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P",
        "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "SLASH", "SPACE", "OVFY", "CLR"
    ];

    private static readonly string[] PacketButtons = ["A", "B", "X", "Y", "LB", "RB", "Up", "Down", "Left", "Right"];
    private readonly XboxOutput output;
    private readonly string deviceId;
    private readonly int side;
    private readonly bool[] suppressed = new bool[Keys.Length];
    private bool initialized;

    public McduInput(string deviceId, int side)
    {
        if (side is not (1 or 2) || string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Choose a connected MCDU and side 1 or 2.");
        this.deviceId = deviceId;
        this.side = side;
        output = new XboxOutput();
    }

    public void Update(Dictionary<string, double> readings)
    {
        var down = new bool[Keys.Length];
        for (var index = 0; index < Keys.Length; index++)
        {
            if (!readings.TryGetValue($"{deviceId}/B{index}", out var value))
                throw new IOException("MCDU input disconnected. Reconnect the device, refresh devices, and restart the MCDU bridge.");
            var pressed = value > 0.5;
            if (!initialized) suppressed[index] = pressed;
            if (!pressed) suppressed[index] = false;
            down[index] = pressed && !suppressed[index];
        }
        initialized = true;
        output.Send(Encode(down, side, Environment.TickCount64 / 250 % 2 == 0));
    }

    public static Dictionary<string, double> Encode(bool[] physicalKeys, int side, bool heartbeat)
    {
        if (side is not (1 or 2) || physicalKeys.Length != Keys.Length)
            throw new ArgumentException("MCDU packets require side 1 or 2 and 74 physical keys.");
        var bits = physicalKeys.Where((_, index) => Keys[index] != "").ToArray();
        int Mask(int offset, int length)
        {
            var mask = 0;
            for (var bit = 0; bit < length; bit++)
                if (bits[offset + bit]) mask |= 1 << bit;
            return mask;
        }
        // Roblox adds one unit to vertical axes and suppresses trigger values near zero.
        // Leave a spare bit on Y and keep trigger payloads in the middle of their range.
        // Flipping a payload bit refreshes stationary inputs after Roblox regains focus.
        var marker = Mask(64, 8) + (heartbeat ? 256 : 0) + (side - 1) * 512;
        var values = new Dictionary<string, double>
        {
            ["LX"] = (16384 + (Mask(0, 14) ^ (heartbeat ? 8192 : 0))) / 32767.0,
            ["LY"] = (16384 + (Mask(14, 13) ^ (heartbeat ? 4096 : 0)) * 2) / 32767.0,
            ["RY"] = (16384 + (Mask(27, 13) ^ (heartbeat ? 4096 : 0)) * 2) / 32767.0,
            ["RX"] = -(24576 + marker * 8) / 32768.0,
            ["LT"] = (64 + (Mask(40, 7) ^ (heartbeat ? 64 : 0))) / 255.0,
            ["RT"] = (64 + (Mask(47, 7) ^ (heartbeat ? 64 : 0))) / 255.0,
            ["LS"] = 1, ["RS"] = 1 // Constant identifier; never assigned to an MCDU key.
        };
        for (var bit = 0; bit < PacketButtons.Length; bit++)
            values[PacketButtons[bit]] = bits[54 + bit] ? 1 : 0;
        return values;
    }

    public void Dispose() => output.Dispose();
}
