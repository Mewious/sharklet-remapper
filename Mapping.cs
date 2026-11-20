using System.Text.Json;

namespace TailwindRemapper;

public sealed class Binding
{
    public string Target { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public bool Invert { get; set; }
    public double Minimum { get; set; } = 0;
    public double Center { get; set; } = 32767.5;
    public double Maximum { get; set; } = 65535;
    public double Deadzone { get; set; } = 0.03;
    public double Curve { get; set; } = 1;
    public bool IsTrigger => Target is "LT" or "RT" or "BrakeL" or "BrakeR" or "Flaps" or "Spoilers";
    public bool IsEngineControl => Target is "EngineMaster1" or "EngineMaster2" or "IgnitionCrank" or "IgnitionStart";
    public bool IsAuxiliary => IsEngineControl || Target is "GearToggle" or "Flaps" or "Spoilers" or "ParkingBrake" or "SpoilersArm";
    public bool IsAxis => Target is "LX" or "LY" or "RX" or "RY" || IsTrigger;

    public double Map(double raw)
    {
        if (!IsAxis) return (raw > 0) != Invert ? 1 : 0;
        double value;
        if (IsTrigger)
        {
            value = Math.Clamp((raw - Minimum) / (Maximum - Minimum), 0, 1);
            if (Invert) value = 1 - value;
            value = Math.Max(0, (value - Deadzone) / (1 - Deadzone));
        }
        else
        {
            value = raw >= Center ? (raw - Center) / (Maximum - Center) : (raw - Center) / (Center - Minimum);
            value = Math.Clamp(value, -1, 1);
            if (Invert) value = -value;
            value = Math.Sign(value) * Math.Max(0, (Math.Abs(value) - Deadzone) / (1 - Deadzone));
        }
        return Math.Sign(value) * Math.Pow(Math.Abs(value), Curve);
    }

    public void Validate()
    {
        if (!double.IsFinite(Minimum) || !double.IsFinite(Center) || !double.IsFinite(Maximum)
            || Minimum >= Maximum || (!IsTrigger && IsAxis && (Center <= Minimum || Center >= Maximum)))
            throw new InvalidDataException($"{Target}: calibration must have minimum < center < maximum. Triggers only need minimum < maximum.");
        if (!double.IsFinite(Deadzone) || Deadzone < 0 || Deadzone > 0.5 || !double.IsFinite(Curve) || Curve < 0.2 || Curve > 5)
            throw new InvalidDataException($"{Target}: deadzone must be 0–0.5 and curve 0.2–5.");
    }
}

public sealed class Profile
{
    public int Version { get; set; } = 1;
    public List<Binding> Bindings { get; set; } = Defaults();
    public static readonly string[] Targets = ["LX", "LY", "RX", "RY", "LT", "RT", "BrakeL", "BrakeR", "Flaps", "Spoilers", "ParkingBrake", "SpoilersArm", "EngineMaster1", "EngineMaster2", "IgnitionCrank", "IgnitionStart", "GearToggle", "A", "B", "X", "Y", "LB", "RB", "Back", "Start", "LS", "RS", "Up", "Down", "Left", "Right"];
    public static List<Binding> Defaults() => Targets.Select(target => new Binding { Target = target, Deadzone = target is "Flaps" or "Spoilers" ? 0 : 0.03 }).ToList();

    public void Validate()
    {
        if (Version != 1 || Bindings == null || Bindings.Count != Targets.Length
            || Bindings.Any(b => b == null) || !Bindings.Select(b => b.Target).Order().SequenceEqual(Targets.Order()))
            throw new InvalidDataException("This is not a supported Tailwind remapper profile.");
        foreach (var binding in Bindings)
        {
            binding.Validate();
            if (binding.Source == null || binding.SourceLabel == null) throw new InvalidDataException("Invalid source.");
        }
        if (Bindings.Any(b => b.Target is "BrakeL" or "BrakeR" && b.Source != "")
            && Bindings.Any(b => b.Target == "RY" && b.Source != ""))
            throw new InvalidDataException("Clear the RY binding before using toe brakes. Both brake rows share RY.");
    }

    public static Profile Read(string path)
    {
        var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty profile.");
        // Older profiles have no brake rows. Keep every existing hardware binding.
        if (profile.Version == 1 && profile.Bindings is { Count: 20 }
            && profile.Bindings.All(b => b != null && b.Target is not ("BrakeL" or "BrakeR")))
            profile.Bindings.InsertRange(6, [new Binding { Target = "BrakeL" }, new Binding { Target = "BrakeR" }]);
        if (profile.Version == 1 && profile.Bindings is { Count: 22 }
            && profile.Bindings.All(b => b != null && !b.IsAuxiliary))
            profile.Bindings.InsertRange(8, Defaults().Where(b => b.IsAuxiliary && !b.IsEngineControl && b.Target != "GearToggle"));
        if (profile.Version == 1 && profile.Bindings is { Count: 26 }
            && profile.Bindings.All(b => b != null && !b.IsEngineControl))
            profile.Bindings.InsertRange(12, Defaults().Where(b => b.IsEngineControl));
        if (profile.Version == 1 && profile.Bindings is { Count: 30 }
            && profile.Bindings.All(b => b != null && b.Target != "GearToggle"))
            profile.Bindings.Insert(16, new Binding { Target = "GearToggle" });
        profile.Validate();
        return profile;
    }

    public void Write(string path)
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    public static void CombineBrakes(Dictionary<string, double> mapped)
    {
        if (!mapped.ContainsKey("BrakeL") && !mapped.ContainsKey("BrakeR")) return;
        mapped["RY"] = Math.Max(mapped.GetValueOrDefault("BrakeL"), mapped.GetValueOrDefault("BrakeR"));
        mapped.Remove("BrakeL");
        mapped.Remove("BrakeR");
    }

    // A second Xbox pad preserves all primary axes and button bindings.
    // The spare stick carries a signature and assignment bits. Its heartbeat lets
    // Roblox reacquire the metadata after focus changes without held button events.
    public static Dictionary<string, double> AuxiliaryValues(Dictionary<string, double> mapped, bool heartbeat = false)
    {
        var values = new Dictionary<string, double>();
        var assigned = 0;
        if (mapped.TryGetValue("Flaps", out var flaps)) { assigned |= 1; values["LX"] = flaps; }
        if (mapped.TryGetValue("Spoilers", out var spoilers)) { assigned |= 2; values["LY"] = spoilers; }
        if (mapped.TryGetValue("ParkingBrake", out var brake)) { assigned |= 4; values["LT"] = brake; }
        if (mapped.TryGetValue("SpoilersArm", out var armed)) { assigned |= 8; values["RT"] = armed; }
        values["RX"] = heartbeat ? -0.625 : -0.5;
        values["RY"] = -(assigned + 1) / 32.0;
        // Extended packets retain the four lever/switch bits and add two-bit
        // engine states: master 0=unbound, 1=off, 2=on; ignition 1=crank, 2=norm, 3=start.
        var switchStates = 0;
        if (mapped.TryGetValue("EngineMaster1", out var master1)) switchStates |= (master1 > 0.5 ? 2 : 1) << 4;
        if (mapped.TryGetValue("EngineMaster2", out var master2)) switchStates |= (master2 > 0.5 ? 2 : 1) << 6;
        if (mapped.ContainsKey("IgnitionCrank") || mapped.ContainsKey("IgnitionStart"))
            switchStates |= (mapped.GetValueOrDefault("IgnitionStart") > 0.5 ? 3 : mapped.GetValueOrDefault("IgnitionCrank") > 0.5 ? 1 : 2) << 8;
        if (mapped.TryGetValue("GearToggle", out var gearToggle))
        {
            switchStates |= 1 << 10;
            values["A"] = gearToggle;
        }
        if (switchStates != 0)
        {
            values["RX"] = heartbeat ? 0.625 : 0.5;
            values["RY"] = -((assigned | switchStates) + 1) / 2048.0;
        }
        if (heartbeat)
        {
            // Roblox restores each component only when it changes after focus loss.
            // One output unit refreshes stationary controls without changing detents.
            foreach (var target in new[] { "LX", "LY", "LT", "RT", "RY" })
            {
                if (!values.TryGetValue(target, out var value)) continue;
                var unit = target is "LT" or "RT" ? 1 / 255.0 : 1 / 32767.0;
                values[target] = value >= 1 ? value - unit : value + unit;
            }
        }
        return values;
    }
}
