namespace TailwindRemapper;

internal static class SelfTest
{
    public static void Run()
    {
        var checks = 0;
        checks += McduLogTests.Run();
        checks += McduDisplayTests.Run();
        checks += McduInputTests.Run();
        void Near(double actual, double expected)
        {
            if (Math.Abs(actual - expected) > 0.00001) throw new Exception($"Expected {expected}, got {actual}.");
            checks++;
        }
        void Reject(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { checks++; return; }
            throw new Exception("Invalid profile was accepted.");
        }
        var axis = new Binding { Target = "LX", Minimum = 100, Center = 300, Maximum = 900, Deadzone = 0.1 };
        Near(axis.Map(100), -1);
        Near(axis.Map(300), 0);
        Near(axis.Map(900), 1);
        Near(axis.Map(10000), 1);
        Near(axis.Map(-10), -1);
        Near(axis.Map(310), 0);
        Near(axis.Map(280), 0);
        Near(axis.Map(630), 0.5);
        axis.Invert = true;
        Near(axis.Map(630), -0.5);
        axis.Curve = 2;
        Near(axis.Map(630), -0.25);
        var trigger = new Binding { Target = "LT", Minimum = 100, Maximum = 1100, Deadzone = 0 };
        Near(trigger.Map(100), 0);
        Near(trigger.Map(600), 0.5);
        Near(trigger.Map(1100), 1);
        trigger.Invert = true;
        Near(trigger.Map(100), 1);
        Near(trigger.Map(1100), 0);
        trigger.Deadzone = 0.1;
        Near(trigger.Map(1050), 0);
        Near(new Binding { Target = "A" }.Map(1), 1);
        Near(new Binding { Target = "A" }.Map(0), 0);
        Near(new Binding { Target = "ParkingBrake", Invert = true }.Map(0), 1);
        Near(new Binding { Target = "ParkingBrake", Invert = true }.Map(1), 0);
        foreach (var target in new[] { "Flaps", "Spoilers" })
        {
            var lever = new Binding { Target = target, Minimum = 100, Maximum = 1100, Deadzone = 0 };
            Near(lever.Map(100), 0);
            Near(lever.Map(600), 0.5);
            Near(lever.Map(1100), 1);
            lever.Invert = true;
            Near(lever.Map(100), 1);
        }
        var auxiliary = Profile.AuxiliaryValues(new() { ["Flaps"] = 0.75, ["Spoilers"] = 0.4, ["ParkingBrake"] = 0, ["SpoilersArm"] = 1 });
        Near(auxiliary["LX"], 0.75);
        Near(auxiliary["LY"], 0.4);
        Near(auxiliary["LT"], 0);
        Near(auxiliary["RT"], 1);
        Near(auxiliary["RX"], -0.5);
        Near(auxiliary["RY"], -0.5);
        auxiliary = Profile.AuxiliaryValues(new() { ["ParkingBrake"] = 0 });
        if (auxiliary.ContainsKey("LX") || auxiliary.ContainsKey("LY") || auxiliary.ContainsKey("RT"))
            throw new Exception("Unbound auxiliary controls must not claim cockpit controls.");
        Near(auxiliary["RY"], -5 / 32.0);
        Near(auxiliary["LT"], 0);
        Near(Profile.AuxiliaryValues(new() { ["ParkingBrake"] = 0 }, true)["RX"], -0.625);
        var pulse = Profile.AuxiliaryValues(new() { ["Flaps"] = 0, ["Spoilers"] = 1, ["ParkingBrake"] = 0, ["SpoilersArm"] = 1 }, true);
        Near(pulse["LX"], 1 / 32767.0);
        Near(pulse["LY"], 1 - 1 / 32767.0);
        Near(pulse["LT"], 1 / 255.0);
        Near(pulse["RT"], 1 - 1 / 255.0);
        Near(pulse["RY"], -0.5 + 1 / 32767.0);
        foreach (var target in new[] { "EngineMaster1", "EngineMaster2", "IgnitionCrank", "IgnitionStart", "GearToggle" })
        {
            Near(new Binding { Target = target }.Map(1), 1);
            Near(new Binding { Target = target }.Map(0), 0);
            Near(new Binding { Target = target, Invert = true }.Map(0), 1);
        }
        for (var master1 = 0; master1 <= 2; master1++)
        for (var master2 = 0; master2 <= 2; master2++)
        for (var ignition = 0; ignition <= 3; ignition++)
        {
            if (master1 == 0 && master2 == 0 && ignition == 0) continue;
            var switches = new Dictionary<string, double> { ["Flaps"] = 0.75, ["ParkingBrake"] = 1 };
            if (master1 != 0) switches["EngineMaster1"] = master1 - 1;
            if (master2 != 0) switches["EngineMaster2"] = master2 - 1;
            if (ignition != 0)
            {
                switches["IgnitionCrank"] = ignition == 1 ? 1 : 0;
                switches["IgnitionStart"] = ignition == 3 ? 1 : 0;
            }
            var packed = 5 + master1 * 16 + master2 * 64 + ignition * 256;
            var normal = Profile.AuxiliaryValues(switches);
            Near(normal["RX"], 0.5);
            Near(normal["RY"], -(packed + 1) / 2048.0);
            Near(normal["LX"], 0.75);
            Near(normal["LT"], 1);
            var refresh = Profile.AuxiliaryValues(switches, true);
            Near(refresh["RX"], 0.625);
            Near(Math.Round(-refresh["RY"] * 2048) - 1, packed);
        }
        Near(Profile.AuxiliaryValues(new() { ["IgnitionStart"] = 0 })["RY"], -513 / 2048.0);
        Near(Profile.AuxiliaryValues(new() { ["IgnitionStart"] = 1, ["IgnitionCrank"] = 1 })["RY"], -769 / 2048.0);
        foreach (var pressed in new[] { 0.0, 1.0 })
        foreach (var heartbeat in new[] { false, true })
        {
            var gear = Profile.AuxiliaryValues(new() { ["GearToggle"] = pressed }, heartbeat);
            Near(gear["A"], pressed);
            Near(gear["RX"], heartbeat ? 0.625 : 0.5);
            Near(Math.Round(-gear["RY"] * 2048) - 1, 1024);
            var combined = Profile.AuxiliaryValues(new() { ["GearToggle"] = pressed, ["EngineMaster1"] = 1, ["Flaps"] = 0.75, ["A"] = 1 - pressed }, heartbeat);
            Near(combined["A"], pressed);
            Near(Math.Round(-combined["RY"] * 2048) - 1, 1057);
        }
        if (Profile.AuxiliaryValues(new() { ["Flaps"] = 0 }).ContainsKey("A"))
            throw new Exception("Unbound gear must not send A.");
        checks++;
        var brake = new Binding { Target = "BrakeL", Minimum = 100, Maximum = 1100, Deadzone = 0 };
        Near(brake.Map(100), 0);
        Near(brake.Map(600), 0.5);
        Near(brake.Map(1100), 1);
        brake.Invert = true;
        Near(brake.Map(1100), 0);
        Near(brake.Map(100), 1);
        var mapped = new Dictionary<string, double> { ["BrakeL"] = 0.25, ["BrakeR"] = 0.8, ["LT"] = 0.4, ["RT"] = 0.6, ["RX"] = -0.3 };
        Profile.CombineBrakes(mapped);
        Near(mapped["RY"], 0.8);
        Near(mapped["LT"], 0.4);
        Near(mapped["RT"], 0.6);
        Near(mapped["RX"], -0.3);
        if (mapped.ContainsKey("BrakeL") || mapped.ContainsKey("BrakeR")) throw new Exception("Brake helpers must not reach Xbox output.");
        mapped = new() { ["BrakeL"] = 0.7 };
        Profile.CombineBrakes(mapped);
        Near(mapped["RY"], 0.7);
        mapped = new() { ["BrakeL"] = 0, ["BrakeR"] = 0 };
        Profile.CombineBrakes(mapped);
        Near(mapped["RY"], 0);
        var conflict = new Profile();
        conflict.Bindings.First(b => b.Target == "BrakeL").Source = "pedal/X";
        conflict.Bindings.First(b => b.Target == "RY").Source = "stick/Y";
        Reject(conflict.Validate);
        Reject(() => new Binding { Minimum = 5, Maximum = 5 }.Validate());
        Reject(() => new Binding { Target = "LX", Center = 0 }.Validate());
        Reject(() => new Binding { Curve = double.NaN }.Validate());
        Reject(() => new Binding { Deadzone = 1 }.Validate());
        Reject(() => new Profile { Bindings = [] }.Validate());
        Reject(() => new Profile { Version = 20 }.Validate());
        var profile = new Profile();
        profile.Bindings[0].Source = "test-device/X";
        profile.Bindings[0].SourceLabel = "Test stick X";
        var path = Path.Combine(Path.GetTempPath(), $"tailwind-profile-test-{Guid.NewGuid()}.json");
        try
        {
            profile.Write(path);
            var loaded = Profile.Read(path);
            if (loaded.Bindings[0].Source != "test-device/X") throw new Exception("Lost binding after saving.");
            checks++;
            var legacy = new Profile { Bindings = profile.Bindings.Where(b => !b.IsAuxiliary && b.Target is not ("BrakeL" or "BrakeR")).ToList() };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(legacy));
            var upgraded = Profile.Read(path);
            if (upgraded.Bindings.Count != Profile.Targets.Length || upgraded.Bindings[0].Source != "test-device/X"
                || upgraded.Bindings.Any(b => b.Target is "BrakeL" or "BrakeR" && b.Source != ""))
                throw new Exception("Legacy profile migration changed bindings.");
            checks++;
            legacy.Bindings.InsertRange(6, [new Binding { Target = "BrakeL", Source = "pedal/X" }, new Binding { Target = "BrakeR" }]);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(legacy));
            upgraded = Profile.Read(path);
            if (upgraded.Bindings.First(b => b.Target == "BrakeL").Source != "pedal/X"
                || upgraded.Bindings.Any(b => b.IsAuxiliary && b.Source != ""))
                throw new Exception("Upgrade lost brake bindings or assigned auxiliary controls.");
            checks++;
            upgraded.Bindings.First(b => b.Target == "ParkingBrake").Invert = true;
            upgraded.Bindings.First(b => b.Target == "Flaps").Source = "lever/Z";
            upgraded.Write(path);
            loaded = Profile.Read(path);
            if (!loaded.Bindings.First(b => b.Target == "ParkingBrake").Invert
                || loaded.Bindings.First(b => b.Target == "Flaps").Source != "lever/Z")
                throw new Exception("Lost auxiliary bindings after saving.");
            checks++;
            var preEngine = new Profile { Bindings = loaded.Bindings.Where(b => !b.IsEngineControl && b.Target != "GearToggle").ToList() };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(preEngine));
            loaded = Profile.Read(path);
            if (loaded.Bindings.Count != Profile.Targets.Length
                || loaded.Bindings.First(b => b.Target == "Flaps").Source != "lever/Z"
                || !loaded.Bindings.First(b => b.Target == "ParkingBrake").Invert
                || loaded.Bindings.Any(b => b.IsEngineControl && b.Source != ""))
                throw new Exception("Engine upgrade changed existing bindings.");
            checks++;
            var preGear = new Profile { Bindings = loaded.Bindings.Where(b => b.Target != "GearToggle").ToList() };
            preGear.Bindings.First(b => b.Target == "EngineMaster1").Source = "throttle/Button1";
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(preGear));
            loaded = Profile.Read(path);
            var gearBinding = loaded.Bindings.Single(b => b.Target == "GearToggle");
            if (gearBinding.Source != "" || gearBinding.IsAxis || !gearBinding.IsAuxiliary
                || loaded.Bindings.First(b => b.Target == "EngineMaster1").Source != "throttle/Button1"
                || loaded.Bindings.First(b => b.Target == "Flaps").Source != "lever/Z")
                throw new Exception("Gear upgrade changed existing bindings or assigned the new button.");
            checks++;
            gearBinding.Source = "stick/Button2";
            gearBinding.Invert = true;
            loaded.Write(path);
            loaded = Profile.Read(path);
            if (loaded.Bindings.Single(b => b.Target == "GearToggle") is not { Source: "stick/Button2", Invert: true })
                throw new Exception("Lost gear binding after saving.");
            checks++;
            loaded.Bindings[1].Target = "LX";
            Reject(loaded.Validate);
        }
        finally { File.Delete(path); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), $"PASS: {checks} mapping, profile, MCDU log, display and input checks.");
    }
}
