# Sharklet (Tailwind flight hardware)

A Windows desktop app that combines DirectInput flight controllers into virtual Xbox 360 controllers and connects the WINCTRL MCDU-32 to Tailwind. Device axes are learned instead of guessed from model names.

## WINCTRL MCDU-32

Open **WINCTRL MCDU** in the remapper, or run `TailwindRemapper.exe --mcdu`. The captain's unit is selected by default. This bridge needs the updated Tailwind client as well as the updated desktop app.

1. Connect the Airbus MCDU-32 over USB and click **Refresh devices** in the MCDU window. It detects VID `4098` with captain PID `BB36`, observer `BB3A`, first-officer `BB3E`, or base ID `BB32`. The command prefix remains `BB32` for every seat. Select the USB screen and button device.
2. Close SimAppPro, SimLogic and WWTStream before enabling USB output. Tailwind writes the screen directly using the same HID display protocol. Running both writers can overwrite the screen. The app reports a conflict instead of closing another application.
3. Keep the default SimAppPro `.wwf` font path, or browse to the MCDU font in your installation. The usual path is `%LOCALAPPDATA%\Programs\SimAppPro\resources\app.asar.unpacked\config\GameExtendDisplay\MCDU\EditSpace\WWT_MCDU\WWT_MCDU.wwf`. Tailwind reads this local file and loads slots 5 and 6 into device RAM. It does not flash firmware, write font flash, modify SimAppPro or distribute its font files. Reconnecting reloads the fonts.
4. Click **Start MCDU bridge**, then **Test screen + lights** to check colors, small text, symbols and annunciators. The test lasts five seconds. Adjust the screen X/Y offsets while stopped if the text needs alignment. Defaults follow SimAppPro's MCDU grid at X 52, Y 37.
5. Start Tailwind in Roblox or Studio and load the aircraft. The screen follows the selected MCDU's page, scratchpad, color, character size, brightness and power state. The aircraft's electrical power and FMGC availability still apply.
6. With **Send keys to Roblox** enabled, all 72 labeled keys control the same unit, including holding CLR to clear the scratchpad. This uses ViGEmBus and one additional Xbox slot. Start the main flight output first if using it too, then start the MCDU bridge. Release keys before connecting or returning focus to Roblox. Chat, text fields, the Roblox menu and focus loss suppress MCDU input.

Screen output works without ViGEmBus. Turn off **Send keys to Roblox** to use only the screen. Turn off both outputs to inspect the log stream and text preview without hardware. MCDU settings save separately to `%LOCALAPPDATA%\TailwindRemapper\mcdu.json`; flight profiles are unchanged. The bridge stops on a USB or button-device disconnect. Reconnect, refresh and restart it.

### Roblox log transport

`McduHardwareController` prints only when a screen or its brightness changes, at most ten snapshots per second per side. Each snapshot has a session ID, sequence, side, timestamp, brightness and 14 rows of text runs. Detaching the aircraft emits a blank screen only if needed. There is no repeating screen heartbeat.

Studio truncates long output lines. Version 2 splits the JSON into UTF-8 chunks of at most 600 bytes, each prefixed with `[Tailwind:MCDU:2]session|sequence|part|count|`. The desktop assembles a complete snapshot before displaying it. Missing, mixed, expired or oversized chunks are discarded. The reader also accepts legacy version 1 records.

The desktop tails local `*_Player_*.log` and `*_Studio_*.log` files in `%LOCALAPPDATA%\Roblox\logs`. It starts at EOF to avoid replaying old flights. Start the bridge before loading the aircraft, or change the in-game MCDU page after starting it. It handles partial UTF-8 lines, rotation and malformed records with bounded buffers. No HTTP or WebSocket connection is needed, and log contents are not uploaded.

An unchanged page stays visible indefinitely. Closing the bridge clears the hardware screen; leaving the aircraft sends an explicit blank frame. If Roblox exits without sending that frame, the last page remains until the bridge stops. To disable export in client code, call `McduHardwareController.setEnabled(false)`. Navigation and scratchpad text remain in Roblox's local logs.

The MCDU uses a separate virtual controller. Version 1.1.1 spaces vertical-axis bits to tolerate Roblox's one-unit offset and keeps trigger data away from its zero deadzone. Its heartbeat refreshes stationary controller values after focus returns; it does not print screen records. Update both the desktop app and Roblox client together, and restart Studio play after syncing the client. Flight controls exclude recognized MCDU pads until they disconnect. The fixed thumb-button pair is an extra identification hint, not a decoder requirement: Roblox can clear held button states when focus changes. The receiver validates the analog packet independently. `McduInputController.getStatus()` reports focus, menu/text-box blocking, device arming and heartbeat age without printing.

### Display protocol and verification

The implementation uses 64-byte HID reports for F0 commands and F2 screen data, and a separate 14-byte report for brightness. F0 loads RAM fonts and the format table. F2 carries the 24 x 14 grid with little-endian color/font indices. Tailwind's custom box, degree, overfly and arrow glyphs map to the MCDU font. The protocol was checked against the installed SimAppPro `mainsrc/GameExtendDisplay/CDU/WWTLCD.js` and `CDU.js`, plus the [Winctrl implementation](https://github.com/landre-cerp/WwDevicesDotnet/blob/main/DisplayBufferFontAndColour.cs) and [MCDU button map](https://github.com/landre-cerp/WwDevicesDotnet/blob/main/Winctrl/Mcdu/KeyboardMap.cs). This is direct USB support, not a SimLogic plugin or a documented SimLogic API integration. The colored test grid was confirmed on a connected captain MCDU, PID `BB36`.

`--self-test` includes log parser/tailer, display packet, font validation and MCDU key encoding checks. Run `lune run scripts/check-mcdu-export.luau` for change-only chunked snapshots, `lune run scripts/check-mcdu-hardware.luau` for the Roblox input receiver and `lune run scripts/check-flight-gamepad.luau` for existing flight controls. Hardware verification on the captain unit confirmed the colored screen, a fresh Studio log frame reaching the display, and physical INIT, A and CLR button mapping. The revised input packet was checked against live Studio gamepad states for all 72 keys. A complete physical button/display session remains a separate check.

### MCDU annunciators

Version 1.1.2 mirrors FAIL, FM, FM1, FM2 and RDY from the selected cockpit unit. ANN LT TEST also lights IND and both halves of MCDU MENU, matching the cockpit; those two indicators remain off in normal operation. The unused line LED remains off. The captain uses AC ESS SHED power and the first officer uses AC 2. ANN LT DIM sets LED brightness to 35 percent, independently of the LCD brightness. Power loss, aircraft detach and bridge shutdown clear the lights.

Lamp-only changes produce a snapshot even when the page stays the same. No periodic log output was added. The frame carries `annunciators`, a 9-bit mask in WINCTRL selector order 08 through 10, and `annunciatorBrightness`, from 0 to 255. Older frames default to all lamps off. USB writes use the [MCDU illumination protocol](https://github.com/landre-cerp/WwDevicesDotnet/blob/main/Winctrl/README.md#illumination), with selector 02 for LED brightness. Unchanged lights do not cause repeated USB writes.

## Run

Run `dist/installer/TailwindRemapper-Setup-1.1.2.exe` on 64-bit Windows 10 or 11. Setup installs for your Windows account, adds a Start menu shortcut and offers a desktop shortcut. The .NET runtime is included. If ViGEmBus is missing, the final page offers to open the bundled official driver wizard, which needs administrator approval. Complete that wizard and restart Windows if requested.

The portable build remains available at `dist/TailwindRemapper.exe`. Keep the app open while playing. It continues reading hardware when Roblox has focus. Upgrades and uninstall preserve saved profiles. Uninstall through Windows Settings > Apps; the shared ViGEmBus driver is left installed.

To share the app, send `dist/installer/Tailwind-Flight-Hardware-1.1.2-Windows.zip`. It contains the self-contained installer, its checksum and `START HERE.txt`. Recipients extract the ZIP and run setup; they do not need this repository or development tools. Each person learns and calibrates their own controllers. The package does not include Roblox or the Tailwind game.

Xbox output requires [ViGEmBus 1.22.0 from its publisher](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0). Use the app's **Official driver download** button, install the driver, and restart Windows if requested. The app does not install drivers. ViGEmBus is retired and no longer maintained; see the [publisher's notice](https://docs.nefarius.at/projects/ViGEm/End-of-Life/). Hardware detection and calibration work without that driver.

1. Connect the stick, throttle and pedals. Click **Refresh devices**.
2. Select **LX · Roll**, click **Learn selected input**, and move the stick left/right. Repeat for pitch, rudder and each throttle lever using the table below. All Xbox buttons can also be assigned to a hardware button or hat direction.
3. Check the live raw and output values. Capture the lowest raw endpoint as **Min**, neutral as **Center**, and the highest raw endpoint as **Max**. Triggers ignore Center. Use **Invert** when needed. Full back stick should produce positive LY, right stick positive LX, right pedal positive RX, and forward throttle positive LT/RT.
4. Click **Save profile**, then **Start Xbox output**. Profile bindings also save when starting. Use **Windows controller test** to check the virtual Xbox controller.
5. Start Tailwind in Studio. In the existing plane physics test panel, enable **Xbox flight controls**. For this panel the virtual controller must be Roblox's Gamepad1. Disconnect other Xbox pads before starting Roblox if necessary.

| Hardware | Xbox output | Tailwind meaning |
| --- | --- | --- |
| Sidestick left/right | LX / Thumbstick1.X | Roll |
| Sidestick forward/back | LY / Thumbstick1.Y | Pitch, positive when pulled back |
| Rudder left/right | RX / Thumbstick2.X | Rudder / nose steering |
| Both toe brakes | RY / Thumbstick2.Y | Combined braking, 0–1 |
| Left throttle | LT / ButtonL2.Z | Left throttle, 0–1 |
| Right throttle | RT / ButtonR2.Z | Right throttle, 0–1 |

For a single throttle lever, bind that axis to both LT and RT. Unassigned triggers remain zero. Both throttles remain independent when using combined toe brakes. The tradeoff is losing differential braking and the spare RY axis.

## Flaps, spoilers and parking brake

1. Select **Flaps · Lever**, click **Learn selected input**, and move the flap lever. Capture Min and Max at the lowest and highest raw readings. Use Invert so retracted reads 0 and FULL reads 1. Keep Deadzone at 0 and Curve at 1 for evenly spaced detents. Tailwind selects flap positions 0, 1, 2, 3 and FULL across the lever's travel.
2. Bind **Spoilers · Lever** the same way. Retracted should read 0 and fully extended should read 1. The lever commands the aircraft's speedbrake surfaces through the existing hydraulic model.
3. Select **Parking brake · Switch**, click Learn, and turn the switch on. Output should read 1 when set and 0 when released. Use Invert if the hardware reports the opposite. This expects a maintained switch, not a momentary toggle button.
4. If the spoiler lever has a separate arm switch, bind **Spoilers arm · Switch**. Output 1 arms the existing ground-spoiler SimVar; output 0 disarms it. This does not add automatic ground-spoiler deployment logic.
5. Save and start output before launching Roblox. Use the updated Tailwind client as well as the updated remapper.

When any of these rows is assigned, the app creates a second virtual Xbox controller. Tailwind discovers it automatically among the other gamepad slots. The first controller still carries the stick, throttles, pedals and existing button assignments. Keep other Xbox controllers disconnected so the primary flight controller gets Gamepad1. If Windows puts the auxiliary controller in Gamepad1, Tailwind ignores it instead of treating its levers as flight-stick input; stop output, disconnect other pads, restart output and then restart Roblox.

Unassigned controls leave their cockpit settings alone. Stationary levers and switches allow mouse adjustments, and moving them takes control again. Disconnecting preserves flap, spoiler and parking-brake positions. Reconnecting synchronizes assigned controls to the hardware again. Older profiles gain the new unassigned rows while retaining their existing bindings.

## Landing gear toggle

Select **Landing gear · Toggle**, click **Learn selected input**, and press a momentary hardware button or hat direction. Save the profile and start Xbox output. One press moves the cockpit lever between DOWN and UP; holding the button does not repeat. A button held when the aircraft or controller connects must be released before it can toggle. The cockpit lever interlock still blocks retraction on the ground. A blocked press is consumed; release and press again after the lever unlocks.

This row uses the auxiliary controller's A button. Bit 10 of the extended RY packet marks it as assigned, including when no other auxiliary controls are bound. The game detects the press edge. Existing profiles load with this row unassigned. Use both the updated remapper and updated Tailwind client. Close the old app yourself before replacing or reinstalling it, then launch the new build and bind this row.

## Engine master and ignition switches

Bind **ENG MASTER 1 · Switch** and **ENG MASTER 2 · Switch** by clicking Learn and moving each switch to ON. Output should be 1 at ON and 0 at OFF; use Invert if reversed.

For a three-position ignition selector, learn **Ignition · CRANK** while moving from NORM to CRANK, then return to NORM and learn **Ignition · IGN/START** while moving to IGN/START. NORM is automatic when neither contact is active. You can bind just IGN/START for a two-position NORM/START switch. If both contacts report active, IGN/START takes priority. Leave both rows unassigned to retain cockpit-only control. These bindings operate the existing engine master commands and mode selector; the aircraft's normal start requirements still apply.

The auxiliary layout uses LX for flaps, LY for spoilers, LT for parking brake and RT for spoiler arming. RX alternates between -0.5 and -0.625 every 250 milliseconds to identify the controller after focus changes. RY encodes which controls are assigned as `-(mask + 1) / 32`, with flap/spoiler/parking-brake/arming bits 1/2/4/8. Tailwind claims this controller's inputs so they do not move the Roblox character or camera.

The same heartbeat changes each other assigned component by one Xbox output unit so Studio refreshes stationary controls after regaining focus. Flap detents and switch states remain unchanged, and the client snaps the spoiler endpoints back to exactly 0 and 1.

With engine switches or landing gear assigned, RX uses +0.5/+0.625 and RY encodes `-(packed + 1) / 2048`. The low four bits retain the assignments above. Bits 4–5 and 6–7 carry masters 1 and 2 (0 unassigned, 1 off, 2 on); bits 8–9 carry ignition (0 unassigned, 1 CRANK, 2 NORM, 3 IGN/START). The client also accepts the original auxiliary layout.

## Rudder and toe brakes

1. Bind **RX · Rudder** to the sliding rudder axis using Learn.
2. Leave the main **RY** hardware binding unassigned. Bind **Brake L → RY** to the left toe pedal and **Brake R → RY** to the right toe pedal.
3. Capture each brake's Min and Max. Use Invert so a released pedal reads 0 and a fully pressed pedal reads 1. Brake rows ignore Center, just like triggers.
4. Save and start Xbox output. The RY output meter shows the stronger of the two brake inputs. Either pedal applies both wheel-brake commands equally, even when the other pedal is released.

InputController writes `a320/systems/flightControls/rudderCommand` with 0.5 centered, and `a320/flightControls/breakLeft` / `a320/flightControls/breakRight` from 0 released to 1 fully pressed. These are input SimVars for testing; the current plane physics test has no wheel-brake force model. Disconnecting centers rudder and releases both brake commands. Existing profiles load with two new unassigned brake rows while keeping their original bindings.

For now the plane test applies the average of both throttles to its existing total-thrust force and maps rudder to nose steering. Its current range is 0–100,000 lbf and ±75 degrees. Set throttle idle to zero before enabling. This is the existing ground-physics test, not an aerodynamic flight model. The client InputController also writes the current aircraft's throttle and sidestick SimVars automatically. It maps physical idle at 0.286 to cockpit idle at 0.305 while preserving both endpoints. Stationary hardware levers do not overwrite mouse throttle adjustments. PitchCommand and RollCommand use 0–1 with neutral at 0.5. Disconnecting centers the sidestick and leaves throttle positions unchanged. This updates cockpit state; it does not add aerodynamic flight physics.

The Xbox toggle defaults off. Turning it off, losing Roblox focus, typing into a text box, blocked controls, or disconnecting the virtual controller resets test thrust and steering to zero. A manual thrust/steering adjustment also turns Xbox control off. The test claims thumbsticks and triggers while enabled so they do not move the default Roblox camera or character.

## Volanta

**Connect Volanta** sends Tailwind flights to the Volanta desktop app, replacing `TailwindTools/volanta-bridge`. It reads the game's `[Tailwind:Volanta:1]` lines from the newest Roblox client logs and passes them to Volanta's local plugin socket, `ws://127.0.0.1:6745/plugin`, retrying every 3 s until Volanta is open. Volanta lists the flight as FlightGear. Only the aircraft title and model are sent, never a type or registration, so the aircraft you pick in Volanta is kept.

## Profiles and reconnecting

Profiles save to `%LOCALAPPDATA%\TailwindRemapper\profile.json`. Import and export buttons support separate profiles. Device instance GUIDs identify bindings. Reconnect to the same USB ports when possible. If Windows gives a device a new GUID, learn its bindings again. Refresh preserves missing bindings instead of silently assigning another device.

If any assigned input disappears while running, the app neutralizes and disconnects the virtual controller. Reconnect the hardware, refresh and start again. Stopping or closing the app also disconnects output. Xbox/XInput devices are excluded as sources to prevent feedback. Mapping edits are disabled while running. This app does not hide or modify physical devices.

## Build and checks

Requires a .NET 8 SDK on Windows:

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
$test = Start-Process -FilePath .\dist\TailwindRemapper.exe -ArgumentList '--self-test' -PassThru -Wait -WindowStyle Hidden
Get-Content .\dist\self-test.txt
```

To build the installer, also obtain [Inno Setup 6.3 or newer](https://jrsoftware.org/isdl.php) and run this from `TailwindTools/TailwindRemapper`:

```powershell
.\build-installer.ps1
# Optional compiler, SDK and release version overrides:
.\build-installer.ps1 -Version 1.0.1 -DotNetPath 'C:\path\to\dotnet.exe' -InnoCompiler 'C:\path\to\ISCC.exe'
```

The build publishes from source, runs the C# self-tests, downloads the official ViGEmBus 1.22.0 installer and checks its pinned SHA-256 before packaging. It writes the setup executable, a `.sha256` file and the shareable Windows ZIP to `dist/installer`. Only the app, documentation, notices and driver installer are packaged; local profiles and test output are excluded. The build needs internet access on its first run. The finished installer works offline. Release installers are unsigned unless signed separately.

Silent setup supports `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`. It installs only the app and skips driver setup and app launch; provision ViGEmBus separately for unattended deployment.

### Signing releases

Signing requires a publicly trusted code-signing certificate or an activated signing service. A self-signed certificate will not remove warnings on other people's PCs. New signed releases can still show SmartScreen warnings while reputation builds. See [Microsoft's explanation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).

For Microsoft Artifact Signing, first complete the [account and identity setup](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart). This requires an Azure subscription, a paid signing account, identity validation and a **Public Trust** certificate profile. Assign your signing identity the **Artifact Signing Certificate Profile Signer** role. Microsoft handles identity verification; do not put identity documents or account credentials in this repository.

Install the [Artifact Signing Client Tools](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations), which provide a compatible SignTool and signing library. Copy `signing-metadata.example.json` to `signing-metadata.local.json` and replace all placeholders with the account endpoint, account name and profile name from Azure. The local file is ignored by Git and excluded from the installer. Authenticate using one of the client tools' supported Azure credentials, such as `az login` when Azure CLI is installed.

Pass the signing command to the build, using your installed tool paths and an absolute metadata path. Keep the PowerShell string single-quoted so Inno Setup receives its literal `$q` and `$f` placeholders:

```powershell
$sign = '$qC:\path\to\signtool.exe$q sign /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib $qC:\path\to\Azure.CodeSigning.Dlib.dll$q /dmdf $qC:\path\to\signing-metadata.local.json$q $f'
.\build-installer.ps1 -SignToolCommand $sign
```

The build signs the remapper, setup executable and uninstaller. Before creating the checksum and shareable ZIP, it checks that the remapper and setup have trusted, timestamped signatures. Signing failures stop the build. The signing command can also use a certificate in the Windows certificate store. Do not put passwords or private keys in the command. Omitting `-SignToolCommand` still creates an unsigned build.

From the repository root, run `lune run scripts/check-flight-gamepad.luau`, `lune run scripts/check-input-controller.luau` and `lune run scripts/check-hydraulic.luau`. The C# checks cover calibration, inversion, auxiliary output, invalid profiles, legacy upgrades and profile round-tripping. The Luau checks cover both controller layouts, cockpit updates, disconnects and hydraulic speedbrake operation. A real hardware and Roblox session is still required to verify model-specific directions, controller discovery and end-to-end input.

Dependencies: [SharpDX.DirectInput 4.2.0](https://github.com/sharpdx/SharpDX), [Nefarius.ViGEm.Client 1.21.256](https://github.com/nefarius/ViGEm.NET), and [HIDSharp 2.6.4](https://software.seekye.com/hidsharp). SharpDX and ViGEm.NET are archived. See `THIRD-PARTY-NOTICES.txt` alongside the executable for dependency licenses.
