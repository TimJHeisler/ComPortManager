using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

const string PortsKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Ports";
const string EnumKeyPath = @"SYSTEM\CurrentControlSet\Enum";
const string ComNameArbiterDevicesKeyPath = @"SYSTEM\CurrentControlSet\Control\COM Name Arbiter\Devices";
const int DigcfPresent = 0x00000002;
const int SpdrpDevicedesc = 0x00000000;
const int SpdrpFriendlyname = 0x0000000C;
const int SpdrpService = 0x00000004;

EnsureAdministrator();

while (true)
{
    var ports = GetComPorts();

    Console.Clear();
    DrawTitleScreen(ports.Count);

    PrintPorts(ports);

    Console.WriteLine();
    Console.WriteLine("1. Change COM port number");
    Console.WriteLine("2. Change bits per second");
    Console.WriteLine("3. Change data bits");
    Console.WriteLine("4. Change parity");
    Console.WriteLine("5. Change stop bits");
    Console.WriteLine("6. Change flow control");
    Console.WriteLine("7. Change all serial settings");
    Console.WriteLine("8. Refresh");
    Console.WriteLine("0. Exit");

    switch (ReadMenuChoice("Select an option", 0, 8))
    {
        case 0:
            return;
        case 1:
            ChangeComPortNumber(ports);
            break;
        case 2:
            ChangeSetting(ports, SerialSetting.BaudRate);
            break;
        case 3:
            ChangeSetting(ports, SerialSetting.DataBits);
            break;
        case 4:
            ChangeSetting(ports, SerialSetting.Parity);
            break;
        case 5:
            ChangeSetting(ports, SerialSetting.StopBits);
            break;
        case 6:
            ChangeSetting(ports, SerialSetting.FlowControl);
            break;
        case 7:
            ChangeAllSettings(ports);
            break;
    }
}

static void EnsureAdministrator()
{
    if (OperatingSystem.IsWindows())
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            return;
        }
    }

    Console.WriteLine("This tool must run as Administrator to edit COM port registry settings.");

    var exePath = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(exePath))
    {
        PressEnterToContinue();
        Environment.Exit(1);
    }

    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            Verb = "runas"
        };

        if (Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && Environment.GetCommandLineArgs().Length > 0)
        {
            startInfo.Arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(QuoteArgument));
        }

        Process.Start(startInfo);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not restart as Administrator: {ex.Message}");
        PressEnterToContinue();
    }

    Environment.Exit(0);
}

static List<ComPortInfo> GetComPorts()
{
    var ports = new Dictionary<string, ComPortInfo>(StringComparer.OrdinalIgnoreCase);
    var portsClassGuid = new Guid("4d36e978-e325-11ce-bfc1-08002be10318");
    var deviceInfoSet = SetupDiGetClassDevs(ref portsClassGuid, null, IntPtr.Zero, DigcfPresent);

    if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
    {
        return [];
    }

    try
    {
        for (var index = 0; ; index++)
        {
            var deviceInfoData = new SpDevinfoData
            {
                CbSize = Marshal.SizeOf<SpDevinfoData>()
            };

            if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfoData))
            {
                if (Marshal.GetLastWin32Error() == 259)
                {
                    break;
                }

                continue;
            }

            var instanceId = GetDeviceInstanceId(deviceInfoSet, ref deviceInfoData);
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                continue;
            }

            var deviceKeyPath = $@"{EnumKeyPath}\{instanceId}";
            using var deviceKey = Registry.LocalMachine.OpenSubKey(deviceKeyPath, writable: false);
            using var parametersKey = Registry.LocalMachine.OpenSubKey($@"{deviceKeyPath}\Device Parameters", writable: false);
            var friendlyName = GetDeviceRegistryProperty(deviceInfoSet, ref deviceInfoData, SpdrpFriendlyname)
                ?? ReadString(deviceKey, "FriendlyName");
            var deviceDesc = CleanDeviceDescription(GetDeviceRegistryProperty(deviceInfoSet, ref deviceInfoData, SpdrpDevicedesc)
                ?? ReadString(deviceKey, "DeviceDesc"));
            var service = GetDeviceRegistryProperty(deviceInfoSet, ref deviceInfoData, SpdrpService)
                ?? ReadString(deviceKey, "Service")
                ?? string.Empty;
            var portName = ReadString(parametersKey, "PortName");
            var dosDeviceName = ReadString(parametersKey, "DosDeviceName");

            var comName = ExtractComName(friendlyName)
                ?? ExtractComName(dosDeviceName)
                ?? ExtractComName(portName);

            if (!IsComName(comName))
            {
                continue;
            }

            comName = comName!.ToUpperInvariant();
            var settings = ReadPortSettings(comName);
            AddOrUpdatePort(ports, new ComPortInfo
            {
                ComName = comName,
                Name = !string.IsNullOrWhiteSpace(friendlyName) ? friendlyName! : deviceDesc ?? comName,
                DeviceKeyName = deviceKey?.Name ?? $@"HKEY_LOCAL_MACHINE\{deviceKeyPath}",
                DeviceParametersKeyName = parametersKey?.Name,
                Service = service,
                IsActive = true,
                BaudRate = settings.BaudRate,
                DataBits = settings.DataBits,
                Parity = settings.Parity,
                StopBits = settings.StopBits,
                FlowControl = "Unknown"
            });
        }
    }
    finally
    {
        SetupDiDestroyDeviceInfoList(deviceInfoSet);
    }

    return ports.Values
        .OrderBy(port => ComNumber(port.ComName))
        .ThenBy(port => port.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static void AddOrUpdatePort(Dictionary<string, ComPortInfo> ports, ComPortInfo port)
{
    port.ComName = port.ComName.ToUpperInvariant();

    if (!ports.TryGetValue(port.ComName, out var existing))
    {
        ports.Add(port.ComName, port);
        return;
    }

    if (existing.Name == existing.ComName && port.Name != port.ComName)
    {
        existing.Name = port.Name;
    }

    existing.IsActive |= port.IsActive;
    existing.DeviceKeyName ??= port.DeviceKeyName;
    existing.DeviceParametersKeyName ??= port.DeviceParametersKeyName;
    existing.Service = string.IsNullOrWhiteSpace(existing.Service) ? port.Service : existing.Service;
}

static void DrawTitleScreen(int portCount)
{
    Console.Title = "COM Port Manager";
    var originalForeground = Console.ForegroundColor;

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(@"   ______ ____  __  ___   ____             __     __  ___                                  ");
    Console.WriteLine(@"  / ____// __ \/  |/  /  / __ \____  _____/ /_   /  |/  /___ _____  ____ _____ ____  _____");
    Console.WriteLine(@" / /    / / / / /|_/ /  / /_/ / __ \/ ___/ __/  / /|_/ / __ `/ __ \/ __ `/ __ `/ _ \/ ___/");
    Console.WriteLine(@"/ /___ / /_/ / /  / /  / ____/ /_/ / /  / /_   / /  / / /_/ / / / / /_/ / /_/ /  __/ /    ");
    Console.WriteLine(@"\____/ \____/_/  /_/  /_/    \____/_/   \__/  /_/  /_/\__,_/_/ /_/\__,_/\__, /\___/_/     ");
    Console.WriteLine(@"                                                                       /____/              ");

    Console.ForegroundColor = ConsoleColor.DarkCyan;
    Console.WriteLine("==========================================================================================");
    Console.ForegroundColor = ConsoleColor.White;
    Console.Write("  Present ports: ");
    Console.ForegroundColor = portCount > 0 ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write(portCount);
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine("    Controls: type a menu number and press Enter    Mode: Administrator");
    Console.ForegroundColor = ConsoleColor.DarkCyan;
    Console.WriteLine("==========================================================================================\n");

    Console.ForegroundColor = originalForeground;
}

static void PrintPorts(IReadOnlyList<ComPortInfo> ports)
{
    if (ports.Count == 0)
    {
        Console.WriteLine("No COM ports were found.");
        return;
    }

    Console.WriteLine(" #  COM     Status   Settings                    Name");
    Console.WriteLine("--  ------  -------  --------------------------  ------------------------------");

    for (var i = 0; i < ports.Count; i++)
    {
        var port = ports[i];
        var settings = $"{port.BaudRate},{ParityToRegistry(port.Parity)},{port.DataBits},{port.StopBits}";
        Console.WriteLine($"{i + 1,2}. {port.ComName,-6}  {(port.IsActive ? "Present" : "Saved"),-7}  {settings,-26}  {port.Name}");
    }
}

static void ChangeComPortNumber(IReadOnlyList<ComPortInfo> ports)
{
    var port = SelectPort(ports);
    if (port is null)
    {
        return;
    }

    Console.WriteLine($"\nSelected: {port.Name} [{port.ComName}]");
    var newCom = ReadComName("Enter the new COM port number, like COM7");
    if (newCom.Equals(port.ComName, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("No change needed.");
        PressEnterToContinue();
        return;
    }

    var currentPorts = GetComPorts();
    var existing = currentPorts.FirstOrDefault(item => item.ComName.Equals(newCom, StringComparison.OrdinalIgnoreCase));
    if (existing is not null)
    {
        Console.WriteLine($"Warning: {newCom} already exists: {existing.Name}");
        if (!ReadYesNo("Continue anyway"))
        {
            return;
        }
    }

    try
    {
        RenameComPort(port, newCom);
        Console.WriteLine($"Changed {port.ComName} to {newCom}.");
        Console.WriteLine("Restart the device, unplug/replug it, or reboot if Windows still shows the old number.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to change COM port number: {ex.Message}");
    }

    PressEnterToContinue();
}

static void RenameComPort(ComPortInfo port, string newCom)
{
    var oldCom = port.ComName.ToUpperInvariant();
    newCom = newCom.ToUpperInvariant();

    var settings = ReadPortSettings(oldCom);
    WritePortSettings(newCom, settings);
    DeletePortSettings(oldCom);

    if (!string.IsNullOrWhiteSpace(port.DeviceParametersKeyName))
    {
        using var parametersKey = Registry.LocalMachine.OpenSubKey(ToLocalMachineRelativePath(port.DeviceParametersKeyName), writable: true);
        parametersKey?.SetValue("PortName", newCom, RegistryValueKind.String);

        if (!string.IsNullOrWhiteSpace(ReadString(parametersKey, "DosDeviceName")))
        {
            parametersKey?.SetValue("DosDeviceName", newCom, RegistryValueKind.String);
        }
    }

    if (!string.IsNullOrWhiteSpace(port.DeviceKeyName))
    {
        using var deviceKey = Registry.LocalMachine.OpenSubKey(ToLocalMachineRelativePath(port.DeviceKeyName), writable: true);
        var friendlyName = ReadString(deviceKey, "FriendlyName");
        if (!string.IsNullOrWhiteSpace(friendlyName))
        {
            var newFriendlyName = Regex.Replace(friendlyName, @"\(COM\d+\)", $"({newCom})", RegexOptions.IgnoreCase);
            deviceKey?.SetValue("FriendlyName", newFriendlyName, RegistryValueKind.String);
        }
    }

    MoveComNameArbiterDeviceValue(oldCom, newCom);
}

static void MoveComNameArbiterDeviceValue(string oldCom, string newCom)
{
    using var devicesKey = Registry.LocalMachine.OpenSubKey(ComNameArbiterDevicesKeyPath, writable: true);
    if (devicesKey is null)
    {
        return;
    }

    var oldValue = devicesKey.GetValue(oldCom);
    if (oldValue is not null)
    {
        devicesKey.SetValue(newCom, oldValue, devicesKey.GetValueKind(oldCom));
        devicesKey.DeleteValue(oldCom, throwOnMissingValue: false);
    }
}

static void ChangeSetting(IReadOnlyList<ComPortInfo> ports, SerialSetting setting)
{
    var port = SelectPort(ports);
    if (port is null)
    {
        return;
    }

    var settings = ReadPortSettings(port.ComName);
    var flowControl = FlowControl.None;

    switch (setting)
    {
        case SerialSetting.BaudRate:
            settings.BaudRate = ReadBaudRate();
            break;
        case SerialSetting.DataBits:
            settings.DataBits = ReadDataBits();
            break;
        case SerialSetting.Parity:
            settings.Parity = ReadParity();
            break;
        case SerialSetting.StopBits:
            settings.StopBits = ReadStopBits();
            break;
        case SerialSetting.FlowControl:
            flowControl = ReadFlowControl();
            break;
    }

    ApplySettings(port.ComName, settings, setting == SerialSetting.FlowControl ? flowControl : null);
}

static void ChangeAllSettings(IReadOnlyList<ComPortInfo> ports)
{
    var port = SelectPort(ports);
    if (port is null)
    {
        return;
    }

    var settings = new PortSettings
    {
        BaudRate = ReadBaudRate(),
        DataBits = ReadDataBits(),
        Parity = ReadParity(),
        StopBits = ReadStopBits()
    };
    var flowControl = ReadFlowControl();

    ApplySettings(port.ComName, settings, flowControl);
}

static void ApplySettings(string comName, PortSettings settings, FlowControl? flowControl)
{
    try
    {
        WritePortSettings(comName, settings);
        Console.WriteLine($"Saved default settings for {comName}: {settings.BaudRate},{ParityToRegistry(settings.Parity)},{settings.DataBits},{settings.StopBits}");

        if (flowControl is not null)
        {
            var modeResult = RunModeCommand(comName, settings, flowControl.Value);
            if (modeResult.ExitCode == 0)
            {
                Console.WriteLine($"Applied flow control to the active port session: {flowControl}");
            }
            else
            {
                Console.WriteLine("Saved baud/parity/data/stop defaults, but Windows could not apply flow control to the active session.");
                Console.WriteLine(modeResult.Output.Trim());
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to apply settings: {ex.Message}");
    }

    PressEnterToContinue();
}

static CommandResult RunModeCommand(string comName, PortSettings settings, FlowControl flowControl)
{
    var parity = ParityToMode(settings.Parity);
    var stop = settings.StopBits;
    var flowArgs = flowControl switch
    {
        FlowControl.None => "xon=off octs=off odsr=off rts=on dtr=on",
        FlowControl.XonXoff => "xon=on octs=off odsr=off rts=on dtr=on",
        FlowControl.Hardware => "xon=off octs=on odsr=off rts=hs dtr=on",
        _ => "xon=off octs=off odsr=off rts=on dtr=on"
    };

    var arguments = $"/c mode {comName}: baud={settings.BaudRate} parity={parity} data={settings.DataBits} stop={stop} {flowArgs}";
    var startInfo = new ProcessStartInfo("cmd.exe", arguments)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    using var process = Process.Start(startInfo);
    if (process is null)
    {
        return new CommandResult(1, "Could not start mode command.");
    }

    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit();
    return new CommandResult(process.ExitCode, output);
}

static ComPortInfo? SelectPort(IReadOnlyList<ComPortInfo> ports)
{
    if (ports.Count == 0)
    {
        Console.WriteLine("No COM ports were found.");
        PressEnterToContinue();
        return null;
    }

    Console.WriteLine();
    PrintPorts(ports);
    var index = ReadMenuChoice("Select a COM port", 1, ports.Count) - 1;
    return ports[index];
}

static PortSettings ReadPortSettings(string comName)
{
    using var portsKey = Registry.LocalMachine.OpenSubKey(PortsKeyPath);
    var value = portsKey?.GetValue($"{comName.ToUpperInvariant()}:") as string;
    var parts = (value ?? "9600,n,8,1").Split(',', StringSplitOptions.TrimEntries);

    return new PortSettings
    {
        BaudRate = parts.Length > 0 && int.TryParse(parts[0], out var baudRate) ? baudRate : 9600,
        Parity = parts.Length > 1 ? RegistryToParity(parts[1]) : Parity.None,
        DataBits = parts.Length > 2 && int.TryParse(parts[2], out var dataBits) ? dataBits : 8,
        StopBits = parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3] : "1"
    };
}

static void WritePortSettings(string comName, PortSettings settings)
{
    using var portsKey = Registry.LocalMachine.OpenSubKey(PortsKeyPath, writable: true)
        ?? Registry.LocalMachine.CreateSubKey(PortsKeyPath, writable: true);
    portsKey.SetValue($"{comName.ToUpperInvariant()}:", $"{settings.BaudRate},{ParityToRegistry(settings.Parity)},{settings.DataBits},{settings.StopBits}", RegistryValueKind.String);
}

static void DeletePortSettings(string comName)
{
    using var portsKey = Registry.LocalMachine.OpenSubKey(PortsKeyPath, writable: true);
    portsKey?.DeleteValue($"{comName.ToUpperInvariant()}:", throwOnMissingValue: false);
}

static int ReadBaudRate()
{
    int[] rates = [110, 300, 600, 1200, 2400, 4800, 9600, 14400, 19200, 38400, 57600, 115200, 128000, 256000];
    Console.WriteLine("\nBits per second:");
    for (var i = 0; i < rates.Length; i++)
    {
        Console.WriteLine($"{i + 1}. {rates[i]}");
    }
    Console.WriteLine($"{rates.Length + 1}. Custom");

    var choice = ReadMenuChoice("Select bits per second", 1, rates.Length + 1);
    if (choice <= rates.Length)
    {
        return rates[choice - 1];
    }

    while (true)
    {
        Console.Write("Enter custom bits per second: ");
        if (int.TryParse(Console.ReadLine(), out var baudRate) && baudRate > 0)
        {
            return baudRate;
        }
        Console.WriteLine("Enter a positive number.");
    }
}

static int ReadDataBits()
{
    Console.WriteLine("\nData bits:");
    Console.WriteLine("1. 5");
    Console.WriteLine("2. 6");
    Console.WriteLine("3. 7");
    Console.WriteLine("4. 8");

    return ReadMenuChoice("Select data bits", 1, 4) + 4;
}

static Parity ReadParity()
{
    Console.WriteLine("\nParity:");
    Console.WriteLine("1. None");
    Console.WriteLine("2. Odd");
    Console.WriteLine("3. Even");
    Console.WriteLine("4. Mark");
    Console.WriteLine("5. Space");

    return (Parity)(ReadMenuChoice("Select parity", 1, 5) - 1);
}

static string ReadStopBits()
{
    Console.WriteLine("\nStop bits:");
    Console.WriteLine("1. 1");
    Console.WriteLine("2. 1.5");
    Console.WriteLine("3. 2");

    return ReadMenuChoice("Select stop bits", 1, 3) switch
    {
        1 => "1",
        2 => "1.5",
        _ => "2"
    };
}

static FlowControl ReadFlowControl()
{
    Console.WriteLine("\nFlow control:");
    Console.WriteLine("1. None");
    Console.WriteLine("2. Xon / Xoff");
    Console.WriteLine("3. Hardware");

    return (FlowControl)(ReadMenuChoice("Select flow control", 1, 3) - 1);
}

static int ReadMenuChoice(string prompt, int min, int max)
{
    while (true)
    {
        Console.Write($"{prompt} [{min}-{max}]: ");
        if (int.TryParse(Console.ReadLine(), out var choice) && choice >= min && choice <= max)
        {
            return choice;
        }

        Console.WriteLine("Invalid selection.");
    }
}

static string ReadComName(string prompt)
{
    while (true)
    {
        Console.Write($"{prompt}: ");
        var value = Console.ReadLine()?.Trim().ToUpperInvariant();
        if (IsComName(value))
        {
            return value!;
        }

        Console.WriteLine("Enter a COM port in the form COM1, COM2, COM10, etc.");
    }
}

static bool ReadYesNo(string prompt)
{
    while (true)
    {
        Console.Write($"{prompt} [y/n]: ");
        var value = Console.ReadLine()?.Trim();
        if (string.Equals(value, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "n", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }
}

static void PressEnterToContinue()
{
    Console.WriteLine("\nPress Enter to continue...");
    Console.ReadLine();
}

static string QuoteArgument(string argument)
{
    return argument.Contains(' ') || argument.Contains('"')
        ? $"\"{argument.Replace("\"", "\\\"")}\""
        : argument;
}

static string? ReadString(RegistryKey? key, string valueName)
{
    return key?.GetValue(valueName) as string;
}

static string? ExtractComName(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var match = Regex.Match(value, @"COM\d+", RegexOptions.IgnoreCase);
    return match.Success ? match.Value.ToUpperInvariant() : null;
}

static bool IsComName(string? value)
{
    return !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, @"^COM\d+$", RegexOptions.IgnoreCase);
}

static int ComNumber(string comName)
{
    return int.TryParse(comName[3..], out var number) ? number : int.MaxValue;
}

static string ToLocalMachineRelativePath(string fullKeyName)
{
    const string prefix = @"HKEY_LOCAL_MACHINE\";
    return fullKeyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        ? fullKeyName[prefix.Length..]
        : fullKeyName;
}

static string? CleanDeviceDescription(string? deviceDescription)
{
    if (string.IsNullOrWhiteSpace(deviceDescription))
    {
        return null;
    }

    var parts = deviceDescription.Split(';');
    return parts.Length > 1 ? parts[^1] : deviceDescription;
}

static string? GetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData)
{
    var buffer = new StringBuilder(512);
    if (SetupDiGetDeviceInstanceId(deviceInfoSet, ref deviceInfoData, buffer, buffer.Capacity, out var requiredSize))
    {
        return buffer.ToString();
    }

    if (requiredSize <= buffer.Capacity)
    {
        return null;
    }

    buffer = new StringBuilder(requiredSize);
    return SetupDiGetDeviceInstanceId(deviceInfoSet, ref deviceInfoData, buffer, buffer.Capacity, out _)
        ? buffer.ToString()
        : null;
}

static string? GetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, int property)
{
    var buffer = new byte[1024];
    if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref deviceInfoData, property, out _, buffer, buffer.Length, out var requiredSize))
    {
        if (requiredSize <= buffer.Length)
        {
            return null;
        }

        buffer = new byte[requiredSize];
        if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref deviceInfoData, property, out _, buffer, buffer.Length, out _))
        {
            return null;
        }
    }

    var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    return string.IsNullOrWhiteSpace(text) ? null : text;
}

static Parity RegistryToParity(string value)
{
    return value.Trim().ToLowerInvariant() switch
    {
        "o" or "odd" => Parity.Odd,
        "e" or "even" => Parity.Even,
        "m" or "mark" => Parity.Mark,
        "s" or "space" => Parity.Space,
        _ => Parity.None
    };
}

static string ParityToRegistry(Parity parity)
{
    return parity switch
    {
        Parity.Odd => "o",
        Parity.Even => "e",
        Parity.Mark => "m",
        Parity.Space => "s",
        _ => "n"
    };
}

static string ParityToMode(Parity parity)
{
    return parity switch
    {
        Parity.Odd => "o",
        Parity.Even => "e",
        Parity.Mark => "m",
        Parity.Space => "s",
        _ => "n"
    };
}

[DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

[DllImport("setupapi.dll", SetLastError = true)]
static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, int memberIndex, ref SpDevinfoData deviceInfoData);

[DllImport("setupapi.dll", SetLastError = true)]
static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

[DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool SetupDiGetDeviceInstanceId(
    IntPtr deviceInfoSet,
    ref SpDevinfoData deviceInfoData,
    StringBuilder deviceInstanceId,
    int deviceInstanceIdSize,
    out int requiredSize);

[DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool SetupDiGetDeviceRegistryProperty(
    IntPtr deviceInfoSet,
    ref SpDevinfoData deviceInfoData,
    int property,
    out int propertyRegDataType,
    byte[] propertyBuffer,
    int propertyBufferSize,
    out int requiredSize);

enum SerialSetting
{
    BaudRate,
    DataBits,
    Parity,
    StopBits,
    FlowControl
}

[StructLayout(LayoutKind.Sequential)]
struct SpDevinfoData
{
    public int CbSize;
    public Guid ClassGuid;
    public int DevInst;
    public IntPtr Reserved;
}

enum Parity
{
    None,
    Odd,
    Even,
    Mark,
    Space
}

enum FlowControl
{
    None,
    XonXoff,
    Hardware
}

sealed class ComPortInfo
{
    public required string ComName { get; set; }
    public required string Name { get; set; }
    public string? DeviceKeyName { get; set; }
    public string? DeviceParametersKeyName { get; set; }
    public string Service { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int BaudRate { get; set; }
    public int DataBits { get; set; }
    public Parity Parity { get; set; }
    public string StopBits { get; set; } = "1";
    public string FlowControl { get; set; } = "Unknown";
}

sealed class PortSettings
{
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public string StopBits { get; set; } = "1";
}

readonly record struct CommandResult(int ExitCode, string Output);
