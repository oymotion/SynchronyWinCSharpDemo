// Managed wrappers over the sen_* flat C API (SensorCapi.cs). The binding is
// deliberately thin: all behavior (error strings, subscription masks, session
// recovery) lives inside the SDK. Public method names mirror the Python SDK
// (SensorSDKPython): SensorController.startScan/requireSensor/getBinFileInfo/
// replayBinFile/parseBinToCsv/getVersion/setDebugEnabled, SensorProfile
// connect/init/startDataNotification/setParam/getParam/getBatteryLevel/
// setAutoReconnect etc.
//
// Async model: the C API's per-operation completion callbacks
// (init/start/stop/setParam/getParam/battery/fetchDeviceInfo) are exposed as
// Task-returning async methods backed by TaskCompletionSource. A non-empty
// errorMsg from the SDK becomes a SensorException.
//
// Threading: events fire on internal SDK threads (never the caller's UI
// thread). Do not call blocking SDK functions from inside an event handler.

// Keep this directive: the file uses nullable reference annotations, which
// otherwise raise CS8632 in legacy projects that do not enable Nullable
// (.NET Framework 4.8, Unity pre-2022 project defaults). This is a compile-
// time feature only - it does not change which runtime the code needs.
#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using AOT;
using SensorSdk.Capi;

namespace SensorSdk
{
    /// <summary>An SDK-reported error (errorMsg strings match the Python SDK).</summary>
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

    /// <summary>
    /// One sample of one channel. A POCO constructed on demand by
    /// SensorData.GetChannelSample - the binding never materializes Samples
    /// for a whole batch (reads go straight into the backing buffer).
    /// </summary>
    public struct Sample
    {
        /// <summary>LSL-style absolute timestamp (Unix seconds, double):
        /// stream-start wall clock + first-packet delay (Delay) +
        /// SampleIndex/SampleRate, computed at decode time; 0 when the
        /// anchor is unknown.</summary>
        public double AbsTimeStampInSec;
        public int ChannelIndex;
        public int SampleIndex;
        public int RawData;
        public float Data;
        public float Impedance;
        public float Saturation;
        public bool IsLost;
    }

    /// <summary>
    /// One broadcast batch of a single stream. Two backing modes:
    /// - BORROWED (as delivered by SensorProfile.DataReceived): reads go
    ///   straight into SDK memory through the native samples pointer
    ///   (IntPtr, zero copy). The payload is only valid until the
    ///   DataReceived handler returns; afterwards the SDK may overwrite it
    ///   at any time. Call <see cref="Clone"/> inside the handler to keep
    ///   the data.
    /// - OWNED (after <see cref="Clone"/>): one flat byte[] copied with a
    ///   single block copy, fully detached from native memory.
    /// Samples are packed [channel][sample] with the per-channel stride ==
    /// SampleCount, 40 bytes per sample (fixed little-endian ABI, see
    /// sen_capi.h). Both backings share one offset-based read path; no
    /// managed Sample objects are created until GetChannelSample or
    /// ChannelSamples is called. A slot whose stored sampleIndex !=
    /// StartSampleIndex + sampleIndex is stale (overwritten by newer data,
    /// or a zeroed masked-out channel), and a view whose StartTimeStamp no
    /// longer matches the stream's current value belongs to a previous
    /// session. The single-slot accessors validate only the index ranges
    /// (out-of-range throws ArgumentOutOfRangeException) and read the slot
    /// as-is otherwise: probe <see cref="IsDataValid"/> once per batch first
    /// (staleness is batch-atomic), or read via the ChannelSamples matrix,
    /// which fills unreadable slots with default(Sample).
    /// The metadata properties (DeviceMac .. DeviceName) are a zero-copy borrow
    /// of the stream's metadata struct inside the SDK: they read straight
    /// through the borrowed pointer, so LostPackageCount / Delay updates
    /// remain visible. The borrow follows the same lifetime rules as the
    /// samples pointer; <see cref="Clone"/> deep-copies the Info into an
    /// owned struct.
    /// </summary>
    public sealed class SensorData
    {
        // sen_sample_t fixed ABI (sen_capi.h, SEN_SAMPLE_SIZE == 40).
        private const int SampleSize = 40;
        private const int OffAbsTimeStamp = 0;
        private const int OffChannelIndex = 8;
        private const int OffSampleIndex = 12;
        private const int OffRawData = 16;
        private const int OffData = 20;
        private const int OffImpedance = 24;
        private const int OffSaturation = 28;
        private const int OffIsLost = 32;

        // sen_data_info_t fixed layout (sen_capi.h, mirrors SensorData::Info).
        private const int OffInfoDataType = 20;
        private const int OffInfoLostPackageCount = 24;
        private const int OffInfoSampleRate = 28;
        private const int OffInfoChannelCount = 32;
        private const int OffInfoChannelMask = 40;
        private const int OffInfoSampleCount = 48;
        private const int OffInfoStartTimeStamp = 52;
        private const int OffInfoDelay = 56;
        private const int OffInfoStartTimeSec = 64;   // 8-aligned (pad at 60)
        private const int OffInfoDeviceName = 72;

        public string DeviceMac => ReadInfoString(0, i => i.deviceMac);
        /// <summary>Device name from the cached DeviceInfo, stamped once
        /// when the stream is created; empty when unknown.</summary>
        public string DeviceName => ReadInfoString(OffInfoDeviceName, i => i.deviceName);
        public SenDataType DataType => (SenDataType)ReadInfoInt32(OffInfoDataType, i => i.dataType);
        public int LostPackageCount => ReadInfoInt32(OffInfoLostPackageCount, i => i.lostPackageCount);
        public float SampleRate => ReadInfoSingle(OffInfoSampleRate, i => i.sampleRate);
        public int ChannelCount => ReadInfoInt32(OffInfoChannelCount, i => i.channelCount);
        public ulong ChannelMask => (ulong)ReadInfoInt64(OffInfoChannelMask, i => (long)i.channelMask);
        public int SampleCount => ReadInfoInt32(OffInfoSampleCount, i => i.sampleCount);
        public int StartSampleIndex { get; }

        /// <summary>The stream's StartTimeStamp snapshot taken when this view
        /// was broadcast (session tag): a stream (re)start re-stamps the
        /// stream's value, which instantly invalidates every view of the
        /// previous session (IsDataValid compares the two). Steady-clock ms
        /// low 32 bits live, the bin record timestamp on replay.</summary>
        public uint StartTimeStamp { get; }

        /// <summary>First raw packet arrival minus StartTimeStamp; 0 until the
        /// first packet of the current start.</summary>
        public uint Delay => (uint)ReadInfoInt32(OffInfoDelay, i => (int)i.delay);

        /// <summary>Wall-clock Unix time in seconds (double) when the stream
        /// was started; on replay restored from the bin record timestamps.
        /// 0 = unknown. This is the anchor of every sample's
        /// AbsTimeStampInSec.</summary>
        public double StartTimeSec => ReadInfoDouble(OffInfoStartTimeSec, i => i.startTimeSec);

        /// <summary>
        /// True while this instance borrows SDK memory (valid only inside
        /// the DataReceived handler); false once owned via Clone().
        /// </summary>
        public bool IsBorrowed => _ownedSamples == null;

        private readonly IntPtr _infoPtr;        // borrowed per-stream Info (Zero when owned)
        private SenDataInfo? _ownInfo;           // owned Info (set by Clone)
        private readonly IntPtr _samplesPtr;     // borrowed backing (Zero when owned)
        private readonly long _samplesBytes;     // sample-block byte length (view.samplesBytes / owned length)
        private readonly byte[]? _ownedSamples;  // owned backing (null while borrowed)
        private Sample[][]? _channelSamples;     // lazy compatibility cache

        internal SensorData(in SenDataView view)
        {
            StartSampleIndex = view.startSampleIndex;
            StartTimeStamp = view.startTimeStamp;
            _infoPtr = view.info; // borrowed; same lifetime rules as samples
            _samplesPtr = view.samples; // borrowed; dies when the callback returns
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

        /* ---- Info reads (borrowed pointer or owned struct) ---- */

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

        // NUL-terminated ASCII string at the given Info offset (borrowed
        // pointer or owned struct); empty when there is no Info at all.
        private string ReadInfoString(int off, Func<SenDataInfo, string?> fromOwned)
            => _ownInfo.HasValue ? fromOwned(_ownInfo.Value) ?? string.Empty
               : _infoPtr != IntPtr.Zero
                 ? Marshal.PtrToStringAnsi(IntPtr.Add(_infoPtr, off)) ?? string.Empty
               : string.Empty;

        private bool HasSamples
            => _ownedSamples != null ? _ownedSamples.Length > 0
                                     : _samplesPtr != IntPtr.Zero;

        /// <summary>
        /// Detaches the payload from native memory with ONE block copy
        /// (sen_data_view_t.samplesBytes bytes); the returned instance is
        /// safe to keep after the data callback returns. Cloning an already
        /// owned instance copies managed memory only.
        /// </summary>
        public SensorData Clone()
        {
            int n = checked((int)(_ownedSamples != null ? _ownedSamples.Length : _samplesBytes));
            var buf = new byte[n];
            if (_ownedSamples != null) Array.Copy(_ownedSamples, buf, n);
            else if (_samplesPtr != IntPtr.Zero && n > 0)
                Marshal.Copy(_samplesPtr, buf, 0, n);
            return new SensorData(this, buf);
        }

        /// <summary>
        /// Compatibility accessor: the full [channel][sample] matrix of
        /// managed Samples, built lazily on first access. Batch path, never
        /// throws: slots failing the IsDataValid probe are filled with
        /// default(Sample). Prefer the single-slot accessors
        /// (GetData/GetChannelSample/...) - they do not materialize the
        /// matrix.
        /// </summary>
        public Sample[][] ChannelSamples => _channelSamples ??= BuildChannelSamples();

        /* ---- single-slot accessors (no managed objects; index-range checked) ---- */

        /// <summary>
        /// Non-throwing probe (mirrors C++ SensorData::isDataValid): true when
        /// slot (channel, sampleIndex) is readable — in range, a payload is
        /// present, this view still belongs to the stream's current session
        /// (its StartTimeStamp snapshot matches the stream's current value),
        /// and the slot is not stale. The single-slot accessors do NOT repeat
        /// this detection (they validate only the index ranges), so probe once
        /// per batch first — staleness is batch-atomic, one (0, 0) probe
        /// covers the whole batch. Note that channels masked out of
        /// ChannelMask carry zeroed slots and therefore probe false. Both
        /// indices default to 0: a bare IsDataValid() probes the batch head.
        /// </summary>
        public bool IsDataValid(int channel = 0, int sampleIndex = 0)
        {
            if (channel < 0 || channel >= ChannelCount) return false;
            if (sampleIndex < 0 || sampleIndex >= SampleCount) return false;
            if (!HasSamples) return false;
            if (StartTimeStamp != (uint)ReadInfoInt32(OffInfoStartTimeStamp, i => (int)i.startTimeStamp)) return false;
            int off = (channel * SampleCount + sampleIndex) * SampleSize;
            return ReadInt32(off + OffSampleIndex) == StartSampleIndex + sampleIndex;
        }

        /// <summary>
        /// True when channel <paramref name="channel"/> is enabled in
        /// ChannelMask; false when channel is out of [0, 64).
        /// </summary>
        public bool IsChannelEnabled(int channel)
            => channel >= 0 && channel < 64 && ((ChannelMask >> channel) & 1UL) != 0;

        /// <summary>
        /// Returns the full slot of (channel, sampleIndex). Reads straight
        /// from the backing buffer; this is the only place a managed Sample
        /// is constructed. Only the index ranges are validated — probe
        /// <see cref="IsDataValid"/> first when the distinction between real
        /// data and a stale/previous-session slot matters.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">channel/sampleIndex out of range.</exception>
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

        /// <inheritdoc cref="GetChannelSample" path="/summary|/exception"/>
        public float GetData(int channel, int sampleIndex)
            => ReadSingle(CheckedSlotOffset(channel, sampleIndex) + OffData);

        /// <summary>
        /// Sample timestamp in milliseconds, computed from the slot's
        /// absolute index over the nominal rate (sampleIndex * 1000 /
        /// SampleRate); 0 when the rate is unknown.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">channel/sampleIndex out of range.</exception>
        public int GetTimeStampInMs(int channel, int sampleIndex)
        {
            float rate = SampleRate;
            return rate > 0
                ? (int)(GetSampleIndex(channel, sampleIndex) * 1000.0 / rate)
                : 0;
        }

        /// <summary>
        /// Absolute sample timestamp in LSL format (double seconds since the
        /// Unix epoch), computed at decode time and stored in the slot. The
        /// per-sample resolution is 1/SampleRate seconds at any rate —
        /// including rates above 1000 Hz, where the int-ms GetTimeStampInMs
        /// collapses. 0 when the anchor is unknown.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">channel/sampleIndex out of range.</exception>
        public double GetAbsTimeStampInSec(int channel, int sampleIndex)
            => ReadDouble(CheckedSlotOffset(channel, sampleIndex) + OffAbsTimeStamp);

        /// <summary>
        /// Returns the stored absolute sample index of (channel, sampleIndex)
        /// (== StartSampleIndex + sampleIndex for every valid slot).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">channel/sampleIndex out of range.</exception>
        public int GetSampleIndex(int channel, int sampleIndex)
            => ReadInt32(CheckedSlotOffset(channel, sampleIndex) + OffSampleIndex);

        /// <inheritdoc cref="GetChannelSample" path="/summary|/exception"/>
        public int GetRawData(int channel, int sampleIndex)
            => ReadInt32(CheckedSlotOffset(channel, sampleIndex) + OffRawData);

        /// <inheritdoc cref="GetChannelSample" path="/summary|/exception"/>
        public float GetImpedance(int channel, int sampleIndex)
            => ReadSingle(CheckedSlotOffset(channel, sampleIndex) + OffImpedance);

        /// <inheritdoc cref="GetChannelSample" path="/summary|/exception"/>
        public float GetSaturation(int channel, int sampleIndex)
            => ReadSingle(CheckedSlotOffset(channel, sampleIndex) + OffSaturation);

        /// <inheritdoc cref="GetChannelSample" path="/summary|/exception"/>
        public bool IsLost(int channel, int sampleIndex)
            => ReadByte(CheckedSlotOffset(channel, sampleIndex) + OffIsLost) != 0;

        // Range-checked byte offset of slot (channel, sampleIndex): every
        // single-slot accessor funnels through here. Only the index ranges
        // are validated — staleness/session detection lives in IsDataValid;
        // probe it once per batch first, otherwise a stale or
        // previous-session slot reads as garbage.
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

        // One read path for both backings. The byte[] path relies on the
        // host being little-endian (the ABI is fixed little-endian).
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

        // BitConverter.Int32BitsToSingle only exists on netstandard2.1 /
        // netcoreapp3.0+; this explicit-layout union does the same bit
        // reinterpretation on .NET Framework 4.8 and Unity/IL2CPP.
        private static float Int32BitsToSingle(int value)
            => new Int32SingleUnion { Int32 = value }.Single;

        [StructLayout(LayoutKind.Explicit)]
        private struct Int32SingleUnion
        {
            [FieldOffset(0)] public int Int32;
            [FieldOffset(0)] public float Single;
        }

        // Same net48/IL2CPP portability reason as Int32BitsToSingle:
        // BitConverter.Int64BitsToDouble does not exist there either.
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
                    // Batch path: stale/masked/previous-session slots read as
                    // default(Sample).
                    if (IsDataValid(ch, i)) col[i] = GetChannelSample(ch, i);
                }
                cols[ch] = col;
            }
            return cols;
        }
    }

    /// <summary>A scanned BLE device (managed copy of sen_ble_device_t).</summary>
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

    /// <summary>Managed copy of sen_device_info_t (cached by init/fetchDeviceInfo).</summary>
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
        /// <summary>Max sample rates from the device capability queries; 0 = not reported or not supported.</summary>
        public ushort EmgMaxSampleRate;
        /// <inheritdoc cref="EmgMaxSampleRate"/>
        public ushort EegMaxSampleRate;
        /// <inheritdoc cref="EmgMaxSampleRate"/>
        public ushort EcgMaxSampleRate;
        /// <summary>Link connection interval in ms; 0 = unknown (the C++ BLE backends do not expose it).</summary>
        public double ConnectionIntervalMs;
        /// <summary>Peripheral latency in events; -1 = unknown (0 is a legal value).</summary>
        public int PeripheralLatency;
        /// <summary>Supervision timeout in ms; 0 = unknown.</summary>
        public int SupervisionTimeoutMs;

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
                SupervisionTimeoutMs = i.SupervisionTimeoutMs
            };
        }
    }

    /// <summary>Summary of a raw BLE bin capture (managed copy of sen_bin_file_info_t).</summary>
    public sealed class BinFileInfo
    {
        public string Mac = string.Empty;
        public string DeviceName = string.Empty;
        public double DurationSec;
        public bool Valid;
        /// <summary>
        /// DeviceInfo decoded from the first CONFIG record of the capture;
        /// all-zero/empty when the file has no decodable config.
        /// </summary>
        public DeviceInfo DeviceInfo = new DeviceInfo();

        internal static BinFileInfo FromNative(in SenBinFileInfo i)
        {
            return new BinFileInfo
            {
                Mac = CapiString.FromBytes(i.mac),
                DeviceName = CapiString.FromBytes(i.deviceName),
                DurationSec = i.durationSec,
                Valid = i.valid != 0,
                DeviceInfo = SensorSdk.DeviceInfo.FromNative(in i.deviceInfo)
            };
        }
    }

    /// <summary>
    /// Per-device profile (Python SensorProfile parity). Handles are owned by
    /// the SensorController; wrappers are cached per native handle.
    /// Events fire on internal SDK threads.
    /// </summary>
    public sealed class SensorProfile
    {
        internal IntPtr Handle { get; }
        private GCHandle _ctxHandle;

        /// <summary>
        /// All batches accumulated since the last callback, in one call.
        /// The delivered SensorData objects BORROW SDK memory: their payload
        /// is only valid until this handler returns. Call SensorData.Clone()
        /// inside the handler to keep any batch.
        /// </summary>
        public event Action<SensorProfile, List<SensorData>>? DataReceived;
        public event Action<SensorProfile, SenDeviceState>? StateChanged;
        public event Action<SensorProfile, string>? ErrorReceived;
        public event Action<SensorProfile, int>? PowerChanged;
        /// <summary>
        /// DeviceInfo field change push (aligned with the Python SDK 0.7.0
        /// onDeviceInfoUpdate): fired after the cached DeviceInfo was updated
        /// in place (e.g. SetParam "EEG_SAMPLE_RATE" rewrote the bound EEG/ECG
        /// rates). The delivered DeviceInfo is a managed copy.
        /// </summary>
        public event Action<SensorProfile, DeviceInfo>? DeviceInfoUpdated;
        /// <summary>
        /// Data stream on/off state change push: fired when the data stream
        /// actually starts (a successful StartDataNotificationAsync, or the
        /// data start of a bin replay) or stops (StopDataNotificationAsync,
        /// link loss, replay end). Fires only on a real state change; the
        /// argument is true while streaming.
        /// </summary>
        public event Action<SensorProfile, bool>? DataTransferStateChanged;
        /// <summary>
        /// Gates session recovery after an auto reconnect. Answer through the
        /// passed action, exactly once and from any thread: answer(true) takes
        /// over the recovery yourself, answer(false) runs the SDK's default
        /// init -> setParam replay -> stream restart flow. If no answer
        /// arrives within 10 s the SDK runs the default recovery.
        /// </summary>
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
                var d = new SenBleDevice();
                Native.sen_profile_get_device(Handle, ref d);
                return BleDevice.FromNative(in d);
            }
        }

        public SenDeviceState DeviceState => (SenDeviceState)Native.sen_profile_get_state(Handle);
        /// <summary>True when the link is Ready (Python SDK 0.7.2 isReady parity).</summary>
        public bool IsReady => DeviceState == SenDeviceState.Ready;
        public bool HasInited => Native.sen_profile_has_init(Handle) != 0;
        public bool IsDataTransfering => Native.sen_profile_has_start_data_notification(Handle) != 0;

        /// <summary>Connects; resolves with the final link result (Python asyncConnect parity).</summary>
        public Task<bool> ConnectAsync()
        {
            var op = new CompletionOp<bool>();
            Native.sen_profile_connect(Handle, NativeCallbacks.BoolCompletion, op.CtxPtr);
            return op.Task;
        }

        /// <summary>Disconnects; resolves true once the link is torn down (Python asyncDisconnect parity).</summary>
        public Task<bool> DisconnectAsync()
        {
            var op = new CompletionOp<bool>();
            Native.sen_profile_disconnect(Handle, NativeCallbacks.BoolCompletion, op.CtxPtr);
            return op.Task;
        }

        /// <summary>Initialize the device; resolves on success, throws SensorException on error.</summary>
        public Task InitAsync(int packageSampleCount, int powerRefreshIntervalMs = 0, int timeoutMs = 10000)
        {
            var op = new CompletionOp<string>();
            Native.sen_profile_init(Handle, packageSampleCount, timeoutMs,
                powerRefreshIntervalMs, NativeCallbacks.Completion, op.CtxPtr);
            return op.Task;
        }

        public Task StartDataNotificationAsync(int timeoutMs = 10000)
        {
            var op = new CompletionOp<string>();
            Native.sen_profile_start_data(Handle, timeoutMs, NativeCallbacks.Completion, op.CtxPtr);
            return op.Task;
        }

        public Task StopDataNotificationAsync(int timeoutMs = 10000)
        {
            var op = new CompletionOp<string>();
            Native.sen_profile_stop_data(Handle, timeoutMs, NativeCallbacks.Completion, op.CtxPtr);
            return op.Task;
        }

        /// <summary>Fresh battery query (unfiltered); resolves with the level in percent.</summary>
        public Task<int> GetBatteryLevelAsync(int timeoutMs = 10000)
        {
            var op = new CompletionOp<int>();
            Native.sen_profile_get_battery_level(Handle, timeoutMs, NativeCallbacks.Battery, op.CtxPtr);
            return op.Task;
        }

        public Task<DeviceInfo> FetchDeviceInfoAsync(int timeoutMs = 10000)
        {
            var op = new CompletionOp<DeviceInfo>();
            Native.sen_profile_fetch_device_info(Handle, timeoutMs, NativeCallbacks.Info, op.CtxPtr);
            return op.Task;
        }

        /// <summary>Cached DeviceInfo populated during init/fetchDeviceInfo; no GATT traffic.</summary>
        public DeviceInfo GetDeviceInfo()
        {
            var info = SenDeviceInfo.Create();
            Native.sen_profile_get_device_info(Handle, ref info);
            return DeviceInfo.FromNative(in info);
        }

        /// <summary>Resolves with the SDK result string ("" on success, "ERROR: ..." on failure).</summary>
        public Task<string> SetParamAsync(string key, string value, int timeoutMs = 10000)
        {
            var op = new CompletionOp<string>();
            Native.sen_profile_set_param(Handle, timeoutMs, key, value, NativeCallbacks.Param, op.CtxPtr);
            return op.Task;
        }

        /// <summary>Resolves with the parameter value or "Error: ..." (Python parity).</summary>
        public Task<string> GetParamAsync(string key, int timeoutMs = 10000)
        {
            var op = new CompletionOp<string>();
            Native.sen_profile_get_param(Handle, timeoutMs, key, NativeCallbacks.Param, op.CtxPtr);
            return op.Task;
        }

        /// <summary>Enables/disables session recovery after an auto reconnect (default on).</summary>
        public void SetAutoReconnect(bool enabled)
            => Native.sen_profile_set_auto_reconnect(Handle, enabled ? 1 : 0);

        /// <summary>
        /// Writes an application log line (tag "App") into the SDK log for
        /// this device. level is the first character, case-insensitive:
        /// d/i/w/e; anything else is treated as "i". Never throws.
        /// </summary>
        public void Log(string message, string level = "I")
        {
            try
            {
                Native.sen_profile_log(Handle, message ?? string.Empty, level ?? "I");
            }
            catch
            {
                // Logging must never surface an error to the caller.
            }
        }

        /* ---- native callback entry points (SDK threads) ---- */

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
                // Lightweight borrowed view: metadata only, no sample copy.
                batch.Add(new SensorData(in view));
            }
            handler(this, batch);
        }

        internal void RaiseState(int newState)
            => StateChanged?.Invoke(this, (SenDeviceState)newState);

        internal void RaiseError(IntPtr errorMsg)
            => ErrorReceived?.Invoke(this, CapiString.FromPtr(errorMsg));

        internal void RaisePower(int power)
            => PowerChanged?.Invoke(this, power);

        internal void RaiseAutoReconnect(int hasLastSession, IntPtr answer, IntPtr answerCtx)
        {
            var answerFn = Marshal.GetDelegateForFunctionPointer<SenAutoReconnectAnswerCb>(answer);
            var handler = OnAutoReconnect;
            if (handler == null)
            {
                answerFn(answerCtx, 0);
                return;
            }
            handler(this, hasLastSession != 0, handled => answerFn(answerCtx, handled ? 1 : 0));
        }

        internal void RaiseDeviceInfoUpdate(IntPtr info)
        {
            var handler = DeviceInfoUpdated;
            if (handler == null || info == IntPtr.Zero) return;
            SenDeviceInfo native = Marshal.PtrToStructure<SenDeviceInfo>(info);
            handler(this, DeviceInfo.FromNative(in native));
        }

        internal void RaiseDataTransferState(int isTransferring)
            => DataTransferStateChanged?.Invoke(this, isTransferring != 0);
    }

    /// <summary>
    /// Static delegate instances passed to the SDK. They must live for the
    /// process lifetime - the SDK stores the function pointers.
    /// ctx is a GCHandle to the target object (SensorProfile / SensorController /
    /// a per-call CompletionOp), recovered with GCHandle.FromIntPtr.
    /// </summary>
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
            => ProfileFromCtx(ctx).RaiseData(views, checked((int)viewCount));

        [MonoPInvokeCallback(typeof(SenStateCb))]
        private static void OnState(IntPtr ctx, IntPtr profile, int newState)
            => ProfileFromCtx(ctx).RaiseState(newState);

        [MonoPInvokeCallback(typeof(SenErrorCb))]
        private static void OnError(IntPtr ctx, IntPtr profile, IntPtr errorMsg)
            => ProfileFromCtx(ctx).RaiseError(errorMsg);

        [MonoPInvokeCallback(typeof(SenPowerCb))]
        private static void OnPower(IntPtr ctx, IntPtr profile, int power)
            => ProfileFromCtx(ctx).RaisePower(power);

        [MonoPInvokeCallback(typeof(SenAutoReconnectCb))]
        private static void OnAutoReconnect(IntPtr ctx, IntPtr profile, int hasLastSession,
                                            IntPtr answer, IntPtr answerCtx)
            => ProfileFromCtx(ctx).RaiseAutoReconnect(hasLastSession, answer, answerCtx);

        [MonoPInvokeCallback(typeof(SenDeviceInfoUpdateCb))]
        private static void OnDeviceInfoUpdate(IntPtr ctx, IntPtr profile, IntPtr info)
            => ProfileFromCtx(ctx).RaiseDeviceInfoUpdate(info);

        [MonoPInvokeCallback(typeof(SenDataTransferStateCb))]
        private static void OnDataTransferState(IntPtr ctx, IntPtr profile, int isTransferring)
            => ProfileFromCtx(ctx).RaiseDataTransferState(isTransferring);

        [MonoPInvokeCallback(typeof(SenScanResultCb))]
        private static void OnScanResult(IntPtr ctx, IntPtr devices, UIntPtr count)
            => ((SensorController)GCHandle.FromIntPtr(ctx).Target!)
                .RaiseScanResult(devices, checked((int)count));

        [MonoPInvokeCallback(typeof(SenEnableChangedCb))]
        private static void OnEnableChanged(IntPtr ctx, int enabled)
            => ((SensorController)GCHandle.FromIntPtr(ctx).Target!).RaiseEnableChanged(enabled != 0);

        [MonoPInvokeCallback(typeof(SenCompletionCb))]
        private static void OnCompletion(IntPtr ctx, int result, IntPtr errorMsg)
            => CompletionOp<string>.Complete(ctx, errorMsg,
                msg => msg);

        [MonoPInvokeCallback(typeof(SenCompletionCb))]
        private static void OnBoolCompletion(IntPtr ctx, int result, IntPtr errorMsg)
            => CompletionOp<bool>.Complete(ctx, errorMsg, _ => result != 0);

        [MonoPInvokeCallback(typeof(SenParamCb))]
        private static void OnParam(IntPtr ctx, IntPtr result, IntPtr errorMsg)
            => CompletionOp<string>.Complete(ctx, errorMsg,
                _ => CapiString.FromPtr(result));

        [MonoPInvokeCallback(typeof(SenBatteryCb))]
        private static void OnBattery(IntPtr ctx, int result, IntPtr errorMsg)
            => CompletionOp<int>.Complete(ctx, errorMsg, _ => result);

        [MonoPInvokeCallback(typeof(SenInfoCb))]
        private static void OnInfo(IntPtr ctx, IntPtr info, IntPtr errorMsg)
            => CompletionOp<DeviceInfo>.Complete(ctx, errorMsg, _ =>
            {
                SenDeviceInfo native = Marshal.PtrToStructure<SenDeviceInfo>(info);
                return DeviceInfo.FromNative(in native);
            });

        [MonoPInvokeCallback(typeof(SenMultiResultCb))]
        private static void OnMultiResult(
            IntPtr ctx, IntPtr macs, IntPtr oks, IntPtr errors, UIntPtr count)
            => MultiResultOp.Complete(ctx, macs, oks, errors, count);
    }

    /// <summary>
    /// One in-flight callback-async operation. The GCHandle is the ctx passed
    /// to the SDK and is freed exactly once, when the completion callback
    /// fires. A non-empty errorMsg completes the task with a SensorException.
    /// </summary>
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
            var op = (CompletionOp<T>)GCHandle.FromIntPtr(ctx).Target!;
            string err = CapiString.FromPtr(errorMsg);
            op._handle.Free();
            if (err.Length == 0) op._tcs.TrySetResult(map(err));
            else op._tcs.TrySetException(new SensorException(err));
        }
    }

    /// <summary>
    /// One in-flight synchronized multi-device start/stop operation. Same
    /// rooting rules as CompletionOp: the GCHandle is the ctx passed to the
    /// SDK and is freed exactly once, when the result callback fires.
    /// </summary>
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
            var op = (MultiResultOp)GCHandle.FromIntPtr(ctx).Target!;
            op._handle.Free();
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
    }

    /// <summary>
    /// Process-wide scan controller (Python SensorController parity). Use the
    /// <see cref="Instance"/> singleton; Dispose/TearDown destroys the native
    /// controller (invalidating every profile handle) and terminates the
    /// whole SDK. Call once at application shutdown; idempotent.
    /// Events fire on internal SDK threads.
    /// </summary>
    public sealed class SensorController : IDisposable
    {
        private static readonly Lazy<SensorController> _instance =
            new(() => new SensorController());
        public static SensorController Instance => _instance.Value;

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

        /// <summary>
        /// Destroys the native controller (every SensorProfile handle dies
        /// with it) and terminates the whole SDK: all scans and connections
        /// stop. Call once at application shutdown; repeated calls are safe.
        /// </summary>
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

        /// <summary>
        /// Scans for periodInMs and returns the deduped device list (Python
        /// asyncScan parity): every scan round's matches are merged by MAC,
        /// later rounds refresh the entry in place.
        /// </summary>
        public async Task<List<BleDevice>> ScanAsync(int periodInMs)
        {
            var found = new Dictionary<string, BleDevice>();
            void Handler(List<BleDevice> devices)
            {
                lock (found)
                {
                    foreach (BleDevice d in devices)
                        found[d.Mac] = d;
                }
            }
            DeviceFound += Handler;
            try
            {
                StartScan(periodInMs);
                await Task.Delay(periodInMs).ConfigureAwait(false);
                StopScan();
            }
            finally
            {
                DeviceFound -= Handler;
            }
            lock (found)
            {
                return new List<BleDevice>(found.Values);
            }
        }

        public void SetDebugEnabled(bool enabled)
            => Native.sen_controller_set_debug_enabled(_handle, enabled ? 1 : 0);

        public void SetDataLogEnabled(bool enabled)
            => Native.sen_controller_set_data_log_enabled(_handle, enabled ? 1 : 0);

        public void SetLogPath(bool enabled, string path = "")
            => Native.sen_controller_set_log_path(_handle, enabled ? 1 : 0, path);

        /// <summary>
        /// Writes an application log line (tag "App") into the SDK log.
        /// level is the first character, case-insensitive: d/i/w/e; anything
        /// else is treated as "i". Never throws.
        /// </summary>
        public void Log(string message, string level = "I")
        {
            try
            {
                Native.sen_controller_log(_handle, message ?? string.Empty, level ?? "I");
            }
            catch
            {
                // Logging must never surface an error to the caller.
            }
        }

        /// <summary>
        /// iOS/Android: call when the app moves to the background. Writes a
        /// "suspend" event marker into every open bin capture and flushes each
        /// capture plus the SDK log queue to disk, so an app killed while
        /// suspended loses as little as possible. Does not stop scanning,
        /// streaming, or any connection. Never throws.
        /// </summary>
        public void OnSuspend()
        {
            try
            {
                Native.sen_controller_on_suspend(_handle);
            }
            catch
            {
                // Best-effort durability hook; never surfaces an error.
            }
        }

        /// <summary>
        /// Returns the profile for a device (creating and registering it when
        /// the MAC is unknown, so it also works for unscanned devices).
        /// </summary>
        public SensorProfile RequireSensor(BleDevice device) => RequireSensor(device.Mac);

        public SensorProfile RequireSensor(string mac)
        {
            IntPtr p = Native.sen_controller_require_sensor(_handle, mac);
            if (p == IntPtr.Zero) throw new SensorException("require_sensor failed");
            return WrapProfile(p);
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

        /* ---- synchronized multi-device stream start/stop ---- */

        /// <summary>
        /// Starts the data stream on several devices with their start writes
        /// released together, so the streams begin as simultaneously as the
        /// link allows (Python multiStartDataNotification parity). When the
        /// spread of the first-packet delays exceeds maxDelayDispersionMs the
        /// round is torn down and retried, up to maxAttempts rounds. Resolves
        /// with mac -> ok for every participant (invalid or not-ready devices
        /// get their own false entry); when errors is supplied it is filled
        /// with the per-device result string ("" on success) before the task
        /// completes. Participants that are already streaming are restarted.
        /// </summary>
        public Task<Dictionary<string, bool>> MultiStartDataNotificationAsync(
            IReadOnlyList<SensorProfile> sensors, int timeoutMs = 30000,
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

        /// <summary>
        /// Stops the data stream on several devices with their stop writes
        /// released together (Python multiStopDataNotification parity).
        /// Devices that are not streaming report success immediately.
        /// Resolves with mac -> ok for every participant; when errors is
        /// supplied it is filled with the per-device result string ("" on
        /// success) before the task completes.
        /// </summary>
        public Task<Dictionary<string, bool>> MultiStopDataNotificationAsync(
            IReadOnlyList<SensorProfile> sensors, int timeoutMs = 10000,
            Dictionary<string, string>? errors = null)
        {
            IntPtr[] handles = CollectHandles(sensors);
            var op = new MultiResultOp(errors);
            Native.sen_controller_multi_stop_data(_handle, handles,
                (UIntPtr)handles.Length, timeoutMs, NativeCallbacks.MultiResult,
                op.CtxPtr);
            return op.Task;
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

        /* ---- bin capture inspection and offline replay ---- */

        public BinFileInfo? GetBinFileInfo(string path)
        {
            var info = SenBinFileInfo.Create();
            int ok = Native.sen_controller_get_bin_file_info(_handle, path, ref info);
            return ok != 0 ? BinFileInfo.FromNative(in info) : null;
        }

        /// <summary>
        /// Replays a bin capture through the normal parse pipeline on a
        /// background thread; attach events to the returned profile.
        /// </summary>
        public SensorProfile? ReplayBinFile(string path, string deviceMac,
            bool realtime = true, uint timeoutMs = 30000)
        {
            IntPtr p = Native.sen_controller_replay_bin_file(
                _handle, path, deviceMac ?? string.Empty, realtime ? 1 : 0, timeoutMs);
            return p == IntPtr.Zero ? null : WrapProfile(p);
        }

        /// <summary>
        /// Synchronized multi-bin replay: every (paths[i], macs[i]) capture
        /// replays on one shared clock aligned by record timestamps (the
        /// earliest record in the group is t=0, so concurrently recorded
        /// captures keep their original relative offsets). Pausing/resuming
        /// any member freezes/resumes the whole group; StopBinReplay stays
        /// per device. The returned array is input-order aligned; a null
        /// entry marks a member that failed validation.
        /// </summary>
        public SensorProfile?[] MultiReplayBinFile(string[] paths, string[] macs,
            bool realtime = true, uint timeoutMs = 30000)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            if (macs == null) throw new ArgumentNullException(nameof(macs));
            if (paths.Length != macs.Length)
                throw new ArgumentException("paths and macs must have the same length");
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

        /// <summary>
        /// Offline full-speed parse of a bin capture into CSV. Blocks the
        /// caller; returns the csv path on success or an "Error: ..." string.
        /// </summary>
        public string ParseBinToCsv(string binPath, string csvPath)
            => CapiString.ReadOutString((buf, len) =>
            {
                Native.sen_controller_parse_bin_to_csv(_handle, binPath, csvPath, buf, len);
                return true;
            });

        public string GetVersion()
            => CapiString.ReadOutString((buf, len) =>
            {
                Native.sen_controller_get_version(_handle, buf, len);
                return true;
            }, capacity: 256);

        /// <summary>
        /// SEN_CAPI_VERSION of the loaded native library, so the caller can
        /// detect a binding/library mismatch at runtime.
        /// </summary>
        public static uint CapiVersion => Native.sen_capi_version();

        /* ---- native callback entry points (SDK threads) ---- */

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
            handler(list);
        }

        internal void RaiseEnableChanged(bool enabled)
            => EnableChanged?.Invoke(enabled);
    }
}
