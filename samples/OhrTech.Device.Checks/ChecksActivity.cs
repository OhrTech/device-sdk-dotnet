using System.Text;
using System.Text.Json.Nodes;
using Android.App;
using Android.Runtime;
using Android.Util;
using Android.Widget;
using OhrTech.Device;

namespace OhrTech.Device.Checks;

/// <summary>Sets the push handlers when the process starts: a message can start the app with no UI.</summary>
[Application]
public sealed class ChecksApp(IntPtr handle, JniHandleOwnership transfer) : Application(handle, transfer)
{
    public override void OnCreate()
    {
        base.OnCreate();
        OhrPush.SetHandlers(
            onMessage: m =>
            {
                Log.Info("OhrSdkCheck", $"PUSH RECEIVED {m.Id} ({m.PushId}): {Encoding.UTF8.GetString(m.Data)}");
                return Task.CompletedTask;
            },
            onTokenChanged: t =>
            {
                Log.Info("OhrSdkCheck", $"PUSH TOKEN now {t?.Token}");
                return Task.CompletedTask;
            },
            onDropped: (id, reason) =>
            {
                Log.Info("OhrSdkCheck", $"PUSH DROPPED {id}: {reason}");
                return Task.CompletedTask;
            });
    }
}

/// <summary>The sample screen (kiosk buttons and the Agent's Wi-Fi screen), or a device check with --es check &lt;name&gt;.</summary>
[Activity(Name = "net.ohrtech.device.dotnet.checks.ChecksActivity", MainLauncher = true, Exported = true)]
public sealed class ChecksActivity : Activity
{
    private const string Tag = "OhrSdkCheck";
    private int passed, failed;
    private TextView status = null!;

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var check = Intent?.GetStringExtra("check");
        if (check is null)
        {
            Sample();
            return;
        }
        if (check == "anr-touch")
        {
            AnrTouch();
            return;
        }
        status = new TextView(this) { Text = "running…" };
        status.SetPadding(48, 96, 48, 48);
        SetContentView(status);
        _ = Run(check);
    }

    private void Sample()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetFitsSystemWindows(true);
        status = new TextView(this);
        status.SetPadding(32, 32, 32, 16);
        var buttons = new LinearLayout(this);
        var on = new Button(this) { Text = "Kiosk on" };
        on.Click += async (_, _) => Show(await Try("kiosk on", () => OhrKiosk.StartAsync(this)));
        var off = new Button(this) { Text = "Kiosk off" };
        off.Click += async (_, _) => Show(await Try("kiosk off", OhrKiosk.StopAsync));
        buttons.AddView(on);
        buttons.AddView(off);
        var wifi = new OhrWifiView(this);
        wifi.Connected += (_, e) => Show($"joined {e.Ssid}");
        wifi.Error += (_, e) => Show($"Wi-Fi screen: {e.Error.Message}");
        root.AddView(status);
        root.AddView(buttons);
        root.AddView(wifi, new LinearLayout.LayoutParams(Android.Views.ViewGroup.LayoutParams.MatchParent, 0, 1f));
        SetContentView(root);
        _ = Task.Run(async () =>
        {
            try
            {
                var info = await OhrDevice.GetInfoAsync();
                var access = await OhrAccess.GetInfoAsync();
                RunOnUiThread(() => Show($"{info.DeviceId} · {info.Fleet?.Name} · Agent {info.AgentVersion}\nmay use: {string.Join(", ", access.Areas.Order())}"));
            }
            catch (OhrException e)
            {
                RunOnUiThread(() => Show($"Device SDK: {e.Message}"));
            }
        });
    }

    /// <summary>
    /// For the per-app ANR time (apps.response_timeout): a touch blocks the main thread for --ei block seconds (30), so a
    /// second touch waits in input until Android's ANR time for the app. No kiosk mode, so the Agent's watchdog stays out of it.
    /// </summary>
    private void AnrTouch()
    {
        var seconds = Intent?.GetIntExtra("block", 30) ?? 30;
        var view = new TextView(this) { Text = $"Touch: the main thread then blocks {seconds} s" };
        view.SetPadding(48, 96, 48, 48);
        view.Touch += (_, e) =>
        {
            if (e.Event?.Action != Android.Views.MotionEventActions.Down) return;
            Log.Info(Tag, $"CHECK BLOCKING {seconds} s");
            Thread.Sleep(seconds * 1000);
            Log.Info(Tag, "CHECK UNBLOCKED");
        };
        SetContentView(view);
        Log.Info(Tag, "CHECK READY anr-touch");
    }

    private static async Task<string> Try(string label, Func<Task> action)
    {
        try
        {
            await action();
            return label;
        }
        catch (OhrException e)
        {
            return $"{label}: {e.GetType().Name}: {e.Message}";
        }
    }

    private void Show(string s)
    {
        Log.Info(Tag, s);
        status.Text = s;
    }

    private string? Arg(string name) => Intent?.GetStringExtra(name);

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

    private async Task Run(string check)
    {
        switch (check)
        {
            case "basics":
                await Step("connect", async () => { await OhrDeviceSdk.ConnectAsync(); return OhrDeviceSdk.Connection.ToString(); });
                await Step("access", async () => (await OhrAccess.GetInfoAsync()).App);
                await Step("device", async () => (await OhrDevice.GetInfoAsync()).DeviceId);
                await Step("updates", async () => $"{(await OhrUpdates.GetAvailableAsync())?.VersionCode}");
                await Step("wifi state", async () => { var s = await OhrWifi.GetStateAsync(); return $"{s.State} {s.Ssid}"; });
                await Step("wifi networks", async () => $"{(await OhrWifi.GetNetworksAsync()).Count} networks");
                await Step("network", async () => (await OhrNetwork.GetStateAsync()).Active?.Transport);
                await Step("kiosk state", async () => (await OhrKiosk.GetStateAsync()).Mode);
                await Step("battery", async () => $"{(await OhrPower.GetBatteryAsync()).Level}%");
                await Step("settings keys", async () => $"{(await OhrSettings.GetKeysAsync()).Count} keys");
                await Step("timeout 0 refused", async () =>
                {
                    try { await OhrSettings.SetAsync("display.timeout", JsonValue.Create(0)); }
                    catch (OhrInvalidConfigException e) { return e.Reason; }
                    throw new InvalidOperationException("accepted 0");
                });
                await Step("timeout 45 accepted", async () =>
                {
                    var before = (await OhrSettings.GetAsync("display.timeout")).Value;
                    await OhrSettings.SetAsync("display.timeout", JsonValue.Create(45));
                    var now = (await OhrSettings.GetAsync("display.timeout")).Value;
                    await OhrSettings.SetAsync("display.timeout", before?.DeepClone());
                    Expect(now?.ToJsonString() == "45", $"read back {now?.ToJsonString()}");
                    return $"45 (back to {before?.ToJsonString()})";
                });
                await Step("config", () => Task.FromResult<string?>(OhrConfig.GetValues().ToJsonString()));
                await Step("usb", async () => $"{(await OhrUsb.GetDevicesAsync()).Count} attached");
                await Step("diag events", async () =>
                {
                    await OhrDiagnostics.EventAsync("dotnet_checks", new JsonObject { ["run"] = 1 });
                    await OhrDiagnostics.LogAsync(LogPriority.Info, "Checks", "a line from the .NET checks");
                    return "1 event, 1 line";
                });
                await Step("bad event refused", async () =>
                {
                    try { await OhrDiagnostics.EventAsync("Bad Name"); }
                    catch (OhrInvalidConfigException e) { return e.Field; }
                    throw new InvalidOperationException("accepted a bad name");
                });
                break;
            case "ui-event":
                // Fresh process: subscribe on the UI thread before anything connects, then connect: the change is raised there.
                await Step("event on the subscriber's thread", async () =>
                {
                    var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var ui = Android.OS.Looper.MainLooper!.Thread;
                    EventHandler<OhrConnectionChangedEventArgs> h = (_, e) =>
                        got.TrySetResult($"{e.Connection} on {(Java.Lang.Thread.CurrentThread()!.Equals(ui) ? "the UI thread" : "another thread")}");
                    var subscribed = new TaskCompletionSource();
                    RunOnUiThread(() => { OhrDeviceSdk.ConnectionChanged += h; subscribed.SetResult(); });
                    await subscribed.Task;
                    await Task.Delay(500);
                    await OhrDeviceSdk.ConnectAsync();
                    var r = await Task.WhenAny(got.Task, Task.Delay(5000)) == got.Task ? got.Task.Result : throw new TimeoutException("no event");
                    RunOnUiThread(() => OhrDeviceSdk.ConnectionChanged -= h);
                    Expect(r.EndsWith("the UI thread"), r);
                    return r;
                });
                break;
            case "security":
                await Step("attest ec", async () => { var a = await OhrAttestation.AttestAsync(Encoding.UTF8.GetBytes("dotnet-checks")); return $"{a.Ec.Count} certificates, {a.Json["suite"]}"; });
                await Step("token ec", async () => $"{(await OhrAttestation.GetTokenAsync("https://example.test")).Split('.').Length} parts");
                await Step("attest ec+ml-dsa-65", async () =>
                {
                    var a = await OhrAttestation.AttestAsync(Encoding.UTF8.GetBytes("dotnet-checks"), OhrAttestationSuite.EcMlDsa65);
                    return $"ec {a.Ec.Count}, ml-dsa {a.MlDsa65!.Count}";
                });
                await Step("kem x-wing", async () =>
                {
                    var pub = await OhrKeys.GenerateAsync("d.xwing", OhrKeyAlgorithm.XWing);
                    var e = await OhrKeys.EncapsulateAsync(OhrKeyAlgorithm.XWing, pub);
                    var s = await OhrKeys.DecapsulateAsync("d.xwing", e.Ciphertext);
                    Expect(s.AsSpan().SequenceEqual(e.SharedSecret), "secrets differ");
                    return $"{pub.Length} B key, {e.Ciphertext.Length} B ciphertext";
                });
                await Step("key attest", async () => (await OhrKeys.AttestAsync("d.xwing", "c"u8.ToArray())).Owner.Split(':')[^1]);
                await Step("key delete", async () => { await OhrKeys.DeleteAsync("d.xwing"); Expect(await OhrKeys.GetPublicKeyAsync("d.xwing") is null, "still there"); return "ok"; });
                await Step("certificates", async () =>
                {
                    if (!await OhrCertificates.CanRequestAsync()) return "no issuing CA for this app";
                    var c = await OhrCertificates.RequestAsync("dotnet-checks");
                    return $"{c.Paths.Count} paths, {(await OhrCertificates.ListAsync()).Count} listed";
                });
                if (Arg("tlsUrl") is { } url)
                    await Step("mutual TLS", async () =>
                    {
                        using var http = new HttpClient(OhrCertificates.CreateHttpHandler(Arg("tlsAlias") ?? "dotnet-checks"));
                        var r = await http.GetAsync(url);
                        return $"HTTP {(int)r.StatusCode}, {(await r.Content.ReadAsByteArrayAsync()).Length} bytes";
                    });
                break;
            case "push":
                await Step("register", async () =>
                {
                    var t = await OhrPush.RegisterAsync([OhrSenderKey.FromBase64(Arg("senderKey")!)]);
                    Log.Info(Tag, $"CHECK TOKEN {{\"token\":\"{t.Token}\",\"suite\":\"x25519\",\"publicKey\":\"{t.PublicKeyBase64}\"}}");
                    return t.Token;
                });
                await Step("token", async () => (await OhrPush.GetTokenAsync())?.Token);
                break;
            case "unregister":
                await Step("unregister", async () => { await OhrPush.UnregisterAsync(); Expect(await OhrPush.GetTokenAsync() is null, "token left"); return "no token"; });
                break;
            case "reconnect":
                await Reconnect();
                break;
            case "heartbeat":
                await Step("kiosk start", async () => { await OhrKiosk.StartAsync(this); return null; });
                await Step("heartbeat, then hang", async () =>
                {
                    // Beats from the main thread every 750 ms; then the main thread stops for 30 s: the Agent restarts the app.
                    RunOnUiThread(() => OhrKiosk.Heartbeat(TimeSpan.FromSeconds(3)));
                    await Task.Delay(2000);
                    Log.Info(Tag, "CHECK HANGING");
                    RunOnUiThread(() => Thread.Sleep(30_000));
                    await Task.Delay(30_000);
                    throw new InvalidOperationException("still alive 30 s later");
                });
                break;
        }
        var summary = $"CHECK DONE {check}: {passed} passed, {failed} failed";
        Log.Info(Tag, summary);
        RunOnUiThread(() => status.Text = summary);
    }

    /// <summary>Events and streams outlive the Agent: the harness stops it after "CHECK READY reconnect".</summary>
    private async Task Reconnect()
    {
        var states = new List<string>();
        var heard = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var back = false;
        EventHandler<OhrConnectionChangedEventArgs> onConnection = (_, e) =>
        {
            lock (states) states.Add(e.Connection.ToString());
            if (e.Connection == OhrConnection.Connected && states.Contains("Reconnecting")) back = true;
        };
        EventHandler<OhrBatteryChangedEventArgs> onBattery = (_, _) => { if (back) heard.AddOrUpdate("battery", 1, (_, n) => n + 1); };
        EventHandler<OhrKioskModeChangedEventArgs> onKiosk = (_, _) => { if (back) heard.AddOrUpdate("kiosk", 1, (_, n) => n + 1); };
        OhrDeviceSdk.ConnectionChanged += onConnection;
        OhrPower.BatteryChanged += onBattery;
        OhrKiosk.ModeChanged += onKiosk;
        using var cts = new CancellationTokenSource();
        // A stream as well as the events.
        var stream = Task.Run(async () =>
        {
            await foreach (var _ in OhrNetwork.Changes(cts.Token))
                if (back) heard.AddOrUpdate("network (stream)", 1, (_, n) => n + 1);
        });
        await Task.Delay(1500);
        Log.Info(Tag, "CHECK READY reconnect");
        var until = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < until && !(back && heard.Count == 3)) await Task.Delay(250);
        await Step("agent stopped and came back", () => { Expect(back, string.Join(" > ", states)); return Task.FromResult<string?>(string.Join(" > ", states)); });
        await Step("events and stream heard again", () => { Expect(heard.Count == 3, string.Join(", ", heard)); return Task.FromResult<string?>(string.Join(", ", heard)); });
        await Step("calls after", async () => (await OhrDevice.GetInfoAsync()).DeviceId);
        OhrDeviceSdk.ConnectionChanged -= onConnection;
        OhrPower.BatteryChanged -= onBattery;
        OhrKiosk.ModeChanged -= onKiosk;
        cts.Cancel();
        try { await stream; } catch (OperationCanceledException) { }
    }
}
