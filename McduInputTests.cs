namespace TailwindRemapper;

internal static class McduInputTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }
        var compactKeys = McduInput.Keys.Where(key => key != "").ToArray();
        Check(compactKeys.Length == 72 && compactKeys.Distinct().Count() == 72, "Expected 72 distinct MCDU keys.");
        var packetButtons = new[] { "A", "B", "X", "Y", "LB", "RB", "Up", "Down", "Left", "Right" };
        foreach (var side in new[] { 1, 2 })
        foreach (var heartbeat in new[] { false, true })
        for (var physical = -1; physical <= 74; physical++)
        {
            var down = new bool[74];
            if (physical == 74) Array.Fill(down, true);
            else if (physical >= 0) down[physical] = true;
            var encoded = McduInput.Encode(down, side, heartbeat);
            var decoded = new List<bool>();
            foreach (var axis in new[] { "LX", "LY", "RY" })
            {
                var value = encoded[axis];
                var raw = (int)Math.Round(value * 32767);
                Check(raw >= 16384 && raw <= 32767, "MCDU bitmaps must stay positive and outside stick deadzones.");
                var mask = axis == "LX" ? raw - 16384 : (raw + 1 - 16384) / 2;
                if (heartbeat) mask ^= axis == "LX" ? 8192 : 4096;
                for (var bit = 0; bit < (axis == "LX" ? 14 : 13); bit++) decoded.Add((mask & (1 << bit)) != 0);
            }
            foreach (var trigger in new[] { "LT", "RT" })
            {
                var raw = (int)Math.Round(encoded[trigger] * 255) - 64;
                if (heartbeat) raw ^= 64;
                for (var bit = 0; bit < 7; bit++) decoded.Add((raw & (1 << bit)) != 0);
            }
            decoded.AddRange(packetButtons.Select(button => encoded[button] > 0.5));
            var marker = (int)Math.Round((-encoded["RX"] * 32768 - 24576) / 8);
            for (var bit = 0; bit < 8; bit++) decoded.Add((marker & (1 << bit)) != 0);
            Check(decoded.SequenceEqual(down.Where((_, index) => McduInput.Keys[index] != "")), "MCDU key packet lost or changed a key.");
            Check(encoded["LS"] == 1 && encoded["RS"] == 1, "MCDU identity stays constant for every key and heartbeat.");
            Check(!encoded.ContainsKey("Start") && !encoded.ContainsKey("Back"), "MCDU packets must not use Xbox menu buttons.");
            Check((marker >> 9) + 1 == side && ((marker & 256) != 0) == heartbeat, "MCDU side/heartbeat marker is incorrect.");
        }
        try { McduInput.Encode(new bool[74], 3, false); throw new Exception("Invalid MCDU side was accepted."); }
        catch (ArgumentException) { checks++; }
        return checks;
    }
}
