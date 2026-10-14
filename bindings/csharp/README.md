# C# Bindings for the sen_* Flat C API

Thin managed bindings over the handle-based flat C API declared in
`include/sen_capi.h` (46 `sen_*` functions, built into `sensor.dll`). The
binding layer carries zero logic: error strings and session behavior live
inside the SDK and surface unchanged. This is the
only C# surface of the SDK.

## Files

- `SensorCapi.cs` — raw interop: enums, blittable structs mirroring
  `sen_capi.h` exactly (`LayoutKind.Sequential`), callback delegate types,
  and the `[DllImport("sensor", CallingConvention = CallingConvention.Cdecl)]`
  declarations.
- `Sensor.cs` — managed wrappers: `SensorController` (singleton),
  `SensorProfile`, `SensorData` / `Sample`, `DeviceInfo`, `BinFileInfo`,
  `BleDevice`, `SensorException`.

There is no separate class library project; the two `.cs` files are compiled
directly into the consuming app (see `example_cs/`).

## Marshaling discipline

- **Fixed char arrays** (`deviceMac[18]`, `name[32]`, ...) are
  `ByValArray byte[]` decoded as NUL-terminated ASCII.
- **`sen_data_view_t.samples`** is an `IntPtr` into SDK-owned memory that
  dies when the data callback returns. The `SensorData` objects delivered by
  `DataReceived` are **lightweight borrowed views**: they copy only the
  metadata and read samples straight from the native pointer via the fixed
  40-byte ABI offsets — no per-sample marshalling anywhere in the data path.
  A borrowed `SensorData` is valid only until the handler returns; call
  `SensorData.Clone()` inside the handler to keep a batch. `Clone()`
  detaches the payload with **one block copy** (`channelCount * sampleCount
  * 40` bytes) into a flat `byte[]`; every accessor works identically on
  borrowed and owned instances. Managed `Sample` objects are only
  constructed on demand by `GetChannelSample`.
- **Sample accessors** (`SensorData`, all offset-based direct reads, no
  object materialization except `GetChannelSample`): `GetData`,
  `GetTimeStampInMs` (computed: `sampleIndex * 1000 / SampleRate`, 0 when
  the rate is unknown), `GetAbsTimeStampInSec` (the stored LSL-style
  absolute timestamp, double seconds since the Unix epoch; its anchor is
  the `StartTimeSec` metadata property, 0 = unknown), `GetSampleIndex`,
  `GetRawData`, `GetImpedance`,
  `GetSaturation`, `IsLost`, plus the full-POCO `GetChannelSample`.
  Exception contract (single-slot accessors): only the index ranges are
  validated — an out-of-range channel/sampleIndex throws
  `ArgumentOutOfRangeException`. Staleness detection is **not** repeated by
  the accessors: probe `IsDataValid(ch, i)` first — false on out-of-range,
  missing payload, a
  **stale slot** (stored sampleIndex != `StartSampleIndex + sampleIndex` —
  overwritten by newer data, or a zeroed masked-out channel) or a view from
  a **previous stream session** (its `StartTimeStamp` snapshot no longer
  matches the stream's current value after a stream restart). Staleness is
  batch-atomic: one (0, 0) probe per batch is enough.
  The batch path (`ChannelSamples`, lazy matrix) never throws: slots failing
  the probe are filled with `default(Sample)`.
- **StructSize versioning**: `SenDeviceInfo.Create()` /
  `SenBinFileInfo.Create()` fill `structSize = Marshal.SizeOf<T>()`; the SDK
  writes at most that many bytes.
- **Callback lifetime**: every function pointer handed to the SDK comes from
  a `static readonly` delegate in `NativeCallbacks`
  (`Marshal.GetFunctionPointerForDelegate`), so the GC can never collect a
  delegate the SDK still references.
- **ctx**: the `void* ctx` of each callback is a `GCHandle` to the managed
  target (`SensorProfile` / `SensorController` / one-shot `CompletionOp<T>`),
  recovered with `GCHandle.FromIntPtr`. Per-operation handles are freed in
  the completion callback; profile/controller handles are freed on
  `SensorController.TearDown()`.

## Threading

All events (`DataReceived`, `StateChanged`, `ErrorReceived`, `PowerChanged`,
`DeviceFound`, `EnableChanged`) fire on internal SDK threads — never on the
caller's UI thread. Do not call blocking SDK functions from inside an event
handler; marshal to your own thread if you need to.

`ErrorReceived` also fires for every failed profile operation, alongside the
operation's own result: `Connect` / `Init` / `StartDataNotification` /
`StopDataNotification` returning false, `SetParam` / `GetParam` answering an
"Error..."/"ERROR..." string, `GetBatteryLevel` returning -1, and a failed
`FetchDeviceInfo` each report the SDK's failure reason there.

`SensorProfile.OnAutoReconnect` (a settable delegate, not an event) gates
stream recovery after an abnormal disconnect: it receives
`(profile, hasLastSession, answer)` and answers asynchronously through the
passed `Action<bool>` — call it exactly once, from any thread, at any later
time. `answer(true)` = the app takes over recovery itself and the SDK skips
its default flow; `answer(false)` (the default when the delegate is unset,
or when no answer arrives within 10 s) = the SDK runs its default recovery
(reconnect, init replay, setParam replay, stream restart). The flag itself
reads back through the `AutoReconnect` property (native default ON; its
setter is the existing `SetAutoReconnect`).

## Async model

The C API's per-operation completion callbacks are exposed as
**Task-returning async methods** backed by `TaskCompletionSource`
(`RunContinuationsAsynchronously`):

```csharp
await profile.InitAsync(packageSampleCount: 15, powerRefreshIntervalMs: 60000);
await profile.StartDataNotificationAsync();
int battery = await profile.GetBatteryLevelAsync();
DeviceInfo info = await profile.FetchDeviceInfoAsync();
string result = await profile.SetParamAsync("NTF_EMG", "ON");
string value  = await profile.GetParamAsync("NTF_EMG");
```

A non-empty SDK `errorMsg` faults the task with a `SensorException` whose
message is the verbatim SDK string (e.g. `"Error: Please connect first"`).
`GetParamAsync` resolves with the value-or-error string itself (error
strings like `"Error: Not supported"` are values, not exceptions).

## Blocking sync methods

Every async operation also has a blocking counterpart that returns the
result directly (with a backstop timeout; on timeout bool/int/DeviceInfo
methods return `false` / `-1` / `null`, string methods return
`"Error: Timeout"`):

```csharp
bool ok = profile.Connect();
ok = profile.Init(15, powerRefreshIntervalMs: 60000);
ok = profile.StartDataNotification();
string result = profile.SetParam("NTF_EMG", "ON");
string value  = profile.GetParam("NTF");
var devices = ctrl.Scan(4000);
string csv = ctrl.ParseBinToCsv(binPath, csvPath);
```

`GetDeviceInfo()` is a local-cache read instead of a blocking operation
(see below). `GetBatteryLevel()` returns the last battery level the SDK
pushed (`PowerChanged`) or that `GetBatteryLevel[Async]` reported; with no
reading yet it fetches one from the device (blocking, like the other sync
forms), `-1` on failure.

A faulted async operation surfaces in the sync form as the failure value
(string forms return the verbatim SDK error string). The sync methods
throw `InvalidOperationException` when called from a thread that delivers
SDK callbacks (the binding registers every callback thread): an
operation's completion is delivered on an SDK thread, so blocking one can
deadlock its delivery. Use `Task.Run(() => profile.Connect())` or the
async forms inside callbacks instead.

## Controller parameters

`ctrl.GetParamAsync(key)` / `ctrl.SetParamAsync(key, value)` manage the
SDK-wide settings: `DEBUG_ENABLED` / `DATA_LOG_ENABLED` / `LOG_PATH`
(file logging), `BLE_TRACE_ENABLED` (per-packet dongle trace, default off),
`BIN_RECORDER_ENABLED` (per-session bin capture master switch, default on —
when off no capture file is opened at all), `BIN_RECORDER_MIN_FREE_PERCENT`
(0-100, default 10 — the capture is skipped when the target drive's free
space drops below this percentage of its capacity, a 100 MB absolute floor
always applies), `TRANSLATE_IMU` ("True"/"False", default on — when on,
the IMU data of new-EMG devices arrives as the separate ACC/GYRO/EULER/QUAT
streams; when off, as the single aggregate NTF_IMU stream), `BACK_END` (read the active BLE backend; set forces one
while BLE is idle), and the timeout/reconnect tunables `CMD_TIMEOUT_MS` /
`CONNECT_TIMEOUT_MS` / `REPLAY_DELEGATE_WAIT_MS` / `RECONNECT_MAX_ATTEMPTS`
/ `RECONNECT_DELAY_MS`. Leftover temp capture files from a crashed session
are removed automatically at startup once untouched for over 24 hours.

## Quick start

```csharp
using SensorSdk;
using SensorSdk.Capi;

var ctrl = SensorController.Instance;
_ = ctrl.SetParamAsync("DEBUG_ENABLED", "True");
Console.WriteLine(ctrl.GetVersion());

ctrl.DeviceFound += devices => { /* SDK thread */ };
ctrl.StartScan(3000);
// ...
var profile = ctrl.RequireSensor(device);   // profile.Device mirrors this
                                            // device instance (incl. RSSI)
profile.DataReceived += (p, batch) =>
{
    // batch items borrow SDK memory - handler scope only.
    // Clone() (one block copy) any batch you keep; probe IsDataValid() once
    // per batch, then read via GetData / GetChannelSample (offset-based).
    foreach (SensorData d in batch) { var owned = d.Clone(); /* ... */ }
};
await profile.ConnectAsync();
await profile.InitAsync(15);
await profile.StartDataNotificationAsync();
```

Offline (no device needed):

```csharp
BinFileInfo? info = ctrl.GetBinFileInfo(binPath);
string csvOrError = await ctrl.ParseBinToCsvAsync(binPath, csvPath);
SensorProfile? replay = ctrl.ReplayBinFile(binPath, info?.Mac ?? "", realtime: false);
var sensors = binMacs.Select(mac => ctrl.RequireSensor(mac)).ToArray();
SensorProfile?[] members = ctrl.MultiReplayBinFile(binPaths, sensors, realtime: true);
```

`MultiReplayBinFile(paths, sensors, realtime, timeoutMs)` starts a synchronized
group replay: it takes one `SensorProfile` per bin path (the profile only
supplies the device address — `ctrl.RequireSensor(mac)` is sufficient) and
returns an input-order-aligned array with `null` for members that failed
validation. `BinFileInfo.ConfigVersion` reports the version of the capture's
CONFIG record (0 when the file carries no decodable config record). The same
CONFIG record also fills `BinFileInfo.ChipType`, the device-class flags
`IsUniversalStream` / `IsNewEmg` / `IsContainQat6` / `PpgModel`, the capture's
`FeatureMap` / `NotifyDataFlag` bitmasks, and `SensorDatas` — one
`BinSensorDataConfig` per configured stream (sample rate, `K`, channel
mask/count, data type, package geometry, resolution and raw-data bias),
listing only the entries the capture actually configured.

`SensorProfile.GetDeviceInfo()` returns a binding-side cached
`DeviceInfo`: the cache is fed by the `DeviceInfoUpdated` event and by
`FetchDeviceInfo`/`FetchDeviceInfoAsync` results, and while empty one
native read populates it; it answers `null` only before the profile was
successfully initialized (a successful `Init`/`InitAsync`, or a replay
configuration loaded by `ReplayBinFile`/`MultiReplayBinFile`; the
`HasInited` property reports the same state), and it is cleared when the
device disconnects. The `Device` property is likewise cached after the
first read.

`SensorController.Instance` is a process-wide singleton
(`sen_controller_create`); call `TearDown()` (or `Dispose()`) at shutdown to
destroy the native controller and terminate the whole SDK (`sen_terminate`)
— every profile handle dies with it.

## Runtime compatibility (net8 / net48 / Unity)

Support matrix (net48 and netstandard2.1 rows verified by actually compiling
the two `.cs` files as class libraries — offline, no NuGet, 0 warnings):

| Target | Status | Notes |
|---|---|---|
| .NET 8 (`net8.0`) | Verified | `example_cs` / `example_csharp` build offline. Default LangVersion is fine. |
| .NET Framework 4.8 (`net48`) | Verified | Compiles as a classic class library with VS2022 MSBuild, 0 warnings. **Requires `<LangVersion>9.0</LangVersion>`** — net48 defaults to C# 7.3, but the binding uses `??=` (C# 8) and target-typed `new()` (C# 9). These are compile-time features only; the net48 CLR runs them without issue. |
| .NET Standard 2.1 (`netstandard2.1`) | Verified | Compiles offline with the net8 SDK's bundled ref pack, 0 warnings. Also needs `<LangVersion>9.0</LangVersion>` (netstandard2.1 defaults to C# 8). This is the Unity 2021.3+ API profile. |
| Unity 2021.3+ (Mono) | Expected OK | .NET Standard 2.1 profile; drop both `.cs` files into `Assets/` plus the native plugin (`sensor.dll` on Windows). Unity's default LangVersion is 9, so no config change is needed. |
| Unity 2021.3+ (IL2CPP AOT) | Expected OK | Reverse P/Invoke callbacks are all **static methods** (`NativeCallbacks` in `Sensor.cs`) held in `static readonly` delegates with `GCHandle` ctx — the IL2CPP-compatible pattern. Every callback is marked `[MonoPInvokeCallback(typeof(...))]`; outside Unity this resolves to a compile-time stub (`AOT.MonoPInvokeCallbackAttribute` at the bottom of `SensorCapi.cs`), in Unity to UnityEngine's real attribute. IL2CPP only supports reverse P/Invoke for static methods — keep it that way when extending the binding. |

Compatibility fixes applied (2026-08):

1. `BitConverter.Int32BitsToSingle` (netcoreapp3.0+/netstandard2.1 only) was
   replaced by an explicit-layout int/float union in `SensorData`, which also
   works on .NET Framework 4.8 and IL2CPP.
2. `#nullable enable` was added at the top of `Sensor.cs` so the nullable
   reference annotations do not raise CS8632 in legacy projects that leave
   `Nullable` disabled (net48 / Unity project defaults).
3. `[MonoPInvokeCallback]` markers were added on every reverse-P/Invoke
   callback for Unity IL2CPP (see matrix above).

Everything else was already portable: no `Span<T>` / `Memory<T>` /
`ArrayPool` / `HashCode` / `Unsafe`; marshalling goes through `Marshal.*`
(available since .NET Framework 4.5.1 / netstandard1.x) plus `GCHandle` and
`IntPtr`.
