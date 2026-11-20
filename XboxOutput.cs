using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace TailwindRemapper;

public sealed class XboxOutput : IDisposable
{
    private readonly ViGEmClient client;
    private readonly IXbox360Controller controller;
    private static readonly Dictionary<string, Xbox360Button> Buttons = new()
    {
        ["A"] = Xbox360Button.A, ["B"] = Xbox360Button.B, ["X"] = Xbox360Button.X, ["Y"] = Xbox360Button.Y,
        ["LB"] = Xbox360Button.LeftShoulder, ["RB"] = Xbox360Button.RightShoulder,
        ["Back"] = Xbox360Button.Back, ["Start"] = Xbox360Button.Start,
        ["LS"] = Xbox360Button.LeftThumb, ["RS"] = Xbox360Button.RightThumb,
        ["Up"] = Xbox360Button.Up, ["Down"] = Xbox360Button.Down,
        ["Left"] = Xbox360Button.Left, ["Right"] = Xbox360Button.Right
    };

    public XboxOutput()
    {
        client = new ViGEmClient();
        try
        {
            controller = client.CreateXbox360Controller();
            controller.AutoSubmitReport = false;
            controller.Connect();
        }
        catch { client.Dispose(); throw; }
    }

    public void Send(Dictionary<string, double> values)
    {
        controller.ResetReport();
        foreach (var (target, value) in values)
        {
            var axis = target switch
            {
                "LX" => Xbox360Axis.LeftThumbX, "LY" => Xbox360Axis.LeftThumbY,
                "RX" => Xbox360Axis.RightThumbX, "RY" => Xbox360Axis.RightThumbY, _ => null
            };
            if (axis != null) controller.SetAxisValue(axis, (short)Math.Round(value * (value < 0 ? 32768 : 32767)));
            else if (target is "LT" or "RT")
                controller.SetSliderValue(target == "LT" ? Xbox360Slider.LeftTrigger : Xbox360Slider.RightTrigger, (byte)Math.Round(value * 255));
            else if (Buttons.TryGetValue(target, out var button)) controller.SetButtonState(button, value > 0.5);
        }
        controller.SubmitReport();
    }

    public void Dispose()
    {
        try { controller.ResetReport(); controller.SubmitReport(); }
        finally
        {
            try { controller.Disconnect(); }
            finally { client.Dispose(); }
        }
    }
}
