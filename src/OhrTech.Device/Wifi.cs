using System.Text.Json.Nodes;
using Android.Content;
using Android.Runtime;
using Android.Util;
using Android.Widget;
using Bound = Net.Ohrtech.Device;

namespace OhrTech.Device;

/// <param name="State">"on", "off", "turningOn" or "turningOff".</param>
/// <param name="Connected">The current network (as <see cref="OhrWifiNetwork.Json"/>), or null.</param>
public sealed record OhrWifiState(bool Enabled, string State, JsonObject? Connected)
{
    /// <summary>The current network's SSID, or null.</summary>
    public string? Ssid => Connected?["ssid"]?.GetValue<string>();

    internal static OhrWifiState Of(Bound.OhrWifi.State s) => new(s.Enabled, s.GetState(), s.Connected is { } c ? Native.Json(c) : null);
}

/// <summary>One network in range. <see cref="Key"/> joins it with <see cref="OhrWifi.ConnectAsync(string)"/>.</summary>
/// <param name="Level">Signal level, 0-4, or -1.</param>
/// <param name="Fleet">One of the fleet's networks: read-only to apps.</param>
/// <param name="Json">Everything the Agent says about it (also "internet", "metered").</param>
public sealed record OhrWifiNetwork(string Key, string Title, string? Ssid, IReadOnlyList<string> Security, int Level, bool Saved, bool Connected,
    bool Fleet, JsonObject Json)
{
    internal static OhrWifiNetwork Of(Bound.OhrWifi.Network n) =>
        new(n.Key, n.Title, n.Ssid, [.. n.Security], n.Level, n.Saved, n.Connected, n.Fleet, Native.Json(n.Json));

    internal static IReadOnlyList<OhrWifiNetwork> List(Java.Lang.Object? v) =>
        v is null || JavaList.FromJniHandle(v.Handle, JniHandleOwnership.DoNotTransfer) is not { } list
            ? [] : [.. list.Cast<Java.Lang.Object>().Select(x => Of(x.JavaCast<Bound.OhrWifi.Network>()!))];
}

public sealed class OhrWifiStateChangedEventArgs(OhrWifiState state) : EventArgs
{
    public OhrWifiState State { get; } = state;
}

public sealed class OhrWifiNetworksChangedEventArgs(IReadOnlyList<OhrWifiNetwork> networks) : EventArgs
{
    public IReadOnlyList<OhrWifiNetwork> Networks { get; } = networks;
}

public interface IOhrWifi
{
    Task<OhrWifiState> GetStateAsync();
    Task<IReadOnlyList<OhrWifiNetwork>> GetNetworksAsync();
    Task ScanAsync();
    Task<IReadOnlyList<JsonObject>> GetSavedAsync();
    Task<string> SaveAsync(JsonObject config);
    Task ConnectAsync(string keyOrIdentity);
    Task<string> ConnectAsync(JsonObject config);
    Task DisconnectAsync();
    Task ForgetAsync(string identity);
    Task SetEnabledAsync(bool on);
    Task<string> ConnectQrAsync(string text);
    event EventHandler<OhrWifiStateChangedEventArgs>? StateChanged;
    event EventHandler<OhrWifiNetworksChangedEventArgs>? NetworksChanged;
    IAsyncEnumerable<OhrWifiState> StateChanges(CancellationToken cancellationToken = default);
}

/// <summary>
/// Wi-Fi: state and the networks around, read always; changes with the fleet's "wifi" grant. The fleet's own networks
/// are read-only (<see cref="OhrFleetLockedException"/> "wifi.networks"). For a ready-made screen, use <see cref="OhrWifiView"/>.
/// Network configs are the OhrTech Wi-Fi model as JSON; <see cref="WifiConfig"/> builds the common ones.
/// </summary>
public static class OhrWifi
{
    public static IOhrWifi Current { get; } = new Impl();

    public static Task<OhrWifiState> GetStateAsync() => Native.Run(() => OhrWifiState.Of(Bound.OhrWifi.InvokeState()));
    public static Task<IReadOnlyList<OhrWifiNetwork>> GetNetworksAsync() =>
        Native.Run<IReadOnlyList<OhrWifiNetwork>>(() => [.. Bound.OhrWifi.Networks().Select(OhrWifiNetwork.Of)]);
    public static Task ScanAsync() => Native.Run(Bound.OhrWifi.Scan);

    /// <summary>Saved networks: {"ssid", "security", "fleet"} (or "passpoint").</summary>
    public static Task<IReadOnlyList<JsonObject>> GetSavedAsync() => Native.Run<IReadOnlyList<JsonObject>>(() => [.. Bound.OhrWifi.Saved().Select(Native.Json)]);

    /// <summary>Saves <paramref name="config"/>; returns its identity ("ssid:…", "passpoint:…"). <see cref="OhrInvalidConfigException"/> names the field.</summary>
    public static Task<string> SaveAsync(JsonObject config) => Native.Run(() => Bound.OhrWifi.Save(Native.JavaObject(config)));

    /// <summary>Joins a network by its <see cref="OhrWifiNetwork.Key"/> or a saved identity.</summary>
    public static Task ConnectAsync(string keyOrIdentity) => Native.Run(() => Bound.OhrWifi.Connect(keyOrIdentity));

    /// <summary>Saves, then joins. Returns the identity.</summary>
    public static Task<string> ConnectAsync(JsonObject config) => Native.Run(() => Bound.OhrWifi.Connect(Native.JavaObject(config)));
    public static Task DisconnectAsync() => Native.Run(Bound.OhrWifi.Disconnect);
    public static Task ForgetAsync(string identity) => Native.Run(() => Bound.OhrWifi.Forget(identity));

    /// <summary><see cref="OhrFleetLockedException"/> when the fleet locks wifi.enabled.</summary>
    public static Task SetEnabledAsync(bool on) => Native.Run(() => Bound.OhrWifi.SetEnabled(on));

    /// <summary>A Wi-Fi QR code the app scanned ("WIFI:…"): saves and joins it. Returns the identity.</summary>
    public static Task<string> ConnectQrAsync(string text) => Native.Run(() => Bound.OhrWifi.ConnectQr(text));

    public static event EventHandler<OhrWifiStateChangedEventArgs>? StateChanged
    {
        add => stateChanged.Add(value);
        remove => stateChanged.Remove(value);
    }

    public static event EventHandler<OhrWifiNetworksChangedEventArgs>? NetworksChanged
    {
        add => networksChanged.Add(value);
        remove => networksChanged.Remove(value);
    }

    private static readonly WatchedEvent<OhrWifiStateChangedEventArgs> stateChanged =
        new(nameof(StateChanged), Bound.OhrWifi.ListenState, v => new OhrWifiStateChangedEventArgs(OhrWifiState.Of((Bound.OhrWifi.State)v!)));

    private static readonly WatchedEvent<OhrWifiNetworksChangedEventArgs> networksChanged =
        new(nameof(NetworksChanged), Bound.OhrWifi.ListenNetworks, v => new OhrWifiNetworksChangedEventArgs(OhrWifiNetwork.List(v)));

    public static IAsyncEnumerable<OhrWifiState> StateChanges(CancellationToken cancellationToken = default) =>
        Streams.Watch(Bound.OhrWifi.ListenState, v => OhrWifiState.Of((Bound.OhrWifi.State)v!), cancellationToken);

    public static IAsyncEnumerable<IReadOnlyList<OhrWifiNetwork>> NetworkChanges(CancellationToken cancellationToken = default) =>
        Streams.Watch(Bound.OhrWifi.ListenNetworks, OhrWifiNetwork.List, cancellationToken);

    private sealed class Impl : IOhrWifi
    {
        public Task<OhrWifiState> GetStateAsync() => OhrWifi.GetStateAsync();
        public Task<IReadOnlyList<OhrWifiNetwork>> GetNetworksAsync() => OhrWifi.GetNetworksAsync();
        public Task ScanAsync() => OhrWifi.ScanAsync();
        public Task<IReadOnlyList<JsonObject>> GetSavedAsync() => OhrWifi.GetSavedAsync();
        public Task<string> SaveAsync(JsonObject config) => OhrWifi.SaveAsync(config);
        public Task ConnectAsync(string keyOrIdentity) => OhrWifi.ConnectAsync(keyOrIdentity);
        public Task<string> ConnectAsync(JsonObject config) => OhrWifi.ConnectAsync(config);
        public Task DisconnectAsync() => OhrWifi.DisconnectAsync();
        public Task ForgetAsync(string identity) => OhrWifi.ForgetAsync(identity);
        public Task SetEnabledAsync(bool on) => OhrWifi.SetEnabledAsync(on);
        public Task<string> ConnectQrAsync(string text) => OhrWifi.ConnectQrAsync(text);
        public event EventHandler<OhrWifiStateChangedEventArgs>? StateChanged { add => OhrWifi.StateChanged += value; remove => OhrWifi.StateChanged -= value; }
        public event EventHandler<OhrWifiNetworksChangedEventArgs>? NetworksChanged { add => OhrWifi.NetworksChanged += value; remove => OhrWifi.NetworksChanged -= value; }
        public IAsyncEnumerable<OhrWifiState> StateChanges(CancellationToken cancellationToken = default) => OhrWifi.StateChanges(cancellationToken);
    }
}

/// <summary>The common network configs of the OhrTech Wi-Fi model. For enterprise and Passpoint, build the JSON.</summary>
public static class WifiConfig
{
    public static JsonObject Open(string ssid, bool hidden = false) => Base(ssid, "open", hidden);

    /// <summary>Enhanced Open.</summary>
    public static JsonObject Owe(string ssid, bool hidden = false) => Base(ssid, "owe", hidden);

    /// <summary>WPA/WPA2 personal: 8-63 characters, or 64 hex digits.</summary>
    public static JsonObject Psk(string ssid, string password, bool hidden = false) => With(Base(ssid, "psk", hidden), password);

    /// <summary>WPA3 personal.</summary>
    public static JsonObject Sae(string ssid, string password, bool hidden = false) => With(Base(ssid, "sae", hidden), password);

    private static JsonObject Base(string ssid, string security, bool hidden)
    {
        var o = new JsonObject { ["ssid"] = ssid, ["security"] = security };
        if (hidden) o["hidden"] = true;
        return o;
    }

    private static JsonObject With(JsonObject o, string password)
    {
        o["password"] = password;
        return o;
    }
}

/// <summary>The look of <see cref="OhrWifiView"/>. Unset values follow the system's Material You colours, as Settings does.</summary>
public sealed record OhrWifiTheme(bool? Dark = null, Android.Graphics.Color? Accent = null, Android.Graphics.Color? Background = null,
    Android.Graphics.Color? Text = null, float? CornerRadiusDp = null)
{
    internal Bound.OhrWifiView.Theme Bound() => new(
        Dark is { } d ? Java.Lang.Boolean.ValueOf(d) : null,
        Accent is { } a ? Java.Lang.Integer.ValueOf(a.ToArgb()) : null,
        Background is { } b ? Java.Lang.Integer.ValueOf(b.ToArgb()) : null,
        Text is { } t ? Java.Lang.Integer.ValueOf(t.ToArgb()) : null,
        CornerRadiusDp is { } r ? Java.Lang.Float.ValueOf(r) : null);
}

/// <summary>What <see cref="OhrWifiView"/> offers (all on by default). Read when the screen opens.</summary>
public sealed record OhrWifiOptions(bool WifiSwitch = true, bool AddNetwork = true, bool Enterprise = true, bool Forget = true, bool Saved = true)
{
    internal Bound.OhrWifiView.Options Bound() => new(WifiSwitch, AddNetwork, Enterprise, Forget, Saved);
}

public sealed class OhrWifiConnectedEventArgs(string ssid) : EventArgs
{
    public string Ssid { get; } = ssid;
}

public sealed class OhrWifiErrorEventArgs(OhrException error) : EventArgs
{
    public OhrException Error { get; } = error;
}

/// <summary>
/// The Wi-Fi screen of Android's Settings, drawn by the OhrTech Agent inside the app's own layout: the network list,
/// joining every type of network, saved networks, forgetting. It opens no other activity or window, so a kiosk app stays
/// a kiosk. Needs the fleet's "wifi" grant (<see cref="Error"/> says if not). It opens once it is laid out; set
/// <see cref="Options"/> before.
/// </summary>
public sealed class OhrWifiView : FrameLayout
{
    private readonly Bound.OhrWifiView view;

    public OhrWifiView(Context context) : this(context, null) { }

    public OhrWifiView(Context context, IAttributeSet? attrs) : base(context, attrs)
    {
        view = new Bound.OhrWifiView(context);
        view.Listener = new ViewListener(this);
        AddView(view, new LayoutParams(Android.Views.ViewGroup.LayoutParams.MatchParent, Android.Views.ViewGroup.LayoutParams.MatchParent));
    }

    private OhrWifiView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) => view = null!;

    /// <summary>The device joined the network named (on the main thread).</summary>
    public event EventHandler<OhrWifiConnectedEventArgs>? Connected;

    /// <summary>The screen couldn't open or stopped (on the main thread).</summary>
    public event EventHandler<OhrWifiErrorEventArgs>? Error;

    /// <summary>Changes apply at once.</summary>
    public OhrWifiTheme Theme
    {
        get => theme;
        set
        {
            theme = value;
            view.SetTheme(value.Bound());
        }
    }

    private OhrWifiTheme theme = new();

    public OhrWifiOptions Options
    {
        get => options;
        set
        {
            options = value;
            view.SetOptions(value.Bound());
        }
    }

    private OhrWifiOptions options = new();

    private sealed class ViewListener(OhrWifiView owner) : Java.Lang.Object, Bound.OhrWifiView.IListener
    {
        public void OnConnected(string ssid) => owner.Connected?.Invoke(owner, new OhrWifiConnectedEventArgs(ssid));

        public void OnError(Java.Lang.Exception error) =>
            owner.Error?.Invoke(owner, new OhrWifiErrorEventArgs(Native.Map(error) as OhrException ?? new OhrFailedException(error.Message ?? "error", error.Message ?? "error", error)));
    }
}
