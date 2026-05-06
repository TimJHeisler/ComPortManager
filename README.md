# COM Port Manager

A Windows console utility for viewing and changing serial COM port assignments and default serial settings from a keyboard-driven menu.

The interface is intentionally simple: type a menu number, press Enter, and follow the prompts.

```text
   ______ ____  __  ___   ____             __     __  ___
  / ____// __ \/  |/  /  / __ \____  _____/ /_   /  |/  /___ _____  ____ _____ ____  _____
 / /    / / / / /|_/ /  / /_/ / __ \/ ___/ __/  / /|_/ / __ `/ __ \/ __ `/ __ `/ _ \/ ___/
/ /___ / /_/ / /  / /  / ____/ /_/ / /  / /_   / /  / / /_/ / / / / /_/ / /_/ /  __/ /
\____/ \____/_/  /_/  /_/    \____/_/   \__/  /_/  /_/\__,_/_/ /_/\__,_/\__, /\___/_/
                                                                       /____/
==========================================================================================
  Present ports: 3    Controls: type a menu number and press Enter    Mode: Administrator
==========================================================================================
```

## Features

- Lists present COM ports using Windows SetupAPI, matching Device Manager's present `Ports (COM & LPT)` devices.
- Changes COM port number assignments.
- Changes default serial settings: bits per second, data bits, parity, stop bits, and flow control.
- Uses a Clonezilla-style numbered console menu.
- Automatically requests Administrator elevation when needed.

## Requirements

- Windows
- .NET SDK capable of building `net10.0-windows`
- Administrator rights for changing settings

## Why Administrator Is Required

Listing COM ports does not require Administrator rights, but changing COM assignments and defaults writes to protected Windows registry locations under `HKLM`, including:

```text
HKLM\SYSTEM\CurrentControlSet\Enum\...
HKLM\SYSTEM\CurrentControlSet\Control\COM Name Arbiter\Devices
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Ports
```

Because of that, the app prompts through UAC and relaunches elevated.

## Build

From the repository root:

```powershell
dotnet build .\ComPortManager.csproj
```

The debug executable is created at:

```text
bin\Debug\net10.0-windows\ComPortManager.exe
```

## Run

```powershell
.\bin\Debug\net10.0-windows\ComPortManager.exe
```

Windows will always show a UAC prompt running this, can be worked around via Task manager tasks running as NT\System.

## Menu Options

```text
1. Change COM port number
2. Change bits per second
3. Change data bits
4. Change parity
5. Change stop bits
6. Change flow control
7. Change all serial settings
8. Refresh
0. Exit
```

## COM Port Discovery

The app uses Windows SetupAPI to enumerate currently present devices in the `Ports` device class. This avoids showing stale COM ports that may still exist in registry history but are not currently present in Device Manager.

## Safety Notes

Changing COM port assignments can affect software that expects a device to stay on a specific COM number.

After changing a COM port number, you may need to restart the device, unplug/replug it, or reboot before every Windows component reflects the new assignment.

## Project Structure

```text
ComPortManager.csproj
Program.cs
README.md
```
