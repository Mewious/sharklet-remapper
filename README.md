# Sharklet

Use your joystick, throttle and pedals in Tailwind.

## Install

1. Run `Sharklet-Setup.exe`.
2. Install the ViGEmBus driver when setup asks, then restart if Windows asks.

## Use

1. Plug in your controls and click **Refresh devices**.
2. Pick a row, click **Learn selected input**, then move that control.
3. Click **Save profile**, then **Start Xbox output**.
4. Keep Sharklet open while you play.

## Extras

- **Connect Volanta** tracks your Tailwind flights in Volanta. It keeps the aircraft you picked in Volanta.
- **WINCTRL MCDU** runs a WINCTRL MCDU-32 with Tailwind.

## Build

Needs the .NET 8 SDK.

```powershell
dotnet build -c Release
```

To build the installer, install Inno Setup 6 and run `build-installer.ps1`.
