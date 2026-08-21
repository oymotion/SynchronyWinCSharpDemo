# example_cs_winui3 — C# WinUI 3 demo (prebuilt SDK)

OYMotion SDK demo for C# / .NET.

## Brief

Full-featured multi-device GUI demo over the C# bindings in
`bindings/csharp/` (`SensorCapi.cs` + `Sensor.cs`, compiled into the app by
path), built against the **prebuilt** `sensor.dll` from
`lib/windows/<arch>/<config>/` (copied next to the exe at build time; x64
Debug by default, selectable via the `SensorSdkArch` / `SensorSdkConfig`
build properties). Feature parity with the Qt demo
(`example_qt`); the window title carries the SDK version and the demo's own
version (bump +0.0.1 per demo change).

WinUI 3, .NET 8, VS2022. Waveforms / spectrum / 3D cube are drawn with Win2D
(`Microsoft.Graphics.Win2D` NuGet). The app is **unpackaged and
self-contained** (`WindowsPackageType=None` + `WindowsAppSDKSelfContained=
true`): it runs straight from the build output, no MSIX install and no
system-wide Windows App Runtime required.

## Installation

```bash
dotnet build example_cs_winui3/example_cs_winui3.csproj
# then run:
example_cs_winui3\bin\Debug\net8.0-windows10.0.19041.0\win-x64\example_cs_winui3.exe
```

The SDK library follows the build configuration and platform (visible in the
VS2022 dropdowns): Debug/Release loads the matching Debug/Release
`sensor.dll`, x64/x86 the matching architecture. On the command line:

```bash
# Release app + Release SDK library:
dotnet build example_cs_winui3/example_cs_winui3.csproj -c Release

# x86 (the output exe lands in the win-x86 folder):
dotnet build example_cs_winui3/example_cs_winui3.csproj -p:Platform=x86
```

or open `example_cs_winui3/` in VS2022 (17.8+) with the "Windows App SDK"
workload and press F5.

`export_standalone.bat [target_dir]` copies the demo as a standalone project
(demo sources + the C# binding sources + the Release `sensor.dll` for x64 and
x86, preserving the relative layout) so it can be handed out without the SDK
repository; the exported project defaults to the Release SDK library on any
configuration.

To use the bindings in your own app, add `bindings/csharp/SensorCapi.cs` and
`bindings/csharp/Sensor.cs` to your project and ship `sensor.dll` next to
the exe (support matrix: net8 / net48 / netstandard2.1 / Unity IL2CPP — see
`bindings/csharp/README.md`).

## 1. Permission

The app uses the OS Bluetooth stack through `sensor.dll`; no capability
declaration or permission prompt is needed for an unpackaged desktop app.

## 2. Import SDK

```csharp
using SensorSdk;
```

The binding is thin: all behavior (error strings, subscription masks,
session recovery) lives inside the SDK and matches the Python SDK verbatim.

## SensorController methods

### 1. Initialize

```csharp
// singleton
var controller = SensorController.Instance;

// register scan listener: the deduped device list, delivered every scan round
controller.DeviceFound += (List<BleDevice> deviceList) =>
{
    // all discovered devices (repeats refresh the entry in place)
};

// bluetooth enable state changes
controller.EnableChanged += (bool enabled) => { };
```

Use `GetVersion()` to get the SDK version string:

```csharp
string version = controller.GetVersion();
```

`SensorController.CapiVersion` returns the native library's C API version,
so an app can detect a binding/library mismatch at runtime (a mismatch is
also traced as a warning at controller creation).

### 2. Start scan

Use `bool StartScan(int periodInMs)` to start scanning; `DeviceFound` fires
every `periodInMs`:

```csharp
bool success = controller.StartScan(6000);
```

Use `Task<List<BleDevice>> ScanAsync(int periodInMs)` to scan once:

```csharp
List<BleDevice> bleDevices = await controller.ScanAsync(6000);
```

### 3. Stop scan

```csharp
controller.StopScan();
```

### 4. Check scanning

```csharp
bool isScanning = controller.IsScanning;
```

### 5. Check if bluetooth is enabled

```csharp
bool isEnable = controller.IsEnable;
```

### 6. Create SensorProfile

Use `RequireSensor` to get (creating and registering when the MAC is
unknown) the profile of a device:

```csharp
SensorProfile sensorProfile = controller.RequireSensor(bleDevice);   // or RequireSensor(mac)
```

### 7. Get SensorProfile

```csharp
SensorProfile? sensorProfile = controller.GetSensor(bleDevice.Mac);  // null when never registered
```

### 8. Get connected SensorProfiles

```csharp
List<SensorProfile> sensorProfiles = controller.GetConnectedSensors();
```

### 9. Terminate

Call `TearDown()` (or `Dispose()`) once at application shutdown: every scan
and connection stops and the whole native SDK is terminated. Repeated calls
are safe.

```csharp
controller.TearDown();

// in this demo: App.xaml.cs hooks the main window's Closed event and calls
// SensorController.Instance.Dispose()
```

Please MAKE SURE to call TearDown when the app exits.

## SensorProfile methods

### 10. Register callbacks

```csharp
SensorProfile sensorProfile = controller.RequireSensor(bleDevice);

sensorProfile.StateChanged += (SensorProfile sensor, SenDeviceState newState) =>
{
    // device state transitions (Connecting/Connected/Ready/Disconnected/...)
    // do the unexpected-disconnect logic here
};

sensorProfile.ErrorReceived += (SensorProfile sensor, string reason) =>
{
    // dongle unplugged, reconnect budget exhausted, ...
};

sensorProfile.PowerChanged += (SensorProfile sensor, int power) =>
{
    // battery 0-100; invalid readings are never reported, and the value is
    // stabilized with a hysteresis band so ADC jitter is filtered out
};

sensorProfile.DataReceived += (SensorProfile sensor, List<SensorData> dataList) =>
{
    // after startDataNotification: each invocation delivers the whole batch
    // of SensorData objects parsed together (loop over it)
    foreach (SensorData data in dataList) { }
};

sensorProfile.DeviceInfoUpdated += (SensorProfile sensor, DeviceInfo info) =>
{
    // the cached DeviceInfo was patched in place (link parameters reported
    // after connect, EEG_SAMPLE_RATE applied, replay config switch, ...)
};

sensorProfile.DataTransferStateChanged += (SensorProfile sensor, bool isTransferring) =>
{
    // real data-stream on/off changes only
};
```

Callback threading model: all events fire on internal SDK threads, never on
the UI thread. Keep handlers short; marshal UI updates through
`DispatcherQueue` (this demo routes everything through a queue drained on
the UI thread). Do not call blocking SDK functions from inside an event
handler.

A seventh hook, `OnAutoReconnect`, customizes stream recovery after an
abnormal disconnect — see section 14.1.

### 11. Connect device

```csharp
bool success = await sensorProfile.ConnectAsync();
```

### 12. Disconnect

```csharp
bool success = await sensorProfile.DisconnectAsync();
```

If data notification is currently active, `DisconnectAsync()` stops it first
before closing the BLE connection.

### 13. Get device status

```csharp
SenDeviceState deviceState = sensorProfile.DeviceState;

// enum SenDeviceState:
//   Disconnected = 0, Connecting = 1, Connected = 2, Ready = 3,
//   Disconnecting = 4, Invalid = 5
```

Send commands only in the `Ready` state. `IsReady` is the shortcut:

```csharp
if (sensorProfile.IsReady) { }
```

### 14. Get BLE device of SensorProfile

```csharp
BleDevice bleDevice = sensorProfile.Device;   // Name / Mac / Rssi
```

### 14.1 Auto reconnect and resume data stream

Auto reconnect is enabled by default. While enabled and the device is
streaming, an abnormal disconnect (remote link loss, or a long no-data
half-dead link) is followed by automatic reconnect -> `InitAsync` with the
previous init arguments -> re-applying the previous session's `SetParam`
values in order -> `StartDataNotificationAsync`. Explicit user calls
(`ConnectAsync()` / `DisconnectAsync()` / `StopDataNotificationAsync()`)
cancel the pending resume.

```csharp
sensorProfile.SetAutoReconnect(true);   // default; false to opt out
```

**Custom recovery via `OnAutoReconnect`**: when the auto-reconnect finds the
disconnected device again (back in `Ready`, about to resume), this delegate
is invoked instead of the default flow:

```csharp
sensorProfile.OnAutoReconnect = (SensorProfile sensor, bool hasLastSession) =>
{
    // hasLastSession=true  -> a previous session exists (init args + setParam
    //                         values can be preserved and restored)
    // return true  -> the app handled recovery itself; the SDK skips the
    //                 default recovery
    // return false -> fall back to the default flow
    return false;
};
```

### 15. Get device info of SensorProfile

Call after the device is `Ready` and init has succeeded:

```csharp
DeviceInfo deviceInfo = sensorProfile.GetDeviceInfo();

// fields: DeviceName, ModelName, HardwareVersion, FirmwareVersion, MTUSize
// plus a ChannelCount / SampleRate field pair per modality:
//   Ppg, Spo2, Impe, Emg, Eeg, Ecg, Acc, Gyro, Brth, MagAngle, Euler, Quat
// plus EmgMaxSampleRate / EegMaxSampleRate / EcgMaxSampleRate (maximum rate
//   from the capability query, 0 = not reported)
// plus ImuChannelCount / ImuSampleRate (aggregated IMU stream; 0 = none)
// plus ConnectionIntervalMs / PeripheralLatency / SupervisionTimeoutMs
//   (negotiated BLE link parameters; 0 / -1 / 0 = unknown)
```

Or fetch explicitly:

```csharp
DeviceInfo info = await sensorProfile.FetchDeviceInfoAsync();
```

### 16. Init data transfer

```csharp
await sensorProfile.InitAsync(packageSampleCount: 15, powerRefreshIntervalMs: 60 * 1000);
```

- `packageSampleCount`: sample count of each `SensorData` batch delivered by
  `DataReceived`
- `powerRefreshIntervalMs`: poll period for the `PowerChanged` push (0 = one
  initial reading only)

A non-successful init throws `SensorException` with the SDK's error string.

### 17. Check if init data transfer succeed

```csharp
bool hasInited = sensorProfile.HasInited;
```

### 18. DataNotify

#### 18.1 Start data transfer

```csharp
await sensorProfile.StartDataNotificationAsync();
```

#### 18.2 Data type list

```csharp
// enum SenDataType:
//   Acc = 0x1            // acceleration, unit is g
//   Gyro = 0x2           // gyroscope, unit is degree/s
//   Euler = 0x4          // euler angle, unit is degree
//   Quaternion = 0x5     // quaternion (w, x, y, z)
//   Gest = 0x07          // gesture id
//   Emg = 0x8            // unit is uV
//   MagAngle = 0x0D
//   Eeg = 0x10           // unit is uV
//   Ecg = 0x11           // unit is uV
//   Impedance = 0x12     // electrode impedance
//   Imu = 0x13           // aggregated IMU batch (acc 0-2 / gyro 3-5 /
//                        // euler 6-8 / quat 9-12; see DeviceInfo.ImuChannelCount)
//   Ads = 0x14
//   Brth = 0x15          // respiration, unit is uV
//   ImpedanceExt = 0x16
//   Spo2 = 0x17          // SpO2 percentage
//   Ppg = 0x18           // PPG raw samples
```

Process data in `DataReceived`. Each `SensorData` exposes:

- metadata properties: `DeviceMac` / `DeviceName` / `DataType` /
  `SampleRate` / `ChannelCount` / `SampleCount` / `ChannelMask` /
  `LostPackageCount` / `StartSampleIndex` / `StartTimeStamp` /
  `StartTimeSec` (wall-clock stream-start anchor in LSL-style Unix seconds,
  0.0 when unknown) / `Delay`
- batch access: `ChannelSamples` (lazy `Sample[][]` matrix) and
  `IsChannelEnabled(channel)` (false for channels masked out of
  `ChannelMask`)
- single-point accessors (`channel`, `sampleIndex`):
  `GetChannelSample` / `GetData` / `GetRawData` / `GetImpedance` /
  `GetSaturation` / `GetSampleIndex` / `GetTimeStampInMs` /
  `GetAbsTimeStampInSec` / `IsLost`; out-of-range indices throw
  `ArgumentOutOfRangeException`
- staleness probe: `IsDataValid(channel = 0, sampleIndex = 0)` — false on
  out-of-range, a stale/overwritten slot, or a view from a previous stream
  session. One probe per batch is enough.

A `SensorData` delivered by `DataReceived` is a **borrowed view** over
SDK-owned memory. It stays readable after the handler returns, but a slot is
eventually overwritten by newer data (detectable with `IsDataValid`). To
hold a batch across threads or time, call `Clone()` at the boundary — one
block copy into an owned buffer; every accessor works identically on the
clone.

```csharp
sensorProfile.DataReceived += (sensor, dataList) =>
{
    foreach (SensorData data in dataList)
    {
        if (!data.IsDataValid())   // one probe per batch
            continue;

        if (data.DataType == SenDataType.Eeg)
        {
            for (int ch = 0; ch < data.ChannelCount; ch++)
            {
                if (!data.IsChannelEnabled(ch))
                    continue;   // masked-out channel
                for (int i = 0; i < data.SampleCount; i++)
                {
                    if (data.IsLost(ch, i))
                        continue;   // loss-compensation placeholder
                    float uv = data.GetData(ch, i);
                    // draw with uv & ch
                }
            }
        }
    }
};
```

#### 18.3 Stop data transfer

```csharp
await sensorProfile.StopDataNotificationAsync();
```

#### 18.4 Check if it's data transfering

```csharp
bool isTransfering = sensorProfile.IsDataTransfering;
```

### 19. Get battery level

```csharp
int batteryPower = await sensorProfile.GetBatteryLevelAsync();
// 0-100; -1 means no valid reading is available yet
// (PowerChanged never reports -1). Explicit queries are unfiltered.
```

### Async model

All profile operations are Task-returning async methods backed by the SDK's
completion callbacks; a non-empty SDK error string becomes a
`SensorException`. There are no synchronous blocking variants:

- `SensorController`: `ScanAsync`
- `SensorProfile`: `ConnectAsync`, `DisconnectAsync`, `InitAsync`,
  `StartDataNotificationAsync`, `StopDataNotificationAsync`,
  `SetParamAsync`, `GetParamAsync`, `GetBatteryLevelAsync`,
  `FetchDeviceInfoAsync`

### setParam method

Use `Task<string> SetParamAsync(string key, string value)` to set a
parameter. Call after the device reaches the `Ready` state; the result is
`"OK"` on success or an error string otherwise. If the device is already
streaming when you change an `NTF_*` key, the SDK stops and restarts the data
notification so the new setting takes effect immediately. `FILTER_*` keys are
applied on the fly without interrupting the stream.

```csharp
// Data stream toggles ("ON" / "OFF")
string result = await sensorProfile.SetParamAsync("NTF_GEST", "ON");
await sensorProfile.SetParamAsync("NTF_EMG", "ON");
await sensorProfile.SetParamAsync("NTF_EEG", "ON");
await sensorProfile.SetParamAsync("NTF_ECG", "ON");
await sensorProfile.SetParamAsync("NTF_IMU", "ON");
await sensorProfile.SetParamAsync("NTF_BRTH", "ON");
await sensorProfile.SetParamAsync("NTF_IMPEDANCE", "ON");
await sensorProfile.SetParamAsync("NTF_MAG_ANGLE", "ON");
await sensorProfile.SetParamAsync("NTF_PPG", "ON");
await sensorProfile.SetParamAsync("NTF_PPG_RAW", "ON");   // alias of NTF_PPG
await sensorProfile.SetParamAsync("NTF_SPO2", "ON");
await sensorProfile.SetParamAsync("NTF_GFORCE_EULER", "ON");
await sensorProfile.SetParamAsync("NTF_GFORCE_QUAT", "ON");
await sensorProfile.SetParamAsync("NTF_GFORCE_ACC", "ON");
await sensorProfile.SetParamAsync("NTF_GFORCE_GYRO", "ON");
// NTF_IMU is the master switch of the four NTF_GFORCE_* streams: toggling it
// updates all four, and toggling any of the four updates the aggregated
// NTF_IMU state. On legacy EMG devices NTF_GEST and NTF_EMG are mutually
// exclusive.

// Firmware filter toggles
await sensorProfile.SetParamAsync("FILTER_50HZ", "ON");   // 50Hz notch
await sensorProfile.SetParamAsync("FILTER_60HZ", "ON");   // 60Hz notch
await sensorProfile.SetParamAsync("FILTER_HPF", "ON");    // 0.5Hz high-pass
await sensorProfile.SetParamAsync("FILTER_LPF", "ON");    // 80Hz low-pass

// EEG/ECG sample rate (bound together on devices that have both)
await sensorProfile.SetParamAsync("EEG_SAMPLE_RATE", "500");
// Validated against the device-reported capability list (see
// getParam("EEG_SAMPLE_RATE_LIST")); an unsupported value returns
// "Error: unsupported sample rate ...".

// NeuCir remote control (NeuCir devices only)
await sensorProfile.SetParamAsync("NEUCIR_SET_MODE", "APP_REMOTE");
await sensorProfile.SetParamAsync("NEUCIR_APP_CONTROL", "OPEN");   // OPEN / CLOSE / STOP

// Debug outputs
await sensorProfile.SetParamAsync("DEBUG_BLE_DATA_PATH", "True");
// export the session's raw BLE capture: "True" exports
// {DeviceName}_data_YYYYMMDD_HHMMSS.bin into the SDK log directory (see
// SetLogPath), or pass an absolute .bin path; "False" / "" disables export.
await sensorProfile.SetParamAsync("DEBUG_LOG_PATH", "True");
// enable this profile's log file ({DeviceName}_log_YYYYMMDD_HHMMSS.txt in
// the SDK log directory), or pass an absolute path; "False" / "" disables.
```

### getParam method

Use `Task<string> GetParamAsync(string key)` to query the current parameter
state. If the key is not supported, the result starts with `"Error"`.

```csharp
string result = await sensorProfile.GetParamAsync("FILTER");
// "FILTER_50HZ|ON|FILTER_60HZ|ON|FILTER_HPF|ON|FILTER_LPF|ON"

result = await sensorProfile.GetParamAsync("NTF");
// "NTF_BRTH|ON|NTF_ECG|ON|NTF_EEG|ON|NTF_EMG|ON|..."
// The aggregate lists every known key regardless of device capability —
// gate UI visibility by the DeviceInfo channel counts, not by presence here.

result = await sensorProfile.GetParamAsync("EEG_SAMPLE_RATE");       // e.g. "250"
result = await sensorProfile.GetParamAsync("EEG_SAMPLE_RATE_LIST");  // e.g. "250|500"
```

## Bin file recording and replay

On every successful connect the SDK records all raw BLE packets of the
session into a temp `.bin` file; `SetParamAsync("DEBUG_BLE_DATA_PATH", ...)`
exports it on stream stop / disconnect. Bin files can be replayed offline
for debugging and packet-loss analysis.

### Get bin file info

```csharp
BinFileInfo? info = controller.GetBinFileInfo("path/to/session.bin");
// fields: Mac, DeviceName, DurationSec, Valid, DeviceInfo
// null when the file does not exist or has no config record
```

### Replay a bin file

Replays a capture through the normal parsing pipeline on a background
thread; parsed batches arrive via `DataReceived` on the returned profile,
same as live data:

```csharp
SensorProfile? replay = controller.ReplayBinFile("path/to/session.bin", deviceMac: "");
replay.DataReceived += (sensor, dataList) => { /* same handler as live */ };
```

- `deviceMac`: profile identity to replay through; pass `""` to use the
  MAC stored in the bin's config record
- `realtime`: `true` replays at the recorded pace; `false` as fast as
  possible
- Returns null when the file has no config record

### Pause / resume / stop replay

```csharp
string result = controller.PauseBinReplay(deviceMac);
result = controller.ResumeBinReplay(deviceMac);
result = controller.StopBinReplay(deviceMac);
// Each returns "OK" on success or an error string otherwise.
```

### Parse a bin file to CSV

Offline full-speed conversion through the real parsing pipeline; blocks the
caller:

```csharp
string csvPath = controller.ParseBinToCsv("d:/temp/test.bin", "d:/temp/test.csv");
// Returns the CSV file path, or an "Error: ..." string.
```

CSV header row:

```
timestamp,mac,type,raw_hex,data_type,sample_rate,channel_count,lost_count,samples_info,first_sample
```

Row kinds in record order: `raw` (one per data record, raw bytes as hex),
`cmd_send` / `cmd_recv` (command bytes as hex; `data_type` names the decoded
command / `NAME:CODE` response), `event` (`connect` / `disconnect` /
`stream_start` / `stream_stop`), and `parsed` (one per parsed batch;
`data_type` is the type name, e.g. `NTF_EEG`; `samples_info` the per-channel
sample counts; `first_sample` the first sample's field summary). A bin
without a config record yields `raw` rows only.

## Logging controls

`SetLogPath` sets the SDK log **directory** (it must be a directory). All
default file outputs live in it: the controller log, the default per-profile
logs (`DEBUG_LOG_PATH=True`) and the default bin exports
(`DEBUG_BLE_DATA_PATH=True`).

```csharp
controller.SetDebugEnabled(true);
// enable SDK debug logs; creates the controller log
// (sensor_controller_log_YYYYMMDD_HHMMSS.txt) in the log directory.
// SetDebugEnabled(false) closes it and drops all file output.

controller.SetLogPath(true, "d:/temp/sdklogs");
// set the log directory (created if missing). SetLogPath(false) disables
// file output; SetLogPath(true) resets to the default
// (Documents/sensorsdklog).
```

### Application log entries

Applications can write their own events into the same SDK log files, keeping
one shared timeline with the SDK's internal logs:

```csharp
controller.Log("User clicked start", "I");        // controller log
sensorProfile.Log("User toggled filter 50Hz", "I"); // profile log when enabled,
                                                    // else the controller log
```

`level` is judged by its first character, case-insensitive `d` / `i` / `w` /
`e` (anything else is `Info`); `d` follows the `SetDebugEnabled` switch.
Entries are tagged `[App]` in the log files. Never throws.

---

## What this demo does

- **Start Scan / Stop Scan**: continuous scanning (3 s rounds); every
  discovered device is listed, RSSI-sorted, repeats update in place.
- **Multi-device**: select a row and Connect — any number of devices stream
  at once; the list row gets a `[Connected]` / `[Streaming]` mark. The
  selected row is the *current* device whose waveforms, counters, info
  labels and setParam controls are shown. Disconnect affects only the
  selected device. **Auto Reconnect** and **Clone Data** (safe deep-copy vs
  zero-copy batch queueing; default off = zero-copy) checkboxes sit above
  the list.
- **Left column**: 3D quaternion cube, 2D waveform of the selected type
  (ACC / GYRO / Quat / Euler) with per-channel labels, an FFT spectrum strip
  (recomputed every 500 ms on a worker thread), and a real-time values box.
  The **Live Filter** combo band-passes the bio waveforms (Off / delta /
  theta / alpha / beta / gamma).
- **Bio panel** (right, 8 stacked waveforms): auto-selected by device
  capability — EMG channels, or paged EEG channels plus ECG/BRTH slots
  (Prev/Next), or the PPG fixed plot set (EEG fp1/fp2 + PPG red/ir + SpO2
  spo2/heart_rate). Impedance side texts appear on the EMG/EEG plots.
- **Device page**: status line (per-type nominal rates), actual-rates line
  (measured Hz once per second, plus stream-start wall clock and
  first-packet delay), Model / HW / FW / Link / MTU / Power labels (battery
  pushed by the SDK polling).
- **Settings**: Packet Loss Stats, Gesture box, Enable SDK Debug Log /
  Enable Debug Bin Data (the session's logs and .bin exports land in one
  timestamped subdir of `Documents/sensorsdklog`), Data Notification
  switches (EEG/EMG/GESTURE/PPG/SpO2/IMU, capability-gated by the device
  info), Filter switches (50Hz/60Hz/HPF/LPF), EEG Sample Rate radios
  (250/500 Hz).
- **Replay Bin File**: replays a `.bin` capture through the normal parse
  pipeline (realtime; Pause/Resume/Stop). **Analyze Bin** parses a capture
  to CSV offline and opens the result.

SDK callbacks arrive on SDK threads; the data callback only enqueues batches
(a worker thread feeds the display rings), and everything UI-side is
marshalled via `DispatcherQueue` and a 50 ms repaint timer.

## License

Same as the SDK (see the repository root).
