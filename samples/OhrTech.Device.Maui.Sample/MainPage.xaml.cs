using Android.Util;
using OhrTech.Device;

namespace OhrTech.Device.Maui.Sample;

/// <summary>The sample screen (kiosk buttons and the Agent's Wi-Fi screen), or a device check with --es check &lt;maui|push&gt;.</summary>
public partial class MainPage : ContentPage
{
    private const string Tag = MauiProgram.Tag;
    private readonly IOhrDevice device;
    private readonly IOhrKiosk kiosk;
    private bool started;
    private int passed, failed;

    // The areas come from the container UseOhrTechDevice() fills.
    public MainPage(IOhrDevice device, IOhrKiosk kiosk)
    {
        this.device = device;
        this.kiosk = kiosk;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (started) return;
        started = true;
        var check = Platform.CurrentActivity?.Intent?.GetStringExtra("check");
        if (check is null)
        {
            Show(await Try("device", async () => $"device {(await device.GetInfoAsync()).DeviceId}"));
            return;
        }
        switch (check)
        {
            case "maui": await Maui(); break;
            case "push": await Push(); break;
            default: Log.Error(Tag, $"CHECK FAIL unknown check {check}"); failed++; break;
        }
        Log.Info(Tag, $"CHECK DONE {check}: {passed} passed, {failed} failed");
        Show($"{check}: {passed} passed, {failed} failed");
    }

    private async Task Maui()
    {
        await Step("page built by the container", async () => (await device.GetInfoAsync()).DeviceId);
        await Step("areas from the container are the Current ones", () =>
        {
            var services = Handler?.MauiContext?.Services ?? throw new InvalidOperationException("no MauiContext");
            var areas = new (Type Type, object Current)[]
            {
                (typeof(IOhrAccess), OhrAccess.Current), (typeof(IOhrDevice), OhrDevice.Current), (typeof(IOhrUpdates), OhrUpdates.Current),
                (typeof(IOhrWifi), OhrWifi.Current), (typeof(IOhrNetwork), OhrNetwork.Current), (typeof(IOhrKiosk), OhrKiosk.Current),
                (typeof(IOhrPower), OhrPower.Current), (typeof(IOhrSettings), OhrSettings.Current), (typeof(IOhrAttestation), OhrAttestation.Current),
            };
            foreach (var (type, current) in areas)
                Expect(ReferenceEquals(services.GetService(type), current), $"{type.Name} from the container isn't {type.Name[1..]}.Current");
            return Task.FromResult<string?>($"{areas.Length} areas");
        });

        string? wifiError = null;
        Wifi.Error += (_, e) => wifiError = e.Error.Message;
        await Step("Wi-Fi control has the library's view, laid out", async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                if (Wifi.Handler?.PlatformView is OhrTech.Device.OhrWifiView v && v.IsAttachedToWindow && v.Width > 0 && v.Height > 0)
                    return $"{v.Width}x{v.Height}";
                await Task.Delay(100);
            }
            throw new TimeoutException($"handler {Wifi.Handler?.GetType().Name ?? "none"}");
        });
        await Step("Wi-Fi screen open, no Error in 8 s", async () =>
        {
            await Task.Delay(8000);
            Expect(wifiError is null, $"Error: {wifiError}");
            Log.Info(Tag, "CHECK READY screenshot");
            await Task.Delay(3000);
            return "no error";
        });
        await Step("Theme, a bindable property, reaches the view", () =>
        {
            var theme = new OhrWifiTheme(Dark: true, Accent: Android.Graphics.Color.Rgb(0, 120, 90), CornerRadiusDp: 4);
            Wifi.Theme = theme;
            var v = (OhrTech.Device.OhrWifiView)Wifi.Handler!.PlatformView!;
            Expect(v.Theme == theme, $"the view's theme is {v.Theme}");
            return Task.FromResult<string?>(v.Theme.ToString());
        });

        await Step("kiosk from the MAUI activity; ModeChanged on the main thread", async () =>
        {
            var changed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<OhrKioskModeChangedEventArgs> h = (_, e) =>
            {
                if (e.Mode != "none") changed.TrySetResult($"{e.Mode}, main thread {MainThread.IsMainThread}");
            };
            kiosk.ModeChanged += h;
            try
            {
                await kiosk.StartAsync(Platform.CurrentActivity!);
                var r = await Task.WhenAny(changed.Task, Task.Delay(5000)) == changed.Task ? changed.Task.Result : throw new TimeoutException("no ModeChanged");
                Expect(r.EndsWith("True"), r);
                var state = await kiosk.GetStateAsync();
                Expect(state.Pinned, $"not pinned: {state}");
                return r;
            }
            finally
            {
                kiosk.ModeChanged -= h;
                await kiosk.StopAsync();
            }
        });
        await Step("kiosk stopped", async () => (await kiosk.GetStateAsync()).Mode);
    }

    private async Task Push()
    {
        var senderKey = Platform.CurrentActivity?.Intent?.GetStringExtra("senderKey") ?? throw new InvalidOperationException("--es senderKey");
        await Step("register", async () =>
        {
            var t = await OhrPush.RegisterAsync([OhrSenderKey.FromBase64(senderKey)]);
            Log.Info(Tag, $"CHECK TOKEN {{\"token\":\"{t.Token}\",\"suite\":\"x25519\",\"publicKey\":\"{t.PublicKeyBase64}\"}}");
            return t.Token;
        });
    }

    private async void OnKioskOn(object? sender, EventArgs e) => Show(await Try("kiosk on", () => kiosk.StartAsync(Platform.CurrentActivity!)));

    private async void OnKioskOff(object? sender, EventArgs e) => Show(await Try("kiosk off", kiosk.StopAsync));

    private void OnWifiConnected(object? sender, OhrWifiConnectedEventArgs e) => Show($"joined {e.Ssid}");

    private void OnWifiError(object? sender, OhrWifiErrorEventArgs e) => Show($"Wi-Fi screen: {e.Error.Message}");

    private void Show(string s)
    {
        Log.Info(Tag, s);
        Status.Text = s;
    }

    private static async Task<string> Try(string label, Func<Task<string>> action)
    {
        try { return await action(); }
        catch (OhrException e) { return $"{label}: {e.GetType().Name}: {e.Message}"; }
    }

    private static Task<string> Try(string label, Func<Task> action) => Try(label, async () => { await action(); return label; });

    private async Task Step(string name, Func<Task<string?>> body)
    {
        try
        {
            var r = await body();
            passed++;
            Log.Info(Tag, $"CHECK PASS {name} {r}");
        }
        catch (Exception e)
        {
            failed++;
            Log.Error(Tag, $"CHECK FAIL {name}: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Expect(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException(what);
    }
}
