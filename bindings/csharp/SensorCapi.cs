// P/Invoke declarations for the handle-based flat C API in include/sen_capi.h
// (44 sen_* functions). Every struct mirrors the C layout exactly
// (LayoutKind.Sequential, default pack); structSize-versioned structs must be
// constructed with structSize = Marshal.SizeOf<T>() before being passed in.
//
// Marshaling notes:
// - Fixed-size char arrays are ByValArray byte[] (NUL-terminated ASCII);
//   decode with the CapiString helper in Sensor.cs. SenDataInfo is the
//   exception: its deviceMac/deviceName are pure ASCII and marshal as
//   ByValTStr string directly.
// - sen_data_view_t.samples is an IntPtr into SDK-owned memory that is only
//   valid for the duration of the data callback - read or copy (clone) it
//   there, never store the pointer for later use.
// - size_t maps to UIntPtr. All entry points are Cdecl.
// - Callback tables are passed as structs of raw function pointers (IntPtr)
//   built from static delegates via Marshal.GetFunctionPointerForDelegate, so
//   the delegates can never be collected while the SDK holds them.

using System;
using System.Runtime.InteropServices;

namespace SensorSdk.Capi
{
    public enum SenDeviceState : int
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Ready = 3,
        Disconnecting = 4,
        Invalid = 5
    }

    public enum SenDataType : int
    {
        Acc = 1,
        Gyro = 2,
        Euler = 4,
        Quaternion = 5,
        Gest = 7,
        Emg = 8,
        MagAngle = 13,
        Eeg = 16,
        Ecg = 17,
        Impedance = 18,
        Imu = 19,
        Ads = 20,
        Brth = 21,
        ImpedanceExt = 22,
        Spo2 = 23,
        Ppg = 24
    }

    // ABI mirror of sen_sample_t (fixed 40-byte little-endian layout, see
    // sen_capi.h). Kept for layout documentation; the data path does NOT
    // marshal samples one by one - SensorData reads fields straight from the
    // native pointer / cloned byte[] via the fixed offsets.
    [StructLayout(LayoutKind.Sequential)]
    public struct SenSample
    {
        // LSL-style absolute timestamp: stream-start wall clock (Unix
        // seconds) + first-packet delay + sampleIndex/sampleRate, computed
        // at decode time; 0 when the anchor is unknown.
        public double absTimeStampInSec;
        public int channelIndex;
        public int sampleIndex;
        public int rawData;
        public float data;
        public float impedance;
        public float saturation;
        public byte isLost;
        public byte reserved0;
        public byte reserved1;
        public byte reserved2;
        public byte reserved3;
        public byte reserved4;
        public byte reserved5;
        public byte reserved6;
    }

    // Broadcast metadata shared by every view of one stream (mirrors
    // sen_data_info_t; the info pointer in SenDataView borrows stream-owned
    // storage, same lifetime rules as the samples pointer).
    [StructLayout(LayoutKind.Sequential)]
    public struct SenDataInfo
    {
        // NUL-terminated ASCII; marshaled as a fixed string (ByValTStr).
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)]
        public string deviceMac;
        public int dataType;
        public int lostPackageCount;
        public float sampleRate;
        public int channelCount;
        public ulong channelMask;
        public int sampleCount;
        // Stream-start stamp: steady-clock ms live (low 32 bits), the bin
        // record ts on replay; re-stamped on every (re)start.
        public uint startTimeStamp;
        public uint delay;
        // Wall-clock Unix seconds (double) at stream start; on replay
        // restored from the bin record timestamps (0 = unknown).
        public double startTimeSec;
        // Device name from the cached DeviceInfo, stamped once when the
        // stream is created; empty when unknown.
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string deviceName;
    }

    // Layout invariant (from sen_capi.h): samples are packed
    // [channel][sample] with per-channel stride == sampleCount, so
    // samples[channelIndex * sampleCount + sampleIndex] is the slot of
    // (channelIndex, sampleIndex). Channels masked out of channelMask carry
    // zeroed slots. A slot whose sampleIndex != startSampleIndex + sampleIndex
    // is stale and must be skipped by consumers; a view whose startTimeStamp
    // no longer matches the stream's current info.startTimeStamp belongs to a
    // previous session and is stale as a whole.
    [StructLayout(LayoutKind.Sequential)]
    public struct SenDataView
    {
        public int startSampleIndex;
        public uint startTimeStamp;   // snapshot of the stream's Info.startTimeStamp at broadcast time
        public IntPtr info;    // borrowed sen_data_info_t, stream-owned storage
        public IntPtr samples; // borrowed, callback scope only
        // Byte size of the samples block (channelCount * sampleCount * 40);
        // 0 when samples is null.
        public UIntPtr samplesBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SenBleDevice
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] name;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 18)]
        public byte[] mac;
        public short rssi;
    }

    // Mirror of sen_device_info_t; structSize-versioned for forward growth.
    [StructLayout(LayoutKind.Sequential)]
    public struct SenDeviceInfo
    {
        public uint structSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] deviceName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] modelName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] hardwareVersion;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] firmwareVersion;
        public ushort MTUSize;
        public byte isMTUFine;
        public byte EMGGain;
        public byte EEGGain;
        public byte ECGGain;
        public byte EMGChannelCount;
        public byte EEGChannelCount;
        public byte ECGChannelCount;
        public byte BRTHChannelCount;
        public byte AccChannelCount;
        public byte GyroChannelCount;
        public byte MagAngleChannelCount;
        public ushort EMGSampleRate;
        public ushort EEGSampleRate;
        public ushort ECGSampleRate;
        public ushort BRTHSampleRate;
        public ushort AccSampleRate;
        public ushort GyroSampleRate;
        public ushort MagAngleSampleRate;
        public byte ImuChannelCount;
        public ushort ImuSampleRate;
        public byte EulerChannelCount;
        public ushort EulerSampleRate;
        public byte QuatChannelCount;
        public ushort QuatSampleRate;
        public byte PpgChannelCount;
        public ushort PpgSampleRate;
        public byte Spo2ChannelCount;
        public ushort Spo2SampleRate;
        public byte ImpeChannelCount;
        public ushort ImpeSampleRate;
        public ushort EmgMaxSampleRate;
        public ushort EegMaxSampleRate;
        public ushort EcgMaxSampleRate;
        // Link connection parameters (aligned with the Python SDK 0.7.0
        // DeviceInfo); the C++ BLE backends do not expose them, so they
        // always report the unknown values.
        public double ConnectionIntervalMs;  // 0 = unknown
        public int PeripheralLatency;        // -1 = unknown (0 is a legal value)
        public int SupervisionTimeoutMs;     // 0 = unknown

        public static SenDeviceInfo Create()
        {
            var info = new SenDeviceInfo();
            info.structSize = (uint)Marshal.SizeOf<SenDeviceInfo>();
            return info;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SenBinFileInfo
    {
        public uint structSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 18)]
        public byte[] mac;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] deviceName;
        public double durationSec;
        public byte valid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)]
        public byte[] reserved;
        // DeviceInfo from the first CONFIG record; zeroed when the file has
        // no decodable config (or when a caller's structSize predates this
        // field).
        public SenDeviceInfo deviceInfo;

        public static SenBinFileInfo Create()
        {
            var info = new SenBinFileInfo();
            info.structSize = (uint)Marshal.SizeOf<SenBinFileInfo>();
            return info;
        }
    }

    /* ---- callback types (Cdecl, raw IntPtr args to stay allocation-free) -- */

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenScanResultCb(IntPtr ctx, IntPtr devices, UIntPtr count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenEnableChangedCb(IntPtr ctx, int enabled);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenDataCb(IntPtr ctx, IntPtr profile, IntPtr views, UIntPtr viewCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenStateCb(IntPtr ctx, IntPtr profile, int newState);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenErrorCb(IntPtr ctx, IntPtr profile, IntPtr errorMsg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenPowerCb(IntPtr ctx, IntPtr profile, int power);
    // Answer callback for SenAutoReconnectCb: call it exactly once, from any
    // thread, with non-zero to take over session recovery yourself, zero for
    // the SDK's default init -> setParam replay -> stream restart flow. If no
    // answer arrives within 10 s the SDK runs the default recovery.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenAutoReconnectAnswerCb(IntPtr answerCtx, int handled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenAutoReconnectCb(IntPtr ctx, IntPtr profile, int hasLastSession,
                                              IntPtr answer, IntPtr answerCtx);
    // DeviceInfo field change push (aligned with the Python SDK 0.7.0
    // onDeviceInfoUpdate): fired after the cached DeviceInfo was updated in
    // place (e.g. setParam "EEG_SAMPLE_RATE" rewrote the bound EEG/ECG rates).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenDeviceInfoUpdateCb(IntPtr ctx, IntPtr profile, IntPtr info);
    // Data stream on/off state change push: fired when the data stream
    // actually starts (successful sen_profile_start_data, replay data start)
    // or stops (sen_profile_stop_data, link loss, replay end), only on a real
    // change. isTransferring: 1 = streaming, 0 = stopped.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenDataTransferStateCb(IntPtr ctx, IntPtr profile, int isTransferring);

    // Callback tables: structSize first, then raw function pointers.
    [StructLayout(LayoutKind.Sequential)]
    internal struct SenProfileCbs
    {
        public uint structSize;
        public IntPtr onData;
        public IntPtr onStateChange;
        public IntPtr onError;
        public IntPtr onPowerChange;
        public IntPtr onAutoReconnect;
        public IntPtr onDeviceInfoUpdate;
        public IntPtr onDataTransferStateChange;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SenControllerCbs
    {
        public uint structSize;
        public IntPtr onScanResult;
        public IntPtr onEnableChanged;
    }

    // Per-operation completions. errorMsg is empty (not NULL) on success.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenCompletionCb(IntPtr ctx, int result, IntPtr errorMsg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenParamCb(IntPtr ctx, IntPtr result, IntPtr errorMsg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenBatteryCb(IntPtr ctx, int result, IntPtr errorMsg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenInfoCb(IntPtr ctx, IntPtr info, IntPtr errorMsg);

    // Synchronized multi-device start/stop result (sen_multi_result_cb):
    // macs/oks/errors are parallel borrowed arrays of count entries
    // (char* / int / char*), valid for the callback scope only; the error
    // strings are empty on success.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenMultiResultCb(
        IntPtr ctx, IntPtr macs, IntPtr oks, IntPtr errors, UIntPtr count);

    internal static class Native
    {
#if UNITY_IOS && !UNITY_EDITOR
        // iOS statically links the SDK into the Unity binary.
        private const string Dll = "__Internal";
#else
        private const string Dll = "sensor";
#endif
        private const CallingConvention Cc = CallingConvention.Cdecl;

        // Mirror of SEN_CAPI_VERSION in sen_capi.h; bump in sync with the header.
        internal const uint ExpectedCapiVersion = 10;

        /* ---- controller ---- */

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_terminate();

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern uint sen_capi_version();

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern IntPtr sen_controller_create();

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_controller_destroy(IntPtr ctrl);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_controller_set_callbacks(
            IntPtr ctrl, in SenControllerCbs cbs, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern int sen_controller_is_enable(IntPtr ctrl);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern int sen_controller_is_scanning(IntPtr ctrl);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern int sen_controller_start_scan(IntPtr ctrl, int periodInMS);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern int sen_controller_stop_scan(IntPtr ctrl);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_controller_set_debug_enabled(IntPtr ctrl, int enabled);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_controller_set_data_log_enabled(IntPtr ctrl, int enabled);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern void sen_controller_set_log_path(
            IntPtr ctrl, int enabled,
            [MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern void sen_controller_log(
            IntPtr ctrl,
            [MarshalAs(UnmanagedType.LPStr)] string message,
            [MarshalAs(UnmanagedType.LPStr)] string level);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_controller_on_suspend(IntPtr ctrl);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern IntPtr sen_controller_require_sensor(
            IntPtr ctrl, [MarshalAs(UnmanagedType.LPStr)] string mac);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern IntPtr sen_controller_get_sensor(
            IntPtr ctrl, [MarshalAs(UnmanagedType.LPStr)] string mac);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern UIntPtr sen_controller_get_sensors(
            IntPtr ctrl, IntPtr[] outHandles, UIntPtr capacity);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern UIntPtr sen_controller_get_connected_sensors(
            IntPtr ctrl, IntPtr[] outHandles, UIntPtr capacity);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern int sen_controller_get_bin_file_info(
            IntPtr ctrl, [MarshalAs(UnmanagedType.LPStr)] string path,
            ref SenBinFileInfo outInfo);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern IntPtr sen_controller_replay_bin_file(
            IntPtr ctrl,
            [MarshalAs(UnmanagedType.LPStr)] string path,
            [MarshalAs(UnmanagedType.LPStr)] string deviceMac,
            int realtime, uint timeoutMs);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern UIntPtr sen_controller_multi_replay_bin_file(
            IntPtr ctrl,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] paths,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] macs,
            UIntPtr count, int realtime, uint timeoutMs,
            [Out] IntPtr[] outProfiles);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern void sen_controller_pause_bin_replay(
            IntPtr ctrl, [MarshalAs(UnmanagedType.LPStr)] string deviceMac,
            IntPtr buf, UIntPtr len);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern void sen_controller_resume_bin_replay(
            IntPtr ctrl, [MarshalAs(UnmanagedType.LPStr)] string deviceMac,
            IntPtr buf, UIntPtr len);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern void sen_controller_stop_bin_replay(
            IntPtr ctrl, [MarshalAs(UnmanagedType.LPStr)] string deviceMac,
            IntPtr buf, UIntPtr len);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern void sen_controller_parse_bin_to_csv(
            IntPtr ctrl,
            [MarshalAs(UnmanagedType.LPStr)] string binPath,
            [MarshalAs(UnmanagedType.LPStr)] string csvPath,
            IntPtr buf, UIntPtr len);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_controller_get_version(
            IntPtr ctrl, IntPtr buf, UIntPtr len);

        // Synchronized multi-device stream start/stop: cb fires exactly once
        // with the per-device results (see SenMultiResultCb).
        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_controller_multi_start_data(
            IntPtr ctrl, IntPtr[] profiles, UIntPtr count,
            int timeoutMs, int maxDelayDispersionMs, int maxAttempts,
            SenMultiResultCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_controller_multi_stop_data(
            IntPtr ctrl, IntPtr[] profiles, UIntPtr count,
            int timeoutMs, SenMultiResultCb cb, IntPtr ctx);

        /* ---- profile ---- */

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_profile_set_callbacks(
            IntPtr profile, in SenProfileCbs cbs, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_profile_get_device(
            IntPtr profile, ref SenBleDevice outDevice);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern int sen_profile_get_state(IntPtr profile);

        // Callback-async (Python asyncConnect/asyncDisconnect parity): cb may be
        // null (fire-and-forget); a non-null cb fires exactly once with the
        // final result and an empty errorMsg on success.
        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_profile_connect(IntPtr profile, SenCompletionCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_profile_disconnect(IntPtr profile, SenCompletionCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern int sen_profile_has_init(IntPtr profile);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern int sen_profile_has_start_data_notification(IntPtr profile);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_profile_init(
            IntPtr profile, int packageSampleCount, int timeoutMs,
            int powerRefreshIntervalMs, SenCompletionCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_profile_start_data(
            IntPtr profile, int timeoutMs, SenCompletionCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_profile_stop_data(
            IntPtr profile, int timeoutMs, SenCompletionCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_profile_get_battery_level(
            IntPtr profile, int timeoutMs, SenBatteryCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_profile_fetch_device_info(
            IntPtr profile, int timeoutMs, SenInfoCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_profile_get_device_info(
            IntPtr profile, ref SenDeviceInfo outInfo);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern void sen_profile_set_param(
            IntPtr profile, int timeoutMs,
            [MarshalAs(UnmanagedType.LPStr)] string key,
            [MarshalAs(UnmanagedType.LPStr)] string value,
            SenParamCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern void sen_profile_get_param(
            IntPtr profile, int timeoutMs,
            [MarshalAs(UnmanagedType.LPStr)] string key,
            SenParamCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_profile_set_auto_reconnect(IntPtr profile, int enabled);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern void sen_profile_log(
            IntPtr profile,
            [MarshalAs(UnmanagedType.LPStr)] string message,
            [MarshalAs(UnmanagedType.LPStr)] string level);
    }
}

#if !UNITY_5_3_OR_NEWER
// Compile-time stub of UnityEngine's AOT.MonoPInvokeCallbackAttribute so the
// reverse-P/Invoke callback markers in Sensor.cs compile outside Unity. Unity
// always defines UNITY_5_3_OR_NEWER, so Unity builds use the real attribute
// (UnityEngine.CoreModule, namespace AOT). It is a marker only: IL2CPP reverse
// P/Invoke works for static methods regardless, the attribute documents intent.
namespace AOT
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class MonoPInvokeCallbackAttribute : Attribute
    {
        public MonoPInvokeCallbackAttribute(Type type) { }
    }
}
#endif
