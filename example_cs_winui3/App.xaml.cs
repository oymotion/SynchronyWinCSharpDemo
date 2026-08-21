using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace SensorSdk.ExampleWinUI3;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        try
        {
            LoadAsanRuntimeIfPresent();
            InitializeComponent();
            StartupLog("ctor ok");
        }
        catch (System.Exception ex)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "winui3_startup.log"), ex.ToString());
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupLog("OnLaunched");
        _window = new MainWindow();
        StartupLog("window created");
        _window.Closed += (_, _) => SensorController.Instance.Dispose();
        _window.Activate();
        StartupLog("window activated");
    }

    private static void StartupLog(string msg)
    {
        System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "winui3_startup.log"), msg + "\n");
    }

    // Load the ASan runtime first when an ASan-built sensor.dll is used
    private static void LoadAsanRuntimeIfPresent()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "clang_rt.asan_dynamic-x86_64.dll");
        if (System.IO.File.Exists(path))
            LoadLibrary(path);
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern System.IntPtr LoadLibrary(string lpFileName);
}
