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

    [StructLayout(LayoutKind.Sequential)]
    public struct SenSample
    {
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

    [StructLayout(LayoutKind.Sequential)]
    public struct SenDataInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)]
        public string deviceMac;
        public int dataType;
        public int lostPackageCount;
        public float sampleRate;
        public int channelCount;
        public ulong channelMask;
        public int sampleCount;
        public uint startTimeStamp;
        public uint delay;
        public double startTimeSec;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string deviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SenDataView
    {
        public int startSampleIndex;
        public uint startTimeStamp;
        public IntPtr info;
        public IntPtr samples;
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
        public double ConnectionIntervalMs;
        public int PeripheralLatency;
        public int SupervisionTimeoutMs;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] backend;
        public byte GestChannelCount;
        public ushort GestSampleRate;

        public static SenDeviceInfo Create()
        {
            var info = new SenDeviceInfo();
            info.structSize = (uint)Marshal.SizeOf<SenDeviceInfo>();
            return info;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SenBinSensorDataConfig
    {
        public double sampleRate;
        public double k;
        public ulong channelMask;
        public int dataType;
        public byte typeIndex;
        public byte channelCount;
        public byte packageIndexLength;
        public byte resolutionBits;
        public byte resolutionSigned;
        public sbyte rawDataBias;
        public ushort packageSampleCount;
        public ushort minPackageSampleCount;
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
        public SenDeviceInfo deviceInfo;
        public uint ConfigVersion;
        public int chipType;
        public byte isUniversalStream;
        public byte isNewEmg;
        public byte isContainQat6;
        public byte ppgModel;
        public long featureMap;
        public long notifyDataFlag;
        public uint sensorDataCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 14)]
        public SenBinSensorDataConfig[] sensorDatas;
        public ulong firstDataTsMs;
        public ulong lastDataTsMs;

        public static SenBinFileInfo Create()
        {
            var info = new SenBinFileInfo();
            info.structSize = (uint)Marshal.SizeOf<SenBinFileInfo>();
            return info;
        }
    }

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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenAutoReconnectAnswerCb(IntPtr answerCtx, int handled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenAutoReconnectCb(IntPtr ctx, IntPtr profile, int hasLastSession,
                                              IntPtr answer, IntPtr answerCtx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenDeviceInfoUpdateCb(IntPtr ctx, IntPtr profile, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenDataTransferStateCb(IntPtr ctx, IntPtr profile, int isTransferring);

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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenCompletionCb(IntPtr ctx, int result, IntPtr errorMsg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenParamCb(IntPtr ctx, IntPtr result, IntPtr errorMsg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenBatteryCb(IntPtr ctx, int result, IntPtr errorMsg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenInfoCb(IntPtr ctx, IntPtr info, IntPtr errorMsg);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SenMultiResultCb(
        IntPtr ctx, IntPtr macs, IntPtr oks, IntPtr errors, UIntPtr count);

    internal static class Native
    {
#if UNITY_IOS && !UNITY_EDITOR
        private const string Dll = "__Internal";
#else
        private const string Dll = "sensor";
#endif
        private const CallingConvention Cc = CallingConvention.Cdecl;

        internal const uint ExpectedCapiVersion = 20;

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
        internal static extern void sen_controller_scan_once(IntPtr ctrl, int periodInMS, SenScanResultCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern void sen_controller_log(
            IntPtr ctrl,
            [MarshalAs(UnmanagedType.LPStr)] string message,
            [MarshalAs(UnmanagedType.LPStr)] string level);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_controller_on_suspend(IntPtr ctrl);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_controller_check_setup_dongle(
            IntPtr ctrl, SenParamCb cb, IntPtr ctx);

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
            SenParamCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_controller_get_version(
            IntPtr ctrl, IntPtr buf, UIntPtr len);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern void sen_controller_get_param(
            IntPtr ctrl, [MarshalAs(UnmanagedType.LPStr)] string key,
            SenParamCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc, CharSet = CharSet.Ansi)]
        internal static extern void sen_controller_set_param(
            IntPtr ctrl, [MarshalAs(UnmanagedType.LPStr)] string key,
            [MarshalAs(UnmanagedType.LPStr)] string value,
            SenParamCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_controller_multi_start_data(
            IntPtr ctrl, IntPtr[] profiles, UIntPtr count,
            int timeoutMs, int maxDelayDispersionMs, int maxAttempts,
            SenMultiResultCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_controller_multi_stop_data(
            IntPtr ctrl, IntPtr[] profiles, UIntPtr count,
            int timeoutMs, SenMultiResultCb cb, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_profile_set_callbacks(
            IntPtr profile, in SenProfileCbs cbs, IntPtr ctx);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern void sen_profile_get_device(
            IntPtr profile, ref SenBleDevice outDevice);

        [DllImport(Dll, CallingConvention = Cc)]
        internal static extern int sen_profile_get_state(IntPtr profile);

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
namespace AOT
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class MonoPInvokeCallbackAttribute : Attribute
    {
        public MonoPInvokeCallbackAttribute(Type type) { }
    }
}
#endif
