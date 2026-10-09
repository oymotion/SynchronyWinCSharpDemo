#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AOT;
using SensorSdk.Capi;

namespace SensorSdk
{
    public sealed class SensorException : Exception
    {
        public SensorException(string message) : base(message) { }
    }

    internal static class CapiString
    {
        internal static string FromBytes(byte[] bytes)
        {
            if (bytes == null) return string.Empty;
            int len = Array.IndexOf(bytes, (byte)0);
            if (len < 0) len = bytes.Length;
            return Encoding.ASCII.GetString(bytes, 0, len);
        }

        internal static string FromPtr(IntPtr ptr)
        {
            return ptr == IntPtr.Zero ? string.Empty
                : (Marshal.PtrToStringAnsi(ptr) ?? string.Empty);
        }

        internal static string ReadOutString(Func<IntPtr, UIntPtr, bool> fill, int capacity = 4096)
        {
            IntPtr buf = Marshal.AllocHGlobal(capacity);
            try
            {
                fill(buf, (UIntPtr)capacity);
                return FromPtr(buf);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    internal static class CallbackGuard
    {
        private static readonly ConcurrentDictionary<int, byte> SdkCallbackThreads = new();

        internal static void Log(Exception ex)
            => Console.Error.WriteLine("sensor callback threw: " + ex);

        internal static void MarkSdkCallbackThread()
            => SdkCallbackThreads.TryAdd(Environment.CurrentManagedThreadId, 0);

        internal static void ThrowOnSdkCallbackThread()
        {
            if (SdkCallbackThreads.ContainsKey(Environment.CurrentManagedThreadId))
                throw new InvalidOperationException(
                    "blocking sync call from an SDK callback thread");
        }
    }

    internal static class SyncWait
    {
        internal static bool Await(Task op, int backstopMs)
        {
            if (Task.WhenAny(op, Task.Delay(backstopMs)).GetAwaiter().GetResult() != op)
                return false;
            try
            {
                op.GetAwaiter().GetResult();
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static T Await<T>(Task<T> op, int backstopMs, T timeoutValue)
        {
            if (Task.WhenAny(op, Task.Delay(backstopMs)).GetAwaiter().GetResult() != op)
                return timeoutValue;
            try
            {
                return op.GetAwaiter().GetResult();
            }
            catch
            {
                return timeoutValue;
            }
        }

        internal static string AwaitString(Task<string> op, int backstopMs)
        {
            if (Task.WhenAny(op, Task.Delay(backstopMs)).GetAwaiter().GetResult() != op)
                return "Error: Timeout";
            try
            {
                return op.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    public struct Sample
    {
        public double AbsTimeStampInSec;
        public int ChannelIndex;
        public int SampleIndex;
        public int RawData;
        public float Data;
        public float Impedance;
        public float Saturation;
        public bool IsLost;
    }

    public sealed class SensorData
    {
        private const int SampleSize = 40;
        private const int OffAbsTimeStamp = 0;
        private const int OffChannelIndex = 8;
        private const int OffSampleIndex = 12;
        private const int OffRawData = 16;
        private const int OffData = 20;
        private const int OffImpedance = 24;
        private const int OffSaturation = 28;
        private const int OffIsLost = 32;

        private const int OffInfoDataType = 20;
        private const int OffInfoLostPackageCount = 24;
        private const int OffInfoSampleRate = 28;
        private const int OffInfoChannelCount = 32;
        private const int OffInfoChannelMask = 40;
        private const int OffInfoSampleCount = 48;
        private const int OffInfoStartTimeStamp = 52;
        private const int OffInfoDelay = 56;
        private const int OffInfoStartTimeSec = 64;
        private const int OffInfoDeviceName = 72;

        public string DeviceMac => ReadInfoString(0, i => i.deviceMac);
        public string DeviceName => ReadInfoString(OffInfoDeviceName, i => i.deviceName);
        public SenDataType DataType => (SenDataType)ReadInfoInt32(OffInfoDataType, i => i.dataType);
        public int LostPackageCount => ReadInfoInt32(OffInfoLostPackageCount, i => i.lostPackageCount);
        public float SampleRate => ReadInfoSingle(OffInfoSampleRate, i => i.sampleRate);
        public int ChannelCount => ReadInfoInt32(OffInfoChannelCount, i => i.channelCount);
        public ulong ChannelMask => (ulong)ReadInfoInt64(OffInfoChannelMask, i => (long)i.channelMask);
        public int SampleCount => ReadInfoInt32(OffInfoSampleCount, i => i.sampleCount);
        public int StartSampleIndex { get; }

        public uint StartTimeStamp { get; }

        public uint Delay => (uint)ReadInfoInt32(OffInfoDelay, i => (int)i.delay);

        public double StartTimeSec => ReadInfoDouble(OffInfoStartTimeSec, i => i.startTimeSec);

        public bool IsBorrowed => _ownedSamples == null;

        private readonly IntPtr _infoPtr;
        private SenDataInfo? _ownInfo;
        private readonly IntPtr _samplesPtr;
        private readonly long _samplesBytes;
        private readonly byte[]? _ownedSamples;
        private Sample[][]? _channelSamples;

        internal SensorData(in SenDataView view)
        {
            StartSampleIndex = view.startSampleIndex;
            StartTimeStamp = view.startTimeStamp;
            _infoPtr = view.info;
            _samplesPtr = view.samples;
            _samplesBytes = (long)view.samplesBytes.ToUInt64();
        }

        private SensorData(SensorData src, byte[] owned)
        {
            _infoPtr = IntPtr.Zero;
            if (src._ownInfo.HasValue)
                _ownInfo = src._ownInfo;
            else if (src._infoPtr != IntPtr.Zero)
                _ownInfo = Marshal.PtrToStructure<SenDataInfo>(src._infoPtr);
            StartSampleIndex = src.StartSampleIndex;
            StartTimeStamp = src.StartTimeStamp;
            _samplesPtr = IntPtr.Zero;
            _samplesBytes = owned.Length;
            _ownedSamples = owned;
        }

        private int ReadInfoInt32(int off, Func<SenDataInfo, int> fromOwned)
            => _ownInfo.HasValue ? fromOwned(_ownInfo.Value)
               : _infoPtr != IntPtr.Zero ? Marshal.ReadInt32(_infoPtr, off) : 0;

        private long ReadInfoInt64(int off, Func<SenDataInfo, long> fromOwned)
            => _ownInfo.HasValue ? fromOwned(_ownInfo.Value)
               : _infoPtr != IntPtr.Zero ? Marshal.ReadInt64(_infoPtr, off) : 0L;

        private float ReadInfoSingle(int off, Func<SenDataInfo, float> fromOwned)
            => _ownInfo.HasValue ? fromOwned(_ownInfo.Value)
               : _infoPtr != IntPtr.Zero ? Int32BitsToSingle(Marshal.ReadInt32(_infoPtr, off)) : 0f;

        private double ReadInfoDouble(int off, Func<SenDataInfo, double> fromOwned)
            => _ownInfo.HasValue ? fromOwned(_ownInfo.Value)
               : _infoPtr != IntPtr.Zero ? Int64BitsToDouble(Marshal.ReadInt64(_infoPtr, off)) : 0.0;

        private string ReadInfoString(int off, Func<SenDataInfo, string?> fromOwned)
            => _ownInfo.HasValue ? fromOwned(_ownInfo.Value) ?? string.Empty
               : _infoPtr != IntPtr.Zero
                 ? Marshal.PtrToStringAnsi(IntPtr.Add(_infoPtr, off)) ?? string.Empty
               : string.Empty;

        private bool HasSamples
            => _ownedSamples != null ? _ownedSamples.Length > 0
                                     : _samplesPtr != IntPtr.Zero;

        public SensorData Clone()
        {
            int n = checked((int)(_ownedSamples != null ? _ownedSamples.Length : _samplesBytes));
            var buf = new byte[n];
            if (_ownedSamples != null) Array.Copy(_ownedSamples, buf, n);
            else if (_samplesPtr != IntPtr.Zero && n > 0)
                Marshal.Copy(_samplesPtr, buf, 0, n);
            return new SensorData(this, buf);
        }

        public Sample[][] ChannelSamples => _channelSamples ??= BuildChannelSamples();

        public bool IsDataValid(int channel = 0, int sampleIndex = 0)
        {
            if (channel < 0 || channel >= ChannelCount) return false;
            if (sampleIndex < 0 || sampleIndex >= SampleCount) return false;
            if (!HasSamples) return false;
            if (StartTimeStamp != (uint)ReadInfoInt32(OffInfoStartTimeStamp, i => (int)i.startTimeStamp)) return false;
            int off = (channel * SampleCount + sampleIndex) * SampleSize;
            return ReadInt32(off + OffSampleIndex) == StartSampleIndex + sampleIndex;
        }

        public bool IsChannelEnabled(int channel)
            => channel >= 0 && channel < 64 && ((ChannelMask >> channel) & 1UL) != 0;

        public Sample GetChannelSample(int channel, int sampleIndex)
        {
            int off = CheckedSlotOffset(channel, sampleIndex);
            return new Sample
            {
                AbsTimeStampInSec = ReadDouble(off + OffAbsTimeStamp),
                ChannelIndex = ReadInt32(off + OffChannelIndex),
                SampleIndex = ReadInt32(off + OffSampleIndex),
                RawData = ReadInt32(off + OffRawData),
                Data = ReadSingle(off + OffData),
                Impedance = ReadSingle(off + OffImpedance),
                Saturation = ReadSingle(off + OffSaturation),
                IsLost = ReadByte(off + OffIsLost) != 0
            };
        }

        public float GetData(int channel, int sampleIndex)
            => ReadSingle(CheckedSlotOffset(channel, sampleIndex) + OffData);

        public int GetTimeStampInMs(int channel, int sampleIndex)
        {
            float rate = SampleRate;
            return rate > 0
                ? (int)(GetSampleIndex(channel, sampleIndex) * 1000.0 / rate)
                : 0;
        }

        public double GetAbsTimeStampInSec(int channel, int sampleIndex)
            => ReadDouble(CheckedSlotOffset(channel, sampleIndex) + OffAbsTimeStamp);

        public int GetSampleIndex(int channel, int sampleIndex)
            => ReadInt32(CheckedSlotOffset(channel, sampleIndex) + OffSampleIndex);

        public int GetRawData(int channel, int sampleIndex)
            => ReadInt32(CheckedSlotOffset(channel, sampleIndex) + OffRawData);

        public float GetImpedance(int channel, int sampleIndex)
            => ReadSingle(CheckedSlotOffset(channel, sampleIndex) + OffImpedance);

        public float GetSaturation(int channel, int sampleIndex)
            => ReadSingle(CheckedSlotOffset(channel, sampleIndex) + OffSaturation);

        public bool IsLost(int channel, int sampleIndex)
            => ReadByte(CheckedSlotOffset(channel, sampleIndex) + OffIsLost) != 0;

        private int CheckedSlotOffset(int channel, int sampleIndex)
        {
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel), channel,
                    $"valid channel range is [0, {ChannelCount})");
            if (sampleIndex < 0 || sampleIndex >= SampleCount)
                throw new ArgumentOutOfRangeException(nameof(sampleIndex), sampleIndex,
                    $"valid sampleIndex range is [0, {SampleCount})");
            return (channel * SampleCount + sampleIndex) * SampleSize;
        }

        private int ReadInt32(int off)
            => _ownedSamples != null
                ? BitConverter.ToInt32(_ownedSamples, off)
                : Marshal.ReadInt32(_samplesPtr, off);

        private float ReadSingle(int off)
            => _ownedSamples != null
                ? BitConverter.ToSingle(_ownedSamples, off)
                : Int32BitsToSingle(Marshal.ReadInt32(_samplesPtr, off));

        private double ReadDouble(int off)
            => _ownedSamples != null
                ? BitConverter.ToDouble(_ownedSamples, off)
                : Int64BitsToDouble(Marshal.ReadInt64(_samplesPtr, off));

        private static float Int32BitsToSingle(int value)
            => new Int32SingleUnion { Int32 = value }.Single;

        [StructLayout(LayoutKind.Explicit)]
        private struct Int32SingleUnion
        {
            [FieldOffset(0)] public int Int32;
            [FieldOffset(0)] public float Single;
        }

        private static double Int64BitsToDouble(long value)
            => new Int64DoubleUnion { Int64 = value }.Double;

        [StructLayout(LayoutKind.Explicit)]
        private struct Int64DoubleUnion
        {
            [FieldOffset(0)] public long Int64;
            [FieldOffset(0)] public double Double;
        }

        private byte ReadByte(int off)
            => _ownedSamples != null
                ? _ownedSamples[off]
                : Marshal.ReadByte(_samplesPtr, off);

        private Sample[][] BuildChannelSamples()
        {
            var cols = new Sample[ChannelCount][];
            for (int ch = 0; ch < ChannelCount; ch++)
            {
                var col = new Sample[SampleCount];
                for (int i = 0; i < SampleCount; i++)
                {
                    if (IsDataValid(ch, i)) col[i] = GetChannelSample(ch, i);
                }
                cols[ch] = col;
            }
            return cols;
        }
    }

    public struct BleDevice
    {
        public string Name;
        public string Mac;
        public short Rssi;

        internal static BleDevice FromNative(in SenBleDevice d)
        {
            return new BleDevice
            {
                Name = CapiString.FromBytes(d.name),
                Mac = CapiString.FromBytes(d.mac),
                Rssi = d.rssi
            };
        }
    }

    public sealed class DeviceInfo
    {
        public string DeviceName = string.Empty;
        public string ModelName = string.Empty;
        public string HardwareVersion = string.Empty;
        public string FirmwareVersion = string.Empty;
        public ushort MTUSize;
        public byte IsMTUFine;
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
        public string Backend = string.Empty;
        public byte GestChannelCount;
        public ushort GestSampleRate;

        internal static DeviceInfo FromNative(in SenDeviceInfo i)
        {
            return new DeviceInfo
            {
                DeviceName = CapiString.FromBytes(i.deviceName),
                ModelName = CapiString.FromBytes(i.modelName),
                HardwareVersion = CapiString.FromBytes(i.hardwareVersion),
                FirmwareVersion = CapiString.FromBytes(i.firmwareVersion),
                MTUSize = i.MTUSize,
                IsMTUFine = i.isMTUFine,
                EMGGain = i.EMGGain,
                EEGGain = i.EEGGain,
                ECGGain = i.ECGGain,
                EMGChannelCount = i.EMGChannelCount,
                EEGChannelCount = i.EEGChannelCount,
                ECGChannelCount = i.ECGChannelCount,
                BRTHChannelCount = i.BRTHChannelCount,
                AccChannelCount = i.AccChannelCount,
                GyroChannelCount = i.GyroChannelCount,
                MagAngleChannelCount = i.MagAngleChannelCount,
                EMGSampleRate = i.EMGSampleRate,
                EEGSampleRate = i.EEGSampleRate,
                ECGSampleRate = i.ECGSampleRate,
                BRTHSampleRate = i.BRTHSampleRate,
                AccSampleRate = i.AccSampleRate,
                GyroSampleRate = i.GyroSampleRate,
                MagAngleSampleRate = i.MagAngleSampleRate,
                ImuChannelCount = i.ImuChannelCount,
                ImuSampleRate = i.ImuSampleRate,
                EulerChannelCount = i.EulerChannelCount,
                EulerSampleRate = i.EulerSampleRate,
                QuatChannelCount = i.QuatChannelCount,
                QuatSampleRate = i.QuatSampleRate,
                PpgChannelCount = i.PpgChannelCount,
                PpgSampleRate = i.PpgSampleRate,
                Spo2ChannelCount = i.Spo2ChannelCount,
                Spo2SampleRate = i.Spo2SampleRate,
                ImpeChannelCount = i.ImpeChannelCount,
                ImpeSampleRate = i.ImpeSampleRate,
                EmgMaxSampleRate = i.EmgMaxSampleRate,
                EegMaxSampleRate = i.EegMaxSampleRate,
                EcgMaxSampleRate = i.EcgMaxSampleRate,
                ConnectionIntervalMs = i.ConnectionIntervalMs,
                PeripheralLatency = i.PeripheralLatency,
                SupervisionTimeoutMs = i.SupervisionTimeoutMs,
                Backend = CapiString.FromBytes(i.backend),
                GestChannelCount = i.GestChannelCount,
                GestSampleRate = i.GestSampleRate
            };
        }
    }

    public sealed class BinSensorDataConfig
    {
        public double SampleRate;
        public double K;
        public ulong ChannelMask;
        public SenDataType DataType;
        public byte TypeIndex;
        public byte ChannelCount;
        public byte PackageIndexLength;
        public byte ResolutionBits;
        public byte ResolutionSigned;
        public sbyte RawDataBias;
        public ushort PackageSampleCount;
        public ushort MinPackageSampleCount;

        internal static BinSensorDataConfig FromNative(in SenBinSensorDataConfig c)
        {
            return new BinSensorDataConfig
            {
                SampleRate = c.sampleRate,
                K = c.k,
                ChannelMask = c.channelMask,
                DataType = (SenDataType)c.dataType,
                TypeIndex = c.typeIndex,
                ChannelCount = c.channelCount,
                PackageIndexLength = c.packageIndexLength,
                ResolutionBits = c.resolutionBits,
                ResolutionSigned = c.resolutionSigned,
                RawDataBias = c.rawDataBias,
                PackageSampleCount = c.packageSampleCount,
                MinPackageSampleCount = c.minPackageSampleCount
            };
        }
    }

    public sealed class BinFileInfo
    {
        public string Mac = string.Empty;
        public string DeviceName = string.Empty;
        public double DurationSec;
        public bool Valid;
        public DeviceInfo DeviceInfo = new DeviceInfo();
        public uint ConfigVersion;
        public int ChipType;
        public bool IsUniversalStream;
        public bool IsNewEmg;
        public bool IsContainQat6;
        public byte PpgModel;
        public long FeatureMap;
        public long NotifyDataFlag;
        public List<BinSensorDataConfig> SensorDatas = new List<BinSensorDataConfig>();
        public ulong FirstDataTsMs;
        public ulong LastDataTsMs;

        internal static BinFileInfo FromNative(in SenBinFileInfo i)
        {
            var info = new BinFileInfo
            {
                Mac = CapiString.FromBytes(i.mac),
                DeviceName = CapiString.FromBytes(i.deviceName),
                DurationSec = i.durationSec,
                Valid = i.valid != 0,
                DeviceInfo = SensorSdk.DeviceInfo.FromNative(in i.deviceInfo),
                ConfigVersion = i.ConfigVersion,
                ChipType = i.chipType,
                IsUniversalStream = i.isUniversalStream != 0,
                IsNewEmg = i.isNewEmg != 0,
                IsContainQat6 = i.isContainQat6 != 0,
                PpgModel = i.ppgModel,
                FeatureMap = i.featureMap,
                NotifyDataFlag = i.notifyDataFlag,
                FirstDataTsMs = i.firstDataTsMs,
                LastDataTsMs = i.lastDataTsMs
            };
            int count = (int)i.sensorDataCount;
            SenBinSensorDataConfig[]? nativeDatas = i.sensorDatas;
            if (nativeDatas != null)
            {
                if (count > nativeDatas.Length) count = nativeDatas.Length;
                for (int n = 0; n < count; n++)
                    info.SensorDatas.Add(BinSensorDataConfig.FromNative(in nativeDatas[n]));
            }
            return info;
        }
    }

    public sealed class SensorProfile
    {
        internal IntPtr Handle { get; }
        private GCHandle _ctxHandle;
        private DeviceInfo? _deviceInfoCache;
        private BleDevice? _bleDeviceCache;
        private int _powerCache = -1;

        public event Action<SensorProfile, List<SensorData>>? DataReceived;
        public event Action<SensorProfile, SenDeviceState>? StateChanged;
        public event Action<SensorProfile, string>? ErrorReceived;
        public event Action<SensorProfile, int>? PowerChanged;
        public event Action<SensorProfile, DeviceInfo>? DeviceInfoUpdated;
        public event Action<SensorProfile, bool>? DataTransferStateChanged;
        public Action<SensorProfile, bool, Action<bool>>? OnAutoReconnect { get; set; }

        internal SensorProfile(IntPtr handle)
        {
            Handle = handle;
            _ctxHandle = GCHandle.Alloc(this);
            var cbs = new SenProfileCbs
            {
                structSize = (uint)Marshal.SizeOf<SenProfileCbs>(),
                onData = Marshal.GetFunctionPointerForDelegate(NativeCallbacks.Data),
                onStateChange = Marshal.GetFunctionPointerForDelegate(NativeCallbacks.State),
                onError = Marshal.GetFunctionPointerForDelegate(NativeCallbacks.Error),
                onPowerChange = Marshal.GetFunctionPointerForDelegate(NativeCallbacks.Power),
                onAutoReconnect = Marshal.GetFunctionPointerForDelegate(NativeCallbacks.AutoReconnect),
                onDeviceInfoUpdate = Marshal.GetFunctionPointerForDelegate(NativeCallbacks.DeviceInfoUpdate),
                onDataTransferStateChange = Marshal.GetFunctionPointerForDelegate(NativeCallbacks.DataTransferState)
            };
            Native.sen_profile_set_callbacks(Handle, in cbs, GCHandle.ToIntPtr(_ctxHandle));
        }

        internal void ReleaseContext()
        {
            if (_ctxHandle.IsAllocated) _ctxHandle.Free();
        }

        public BleDevice Device
        {
            get
            {
                if (_bleDeviceCache.HasValue) return _bleDeviceCache.Value;
                var d = new SenBleDevice();
                Native.sen_profile_get_device(Handle, ref d);
                _bleDeviceCache = BleDevice.FromNative(in d);
                return _bleDeviceCache.Value;
            }
        }

        public SenDeviceState DeviceState => (SenDeviceState)Native.sen_profile_get_state(Handle);
        public bool IsReady => DeviceState == SenDeviceState.Ready;
        public bool HasInited => Native.sen_profile_has_init(Handle) != 0;
        public bool IsDataTransfering => Native.sen_profile_has_start_data_notification(Handle) != 0;

        public Task<bool> ConnectAsync()
        {
            var op = new CompletionOp<bool>();
            Native.sen_profile_connect(Handle, NativeCallbacks.BoolCompletion, op.CtxPtr);
            return op.Task;
        }

        public Task<bool> DisconnectAsync()
        {
            var op = new CompletionOp<bool>();
            Native.sen_profile_disconnect(Handle, NativeCallbacks.BoolCompletion, op.CtxPtr);
            return op.Task;
        }

        public Task InitAsync(int packageSampleCount, int powerRefreshIntervalMs = 0, int timeoutMs = 0)
        {
            var op = new CompletionOp<string>();
            Native.sen_profile_init(Handle, packageSampleCount, timeoutMs,
                powerRefreshIntervalMs, NativeCallbacks.Completion, op.CtxPtr);
            return op.Task;
        }

        public Task StartDataNotificationAsync(int timeoutMs = 0)
        {
            var op = new CompletionOp<string>();
            Native.sen_profile_start_data(Handle, timeoutMs, NativeCallbacks.Completion, op.CtxPtr);
            return op.Task;
        }

        public Task StopDataNotificationAsync(int timeoutMs = 0)
        {
            var op = new CompletionOp<string>();
            Native.sen_profile_stop_data(Handle, timeoutMs, NativeCallbacks.Completion, op.CtxPtr);
            return op.Task;
        }

        public async Task<int> GetBatteryLevelAsync(int timeoutMs = 0)
        {
            var op = new CompletionOp<int>();
            Native.sen_profile_get_battery_level(Handle, timeoutMs, NativeCallbacks.Battery, op.CtxPtr);
            int level = await op.Task.ConfigureAwait(false);
            if (level >= 0) _powerCache = level;
            return level;
        }

        public async Task<DeviceInfo> FetchDeviceInfoAsync(int timeoutMs = 0)
        {
            var op = new CompletionOp<DeviceInfo>();
            Native.sen_profile_fetch_device_info(Handle, timeoutMs, NativeCallbacks.Info, op.CtxPtr);
            var info = await op.Task.ConfigureAwait(false);
            _deviceInfoCache = info;
            return info;
        }

        public DeviceInfo? GetDeviceInfo()
        {
            if (_deviceInfoCache != null) return _deviceInfoCache;
            var info = SenDeviceInfo.Create();
            Native.sen_profile_get_device_info(Handle, ref info);
            var outInfo = DeviceInfo.FromNative(in info);
            if (outInfo.DeviceName.Length == 0 && outInfo.ModelName.Length == 0) return null;
            _deviceInfoCache = outInfo;
            return outInfo;
        }

        public Task<string> SetParamAsync(string key, string value, int timeoutMs = 0)
        {
            var op = new CompletionOp<string>();
            Native.sen_profile_set_param(Handle, timeoutMs, key, value, NativeCallbacks.Param, op.CtxPtr);
            return op.Task;
        }

        public Task<string> GetParamAsync(string key, int timeoutMs = 0)
        {
            var op = new CompletionOp<string>();
            Native.sen_profile_get_param(Handle, timeoutMs, key, NativeCallbacks.Param, op.CtxPtr);
            return op.Task;
        }

        public bool Connect()
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.Await(ConnectAsync(), 40000, false);
        }

        public bool Disconnect()
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.Await(DisconnectAsync(), 25000, false);
        }

        public bool Init(int packageSampleCount, int powerRefreshIntervalMs = 0,
            int timeoutMs = 0)
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.Await(
                InitAsync(packageSampleCount, powerRefreshIntervalMs, timeoutMs),
                (timeoutMs > 0 ? timeoutMs : SensorController.AssumedCmdTimeoutMs) + 15000);
        }

        public bool StartDataNotification(int timeoutMs = 0)
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.Await(StartDataNotificationAsync(timeoutMs),
                (timeoutMs > 0 ? timeoutMs : SensorController.AssumedCmdTimeoutMs) + 15000);
        }

        public bool StopDataNotification(int timeoutMs = 0)
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.Await(StopDataNotificationAsync(timeoutMs),
                (timeoutMs > 0 ? timeoutMs : SensorController.AssumedCmdTimeoutMs) + 15000);
        }

        public string SetParam(string key, string value, int timeoutMs = 0)
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.AwaitString(SetParamAsync(key, value, timeoutMs),
                (timeoutMs > 0 ? timeoutMs : SensorController.AssumedCmdTimeoutMs) + 15000);
        }

        public string GetParam(string key, int timeoutMs = 0)
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.AwaitString(GetParamAsync(key, timeoutMs),
                (timeoutMs > 0 ? timeoutMs : SensorController.AssumedCmdTimeoutMs) + 15000);
        }

        public int GetBatteryLevel()
        {
            if (_powerCache >= 0) return _powerCache;
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.Await(GetBatteryLevelAsync(),
                SensorController.AssumedCmdTimeoutMs + 15000, -1);
        }

        public DeviceInfo? FetchDeviceInfo(int timeoutMs = 0)
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.Await<DeviceInfo>(FetchDeviceInfoAsync(timeoutMs),
                (timeoutMs > 0 ? timeoutMs : SensorController.AssumedCmdTimeoutMs) + 15000, null!);
        }

        public void SetAutoReconnect(bool enabled)
            => Native.sen_profile_set_auto_reconnect(Handle, enabled ? 1 : 0);

        public void Log(string message, string level = "I")
        {
            try
            {
                Native.sen_profile_log(Handle, message ?? string.Empty, level ?? "I");
            }
            catch
            {
            }
        }

        internal void RaiseData(IntPtr views, int viewCount)
        {
            var handler = DataReceived;
            if (handler == null) return;
            var batch = new List<SensorData>(viewCount);
            int viewSize = Marshal.SizeOf<SenDataView>();
            for (int i = 0; i < viewCount; i++)
            {
                SenDataView view = Marshal.PtrToStructure<SenDataView>(
                    IntPtr.Add(views, i * viewSize));
                batch.Add(new SensorData(in view));
            }
            foreach (Action<SensorProfile, List<SensorData>> h in
                     handler.GetInvocationList())
            {
                try
                {
                    h(this, batch);
                }
                catch (Exception ex)
                {
                    CallbackGuard.Log(ex);
                }
            }
        }

        internal void RaiseState(int newState)
        {
            if (newState == (int)SenDeviceState.Disconnected)
            {
                _deviceInfoCache = null;
                _bleDeviceCache = null;
                _powerCache = -1;
            }
            var handler = StateChanged;
            if (handler == null) return;
            foreach (Action<SensorProfile, SenDeviceState> h in
                     handler.GetInvocationList())
            {
                try
                {
                    h(this, (SenDeviceState)newState);
                }
                catch (Exception ex)
                {
                    CallbackGuard.Log(ex);
                }
            }
        }

        internal void RaiseError(IntPtr errorMsg)
        {
            var handler = ErrorReceived;
            if (handler == null) return;
            string msg = CapiString.FromPtr(errorMsg);
            foreach (Action<SensorProfile, string> h in handler.GetInvocationList())
            {
                try
                {
                    h(this, msg);
                }
                catch (Exception ex)
                {
                    CallbackGuard.Log(ex);
                }
            }
        }

        internal void RaisePower(int power)
        {
            _powerCache = power;
            var handler = PowerChanged;
            if (handler == null) return;
            foreach (Action<SensorProfile, int> h in handler.GetInvocationList())
            {
                try
                {
                    h(this, power);
                }
                catch (Exception ex)
                {
                    CallbackGuard.Log(ex);
                }
            }
        }

        internal void RaiseAutoReconnect(int hasLastSession, IntPtr answer, IntPtr answerCtx)
        {
            SenAutoReconnectAnswerCb? answerFn = null;
            bool answered = false;
            void AnswerOnce(bool handled)
            {
                if (answered || answerFn == null) return;
                answered = true;
                try
                {
                    answerFn(answerCtx, handled ? 1 : 0);
                }
                catch (Exception ex)
                {
                    CallbackGuard.Log(ex);
                }
            }
            try
            {
                answerFn = Marshal.GetDelegateForFunctionPointer<SenAutoReconnectAnswerCb>(answer);
                var handler = OnAutoReconnect;
                if (handler == null)
                {
                    AnswerOnce(false);
                    return;
                }
                handler(this, hasLastSession != 0, AnswerOnce);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
                AnswerOnce(false);
            }
        }

        internal void RaiseDeviceInfoUpdate(IntPtr info)
        {
            if (info == IntPtr.Zero) return;
            SenDeviceInfo native = Marshal.PtrToStructure<SenDeviceInfo>(info);
            var deviceInfo = DeviceInfo.FromNative(in native);
            _deviceInfoCache = deviceInfo;
            var handler = DeviceInfoUpdated;
            if (handler == null) return;
            foreach (Action<SensorProfile, DeviceInfo> h in handler.GetInvocationList())
            {
                try
                {
                    h(this, deviceInfo);
                }
                catch (Exception ex)
                {
                    CallbackGuard.Log(ex);
                }
            }
        }

        internal void RaiseDataTransferState(int isTransferring)
        {
            var handler = DataTransferStateChanged;
            if (handler == null) return;
            foreach (Action<SensorProfile, bool> h in handler.GetInvocationList())
            {
                try
                {
                    h(this, isTransferring != 0);
                }
                catch (Exception ex)
                {
                    CallbackGuard.Log(ex);
                }
            }
        }
    }

    internal static class NativeCallbacks
    {
        internal static readonly SenDataCb Data = OnData;
        internal static readonly SenStateCb State = OnState;
        internal static readonly SenErrorCb Error = OnError;
        internal static readonly SenPowerCb Power = OnPower;
        internal static readonly SenAutoReconnectCb AutoReconnect = OnAutoReconnect;
        internal static readonly SenDeviceInfoUpdateCb DeviceInfoUpdate = OnDeviceInfoUpdate;
        internal static readonly SenDataTransferStateCb DataTransferState = OnDataTransferState;
        internal static readonly SenScanResultCb ScanResult = OnScanResult;
        internal static readonly SenScanResultCb ScanOnceResult = OnScanOnceResult;
        internal static readonly SenEnableChangedCb EnableChanged = OnEnableChanged;
        internal static readonly SenCompletionCb Completion = OnCompletion;
        internal static readonly SenCompletionCb BoolCompletion = OnBoolCompletion;
        internal static readonly SenParamCb Param = OnParam;
        internal static readonly SenBatteryCb Battery = OnBattery;
        internal static readonly SenInfoCb Info = OnInfo;
        internal static readonly SenMultiResultCb MultiResult = OnMultiResult;

        private static SensorProfile ProfileFromCtx(IntPtr ctx)
            => (SensorProfile)GCHandle.FromIntPtr(ctx).Target!;

        [MonoPInvokeCallback(typeof(SenDataCb))]
        private static void OnData(IntPtr ctx, IntPtr profile, IntPtr views, UIntPtr viewCount)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                ProfileFromCtx(ctx).RaiseData(views, checked((int)viewCount));
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenStateCb))]
        private static void OnState(IntPtr ctx, IntPtr profile, int newState)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                ProfileFromCtx(ctx).RaiseState(newState);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenErrorCb))]
        private static void OnError(IntPtr ctx, IntPtr profile, IntPtr errorMsg)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                ProfileFromCtx(ctx).RaiseError(errorMsg);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenPowerCb))]
        private static void OnPower(IntPtr ctx, IntPtr profile, int power)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                ProfileFromCtx(ctx).RaisePower(power);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenAutoReconnectCb))]
        private static void OnAutoReconnect(IntPtr ctx, IntPtr profile, int hasLastSession,
                                            IntPtr answer, IntPtr answerCtx)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                ProfileFromCtx(ctx).RaiseAutoReconnect(hasLastSession, answer, answerCtx);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenDeviceInfoUpdateCb))]
        private static void OnDeviceInfoUpdate(IntPtr ctx, IntPtr profile, IntPtr info)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                ProfileFromCtx(ctx).RaiseDeviceInfoUpdate(info);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenDataTransferStateCb))]
        private static void OnDataTransferState(IntPtr ctx, IntPtr profile, int isTransferring)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                ProfileFromCtx(ctx).RaiseDataTransferState(isTransferring);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenScanResultCb))]
        private static void OnScanResult(IntPtr ctx, IntPtr devices, UIntPtr count)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                ((SensorController)GCHandle.FromIntPtr(ctx).Target!)
                    .RaiseScanResult(devices, checked((int)count));
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenScanResultCb))]
        private static void OnScanOnceResult(IntPtr ctx, IntPtr devices, UIntPtr count)
        {
            CallbackGuard.MarkSdkCallbackThread();
            ScanOnceOp.Complete(ctx, devices, checked((int)count));
        }

        [MonoPInvokeCallback(typeof(SenEnableChangedCb))]
        private static void OnEnableChanged(IntPtr ctx, int enabled)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                ((SensorController)GCHandle.FromIntPtr(ctx).Target!).RaiseEnableChanged(enabled != 0);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenCompletionCb))]
        private static void OnCompletion(IntPtr ctx, int result, IntPtr errorMsg)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                CompletionOp<string>.Complete(ctx, result, errorMsg, msg => msg);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenCompletionCb))]
        private static void OnBoolCompletion(IntPtr ctx, int result, IntPtr errorMsg)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                CompletionOp<bool>.Complete(ctx, errorMsg, _ => result != 0);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenParamCb))]
        private static void OnParam(IntPtr ctx, IntPtr result, IntPtr errorMsg)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                CompletionOp<string>.Complete(ctx, errorMsg,
                    _ => CapiString.FromPtr(result));
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenBatteryCb))]
        private static void OnBattery(IntPtr ctx, int result, IntPtr errorMsg)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                CompletionOp<int>.Complete(ctx, errorMsg, _ => result);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenInfoCb))]
        private static void OnInfo(IntPtr ctx, IntPtr info, IntPtr errorMsg)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                CompletionOp<DeviceInfo>.Complete(ctx, errorMsg, _ =>
                {
                    SenDeviceInfo native = Marshal.PtrToStructure<SenDeviceInfo>(info);
                    return DeviceInfo.FromNative(in native);
                });
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }

        [MonoPInvokeCallback(typeof(SenMultiResultCb))]
        private static void OnMultiResult(
            IntPtr ctx, IntPtr macs, IntPtr oks, IntPtr errors, UIntPtr count)
        {
            CallbackGuard.MarkSdkCallbackThread();
            try
            {
                MultiResultOp.Complete(ctx, macs, oks, errors, count);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
            }
        }
    }

    internal sealed class CompletionOp<T>
    {
        private readonly TaskCompletionSource<T> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly GCHandle _handle;

        internal CompletionOp() { _handle = GCHandle.Alloc(this); }
        internal IntPtr CtxPtr => GCHandle.ToIntPtr(_handle);
        internal Task<T> Task => _tcs.Task;

        internal static void Complete(IntPtr ctx, IntPtr errorMsg, Func<string, T> map)
        {
            Complete(ctx, 1, errorMsg, map);
        }

        internal static void Complete(IntPtr ctx, int result, IntPtr errorMsg, Func<string, T> map)
        {
            CompletionOp<T>? op = null;
            try
            {
                op = (CompletionOp<T>)GCHandle.FromIntPtr(ctx).Target!;
                string err = CapiString.FromPtr(errorMsg);
                if (result != 0 && err.Length == 0) op._tcs.TrySetResult(map(err));
                else op._tcs.TrySetException(new SensorException(err));
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
                if (op != null) op._tcs.TrySetException(ex);
            }
            finally
            {
                if (op != null && op._handle.IsAllocated) op._handle.Free();
            }
        }
    }

    internal sealed class ScanOnceOp
    {
        private readonly TaskCompletionSource<List<BleDevice>> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly GCHandle _handle;

        internal ScanOnceOp()
        {
            _handle = GCHandle.Alloc(this);
        }

        internal IntPtr CtxPtr => GCHandle.ToIntPtr(_handle);
        internal Task<List<BleDevice>> Task => _tcs.Task;

        internal static void Complete(IntPtr ctx, IntPtr devices, int count)
        {
            ScanOnceOp? op = null;
            try
            {
                op = (ScanOnceOp)GCHandle.FromIntPtr(ctx).Target!;
                var list = new List<BleDevice>(count);
                int devSize = Marshal.SizeOf<SenBleDevice>();
                for (int i = 0; i < count; i++)
                {
                    SenBleDevice d = Marshal.PtrToStructure<SenBleDevice>(
                        IntPtr.Add(devices, i * devSize));
                    list.Add(BleDevice.FromNative(in d));
                }
                op._tcs.TrySetResult(list);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
                if (op != null) op._tcs.TrySetException(ex);
            }
            finally
            {
                if (op != null && op._handle.IsAllocated) op._handle.Free();
            }
        }
    }

    internal sealed class MultiResultOp
    {
        private readonly TaskCompletionSource<Dictionary<string, bool>> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly GCHandle _handle;
        private readonly Dictionary<string, string>? _errors;

        internal MultiResultOp(Dictionary<string, string>? errors)
        {
            _handle = GCHandle.Alloc(this);
            _errors = errors;
        }

        internal IntPtr CtxPtr => GCHandle.ToIntPtr(_handle);
        internal Task<Dictionary<string, bool>> Task => _tcs.Task;

        internal static void Complete(
            IntPtr ctx, IntPtr macs, IntPtr oks, IntPtr errors, UIntPtr count)
        {
            MultiResultOp? op = null;
            try
            {
                op = (MultiResultOp)GCHandle.FromIntPtr(ctx).Target!;
                int n = checked((int)count);
                var result = new Dictionary<string, bool>(n);
                for (int i = 0; i < n; i++)
                {
                    string mac = CapiString.FromPtr(Marshal.ReadIntPtr(macs, i * IntPtr.Size));
                    bool ok = Marshal.ReadInt32(oks, i * sizeof(int)) != 0;
                    result[mac] = ok;
                    if (op._errors != null)
                    {
                        string err = errors == IntPtr.Zero
                            ? string.Empty
                            : CapiString.FromPtr(Marshal.ReadIntPtr(errors, i * IntPtr.Size));
                        op._errors[mac] = err;
                    }
                }
                op._tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                CallbackGuard.Log(ex);
                if (op != null) op._tcs.TrySetException(ex);
            }
            finally
            {
                if (op != null && op._handle.IsAllocated) op._handle.Free();
            }
        }
    }

    public sealed class SensorController : IDisposable
    {
        private static readonly Lazy<SensorController> _instance =
            new(() => new SensorController());
        public static SensorController Instance => _instance.Value;

        internal static int AssumedCmdTimeoutMs = 10000;

        private readonly IntPtr _handle;
        private readonly GCHandle _ctxHandle;
        private readonly Dictionary<IntPtr, SensorProfile> _profiles = new();
        private bool _disposed;

        public event Action<List<BleDevice>>? DeviceFound;
        public event Action<bool>? EnableChanged;

        private SensorController()
        {
            uint libVersion = Native.sen_capi_version();
            if (libVersion != Native.ExpectedCapiVersion)
                System.Diagnostics.Trace.TraceWarning(
                    "sensor binding was built for SEN_CAPI_VERSION {0} but the loaded library reports {1}; " +
                    "rebuild the library or update the binding",
                    Native.ExpectedCapiVersion, libVersion);
            _handle = Native.sen_controller_create();
            if (_handle == IntPtr.Zero)
                throw new SensorException("sen_controller_create failed");
            _ctxHandle = GCHandle.Alloc(this);
            var cbs = new SenControllerCbs
            {
                structSize = (uint)Marshal.SizeOf<SenControllerCbs>(),
                onScanResult = Marshal.GetFunctionPointerForDelegate(NativeCallbacks.ScanResult),
                onEnableChanged = Marshal.GetFunctionPointerForDelegate(NativeCallbacks.EnableChanged)
            };
            Native.sen_controller_set_callbacks(_handle, in cbs, GCHandle.ToIntPtr(_ctxHandle));
        }

        public void Dispose() => TearDown();

        public void TearDown()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_profiles)
            {
                foreach (SensorProfile p in _profiles.Values) p.ReleaseContext();
                _profiles.Clear();
            }
            Native.sen_controller_destroy(_handle);
            if (_ctxHandle.IsAllocated) _ctxHandle.Free();
            Native.sen_terminate();
        }

        public bool IsEnable => Native.sen_controller_is_enable(_handle) != 0;
        public bool IsScanning => Native.sen_controller_is_scanning(_handle) != 0;

        public bool StartScan(int periodInMs)
            => Native.sen_controller_start_scan(_handle, periodInMs) != 0;

        public bool StopScan()
            => Native.sen_controller_stop_scan(_handle) != 0;

        public async Task<List<BleDevice>> ScanAsync(int periodInMs)
        {
            var op = new ScanOnceOp();
            Native.sen_controller_scan_once(_handle, periodInMs,
                NativeCallbacks.ScanOnceResult, op.CtxPtr);
            return await op.Task.ConfigureAwait(false);
        }

        public List<BleDevice> Scan(int periodInMs)
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.Await(ScanAsync(periodInMs), periodInMs + 15000,
                new List<BleDevice>());
        }

        public void Log(string message, string level = "I")
        {
            try
            {
                Native.sen_controller_log(_handle, message ?? string.Empty, level ?? "I");
            }
            catch
            {
            }
        }

        public void OnSuspend()
        {
            try
            {
                Native.sen_controller_on_suspend(_handle);
            }
            catch
            {
            }
        }

        public SensorProfile? RequireSensor(BleDevice device) => RequireSensor(device.Mac);

        public SensorProfile? RequireSensor(string mac)
        {
            if (!IsValidMac(mac)) return null;
            IntPtr p = Native.sen_controller_require_sensor(_handle, mac);
            if (p == IntPtr.Zero) throw new SensorException("require_sensor failed");
            return WrapProfile(p);
        }

        private static bool IsValidMac(string mac)
        {
            if (mac == null || mac.Length != 17) return false;
            for (int i = 0; i < mac.Length; i++)
            {
                char c = mac[i];
                if ((i + 1) % 3 == 0)
                {
                    if (c != ':') return false;
                }
                else if (!Uri.IsHexDigit(c))
                {
                    return false;
                }
            }
            return true;
        }

        public SensorProfile? GetSensor(string mac)
        {
            IntPtr p = Native.sen_controller_get_sensor(_handle, mac);
            return p == IntPtr.Zero ? null : WrapProfile(p);
        }

        public List<SensorProfile> GetSensors()
            => CollectProfiles(Native.sen_controller_get_sensors);

        public List<SensorProfile> GetConnectedSensors()
            => CollectProfiles(Native.sen_controller_get_connected_sensors);

        private delegate UIntPtr SensorListFn(IntPtr ctrl, IntPtr[] outHandles, UIntPtr capacity);

        private List<SensorProfile> CollectProfiles(SensorListFn fn)
        {
            UIntPtr count = fn(_handle, null!, UIntPtr.Zero);
            int n = checked((int)count);
            var result = new List<SensorProfile>(n);
            if (n == 0) return result;
            var handles = new IntPtr[n];
            UIntPtr written = fn(_handle, handles, (UIntPtr)n);
            for (int i = 0; i < (int)written; i++)
                result.Add(WrapProfile(handles[i]));
            return result;
        }

        internal SensorProfile WrapProfile(IntPtr handle)
        {
            lock (_profiles)
            {
                if (_profiles.TryGetValue(handle, out SensorProfile? existing))
                    return existing;
                var profile = new SensorProfile(handle);
                _profiles.Add(handle, profile);
                return profile;
            }
        }

        public Task<Dictionary<string, bool>> MultiStartDataNotificationAsync(
            IReadOnlyList<SensorProfile> sensors, int timeoutMs = 0,
            int maxDelayDispersionMs = 5, int maxAttempts = 3,
            Dictionary<string, string>? errors = null)
        {
            IntPtr[] handles = CollectHandles(sensors);
            var op = new MultiResultOp(errors);
            Native.sen_controller_multi_start_data(_handle, handles,
                (UIntPtr)handles.Length, timeoutMs, maxDelayDispersionMs,
                maxAttempts, NativeCallbacks.MultiResult, op.CtxPtr);
            return op.Task;
        }

        public Task<Dictionary<string, bool>> MultiStopDataNotificationAsync(
            IReadOnlyList<SensorProfile> sensors, int timeoutMs = 0,
            Dictionary<string, string>? errors = null)
        {
            IntPtr[] handles = CollectHandles(sensors);
            var op = new MultiResultOp(errors);
            Native.sen_controller_multi_stop_data(_handle, handles,
                (UIntPtr)handles.Length, timeoutMs, NativeCallbacks.MultiResult,
                op.CtxPtr);
            return op.Task;
        }

        public Dictionary<string, bool> MultiStartDataNotification(
            IReadOnlyList<SensorProfile> sensors, int timeoutMs = 0,
            int maxDelayDispersionMs = 5, int maxAttempts = 3,
            Dictionary<string, string>? errors = null)
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.Await(
                MultiStartDataNotificationAsync(sensors, timeoutMs,
                    maxDelayDispersionMs, maxAttempts, errors),
                (timeoutMs > 0 ? timeoutMs : 60000) + 45000, new Dictionary<string, bool>());
        }

        public Dictionary<string, bool> MultiStopDataNotification(
            IReadOnlyList<SensorProfile> sensors, int timeoutMs = 0,
            Dictionary<string, string>? errors = null)
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.Await(
                MultiStopDataNotificationAsync(sensors, timeoutMs, errors),
                timeoutMs + 45000, new Dictionary<string, bool>());
        }

        private static IntPtr[] CollectHandles(IReadOnlyList<SensorProfile> sensors)
        {
            if (sensors == null) throw new ArgumentNullException(nameof(sensors));
            var handles = new IntPtr[sensors.Count];
            for (int i = 0; i < sensors.Count; i++)
            {
                if (sensors[i] == null)
                    throw new ArgumentException(
                        "sensors must not contain null entries", nameof(sensors));
                handles[i] = sensors[i].Handle;
            }
            return handles;
        }

        public BinFileInfo? GetBinFileInfo(string path)
        {
            var info = SenBinFileInfo.Create();
            int ok = Native.sen_controller_get_bin_file_info(_handle, path, ref info);
            return ok != 0 ? BinFileInfo.FromNative(in info) : null;
        }

        public SensorProfile? ReplayBinFile(string path, string deviceMac,
            bool realtime = true, uint timeoutMs = 0)
        {
            IntPtr p = Native.sen_controller_replay_bin_file(
                _handle, path, deviceMac ?? string.Empty, realtime ? 1 : 0, timeoutMs);
            return p == IntPtr.Zero ? null : WrapProfile(p);
        }

        public SensorProfile?[] MultiReplayBinFile(string[] paths,
            IReadOnlyList<SensorProfile> sensors,
            bool realtime = true, uint timeoutMs = 0)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            if (sensors == null) throw new ArgumentNullException(nameof(sensors));
            if (paths.Length != sensors.Count)
                throw new ArgumentException("paths and sensors must have the same length");
            var macs = new string[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                if (sensors[i] == null)
                    throw new ArgumentException(
                        "sensors must not contain null entries", nameof(sensors));
                macs[i] = sensors[i].Device.Mac;
            }
            var outProfiles = new IntPtr[paths.Length];
            Native.sen_controller_multi_replay_bin_file(
                _handle, paths, macs, (UIntPtr)paths.Length,
                realtime ? 1 : 0, timeoutMs, outProfiles);
            var result = new SensorProfile?[paths.Length];
            for (int i = 0; i < paths.Length; i++)
                result[i] = outProfiles[i] == IntPtr.Zero ? null : WrapProfile(outProfiles[i]);
            return result;
        }

        public string PauseBinReplay(string deviceMac)
            => CapiString.ReadOutString((buf, len) =>
            {
                Native.sen_controller_pause_bin_replay(_handle, deviceMac, buf, len);
                return true;
            });

        public string ResumeBinReplay(string deviceMac)
            => CapiString.ReadOutString((buf, len) =>
            {
                Native.sen_controller_resume_bin_replay(_handle, deviceMac, buf, len);
                return true;
            });

        public string StopBinReplay(string deviceMac)
            => CapiString.ReadOutString((buf, len) =>
            {
                Native.sen_controller_stop_bin_replay(_handle, deviceMac, buf, len);
                return true;
            });

        public Task<string> ParseBinToCsvAsync(string binPath, string csvPath)
        {
            var op = new CompletionOp<string>();
            Native.sen_controller_parse_bin_to_csv(_handle, binPath, csvPath,
                NativeCallbacks.Param, op.CtxPtr);
            return op.Task;
        }

        public string ParseBinToCsv(string binPath, string? csvPath = null)
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            string csv = string.IsNullOrEmpty(csvPath)
                ? System.IO.Path.ChangeExtension(binPath, ".csv")
                : csvPath!;
            return SyncWait.AwaitString(ParseBinToCsvAsync(binPath, csv), 320000);
        }

        public string GetVersion()
            => CapiString.ReadOutString((buf, len) =>
            {
                Native.sen_controller_get_version(_handle, buf, len);
                return true;
            }, capacity: 256);

        public Task<string> GetParamAsync(string key)
        {
            var op = new CompletionOp<string>();
            Native.sen_controller_get_param(_handle, key, NativeCallbacks.Param, op.CtxPtr);
            return op.Task;
        }

        public async Task<string> SetParamAsync(string key, string value)
        {
            var op = new CompletionOp<string>();
            Native.sen_controller_set_param(_handle, key, value,
                NativeCallbacks.Param, op.CtxPtr);
            string result = await op.Task.ConfigureAwait(false);
            if (key == "CMD_TIMEOUT_MS" && result == "OK" &&
                int.TryParse(value, out int ms) && ms > 0)
            {
                AssumedCmdTimeoutMs = ms;
            }
            return result;
        }

        public Task<string> CheckSetupDongleAsync()
        {
            var op = new CompletionOp<string>();
            Native.sen_controller_check_setup_dongle(_handle, NativeCallbacks.Param, op.CtxPtr);
            return op.Task;
        }

        public string GetParam(string key)
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.AwaitString(GetParamAsync(key), 20000);
        }

        public string SetParam(string key, string value)
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.AwaitString(SetParamAsync(key, value), 20000);
        }

        public string CheckSetupDongle()
        {
            CallbackGuard.ThrowOnSdkCallbackThread();
            return SyncWait.AwaitString(CheckSetupDongleAsync(), 320000);
        }

        public static uint CapiVersion => Native.sen_capi_version();

        internal void RaiseScanResult(IntPtr devices, int count)
        {
            var handler = DeviceFound;
            if (handler == null) return;
            var list = new List<BleDevice>(count);
            int devSize = Marshal.SizeOf<SenBleDevice>();
            for (int i = 0; i < count; i++)
            {
                SenBleDevice d = Marshal.PtrToStructure<SenBleDevice>(
                    IntPtr.Add(devices, i * devSize));
                list.Add(BleDevice.FromNative(in d));
            }
            foreach (Action<List<BleDevice>> h in handler.GetInvocationList())
            {
                try
                {
                    h(list);
                }
                catch (Exception ex)
                {
                    CallbackGuard.Log(ex);
                }
            }
        }

        internal void RaiseEnableChanged(bool enabled)
        {
            var handler = EnableChanged;
            if (handler == null) return;
            foreach (Action<bool> h in handler.GetInvocationList())
            {
                try
                {
                    h(enabled);
                }
                catch (Exception ex)
                {
                    CallbackGuard.Log(ex);
                }
            }
        }
    }
}
