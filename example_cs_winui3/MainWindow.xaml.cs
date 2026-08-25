using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SensorSdk.Capi;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace SensorSdk.ExampleWinUI3;

/// <summary>Multi-device sensor demo main window.</summary>
public sealed partial class MainWindow : Window
{
    private const int ScanDevicePeriodMs = 3000;
    private const int PackageCount = 32;
    private const int CmdTimeoutMs = 5000;
    private const int PlotUpdateIntervalMs = 50;
    private const int FftUpdateIntervalMs = 500;
    private const string DemoVersion = "0.1.21";
    private const int PowerRefreshPeriodMs = 60000;
    private const int PowerStableBand = 4;
    private const uint ReplayDelegateTimeoutMs = 5000;

    private static readonly string[] NtfKeys = ["NTF_EEG", "NTF_EMG", "NTF_GEST", "NTF_PPG", "NTF_SPO2", "NTF_IMU"];
    private static readonly string[] FilterKeys = ["FILTER_50HZ", "FILTER_60HZ", "FILTER_HPF", "FILTER_LPF"];
    private static readonly int[] SampleRateCandidates = [250, 500, 1000, 2000];
    private static readonly Dictionary<string, string> NtfLabels = new()
    {
        ["NTF_EEG"] = "EEG", ["NTF_EMG"] = "EMG", ["NTF_GEST"] = "GESTURE",
        ["NTF_PPG"] = "PPG", ["NTF_SPO2"] = "SpO2", ["NTF_IMU"] = "IMU",
    };
    private static readonly Dictionary<string, string> FilterLabels = new()
    {
        ["FILTER_50HZ"] = "50Hz", ["FILTER_60HZ"] = "60Hz",
        ["FILTER_HPF"] = "HPF", ["FILTER_LPF"] = "LPF",
    };

    private readonly SensorController _ctrl = SensorController.Instance;

    private readonly List<DeviceEntry> _discovered = new();
    private readonly Dictionary<string, DeviceState> _deviceStates = new();
    private readonly object _statesMutex = new();
    // Successful user setParam history per device (insertion order, one entry
    // per key), replayed by the app-driven auto-reconnect recovery.
    private readonly Dictionary<string, List<KeyValuePair<string, string>>> _savedParamsByMac = new();
    // Devices whose next successful stream start should be followed by the
    // saved-param restore replay.
    private readonly HashSet<string> _restoreParamsMacs = new();
    private readonly HashSet<string> _streamingMacs = new();
    private string _currentMac = string.Empty;

    // Replay state
    private readonly List<string> _replayMacs = new();
    private bool _replayStopRequested;
    private bool _replayPaused;

    // Per-device log/bin export paths reused across reconnects.
    private readonly Dictionary<string, string> _lastLogPaths = new();
    private readonly Dictionary<string, string> _lastDataPaths = new();
    private bool _debugLogEnabled = true;
    private bool _binDataEnabled = true;

    private bool _updatingControls;
    private bool _shuttingDown;
    private bool _scanning;
    private bool _analyzeRunning;
    private bool _dialogOpen;
    // Cached Clone Data switch state
    private volatile bool _cloneData = false;

    // Data queue + worker
    private readonly struct QueuedItem
    {
        public readonly string Mac;
        public readonly SensorData Data;
        public QueuedItem(string mac, SensorData data) { Mac = mac; Data = data; }
    }
    private readonly Queue<QueuedItem> _dataQueue = new();
    private readonly AutoResetEvent _dataQueueEvent = new(false);
    private readonly Thread _dataWorker;
    private volatile bool _dataWorkerStop;

    // FFT spectrum
    private readonly object _fftMutex = new();
    private bool _fftBusy;
    private bool _fftReady;
    private int _fftTypeIndex = -1;
    private string _fftMac = string.Empty;
    private float[] _fftFreqs = [];
    private List<float[]> _fftMags = new();
    private long _fftLastSubmitMs;

    private int _tickCount;

    private readonly List<Controls.WaveformControl> _bioWaves;
    private readonly Dictionary<string, CheckBox> _ntfBoxes = new();
    private readonly Dictionary<string, CheckBox> _filterBoxes = new();
    private readonly Dictionary<int, RadioButton> _sampleRateRadios = new();
    private readonly Dictionary<string, TextBlock> _valueLabels = new();
    private (List<float>? impedance, int channel)[] _bioTargets = new (List<float>?, int)[8];
    private int _bioPage;

    private readonly DispatcherTimer _plotTimer;

    private sealed class DeviceEntry
    {
        public string Name = string.Empty;
        public string Mac = string.Empty;
        public int Rssi;
    }

    // Device-list row
    private sealed class DeviceRow : INotifyPropertyChanged
    {
        public string Mac { get; }
        private string _text;
        public string Text
        {
            get => _text;
            set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
        }
        public DeviceRow(string mac, string text) { Mac = mac; _text = text; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public MainWindow()
    {
        InitializeComponent();

        string sdkVersion = _ctrl.GetVersion();
        Title = $"SensorSDKCXX IMU + Quaternion + EMG + EEG Demo (Multi) (sensor-sdk v{sdkVersion}, demo v{DemoVersion})";
        SdkLabel.Text = "SDK: " + sdkVersion;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1600, 900));

        _bioWaves = [BioWave0, BioWave1, BioWave2, BioWave3,
                     BioWave4, BioWave5, BioWave6, BioWave7];
        foreach (Controls.WaveformControl w in _bioWaves)
            w.SetAutoYRange();

        CloneDataBox.Click += (_, _) => _cloneData = CloneDataBox.IsChecked == true;

        foreach (string key in NtfKeys)
        {
            var cb = new CheckBox
            {
                Content = NtfLabels[key],
                IsChecked = true,
                IsEnabled = false,
                Margin = new Thickness(0, 0, 12, 0),
            };
            cb.Click += (_, _) => OnNtfToggled(key, cb);
            _ntfBoxes[key] = cb;
            NtfPanel.Children.Add(cb);
        }
        foreach (string key in FilterKeys)
        {
            var cb = new CheckBox
            {
                Content = FilterLabels[key],
                IsChecked = true,
                IsEnabled = false,
                Margin = new Thickness(0, 0, 12, 0),
            };
            cb.Click += (_, _) => OnFilterToggled(key, cb);
            _filterBoxes[key] = cb;
            FilterPanel.Children.Add(cb);
        }
        foreach (int rate in SampleRateCandidates)
        {
            var rb = new RadioButton
            {
                Content = $"{rate} Hz",
                GroupName = "EegSampleRate",
                IsEnabled = false,
            };
            rb.Checked += (_, _) => OnSampleRateChecked(rate);
            _sampleRateRadios[rate] = rb;
            SampleRatePanel.Children.Add(rb);
        }
        foreach (string label in LiveFilter.BandLabels())
            FilterCombo.Items.Add(label);
        FilterCombo.SelectedIndex = 0;
        TypeCombo.SelectedIndex = 0;

        _ctrl.EnableChanged += enabled => Post(() => OnBtEnableChanged(enabled));
        _ctrl.DeviceFound += devices => Post(() => OnScanResults(devices));

        // Data worker
        _dataWorker = new Thread(DrainDataQueue) { IsBackground = true, Name = "DataWorker" };
        _dataWorker.Start();

        if (_debugLogEnabled)
            ApplySdkDebugLog();

        _plotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PlotUpdateIntervalMs) };
        _plotTimer.Tick += (_, _) => OnPlotTick();
        _plotTimer.Start();

        Closed += OnWindowClosed;

        RetargetWaveforms();
    }

    // ------------------------------------------------------------------
    // UI helpers
    // ------------------------------------------------------------------

    private void Post(Action action) => DispatcherQueue.TryEnqueue(() => action());

    private DeviceState? StateFor(string mac)
    {
        lock (_statesMutex)
            return _deviceStates.GetValueOrDefault(mac);
    }

    private DeviceState? CurrentState() => StateFor(_currentMac);

    private string SelectedMac()
        => (DeviceList.SelectedItem as DeviceRow)?.Mac ?? string.Empty;

    /// <summary>Writes one app event line into the SDK log.</summary>
    private void AppLog(string msg, string level = "I", DeviceState? st = null)
    {
        try
        {
            DeviceState? target = st ?? CurrentState();
            if (target != null)
                target.Profile.Log(msg, level);
            else
                _ctrl.Log(msg, level);
        }
        catch { }
    }

    private async void ShowWarning(string title, string message)
    {
        if (_dialogOpen || Content.XamlRoot == null)
            return;
        _dialogOpen = true;
        try
        {
            var dlg = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await dlg.ShowAsync();
        }
        catch { }
        finally { _dialogOpen = false; }
    }

    // ------------------------------------------------------------------
    // Scan
    // ------------------------------------------------------------------

    private void OnStartScan(object sender, RoutedEventArgs e)
    {
        if (!_ctrl.IsEnable)
        {
            AppLog("User: start scan rejected (Bluetooth disabled)", "W");
            StatusText.Text = "Please enable Bluetooth first";
            return;
        }
        AppLog("User: start scan");
        if (!_ctrl.IsScanning)
            _ctrl.StartScan(ScanDevicePeriodMs);
        _scanning = true;
        ScanButton.IsEnabled = false;
        StopScanButton.IsEnabled = true;
    }

    private void OnStopScan(object sender, RoutedEventArgs e)
    {
        AppLog("Stop scan");
        _ctrl.StopScan();
        _scanning = false;
        ScanButton.IsEnabled = _replayMacs.Count == 0;
        StopScanButton.IsEnabled = false;
    }

    private void OnBtEnableChanged(bool enabled)
    {
        if (!enabled)
            StatusText.Text = "Please enable Bluetooth first";
    }

    private void OnScanResults(List<BleDevice> devices)
    {
        foreach (BleDevice d in devices)
        {
            int found = _discovered.FindIndex(x => x.Mac == d.Mac);
            if (found < 0)
            {
                _discovered.Add(new DeviceEntry { Name = d.Name, Mac = d.Mac, Rssi = d.Rssi });
                InsertDeviceRowSorted(new DeviceRow(d.Mac,
                    $"RSSI: {d.Rssi}, Name: {d.Name}, Address: {d.Mac}"), d.Rssi);
            }
            else
            {
                _discovered[found].Rssi = d.Rssi;
                lock (_statesMutex)
                    UpdateDeviceItemText(d.Mac, _deviceStates.ContainsKey(d.Mac));
            }
        }
        UpdateButtonStates();
    }

    // ------------------------------------------------------------------
    // Device list / selection
    // ------------------------------------------------------------------

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string mac = SelectedMac();
        DeviceState? st = StateFor(mac);
        _currentMac = st != null ? mac : string.Empty;
        RetargetWaveforms();
        RefreshInfoPanel();
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        string mac = SelectedMac();
        bool connected;
        int stateCount;
        lock (_statesMutex)
        {
            connected = mac.Length > 0 && _deviceStates.ContainsKey(mac);
            stateCount = _deviceStates.Count;
        }
        bool replaying = _replayMacs.Count > 0;
        ConnectButton.IsEnabled = !replaying && mac.Length > 0 && !connected;
        DisconnectButton.IsEnabled = !replaying && connected;
        ScanButton.IsEnabled = !replaying && !_scanning;
        MultiSyncButton.Content = _streamingMacs.Count > 0 ? "Multi Stop" : "Multi Start";
        MultiSyncButton.IsEnabled = !replaying && stateCount >= 1;
    }

    private void UpdateDeviceItemText(string mac, bool connected)
    {
        DeviceRow? row = DeviceList.Items.OfType<DeviceRow>().FirstOrDefault(r => r.Mac == mac);
        if (row == null)
            return;
        if (_replayMacs.Contains(mac))
        {
            UpdateReplayItemText(mac);
            return;
        }
        string name = mac;
        int rssi = 0;
        foreach (DeviceEntry d in _discovered)
        {
            if (d.Mac == mac) { name = d.Name; rssi = d.Rssi; break; }
        }
        string text = $"RSSI: {rssi}, Name: {name}, Address: {mac}";
        if (_streamingMacs.Contains(mac))
            text = "[Streaming] " + text;
        else if (connected)
            text = "[Connected] " + text;
        row.Text = text;
    }

    private void InsertDeviceRowSorted(DeviceRow row, int rssi)
    {
        int RssiOf(DeviceRow r)
        {
            foreach (DeviceEntry d in _discovered)
                if (d.Mac == r.Mac) return d.Rssi;
            return int.MinValue;
        }
        int pos = DeviceList.Items.Count;
        for (int i = 0; i < DeviceList.Items.Count; i++)
        {
            if (DeviceList.Items[i] is DeviceRow existing && RssiOf(existing) < rssi)
            {
                pos = i;
                break;
            }
        }
        DeviceList.Items.Insert(pos, row);
    }

    // ------------------------------------------------------------------
    // Connect chain
    // ------------------------------------------------------------------

    private async void OnConnectClicked(object sender, RoutedEventArgs e)
    {
        string mac = SelectedMac();
        if (mac.Length == 0)
        {
            AppLog("User: connect rejected (no device selected)", "W");
            StatusText.Text = "Please select a device in the list first";
            return;
        }
        lock (_statesMutex)
        {
            if (_deviceStates.ContainsKey(mac))
                return;
        }
        string name = mac;
        foreach (DeviceEntry d in _discovered)
            if (d.Mac == mac) { name = d.Name; break; }
        AppLog($"User: connect {name} ({mac})");

        SensorProfile profile = _ctrl.RequireSensor(mac);
        HookProfileEvents(profile);
        profile.SetAutoReconnect(AutoReconnectBox.IsChecked == true);

        var st = new DeviceState(profile) { Name = name };
        st.LiveFilterState.SetBand(FilterCombo.SelectedIndex);
        lock (_statesMutex)
            _deviceStates[mac] = st;

        ConnectButton.IsEnabled = false;
        _currentMac = mac;
        RetargetWaveforms();
        RefreshInfoPanel();
        StatusText.Text = $"Connecting: {st.Name} ...";

        await RunConnectChain(st);
    }

    private void HookProfileEvents(SensorProfile profile)
    {
        string mac = profile.Device.Mac;
        profile.DataReceived += (_, dataList) => EnqueueData(mac, dataList);
        profile.StateChanged += (_, state) => Post(() => OnStateChanged(mac, state));
        profile.ErrorReceived += (_, msg) => Post(() => OnError(mac, msg));
        profile.PowerChanged += (_, power) => Post(() => OnPowerChanged(mac, power));
        profile.DeviceInfoUpdated += (_, _) => Post(() => OnDeviceInfoUpdate(mac));
        profile.DataTransferStateChanged += (_, on) => Post(() => OnDataTransferStateChanged(mac, on));
        profile.OnAutoReconnect = (p, hasLastSession, answer) =>
        {
            p.Log("App: auto reconnect callback received, restore=" + (hasLastSession ? "True" : "False"));
            Post(() => RecoverDevice(mac, hasLastSession));
            answer(true);
        };
    }

    private async void RecoverDevice(string mac, bool restore)
    {
        // App-driven recovery: re-select the row and re-run the connect
        // chain; the recorded setParam history replays after the stream
        // start.
        DeviceRow? row = DeviceList.Items.OfType<DeviceRow>().FirstOrDefault(r => r.Mac == mac);
        if (row != null)
            DeviceList.SelectedItem = row;
        lock (_statesMutex)
        {
            if (_deviceStates.ContainsKey(mac))
                return;
        }
        SensorProfile? profile = _ctrl.GetSensor(mac);
        if (profile == null)
            return;
        if (restore)
            _restoreParamsMacs.Add(mac);
        string name = mac;
        foreach (DeviceEntry d in _discovered)
            if (d.Mac == mac) { name = d.Name; break; }
        var st = new DeviceState(profile) { Name = name };
        st.LiveFilterState.SetBand(FilterCombo.SelectedIndex);
        lock (_statesMutex)
            _deviceStates[mac] = st;
        _currentMac = mac;
        RetargetWaveforms();
        RefreshInfoPanel();
        await RunConnectChain(st);
    }

    private async Task RunConnectChain(DeviceState st)
    {
        try
        {
            if (!st.Profile.IsReady)
            {
                bool ok = await st.Profile.ConnectAsync();
                if (!ok)
                {
                    AppLog($"App: failed to connect {st.Name} ({st.Mac})", "E", st);
                    StatusText.Text = $"Failed to connect {st.Name}";
                    UpdateButtonStates();
                    return;
                }
            }
            if (!st.Profile.HasInited)
            {
                StatusText.Text = $"Initializing {st.Name} ...";
                try
                {
                    await st.Profile.InitAsync(PackageCount, PowerRefreshPeriodMs, CmdTimeoutMs);
                }
                catch (Exception ex)
                {
                    AppLog($"App: failed to initialize {st.Name} ({st.Mac})", "E", st);
                    StatusText.Text = $"Failed to initialize {st.Name}: {ex.Message}";
                    UpdateButtonStates();
                    return;
                }
            }
            try
            {
                st.Info = await st.Profile.FetchDeviceInfoAsync(CmdTimeoutMs);
                st.HasInfo = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DemoWinUI3] fetchDeviceInfo failed: " + ex.Message);
            }
            if (!st.Profile.IsDataTransfering)
            {
                try
                {
                    await st.Profile.StartDataNotificationAsync(CmdTimeoutMs);
                }
                catch (Exception ex)
                {
                    AppLog($"App: failed to start data stream on {st.Mac}", "E", st);
                    StatusText.Text = "Failed to start data stream: " + ex.Message;
                    UpdateButtonStates();
                    return;
                }
            }
            st.FlowStarted = true;
            AppLog($"App: device connected and streaming: {st.Name} ({st.Mac})", "I", st);
            UpdateDeviceItemText(st.Mac, true);
            await ApplySessionParams(st);
            if (_restoreParamsMacs.Remove(st.Mac))
                await RestoreSavedParams(st);
            else
                RefreshControlStates(st);
            if (_currentMac == st.Mac)
            {
                RefreshInfoPanel();
                RetargetWaveforms();
            }
            UpdateButtonStates();

            try
            {
                int result = await st.Profile.GetBatteryLevelAsync(CmdTimeoutMs);
                if (result >= 0
                    && (st.LastPower < 0 || result - st.LastPower >= PowerStableBand
                        || st.LastPower - result >= PowerStableBand))
                {
                    st.LastPower = result;
                    if (st.Mac == _currentMac)
                        PowerText.Text = $"Power: {result}%";
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            AppLog("App: connect chain failed: " + ex.Message, "E", st);
            StatusText.Text = "Connect failed: " + ex.Message;
            UpdateButtonStates();
        }
    }

    private void OnDisconnectClicked(object sender, RoutedEventArgs e)
    {
        DeviceState? st = CurrentState();
        if (st == null || st.IsReplay)
            return;
        AppLog($"User: disconnect {st.Mac}", "I", st);
        foreach (CheckBox cb in _ntfBoxes.Values)
            cb.IsEnabled = false;
        foreach (CheckBox cb in _filterBoxes.Values)
            cb.IsEnabled = false;
        foreach (RadioButton rb in _sampleRateRadios.Values)
            rb.IsEnabled = false;
        if (st.Profile.DeviceState == SenDeviceState.Disconnected)
        {
            OnStateChanged(st.Mac, SenDeviceState.Disconnected);
            return;
        }
        DisconnectButton.IsEnabled = false;
        ConnectButton.IsEnabled = false;
        StatusText.Text = "Disconnecting...";
        _ = st.Profile.DisconnectAsync();
    }

    // ------------------------------------------------------------------
    // SDK events
    // ------------------------------------------------------------------

    private void EnqueueData(string mac, List<SensorData> dataList)
    {
        bool clone = _cloneData;
        foreach (SensorData d in dataList)
        {
            if (d.SampleCount <= 0 || d.ChannelCount <= 0)
                continue;
            SensorData item = clone ? d.Clone() : d;
            lock (_dataQueue)
            {
                while (_dataQueue.Count >= 1000)
                    _dataQueue.Dequeue();
                _dataQueue.Enqueue(new QueuedItem(mac, item));
            }
        }
        _dataQueueEvent.Set();
    }

    private void DrainDataQueue()
    {
        var pending = new List<QueuedItem>();
        while (true)
        {
            _dataQueueEvent.WaitOne();
            lock (_dataQueue)
            {
                if (_dataWorkerStop && _dataQueue.Count == 0)
                    return;
                while (_dataQueue.Count > 0)
                    pending.Add(_dataQueue.Dequeue());
            }
            foreach (QueuedItem item in pending)
            {
                DeviceState? st = StateFor(item.Mac);
                st?.AppendData(item.Data);
            }
            pending.Clear();
        }
    }

    private void OnStateChanged(string mac, SenDeviceState state)
    {
        DeviceState? st = StateFor(mac);
        if (state == SenDeviceState.Ready)
        {
            if (st != null && st.FlowStarted && mac == _currentMac)
                StatusText.Text = st.BuildStatusText();
            return;
        }
        if (state != SenDeviceState.Disconnected || st == null || st.IsReplay)
            return;
        // Every disconnect tears the UI state down; with Auto Reconnect on,
        // the app-driven recovery (RecoverDevice) rebuilds it once the link
        // is back.
        _restoreParamsMacs.Remove(mac);
        st.NtfStates.Clear();
        st.FilterStates.Clear();
        st.SampleRateOptions.Clear();
        st.SampleRateCurrent = 0;
        lock (_statesMutex)
            _deviceStates.Remove(mac);
        AppLog($"App: device disconnected, removed from UI: {mac}");
        _streamingMacs.Remove(mac);
        UpdateDeviceItemText(mac, false);
        if (_currentMac == mac)
        {
            _currentMac = string.Empty;
            RetargetWaveforms();
            RefreshInfoPanel();
            StatusText.Text = "Disconnected (device)";
            RateText.Text = string.Empty;
        }
        UpdateButtonStates();
    }

    private void OnError(string mac, string message)
    {
        System.Diagnostics.Debug.WriteLine($"[DemoWinUI3] error from {mac}: {message}");
        DeviceState? st = StateFor(mac);
        AppLog($"App: error callback: {message}", "E", st);
        if (st != null && mac == _currentMac)
            StatusText.Text = "Error: " + message;
    }

    private void OnPowerChanged(string mac, int power)
    {
        DeviceState? st = StateFor(mac);
        if (st == null || power < 0)
            return;
        st.LastPower = power;
        if (mac == _currentMac)
            PowerText.Text = $"Power: {power}%";
    }

    private void OnDeviceInfoUpdate(string mac)
    {
        DeviceState? st = StateFor(mac);
        if (st == null)
            return;
        st.Info = st.Profile.GetDeviceInfo();
        st.HasInfo = true;
        st.SyncSampleRates();
        if (st.Info.EEGSampleRate > 0 && st.Info.EEGSampleRate != st.SampleRateCurrent)
        {
            st.SampleRateCurrent = st.Info.EEGSampleRate;
            if (mac == _currentMac)
                SetSampleRateChecked(st.SampleRateCurrent);
        }
        if (mac == _currentMac)
        {
            LinkText.Text = LinkTextOf(st.Info);
            MtuText.Text = MtuTextOf(st.Info);
            if (st.FlowStarted)
            {
                StatusText.Text = st.BuildStatusText();
                RateText.Text = st.BuildRateText();
            }
        }
    }

    private void OnDataTransferStateChanged(string mac, bool isTransferring)
    {
        DeviceState? st = StateFor(mac);
        if (isTransferring)
            _streamingMacs.Add(mac);
        else
            _streamingMacs.Remove(mac);
        AppLog($"App: data stream {(isTransferring ? "ON" : "OFF")} {mac}", "I", st);
        if (_replayMacs.Contains(mac))
        {
            UpdateReplayItemText(mac);
            if (!isTransferring)
            {
                // Replay EOF (or a user stop): finish the member here.
                OnReplayDone(mac, _replayStopRequested ? "Replay stopped" : "Replay finished");
            }
            return;
        }
        UpdateDeviceItemText(mac, st != null);
        UpdateButtonStates();
    }

    private void UpdateReplayItemText(string mac)
    {
        if (!_replayMacs.Contains(mac))
            return;
        DeviceState? st = StateFor(mac);
        if (st == null)
            return;
        string prefix = _streamingMacs.Contains(mac) ? "[Streaming] [Replay] " : "[Replay] ";
        DeviceRow? row = DeviceList.Items.OfType<DeviceRow>().FirstOrDefault(r => r.Mac == mac);
        if (row != null)
            row.Text = $"{prefix}{st.Name}, Address: {mac}";
    }

    // ------------------------------------------------------------------
    // setParam / getParam controls
    // ------------------------------------------------------------------

    private static (string msg, bool isError) SetParamOutcome(string result)
    {
        bool isError = result.StartsWith("Error") || result.StartsWith("ERROR:");
        return (result, isError);
    }

    private async Task<(string msg, bool isError)> SendSetParam(SensorProfile profile, string key, string value)
    {
        try
        {
            string result = await profile.SetParamAsync(key, value, CmdTimeoutMs);
            return SetParamOutcome(result);
        }
        catch (Exception ex)
        {
            return ("Error: " + ex.Message, true);
        }
    }

    private async void OnNtfToggled(string key, CheckBox cb)
    {
        if (_updatingControls)
            return;
        DeviceState? st = CurrentState();
        if (st == null || !st.Profile.IsReady)
            return;
        string value = cb.IsChecked == true ? "ON" : "OFF";
        (string msg, bool isError) = await SendSetParam(st.Profile, key, value);
        AppLog($"User: setParam({key}, {value}) -> {msg}");
        RecordSavedParam(st.Mac, key, value, msg);
        if (isError)
        {
            ShowWarning("Set Parameter Failed", $"Failed to set {key}:\n{msg}");
            RefreshControlStates(st);
            return;
        }
        RefreshControlStates(CurrentState());
        ClearUiData();
    }

    private async void OnFilterToggled(string key, CheckBox cb)
    {
        if (_updatingControls)
            return;
        DeviceState? st = CurrentState();
        if (st == null || !st.Profile.IsReady)
            return;
        string value = cb.IsChecked == true ? "ON" : "OFF";
        (string msg, bool isError) = await SendSetParam(st.Profile, key, value);
        AppLog($"User: setParam({key}, {value}) -> {msg}");
        RecordSavedParam(st.Mac, key, value, msg);
        if (isError)
        {
            ShowWarning("Set Parameter Failed", $"Failed to set {key}:\n{msg}");
            RefreshControlStates(st);
            return;
        }
        RefreshControlStates(CurrentState());
        ClearUiData();
    }

    private async void OnSampleRateChecked(int rate)
    {
        if (_updatingControls)
            return;
        DeviceState? st = CurrentState();
        if (st == null || !st.Profile.IsReady)
            return;
        string value = rate.ToString();
        (string msg, bool isError) = await SendSetParam(st.Profile, "EEG_SAMPLE_RATE", value);
        AppLog($"User: setParam(EEG_SAMPLE_RATE, {value}) -> {msg}");
        RecordSavedParam(st.Mac, "EEG_SAMPLE_RATE", value, msg);
        if (isError)
        {
            ShowWarning("Set Parameter Failed", $"Failed to set EEG_SAMPLE_RATE:\n{msg}");
            RefreshControlStates(st);
            return;
        }
        RefreshControlStates(CurrentState());
        ClearUiData();
    }

    private static async Task<string> SafeGetParam(SensorProfile profile, string key)
    {
        try
        {
            return await profile.GetParamAsync(key, 5000);
        }
        catch (Exception ex)
        {
            return "Error: " + ex.Message;
        }
    }

    private async void RefreshControlStates(DeviceState? st)
    {
        if (st == null)
            return;
        string mac = st.Mac;
        string ntfResult = await SafeGetParam(st.Profile, "NTF");
        string filterResult = await SafeGetParam(st.Profile, "FILTER");
        string rateListResult = await SafeGetParam(st.Profile, "EEG_SAMPLE_RATE_LIST");
        string rateResult = await SafeGetParam(st.Profile, "EEG_SAMPLE_RATE");
        st = StateFor(mac);
        if (st == null)
            return;
        ApplyRefreshedControlStates(st, ntfResult, filterResult, rateListResult, rateResult);
    }

    private void ApplyRefreshedControlStates(DeviceState st, string ntfResult, string filterResult,
                                             string rateListResult, string rateResult)
    {
        int emgCh = st.HasInfo ? st.Info.EMGChannelCount : 0;
        int eegCh = st.HasInfo ? st.Info.EEGChannelCount : 0;
        int imuCh = st.HasInfo ? Math.Max(st.Info.AccChannelCount, st.Info.GyroChannelCount) : 0;
        int ppgCh = st.HasInfo ? st.Info.PpgChannelCount : 0;
        int spo2Ch = st.HasInfo ? st.Info.Spo2ChannelCount : 0;
        var channelMap = new Dictionary<string, int>
        {
            ["NTF_EEG"] = eegCh,
            ["NTF_EMG"] = emgCh,
            ["NTF_GEST"] = emgCh,
            ["NTF_PPG"] = ppgCh,
            ["NTF_SPO2"] = spo2Ch,
            ["NTF_IMU"] = imuCh,
        };

        var ntf = new Dictionary<string, (bool enabled, bool check)>();
        if (!ntfResult.StartsWith("Error"))
        {
            string[] items = ntfResult.Split('|');
            for (int i = 0; i + 1 < items.Length; i += 2)
            {
                string key = items[i];
                bool enabled = channelMap.GetValueOrDefault(key) > 0;
                ntf[key] = (enabled, enabled && items[i + 1] == "ON");
            }
        }

        var filters = new Dictionary<string, (bool enabled, bool check)>();
        bool hasFilter = filterResult.Length > 0 && !filterResult.StartsWith("Error");
        var parsed = new Dictionary<string, string>();
        if (hasFilter)
        {
            string[] items = filterResult.Split('|');
            for (int i = 0; i + 1 < items.Length; i += 2)
                parsed[items[i]] = items[i + 1];
        }
        foreach (string key in _filterBoxes.Keys)
            filters[key] = (hasFilter, hasFilter && parsed.GetValueOrDefault(key) == "ON");

        // EEG Sample Rate radios
        var rateOptions = new List<int>();
        if (!rateListResult.StartsWith("Error"))
        {
            foreach (string item in rateListResult.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(item, out int rate))
                    rateOptions.Add(rate);
            }
        }
        int rateCurrent = 0;
        if (!rateResult.StartsWith("Error") && int.TryParse(rateResult, out int rc))
            rateCurrent = rc;

        st.NtfStates = ntf;
        st.FilterStates = filters;
        st.SampleRateOptions = rateOptions;
        st.SampleRateCurrent = rateCurrent;
        if (st == CurrentState())
            ApplyControlStates(ntf, filters, rateOptions, rateCurrent,
                               st.HasInfo && st.Info.EEGChannelCount == 0);
    }

    private void ApplyControlStates(Dictionary<string, (bool enabled, bool check)> ntf,
                                    Dictionary<string, (bool enabled, bool check)> filters,
                                    List<int> rateOptions, int rateCurrent, bool hideSampleRate)
    {
        _updatingControls = true;
        try
        {
            NtfPanel.Children.Clear();
            foreach (string key in NtfKeys)
            {
                CheckBox cb = _ntfBoxes[key];
                (bool enabled, bool check) state = ntf.GetValueOrDefault(key);
                cb.IsEnabled = state.enabled;
                cb.IsChecked = state.check;
                if (state.enabled || ntf.Count == 0)
                    NtfPanel.Children.Add(cb);
            }
            foreach (var kv in _filterBoxes)
            {
                (bool enabled, bool check) state = filters.GetValueOrDefault(kv.Key);
                kv.Value.IsEnabled = state.enabled;
                kv.Value.IsChecked = state.check;
            }
            SampleRateSection.Visibility = hideSampleRate ? Visibility.Collapsed : Visibility.Visible;
            foreach (var kv in _sampleRateRadios)
            {
                bool supported = rateOptions.Contains(kv.Key);
                kv.Value.Visibility = supported || rateOptions.Count == 0
                    ? Visibility.Visible : Visibility.Collapsed;
                kv.Value.IsEnabled = supported;
                kv.Value.IsChecked = kv.Key == rateCurrent;
            }
        }
        finally
        {
            _updatingControls = false;
        }
    }

    /// <summary>Syncs the sample-rate radios' checked state.</summary>
    private void SetSampleRateChecked(int rate)
    {
        _updatingControls = true;
        try
        {
            foreach (var kv in _sampleRateRadios)
                kv.Value.IsChecked = kv.Key == rate;
        }
        finally
        {
            _updatingControls = false;
        }
    }

    private void ClearUiData() => CurrentState()?.ClearBuffers();

    // ------------------------------------------------------------------
    // Debug log / bin data toggles
    // ------------------------------------------------------------------

    private void ApplySdkDebugLog()
    {
        string version = _ctrl.GetVersion().Replace('.', '_');
        string dir = Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "sensorsdklog",
            DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + version);
        _ctrl.SetLogPath(true, dir);
        _ctrl.SetDebugEnabled(true);
        System.Diagnostics.Debug.WriteLine("[DemoWinUI3] setLogPath -> " + dir);
    }

    private void OnDebugLogToggled(object sender, RoutedEventArgs e)
    {
        _debugLogEnabled = DebugLogBox.IsChecked == true;
        AppLog($"User: SDK debug log {(_debugLogEnabled ? "ON" : "OFF")}");
        if (_debugLogEnabled)
            ApplySdkDebugLog();
        else
            _ctrl.SetDebugEnabled(false);
        string value = _debugLogEnabled ? "True" : "False";
        foreach (DeviceState st in SnapshotStates())
        {
            if (!st.Profile.IsReady || !st.Profile.HasInited)
                continue;
            _ = PushDebugPathParam(st, "DEBUG_LOG_PATH", value);
        }
    }

    private void OnBinDataToggled(object sender, RoutedEventArgs e)
    {
        _binDataEnabled = BinDataBox.IsChecked == true;
        AppLog($"User: data debug log {(_binDataEnabled ? "ON" : "OFF")}");
        string value = _binDataEnabled ? "True" : "False";
        foreach (DeviceState st in SnapshotStates())
        {
            if (!st.Profile.IsReady || !st.Profile.HasInited)
                continue;
            _ = PushDebugPathParam(st, "DEBUG_BLE_DATA_PATH", value);
        }
    }

    private List<DeviceState> SnapshotStates()
    {
        lock (_statesMutex)
            return _deviceStates.Values.ToList();
    }

    private async Task PushDebugPathParam(DeviceState st, string key, string value)
    {
        (string msg, bool isError) = await SendSetParam(st.Profile, key, value);
        st.Profile.Log($"App: setParam({key}, {value}) -> {msg}");
        if (isError)
            ShowWarning("Set Parameter Failed", $"Failed to set {key}:\n{msg}");
    }

    private async Task ApplySessionParams(DeviceState st)
    {
        // One setParam at a time (the SDK serializes setParam per profile).
        if (_debugLogEnabled)
        {
            string path = _lastLogPaths.GetValueOrDefault(st.Mac, "True");
            await ApplySessionParam(st, "DEBUG_LOG_PATH", path, _lastLogPaths);
        }
        if (_binDataEnabled)
        {
            string path = _lastDataPaths.GetValueOrDefault(st.Mac, "True");
            await ApplySessionParam(st, "DEBUG_BLE_DATA_PATH", path, _lastDataPaths);
        }
    }

    private async Task ApplySessionParam(DeviceState st, string key, string value,
                                         Dictionary<string, string> cache)
    {
        (string _, bool isError) = await SendSetParam(st.Profile, key, value);
        if (isError)
            return;
        string cur = await SafeGetParam(st.Profile, key);
        if (cur.Length > 0 && !cur.StartsWith("Error"))
            cache[st.Mac] = cur;
    }

    private void RecordSavedParam(string mac, string key, string value, string result)
    {
        if (mac.Length == 0 || result.StartsWith("Error"))
            return;
        if (!_savedParamsByMac.TryGetValue(mac, out List<KeyValuePair<string, string>>? saved))
            _savedParamsByMac[mac] = saved = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < saved.Count; i++)
        {
            if (saved[i].Key == key)
            {
                saved[i] = new KeyValuePair<string, string>(key, value);
                return;
            }
        }
        saved.Add(new KeyValuePair<string, string>(key, value));
    }

    private async Task RestoreSavedParams(DeviceState st)
    {
        string mac = st.Mac;
        if (!_savedParamsByMac.TryGetValue(mac, out List<KeyValuePair<string, string>>? saved))
            return;
        foreach (KeyValuePair<string, string> kv in saved)
        {
            (string msg, bool _) = await SendSetParam(st.Profile, kv.Key, kv.Value);
            AppLog($"App: restore setParam({kv.Key}, {kv.Value}) -> {msg}", "I", StateFor(mac));
        }
        RefreshControlStates(StateFor(mac));
        ClearUiData();
    }

    private void OnAutoReconnectToggled(object sender, RoutedEventArgs e)
    {
        bool isChecked = AutoReconnectBox.IsChecked == true;
        AppLog($"User: auto reconnect {(isChecked ? "ON" : "OFF")}");
        foreach (DeviceState st in SnapshotStates())
            st.Profile.SetAutoReconnect(isChecked);
    }

    // ------------------------------------------------------------------
    // Waveform targeting / labels
    // ------------------------------------------------------------------

    private static string LinkTextOf(DeviceInfo info)
    {
        if (info.PeripheralLatency < 0 || info.ConnectionIntervalMs <= 0)
            return "Link: --";
        return $"Link: {info.ConnectionIntervalMs}ms / latency {info.PeripheralLatency} / timeout {info.SupervisionTimeoutMs}ms";
    }

    private static string MtuTextOf(DeviceInfo info)
        => info.MTUSize <= 0 ? "MTU: --" : $"MTU: {info.MTUSize}";

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TypeCombo.SelectedItem is string text)
            AppLog($"User: display data type -> {text}");
        RetargetWaveforms();
    }

    private void OnLiveFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterCombo.SelectedItem is string text)
            AppLog($"User: live filter -> {text}");
        foreach (DeviceState st in SnapshotStates())
            st.LiveFilterState.SetBand(FilterCombo.SelectedIndex);
    }

    private void RetargetWaveforms()
    {
        DeviceState? st = CurrentState();
        int typeIndex = Math.Max(0, TypeCombo.SelectedIndex);
        RingBuffer? buf;
        string[] labels;
        double yLow, yHigh;
        switch (typeIndex)
        {
            case 0:
                buf = st?.Acc;
                labels = ["ACC-X", "ACC-Y", "ACC-Z"];
                yLow = -8; yHigh = 8;
                break;
            case 1:
                buf = st?.Gyro;
                labels = ["GYRO-X", "GYRO-Y", "GYRO-Z"];
                yLow = -2000; yHigh = 2000;
                break;
            case 2:
                buf = st?.Quat;
                labels = ["W", "X", "Y", "Z"];
                yLow = -1; yHigh = 1;
                break;
            default:
                buf = st?.Euler;
                labels = ["Pitch(Y)", "Roll(X)", "Yaw(Z)"];
                yLow = -180; yHigh = 180;
                break;
        }
        if (st != null)
        {
            Wave2d.SetSource(buf, st.BufMutex, -1);
            Wave2d.SetPlaceholder("Waiting for data ...");
        }
        else
        {
            Wave2d.SetSource(null, null, -1);
            Wave2d.SetPlaceholder("Not connected");
        }
        Wave2d.SetLabels(labels);
        Wave2d.SetFixedYRange(yLow, yHigh);

        Spectrum.SetLabels(labels);
        Spectrum.SetPlaceholder(Wave2d.HasSource ? "Waiting for data ..." : "Not connected");
        Spectrum.ClearResult();

        ValuePanel.Children.Clear();
        _valueLabels.Clear();
        foreach (string n in labels)
        {
            var row = new TextBlock
            {
                Text = $"{n}: --",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 13,
            };
            ValuePanel.Children.Add(row);
            _valueLabels[n] = row;
        }

        RetargetBio(st);
    }

    // ------------------------------------------------------------------
    // Bio panel
    // ------------------------------------------------------------------

    private void RetargetBio(DeviceState? st)
    {
        _bioPage = 0;
        LayoutBio(st);
    }

    private int BioPageCount(DeviceState? st)
    {
        if (st == null || st.GetBioKind() != DeviceState.BioKind.EEG)
            return 1;
        int extras = (st.Info.ECGChannelCount > 0 ? 1 : 0)
                   + (st.Info.BRTHChannelCount > 0 ? 1 : 0);
        int perPage = _bioWaves.Count - extras;
        int total = st.Info.EEGChannelCount > 0 ? st.Info.EEGChannelCount
                  : st.Eeg.Allocated ? st.Eeg.Channels : 0;
        return Math.Max(1, (total + perPage - 1) / perPage);
    }

    private void UpdatePageControls()
    {
        DeviceState? st = CurrentState();
        int pages = BioPageCount(st);
        PageControls.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = $"Page {_bioPage + 1} / {pages}";
        PrevPageButton.IsEnabled = _bioPage > 0;
        NextPageButton.IsEnabled = _bioPage < pages - 1;
    }

    private void OnPrevPage(object sender, RoutedEventArgs e)
    {
        if (_bioPage <= 0)
            return;
        --_bioPage;
        AppLog($"User: prev page -> {_bioPage}", "D");
        LayoutBio(CurrentState());
    }

    private void OnNextPage(object sender, RoutedEventArgs e)
    {
        DeviceState? st = CurrentState();
        if (_bioPage >= BioPageCount(st) - 1)
            return;
        ++_bioPage;
        AppLog($"User: next page -> {_bioPage}", "D");
        LayoutBio(st);
    }

    private void LayoutBio(DeviceState? st)
    {
        _bioTargets = new (List<float>?, int)[_bioWaves.Count];
        DeviceState.BioKind kind = st?.GetBioKind() ?? DeviceState.BioKind.None;
        string waiting = st != null ? "Waiting for data ..." : "Not connected";

        if (kind == DeviceState.BioKind.EMG && st != null)
        {
            BioTitleText.Text = "EMG Waveform";
            int emgCh = st.Emg.Allocated ? Math.Min(st.Emg.Channels, _bioWaves.Count) : 0;
            for (int i = 0; i < _bioWaves.Count; i++)
            {
                if (i < emgCh)
                {
                    _bioWaves[i].SetSource(st.Emg, st.BufMutex, i);
                    _bioWaves[i].SetLabels([$"EMG-{i + 1}"]);
                    _bioWaves[i].SetPlaceholder(string.Empty);
                    _bioTargets[i] = (st.EmgImpedance, i);
                }
                else
                {
                    _bioWaves[i].SetSource(null, null, i);
                    _bioWaves[i].SetLabels([]);
                    _bioWaves[i].SetPlaceholder(waiting);
                    _bioWaves[i].SetSideText(string.Empty, Microsoft.UI.Colors.White);
                    _bioTargets[i] = (null, 0);
                }
            }
        }
        else if (kind == DeviceState.BioKind.EEG && st != null)
        {
            BioTitleText.Text = "EEG + ECG + BRTH Waveform";
            bool hasECG = st.Info.ECGChannelCount > 0 || st.Ecg.Allocated;
            bool hasBRTH = st.Info.BRTHChannelCount > 0 || st.Brth.Allocated;
            int perPage = _bioWaves.Count - (hasECG ? 1 : 0) - (hasBRTH ? 1 : 0);
            int total = st.Info.EEGChannelCount > 0 ? st.Info.EEGChannelCount
                      : st.Eeg.Allocated ? st.Eeg.Channels : 0;
            int pages = Math.Max(1, (total + perPage - 1) / perPage);
            _bioPage = Math.Clamp(_bioPage, 0, pages - 1);
            int startCh = _bioPage * perPage;
            int ecgIndex = _bioWaves.Count - 1 - (hasBRTH ? 1 : 0);
            int brthIndex = _bioWaves.Count - 1;
            for (int i = 0; i < _bioWaves.Count; i++)
            {
                int eegCh = startCh + i;
                if (i < perPage && eegCh < total && st.Eeg.Allocated)
                {
                    _bioWaves[i].SetSource(st.Eeg, st.BufMutex, eegCh);
                    _bioWaves[i].SetLabels([$"EEG-{eegCh + 1}"]);
                    _bioWaves[i].SetPlaceholder(string.Empty);
                    _bioTargets[i] = (st.EegImpedance, eegCh);
                }
                else if (hasECG && i == ecgIndex && st.Ecg.Allocated)
                {
                    _bioWaves[i].SetSource(st.Ecg, st.BufMutex, 0);
                    _bioWaves[i].SetLabels(["ECG"]);
                    _bioWaves[i].SetPlaceholder(string.Empty);
                    _bioTargets[i] = (st.EcgImpedance, 0);
                }
                else if (hasBRTH && i == brthIndex && st.Brth.Allocated)
                {
                    _bioWaves[i].SetSource(st.Brth, st.BufMutex, 0);
                    _bioWaves[i].SetLabels(["BRTH"]);
                    _bioWaves[i].SetPlaceholder(string.Empty);
                    _bioTargets[i] = (st.BrthImpedance, 0);
                }
                else
                {
                    _bioWaves[i].SetSource(null, null, i);
                    _bioWaves[i].SetLabels([]);
                    bool noSuchChannel = i < perPage && eegCh >= total;
                    _bioWaves[i].SetPlaceholder(noSuchChannel ? string.Empty : waiting);
                    _bioWaves[i].SetSideText(string.Empty, Microsoft.UI.Colors.White);
                    _bioTargets[i] = (null, 0);
                }
            }
        }
        else if (kind == DeviceState.BioKind.PPG && st != null)
        {
            BioTitleText.Text = "EEG + PPG + SpO2 Waveform";
            (RingBuffer buffer, int channel, string label, bool isEeg)[] plotConfig =
            [
                (st.Eeg, 0, "fp1", true),
                (st.Eeg, 1, "fp2", true),
                (st.Ppg, 0, "red_led", false),
                (st.Ppg, 1, "ir_led", false),
                (st.Spo2, 0, "spo2", false),
                (st.Spo2, 1, "heart_rate", false),
            ];
            for (int i = 0; i < _bioWaves.Count; i++)
            {
                bool bound = false;
                if (i < plotConfig.Length)
                {
                    (RingBuffer buf, int channel, string label, bool isEeg) = plotConfig[i];
                    if (buf.Allocated && channel < buf.Channels)
                    {
                        _bioWaves[i].SetSource(buf, st.BufMutex, channel, i);
                        _bioWaves[i].SetLabels([label]);
                        _bioWaves[i].SetPlaceholder(string.Empty);
                        _bioTargets[i] = isEeg ? (st.EegImpedance, channel) : (null, 0);
                        bound = true;
                    }
                }
                if (!bound)
                {
                    _bioWaves[i].SetSource(null, null, i);
                    _bioWaves[i].SetLabels([]);
                    _bioWaves[i].SetPlaceholder(i < plotConfig.Length ? waiting : string.Empty);
                    _bioWaves[i].SetSideText(string.Empty, Microsoft.UI.Colors.White);
                    _bioTargets[i] = (null, 0);
                }
            }
        }
        else
        {
            BioTitleText.Text = "EMG / EEG Waveform";
            for (int i = 0; i < _bioWaves.Count; i++)
            {
                _bioWaves[i].SetSource(null, null, i);
                _bioWaves[i].SetLabels([]);
                _bioWaves[i].SetPlaceholder(waiting);
                _bioWaves[i].SetSideText(string.Empty, Microsoft.UI.Colors.White);
                _bioTargets[i] = (null, 0);
            }
        }
        UpdatePageControls();
    }

    // ------------------------------------------------------------------
    // Periodic refresh
    // ------------------------------------------------------------------

    private void RefreshValueLabels()
    {
        DeviceState? st = CurrentState();
        if (st == null)
            return;
        RingBuffer buf = Math.Max(0, TypeCombo.SelectedIndex) switch
        {
            0 => st.Acc,
            1 => st.Gyro,
            2 => st.Quat,
            _ => st.Euler,
        };
        lock (st.BufMutex)
        {
            if (!buf.Allocated)
                return;
            int row = 0;
            foreach (var kv in _valueLabels)
            {
                if (row < buf.Channels)
                    kv.Value.Text = $"{kv.Key}: {buf.Latest(row):+0.0000;-0.0000;0.0000}";
                row++;
            }
        }
    }

    private void RefreshBioSideTexts()
    {
        DeviceState? st = CurrentState();
        if (st == null || _bioTargets.Length != _bioWaves.Count)
            return;
        lock (st.BufMutex)
        {
            for (int i = 0; i < _bioWaves.Count; i++)
            {
                (List<float>? impedance, int channel) = _bioTargets[i];
                if (impedance == null || channel >= impedance.Count || impedance[channel] < 0)
                    continue;
                double kOhm = impedance[channel] / 1000.0;
                Color color = kOhm <= 500 ? ColorHelper.FromArgb(255, 60, 200, 60)
                            : kOhm <= 999 ? ColorHelper.FromArgb(255, 230, 160, 40)
                                          : ColorHelper.FromArgb(255, 220, 60, 60);
                _bioWaves[i].SetSideText($"{kOhm:F2} KOhm", color);
            }
        }
    }

    private void RefreshGestureLabel()
    {
        DeviceState? st = CurrentState();
        if (st != null && st.Gesture >= 0)
        {
            lock (st.BufMutex)
            {
                GestureText.Text =
                    $"Gesture:\n  gesture: {st.Gesture} (0-8)\n  raw gesture: {st.RawGesture} (0-8)" +
                    $"\n  possiblity: {st.Possibility} (0-100)\n  strength: {st.Strength} (0-100)";
            }
        }
        else
        {
            GestureText.Text =
                "Gesture:\n  gesture: -- (0-8)\n  raw gesture: -- (0-8)" +
                "\n  possiblity: -- (0-100)\n  strength: -- (0-100)";
        }
    }

    private void RefreshInfoPanel()
    {
        DeviceState? st = CurrentState();
        if (st != null && st.HasInfo)
        {
            ModelText.Text = "Model: " + st.Info.ModelName;
            HwText.Text = "HW Version: " + st.Info.HardwareVersion;
            FwText.Text = "FW Version: " + st.Info.FirmwareVersion;
            LinkText.Text = LinkTextOf(st.Info);
            MtuText.Text = MtuTextOf(st.Info);
        }
        else
        {
            ModelText.Text = "Model: --";
            HwText.Text = "HW Version: --";
            FwText.Text = "FW Version: --";
            LinkText.Text = "Link: --";
            MtuText.Text = "MTU: --";
        }
        PowerText.Text = st != null && st.LastPower >= 0 ? $"Power: {st.LastPower}%" : "Power: --%";
        StatusText.Text = st != null ? st.BuildStatusText() : "Not Connected";
        RateText.Text = st != null ? st.BuildRateText() : string.Empty;
        UpdateLostPacketLabel();
        RefreshGestureLabel();
        if (st == null)
            Cube.ClearQuaternion();
        ApplyControlStates(st?.NtfStates ?? new Dictionary<string, (bool, bool)>(),
                           st?.FilterStates ?? new Dictionary<string, (bool, bool)>(),
                           st?.SampleRateOptions ?? new List<int>(),
                           st?.SampleRateCurrent ?? 0,
                           st != null && st.HasInfo && st.Info.EEGChannelCount == 0);
    }

    private void UpdateLostPacketLabel()
    {
        DeviceState? st = CurrentState();
        if (st != null)
        {
            lock (st.RateMutex)
            {
                if (st.LostCounts.Count > 0)
                {
                    LostPacketText.Text = "Packet Loss Stats: "
                        + string.Join("  ", st.LostCounts.Select(kv => $"{kv.Key}: {kv.Value}"));
                    return;
                }
            }
        }
        LostPacketText.Text = "Packet Loss Stats: None";
    }

    private void MaybeSubmitFft(DeviceState? st)
    {
        if (st == null || _fftBusy)
            return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _fftLastSubmitMs < FftUpdateIntervalMs)
            return;
        RingBuffer buf = Math.Max(0, TypeCombo.SelectedIndex) switch
        {
            0 => st.Acc,
            1 => st.Gyro,
            2 => st.Quat,
            _ => st.Euler,
        };
        List<float[]> snapshot;
        float rate;
        lock (st.BufMutex)
        {
            if (!buf.Allocated || buf.Length < 16 || buf.SampleRate <= 0)
                return;
            rate = buf.SampleRate;
            snapshot = new List<float[]>(buf.Channels);
            for (int ch = 0; ch < buf.Channels; ch++)
            {
                var row = new float[buf.Length];
                float[] src = buf.Samples[ch];
                for (int i = 0; i < buf.Length; i++)
                    row[i] = src[(buf.WriteIndex + i) % buf.Length];
                snapshot.Add(row);
            }
        }
        _fftLastSubmitMs = now;
        _fftBusy = true;
        int typeIndex = Math.Max(0, TypeCombo.SelectedIndex);
        string mac = st.Mac;
        Task.Run(() =>
        {
            SpectrumCompute.Compute(snapshot, rate, out float[] freqs, out List<float[]> mags);
            lock (_fftMutex)
            {
                _fftFreqs = freqs;
                _fftMags = mags;
                _fftTypeIndex = typeIndex;
                _fftMac = mac;
                _fftReady = true;
            }
            _fftBusy = false;
        });
    }

    private void PollFftResult()
    {
        lock (_fftMutex)
        {
            if (!_fftReady)
                return;
            _fftReady = false;
            DeviceState? st = CurrentState();
            if (st == null || _fftTypeIndex != Math.Max(0, TypeCombo.SelectedIndex) || _fftMac != st.Mac)
                return;
            Spectrum.SetResult(_fftFreqs, _fftMags);
        }
    }

    private void OnPlotTick()
    {
        if (_shuttingDown)
            return;
        Wave2d.Invalidate();
        foreach (Controls.WaveformControl w in _bioWaves)
            w.Invalidate();

        DeviceState? st = CurrentState();
        PollFftResult();
        MaybeSubmitFft(st);

        bool bioReady = st != null && ((st.GetBioKind() == DeviceState.BioKind.EMG && st.Emg.Allocated)
                                       || (st.GetBioKind() == DeviceState.BioKind.EEG && st.Eeg.Allocated)
                                       || (st.GetBioKind() == DeviceState.BioKind.PPG && st.Ppg.Allocated));
        if (bioReady && _bioWaves.Count > 0 && !_bioWaves[0].HasSource)
            RetargetBio(st);

        RefreshValueLabels();
        RefreshBioSideTexts();
        RefreshGestureLabel();

        // 3D cube follows the latest quaternion sample.
        if (st != null && st.Quat.Allocated && st.Quat.Channels >= 4)
        {
            lock (st.BufMutex)
            {
                Cube.SetQuaternion(st.Quat.Latest(0), st.Quat.Latest(1),
                                   st.Quat.Latest(2), st.Quat.Latest(3));
            }
        }
        else if (st == null)
        {
            Cube.ClearQuaternion();
        }

        ++_tickCount;
        if (_tickCount % (1000 / PlotUpdateIntervalMs) == 0)
        {
            if (st != null)
            {
                st.UpdateActualRates();
                UpdateLostPacketLabel();
                if (st.FlowStarted)
                {
                    StatusText.Text = st.BuildStatusText();
                    RateText.Text = st.BuildRateText();
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Multi-device sync start/stop
    // ------------------------------------------------------------------

    private List<SensorProfile> ReadyProfiles()
    {
        lock (_statesMutex)
            return _deviceStates.Values
                .Where(st => !st.IsReplay && st.Profile.IsReady && st.Profile.HasInited)
                .Select(st => st.Profile)
                .ToList();
    }

    private async void OnMultiSyncClicked(object sender, RoutedEventArgs e)
    {
        if (_streamingMacs.Count > 0)
            await MultiStop();
        else
            await MultiStart();
    }

    private async Task MultiStart()
    {
        List<SensorProfile> sensors = ReadyProfiles();
        if (sensors.Count == 0)
        {
            AppLog("User: multi start rejected (no connected device)", "W");
            StatusText.Text = "No connected device to sync-start";
            return;
        }
        AppLog($"User: multi start on {sensors.Count} device(s)");
        MultiSyncButton.IsEnabled = false;
        try
        {
            var transferring = sensors.Where(s => _streamingMacs.Contains(s.Device.Mac)).ToList();
            if (transferring.Count > 0)
            {
                Dictionary<string, bool> stopResults = await _ctrl.MultiStopDataNotificationAsync(transferring);
                string[] stopFailed = stopResults.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray();
                if (stopFailed.Length > 0)
                {
                    AppLog($"App: multi stop failed on: {string.Join(", ", stopFailed)}", "W");
                    StatusText.Text = $"Multi stop failed on: {string.Join(", ", stopFailed)}";
                    return;
                }
            }
            // Multi start params by model set
            var modelNames = new HashSet<string?>();
            foreach (SensorProfile s in sensors)
            {
                DeviceState? st = StateFor(s.Device.Mac);
                modelNames.Add(st != null && st.HasInfo ? st.Info.ModelName : null);
            }
            Dictionary<string, bool> results = modelNames.Count == 1 && !modelNames.Contains(null)
                ? await _ctrl.MultiStartDataNotificationAsync(sensors)
                : await _ctrl.MultiStartDataNotificationAsync(sensors, 60000, -1, 5);
            string[] failed = results.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray();
            if (failed.Length > 0)
            {
                AppLog($"App: multi start failed on: {string.Join(", ", failed)}", "W");
                StatusText.Text = $"Multi start failed on: {string.Join(", ", failed)}";
            }
            else
            {
                AppLog($"App: multi start OK: {results.Count} device(s) started");
                StatusText.Text = $"Multi start: {results.Count} device(s) started";
            }
        }
        finally
        {
            UpdateButtonStates();
        }
    }

    private async Task MultiStop()
    {
        List<SensorProfile> sensors = ReadyProfiles();
        if (sensors.Count == 0)
        {
            AppLog("User: multi stop rejected (no connected device)", "W");
            StatusText.Text = "No connected device to sync-stop";
            return;
        }
        AppLog($"User: multi stop on {sensors.Count} device(s)");
        MultiSyncButton.IsEnabled = false;
        try
        {
            Dictionary<string, bool> results = await _ctrl.MultiStopDataNotificationAsync(sensors);
            string[] failed = results.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray();
            if (failed.Length > 0)
            {
                AppLog($"App: multi stop failed on: {string.Join(", ", failed)}", "W");
                StatusText.Text = $"Multi stop failed on: {string.Join(", ", failed)}";
            }
            else
            {
                AppLog($"App: multi stop OK: {results.Count} device(s) stopped");
                StatusText.Text = $"Multi stop: {results.Count} device(s) stopped";
            }
        }
        finally
        {
            UpdateButtonStates();
        }
    }

    // ------------------------------------------------------------------
    // Bin replay
    // ------------------------------------------------------------------

    private void SetReplayModeUi(bool replaying)
    {
        if (replaying && _ctrl.IsScanning)
        {
            _ctrl.StopScan();
            _scanning = false;
        }
        ScanButton.IsEnabled = !replaying;
        StopScanButton.IsEnabled = false;
        DeviceList.IsEnabled = !replaying;
        DebugLogBox.IsEnabled = !replaying;
        BinDataBox.IsEnabled = !replaying;
        ReplayButton.IsEnabled = !replaying;
        MultiReplayButton.IsEnabled = !replaying;
        ReplayPauseButton.IsEnabled = replaying;
        ReplayPauseButton.Content = "Pause Replay";
        ReplayStopButton.IsEnabled = replaying;
        if (replaying)
        {
            ConnectButton.IsEnabled = false;
            DisconnectButton.IsEnabled = false;
            MultiSyncButton.IsEnabled = false;
        }
        else
        {
            UpdateButtonStates();
        }
    }

    private async Task<string?> PickBinFile(string title)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".bin");
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async Task<IReadOnlyList<Windows.Storage.StorageFile>> PickBinFiles(string title)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".bin");
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return await picker.PickMultipleFilesAsync();
    }

    private async void OnReplayClicked(object sender, RoutedEventArgs e)
    {
        lock (_statesMutex)
        {
            if (_deviceStates.Count > 0)
            {
                StatusText.Text = "Please disconnect all devices before replaying a bin file";
                return;
            }
        }
        if (_replayMacs.Count > 0)
            return;
        string? path = await PickBinFile("Select Bin File");
        if (path == null)
            return;
        StartReplay(path);
    }

    private async void OnMultiReplayClicked(object sender, RoutedEventArgs e)
    {
        lock (_statesMutex)
        {
            if (_deviceStates.Count > 0)
            {
                StatusText.Text = "Please disconnect all devices before replaying bin files";
                return;
            }
        }
        if (_replayMacs.Count > 0)
            return;
        IReadOnlyList<Windows.Storage.StorageFile> files = await PickBinFiles("Select Bin Files");
        if (files.Count == 0)
            return;
        var paths = files.Select(f => f.Path.Trim()).Where(p => p.Length > 0).ToList();
        if (paths.Count == 0)
            return;
        if (paths.Count > 1)
        {
            StartMultiReplay(paths);
            return;
        }
        StartReplay(paths[0]);
    }

    // Single-bin replay start
    private void StartReplay(string path)
    {
        AppLog($"User: replay bin file: {path}");
        BinFileInfo? info = _ctrl.GetBinFileInfo(path);
        if (info == null || !info.Valid || info.Mac.Length == 0)
        {
            AppLog($"App: invalid bin file (no config record): {path}", "W");
            StatusText.Text = "Invalid bin file: no config record found";
            return;
        }
        SensorProfile? profile = _ctrl.ReplayBinFile(path, info.Mac, true, ReplayDelegateTimeoutMs);
        if (profile == null)
        {
            StatusText.Text = "Replay failed to start";
            return;
        }
        HookProfileEvents(profile);

        _replayMacs.Add(info.Mac);
        _replayStopRequested = false;
        _replayPaused = false;

        var st = new DeviceState(profile)
        {
            IsReplay = true,
            FlowStarted = true,
            Name = info.DeviceName,
        };
        st.LiveFilterState.SetBand(FilterCombo.SelectedIndex);
        lock (_statesMutex)
            _deviceStates[info.Mac] = st;

        var row = new DeviceRow(info.Mac, $"[Replay] {st.Name}, Address: {info.Mac}");
        DeviceList.Items.Add(row);
        DeviceList.SelectedItem = row;
        _currentMac = info.Mac;

        if (info.DeviceInfo.EEGSampleRate > 0)
        {
            st.SampleRateCurrent = info.DeviceInfo.EEGSampleRate;
            SetSampleRateChecked(st.SampleRateCurrent);
        }

        RetargetWaveforms();
        RefreshInfoPanel();
        StatusText.Text = $"Replaying: {Path.GetFileName(path)} (duration {info.DurationSec:F1}s, realtime) ...";
        SetReplayModeUi(true);
    }

    // Multi-bin synchronized replay start
    private void StartMultiReplay(List<string> paths)
    {
        AppLog($"User: replay {paths.Count} bin files: {string.Join("; ", paths)}");
        var infos = new BinFileInfo[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            BinFileInfo? info = _ctrl.GetBinFileInfo(paths[i]);
            if (info == null || !info.Valid || info.Mac.Length == 0)
            {
                AppLog($"App: invalid bin file (no config record): {paths[i]}", "W");
                StatusText.Text = "Invalid bin file: no config record found";
                return;
            }
            infos[i] = info;
        }
        string[] macs = infos.Select(x => x.Mac).ToArray();
        if (macs.Distinct().Count() != macs.Length)
        {
            AppLog("App: replay aborted: duplicate device address among bin files", "W");
            StatusText.Text = "Replay aborted: duplicate device address among bin files";
            return;
        }
        SensorProfile?[] profiles = _ctrl.MultiReplayBinFile(paths.ToArray(), macs, true, ReplayDelegateTimeoutMs);
        int started = 0;
        for (int i = 0; i < profiles.Length; i++)
        {
            SensorProfile? profile = profiles[i];
            if (profile == null)
            {
                AppLog($"App: replay member failed to start: {paths[i]}", "W");
                continue;
            }
            HookProfileEvents(profile);
            string mac = macs[i];
            var st = new DeviceState(profile)
            {
                IsReplay = true,
                FlowStarted = true,
                Name = infos[i].DeviceName,
            };
            st.LiveFilterState.SetBand(FilterCombo.SelectedIndex);
            lock (_statesMutex)
                _deviceStates[mac] = st;
            var row = new DeviceRow(mac, $"[Replay] {st.Name}, Address: {mac}");
            DeviceList.Items.Add(row);
            if (started == 0)
            {
                DeviceList.SelectedItem = row;
                _currentMac = mac;
            }
            if (infos[i].DeviceInfo.EEGSampleRate > 0)
            {
                st.SampleRateCurrent = infos[i].DeviceInfo.EEGSampleRate;
                if (mac == _currentMac)
                    SetSampleRateChecked(st.SampleRateCurrent);
            }
            _replayMacs.Add(mac);
            started++;
        }
        if (started == 0)
        {
            StatusText.Text = "Replay failed to start";
            return;
        }
        _replayStopRequested = false;
        _replayPaused = false;
        RetargetWaveforms();
        RefreshInfoPanel();
        StatusText.Text = $"Replaying {started} bin files (realtime) ...";
        SetReplayModeUi(true);
    }

    private void OnReplayPauseResume(object sender, RoutedEventArgs e)
    {
        if (_replayMacs.Count == 0)
            return;
        string action = _replayPaused ? "resume" : "pause";
        bool allOk = true;
        foreach (string mac in _replayMacs)
        {
            string result = _replayPaused
                ? _ctrl.ResumeBinReplay(mac)
                : _ctrl.PauseBinReplay(mac);
            AppLog($"User: {action} replay -> {result}", result == "OK" ? "I" : "W", StateFor(mac));
            if (result != "OK")
                allOk = false;
        }
        if (!allOk)
        {
            StatusText.Text = "Replay pause/resume failed";
            return;
        }
        _replayPaused = !_replayPaused;
        ReplayPauseButton.Content = _replayPaused ? "Resume Replay" : "Pause Replay";
        StatusText.Text = _replayPaused ? "Replay paused" : "Replaying ...";
    }

    private void OnReplayStop(object sender, RoutedEventArgs e)
    {
        if (_replayMacs.Count == 0)
            return;
        _replayStopRequested = true;
        ReplayStopButton.IsEnabled = false;
        ReplayPauseButton.IsEnabled = false;
        bool anyOk = false;
        foreach (string mac in _replayMacs.ToList())
        {
            string result = _ctrl.StopBinReplay(mac);
            AppLog($"User: stop replay -> {result}", result == "OK" ? "I" : "W", StateFor(mac));
            if (result == "OK")
                anyOk = true;
        }
        if (!anyOk)
        {
            StatusText.Text = "Stop replay failed";
            _replayStopRequested = false;
            ReplayStopButton.IsEnabled = true;
            ReplayPauseButton.IsEnabled = true;
            return;
        }
        StatusText.Text = "Stopping replay ...";
    }

    private void OnReplayDone(string mac, string message)
    {
        if (!_replayMacs.Contains(mac))
            return;
        AppLog($"App: replay done: {message}", "I", StateFor(mac));
        lock (_statesMutex)
            _deviceStates.Remove(mac);
        _streamingMacs.Remove(mac);
        DeviceRow? row = DeviceList.Items.OfType<DeviceRow>().FirstOrDefault(r => r.Mac == mac);
        if (row != null)
            DeviceList.Items.Remove(row);
        _replayMacs.Remove(mac);
        if (_currentMac == mac)
        {
            _currentMac = _replayMacs.Count > 0 ? _replayMacs[0] : string.Empty;
            DeviceRow? next = DeviceList.Items.OfType<DeviceRow>().FirstOrDefault(r => r.Mac == _currentMac);
            if (next != null)
                DeviceList.SelectedItem = next;
        }
        RetargetWaveforms();
        RefreshInfoPanel();
        StatusText.Text = message;
        if (_replayMacs.Count > 0)
            return;
        _replayStopRequested = false;
        _replayPaused = false;
        SetReplayModeUi(false);
    }

    // ------------------------------------------------------------------
    // Bin analyze
    // ------------------------------------------------------------------

    private async void OnAnalyzeClicked(object sender, RoutedEventArgs e)
    {
        if (_analyzeRunning)
            return;
        string? path = await PickBinFile("Select Bin File to Analyze");
        if (path == null)
            return;
        AppLog($"User: analyze bin file: {path}");
        _analyzeRunning = true;
        AnalyzeButton.IsEnabled = false;
        StatusText.Text = $"Analyzing: {Path.GetFileName(path)} ...";

        string csv = path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
            ? path[..^4] + ".csv"
            : path + ".csv";
        string result = await Task.Run(() => _ctrl.ParseBinToCsv(path, csv));
        _analyzeRunning = false;
        AnalyzeButton.IsEnabled = true;
        if (result.StartsWith("Error"))
        {
            AppLog($"App: analyze failed: {result}", "E");
            StatusText.Text = "Analyze failed: " + result;
            return;
        }
        AppLog($"App: CSV saved: {result}");
        StatusText.Text = "CSV saved: " + result;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(result) { UseShellExecute = true });
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // Shutdown
    // ------------------------------------------------------------------

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _shuttingDown = true;
        AppLog("App: demo window closing");
        _plotTimer.Stop();
        _dataWorkerStop = true;
        _dataQueueEvent.Set();
        _dataWorker.Join();
    }
}
