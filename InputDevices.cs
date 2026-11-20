using SharpDX.DirectInput;

namespace TailwindRemapper;

public sealed record InputSource(string Id, string Label, bool IsAxis);
public sealed record InputDeviceInfo(string Id, string Name, Guid ProductGuid);

public sealed class InputDevices : IDisposable
{
    private readonly DirectInput directInput = new();
    private readonly List<(Joystick Device, string Id, HashSet<string> Axes)> devices = [];
    public List<InputSource> Sources { get; } = [];
    public List<InputDeviceInfo> Controllers { get; } = [];
    public List<string> Messages { get; } = [];
    public int Count => devices.Count;
    private static readonly (string Name, JoystickOffset Offset, Func<JoystickState, int> Read)[] Axes =
    [
        ("X", JoystickOffset.X, s => s.X), ("Y", JoystickOffset.Y, s => s.Y),
        ("Z", JoystickOffset.Z, s => s.Z), ("RotationX", JoystickOffset.RotationX, s => s.RotationX),
        ("RotationY", JoystickOffset.RotationY, s => s.RotationY), ("RotationZ", JoystickOffset.RotationZ, s => s.RotationZ),
        ("Slider1", JoystickOffset.Sliders0, s => s.Sliders[0]), ("Slider2", JoystickOffset.Sliders1, s => s.Sliders[1])
    ];

    public void Refresh(IntPtr window)
    {
        CloseDevices();
        Sources.Clear();
        Controllers.Clear();
        Messages.Clear();
        foreach (var info in directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
        {
            // Never allow the virtual output to feed back into the remapper.
            if (info.ProductName.Contains("Xbox", StringComparison.OrdinalIgnoreCase)
                || info.ProductName.Contains("XInput", StringComparison.OrdinalIgnoreCase)
                || info.ProductGuid.ToString().StartsWith("028e045e", StringComparison.OrdinalIgnoreCase)) continue;
            Joystick? device = null;
            try
            {
                device = new Joystick(directInput, info.InstanceGuid);
                device.SetCooperativeLevel(window, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                var available = new HashSet<string>();
                foreach (var axis in Axes)
                {
                    try
                    {
                        var infoForAxis = device.GetObjectInfoByOffset((int)axis.Offset);
                        device.GetObjectPropertiesById(infoForAxis.ObjectId).Range = new InputRange(0, 65535);
                        available.Add(axis.Name);
                    }
                    catch (SharpDX.SharpDXException) { } // This device does not expose this axis.
                }
                device.Acquire();
                var id = info.InstanceGuid.ToString();
                var name = $"{info.ProductName.Trim()} [{id[..6]}]";
                devices.Add((device, id, available));
                Controllers.Add(new(id, info.ProductName.Trim(), info.ProductGuid));
                foreach (var axis in Axes.Where(a => available.Contains(a.Name)))
                    Sources.Add(new($"{id}/{axis.Name}", $"{name} / {axis.Name}", true));
                for (var i = 0; i < device.Capabilities.ButtonCount; i++)
                    Sources.Add(new($"{id}/B{i}", $"{name} / Button {i + 1}", false));
                for (var i = 0; i < device.Capabilities.PovCount; i++)
                    foreach (var direction in new[] { "Up", "Right", "Down", "Left" })
                        Sources.Add(new($"{id}/H{i}{direction}", $"{name} / Hat {i + 1} {direction}", false));
            }
            catch (Exception ex)
            {
                if (device != null && !devices.Any(d => d.Device == device)) device.Dispose();
                Messages.Add($"{info.ProductName}: {ex.Message}");
            }
        }
    }

    public Dictionary<string, double> Read()
    {
        var values = new Dictionary<string, double>();
        foreach (var (device, id, available) in devices)
        {
            try
            {
                device.Poll();
                var state = device.GetCurrentState();
                foreach (var axis in Axes.Where(a => available.Contains(a.Name))) values[$"{id}/{axis.Name}"] = axis.Read(state);
                for (var i = 0; i < state.Buttons.Length; i++) values[$"{id}/B{i}"] = state.Buttons[i] ? 1 : 0;
                for (var i = 0; i < state.PointOfViewControllers.Length; i++)
                {
                    var angle = state.PointOfViewControllers[i];
                    var sector = angle < 0 ? -1 : ((angle + 2250) / 4500) % 8;
                    values[$"{id}/H{i}Up"] = sector is 7 or 0 or 1 ? 1 : 0;
                    values[$"{id}/H{i}Right"] = sector is 1 or 2 or 3 ? 1 : 0;
                    values[$"{id}/H{i}Down"] = sector is 3 or 4 or 5 ? 1 : 0;
                    values[$"{id}/H{i}Left"] = sector is 5 or 6 or 7 ? 1 : 0;
                }
            }
            catch (SharpDX.SharpDXException)
            {
                // No stale values after unplugging. Output stops if a bound input disappears.
                try { device.Acquire(); } catch (SharpDX.SharpDXException) { }
            }
        }
        return values;
    }

    private void CloseDevices()
    {
        foreach (var item in devices)
        {
            try { item.Device.Unacquire(); } catch (SharpDX.SharpDXException) { }
            item.Device.Dispose();
        }
        devices.Clear();
    }

    public void Dispose() { CloseDevices(); directInput.Dispose(); }
}
