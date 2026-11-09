using System.Text.Json.Nodes;
using Bound = Net.Ohrtech.Device;

namespace OhrTech.Device;

/// <summary>How the library's connection to the Agent stands.</summary>
public enum OhrConnection
{
    /// <summary>Nothing has asked the Agent anything yet.</summary>
    NotConnected,
    /// <summary>Binding the Agent for the first time.</summary>
    Connecting,
    Connected,
    /// <summary>The Agent stopped (a crash, its own update); calls wait for it to come back.</summary>
    Reconnecting,
    /// <summary>No OhrTech Agent with the Device SDK, or the hub didn't install this app.</summary>
    Unavailable,
}

public sealed class OhrConnectionChangedEventArgs(OhrConnection connection) : EventArgs
{
    public OhrConnection Connection { get; } = connection;
}

/// <summary>A subscription to one of the static events couldn't be made: <see cref="Event"/> names it.</summary>
public sealed class OhrEventFailedEventArgs(string @event, OhrException error) : EventArgs
{
    public string Event { get; } = @event;
    public OhrException Error { get; } = error;
}

/// <summary>
/// The library's connection to the OhrTech Agent. There is nothing to connect or close: the library binds the Agent
/// on the first call and keeps the binding for the life of the process; when the Agent stops (a crash, its own
/// update) it binds again, and every event and stream goes on.
/// </summary>
public static class OhrDeviceSdk
{
    /// <summary>Whether this device has an OhrTech Agent with the Device SDK.</summary>
    public static bool IsAvailable => Native.Call(() => Bound.OhrDeviceSdk.IsAvailable);

    /// <summary>
    /// Connects now instead of on the first call: to know at app start whether the Device SDK is there. Throws
    /// <see cref="OhrNotManagedException"/>, <see cref="OhrUnsupportedException"/> or <see cref="OhrFailedException"/>.
    /// </summary>
    public static Task ConnectAsync(TimeSpan? timeout = null) =>
        Native.Run(() => Bound.OhrDeviceSdk.ConnectBlocking((long)(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds));

    /// <summary>The connection now.</summary>
    public static OhrConnection Connection => Of(Bound.OhrDeviceSdk.GetConnection().Value as Bound.OhrDeviceSdk.Connection);

    /// <summary>The connection as it changes.</summary>
    public static event EventHandler<OhrConnectionChangedEventArgs>? ConnectionChanged
    {
        add => connectionChanged.Add(value);
        remove => connectionChanged.Remove(value);
    }

    private static readonly WatchedEvent<OhrConnectionChangedEventArgs> connectionChanged = new(nameof(ConnectionChanged),
        Bound.OhrDeviceSdk.ListenConnection, v => new OhrConnectionChangedEventArgs(Of(v as Bound.OhrDeviceSdk.Connection)));

    /// <summary>A static event's subscription failed (no Agent, not managed, too old an Agent for it).</summary>
    public static event EventHandler<OhrEventFailedEventArgs>? EventFailed;

    internal static void RaiseEventFailed(string name, Exception e) =>
        EventFailed?.Invoke(null, new OhrEventFailedEventArgs(name, Native.Map(e) as OhrException ?? new OhrFailedException(e.Message, e.Message, e)));

    private static OhrConnection Of(Bound.OhrDeviceSdk.Connection? c) => c?.Name() switch
    {
        "CONNECTING" => OhrConnection.Connecting,
        "CONNECTED" => OhrConnection.Connected,
        "RECONNECTING" => OhrConnection.Reconnecting,
        "UNAVAILABLE" => OhrConnection.Unavailable,
        _ => OhrConnection.NotConnected,
    };
}

/// <summary>Who the Agent takes this app for, and what the fleet lets it do.</summary>
/// <param name="App">The hub's id of this app ("app_…").</param>
/// <param name="AppVersion">The hub's id of the installed version, or null when it isn't one the hub shipped.</param>
/// <param name="Areas">What the app may use: always "device", "updates", "keys", "push", the reads ("wifi.read", ...), and each area the fleet grants.</param>
/// <param name="AgentInterface">The Agent's Device SDK interface version.</param>
public sealed record OhrAccessInfo(string App, string PackageName, string? AppVersion, IReadOnlySet<string> Areas, long AgentVersion, int AgentInterface)
{
    /// <summary>Whether the fleet lets the app use (change) <paramref name="area"/>; reads of most areas are always allowed.</summary>
    public bool MayUse(string area) => Areas.Contains(area);
}

public interface IOhrAccess
{
    Task<OhrAccessInfo> GetInfoAsync();
    Task<bool> MayUseAsync(string area);
}

/// <summary>Who the Agent takes this app for, and what the fleet lets it do: asked of the Agent each time.</summary>
public static class OhrAccess
{
    public static IOhrAccess Current { get; } = new Impl();

    public static Task<OhrAccessInfo> GetInfoAsync() => Native.Run(() =>
    {
        var i = Bound.OhrAccess.InvokeInfo();
        return new OhrAccessInfo(i.App, i.PackageName, i.AppVersion, new HashSet<string>(i.Areas), i.AgentVersion, i.AgentInterface);
    });

    /// <summary>Whether the fleet lets the app use (change) <paramref name="area"/> now.</summary>
    public static async Task<bool> MayUseAsync(string area) => (await GetInfoAsync().ConfigureAwait(false)).MayUse(area);

    private sealed class Impl : IOhrAccess
    {
        public Task<OhrAccessInfo> GetInfoAsync() => OhrAccess.GetInfoAsync();
        public Task<bool> MayUseAsync(string area) => OhrAccess.MayUseAsync(area);
    }
}

public sealed record OhrNamed(string Id, string Name);

/// <param name="DeviceId">"dev_…" (null until the hub sent the device its first policy).</param>
/// <param name="Online">The Agent is connected to the hub now.</param>
public sealed record OhrDeviceInfo(string? DeviceId, string? Serial, OhrNamed? Fleet, OhrNamed? Org, string OsRelease, string OsBuild,
    string SecurityPatch, string OsIncremental, long AgentVersion, bool Online);

public interface IOhrDevice
{
    Task<OhrDeviceInfo> GetInfoAsync();
}

/// <summary>The device: who it is, as the OhrTech hub and the Management API name it. Always allowed.</summary>
public static class OhrDevice
{
    public static IOhrDevice Current { get; } = new Impl();

    public static Task<OhrDeviceInfo> GetInfoAsync() => Native.Run(() =>
    {
        var i = Bound.OhrDevice.InvokeInfo();
        static OhrNamed? Named(Bound.OhrDevice.Named? n) => n is null ? null : new OhrNamed(n.Id, n.Name);
        return new OhrDeviceInfo(i.DeviceId, i.Serial, Named(i.Fleet), Named(i.Org), i.OsRelease, i.OsBuild, i.SecurityPatch, i.OsIncremental,
            i.AgentVersion, i.Online);
    });

    private sealed class Impl : IOhrDevice
    {
        public Task<OhrDeviceInfo> GetInfoAsync() => OhrDevice.GetInfoAsync();
    }
}

/// <summary>An update the hub offers. <see cref="IsNone"/>: nothing offered any more (in <see cref="OhrUpdates.Changed"/>).</summary>
public sealed record OhrUpdate(long VersionCode, string? VersionName, string? Notes, bool Downloaded)
{
    public bool IsNone => VersionCode == 0;

    internal static OhrUpdate Of(Bound.OhrUpdates.Update u) => new(u.VersionCode, u.VersionName, u.Notes, u.Downloaded);
}

public sealed class OhrUpdateChangedEventArgs(OhrUpdate update) : EventArgs
{
    public OhrUpdate Update { get; } = update;
}

public interface IOhrUpdates
{
    Task<OhrUpdate?> GetAvailableAsync();
    Task InstallAsync(bool whenIdle = false);
    event EventHandler<OhrUpdateChangedEventArgs>? Changed;
    IAsyncEnumerable<OhrUpdate> Changes(CancellationToken cancellationToken = default);
}

/// <summary>The app's own updates: a version the hub offers as available, downloaded ahead. Always allowed.</summary>
public static class OhrUpdates
{
    public static IOhrUpdates Current { get; } = new Impl();

    /// <summary>The offered update, or null.</summary>
    public static Task<OhrUpdate?> GetAvailableAsync() => Native.Run(() => Bound.OhrUpdates.Available() is { } u ? OhrUpdate.Of(u) : null);

    /// <summary>Installs the offered update: now, or <paramref name="whenIdle"/> the next time the screen is off. The app is replaced, so it stops.</summary>
    public static Task InstallAsync(bool whenIdle = false) => Native.Run(() => Bound.OhrUpdates.Install(whenIdle));

    public static event EventHandler<OhrUpdateChangedEventArgs>? Changed
    {
        add => changed.Add(value);
        remove => changed.Remove(value);
    }

    private static readonly WatchedEvent<OhrUpdateChangedEventArgs> changed =
        new(nameof(Changed), Bound.OhrUpdates.Listen, v => new OhrUpdateChangedEventArgs(OhrUpdate.Of((Bound.OhrUpdates.Update)v!)));

    public static IAsyncEnumerable<OhrUpdate> Changes(CancellationToken cancellationToken = default) =>
        Streams.Watch(Bound.OhrUpdates.Listen, v => OhrUpdate.Of((Bound.OhrUpdates.Update)v!), cancellationToken);

    private sealed class Impl : IOhrUpdates
    {
        public Task<OhrUpdate?> GetAvailableAsync() => OhrUpdates.GetAvailableAsync();
        public Task InstallAsync(bool whenIdle = false) => OhrUpdates.InstallAsync(whenIdle);
        public event EventHandler<OhrUpdateChangedEventArgs>? Changed { add => OhrUpdates.Changed += value; remove => OhrUpdates.Changed -= value; }
        public IAsyncEnumerable<OhrUpdate> Changes(CancellationToken cancellationToken = default) => OhrUpdates.Changes(cancellationToken);
    }
}

/// <param name="Transport">"wifi", "cellular", "ethernet", "vpn" or "other".</param>
public sealed record OhrActiveNetwork(string Transport, bool Validated, bool Captive, bool Metered, bool Vpn);

/// <param name="Active">The network in use, or null for none.</param>
public sealed record OhrNetworkState(OhrActiveNetwork? Active, bool MobileData, bool AirplaneMode, bool DataRoaming, bool EthernetConnected)
{
    internal static OhrNetworkState Of(Bound.OhrNetwork.State s) => new(
        s.Active is { } a ? new OhrActiveNetwork(a.Transport, a.Validated, a.Captive, a.Metered, a.Vpn) : null,
        s.MobileData, s.AirplaneMode, s.DataRoaming, s.EthernetConnected);
}

public sealed class OhrNetworkChangedEventArgs(OhrNetworkState state) : EventArgs
{
    public OhrNetworkState State { get; } = state;
}

public interface IOhrNetwork
{
    Task<OhrNetworkState> GetStateAsync();
    Task SetMobileDataAsync(bool on);
    Task SetAirplaneModeAsync(bool on);
    Task SetDataRoamingAsync(bool on);
    event EventHandler<OhrNetworkChangedEventArgs>? Changed;
    IAsyncEnumerable<OhrNetworkState> Changes(CancellationToken cancellationToken = default);
}

/// <summary>Mobile data, airplane mode, roaming: read always, change with the fleet's "network" grant.</summary>
public static class OhrNetwork
{
    public static IOhrNetwork Current { get; } = new Impl();

    public static Task<OhrNetworkState> GetStateAsync() => Native.Run(() => OhrNetworkState.Of(Bound.OhrNetwork.InvokeState()));
    public static Task SetMobileDataAsync(bool on) => Native.Run(() => Bound.OhrNetwork.SetMobileData(on));
    public static Task SetAirplaneModeAsync(bool on) => Native.Run(() => Bound.OhrNetwork.SetAirplaneMode(on));

    /// <summary><see cref="OhrFleetLockedException"/> when the fleet locks network.data_roaming.</summary>
    public static Task SetDataRoamingAsync(bool on) => Native.Run(() => Bound.OhrNetwork.SetDataRoaming(on));

    public static event EventHandler<OhrNetworkChangedEventArgs>? Changed
    {
        add => changed.Add(value);
        remove => changed.Remove(value);
    }

    private static readonly WatchedEvent<OhrNetworkChangedEventArgs> changed =
        new(nameof(Changed), Bound.OhrNetwork.Listen, v => new OhrNetworkChangedEventArgs(OhrNetworkState.Of((Bound.OhrNetwork.State)v!)));

    public static IAsyncEnumerable<OhrNetworkState> Changes(CancellationToken cancellationToken = default) =>
        Streams.Watch(Bound.OhrNetwork.Listen, v => OhrNetworkState.Of((Bound.OhrNetwork.State)v!), cancellationToken);

    private sealed class Impl : IOhrNetwork
    {
        public Task<OhrNetworkState> GetStateAsync() => OhrNetwork.GetStateAsync();
        public Task SetMobileDataAsync(bool on) => OhrNetwork.SetMobileDataAsync(on);
        public Task SetAirplaneModeAsync(bool on) => OhrNetwork.SetAirplaneModeAsync(on);
        public Task SetDataRoamingAsync(bool on) => OhrNetwork.SetDataRoamingAsync(on);
        public event EventHandler<OhrNetworkChangedEventArgs>? Changed { add => OhrNetwork.Changed += value; remove => OhrNetwork.Changed -= value; }
        public IAsyncEnumerable<OhrNetworkState> Changes(CancellationToken cancellationToken = default) => OhrNetwork.Changes(cancellationToken);
    }
}

/// <summary>What stays available in kiosk mode.</summary>
[Flags]
public enum OhrKioskFeatures
{
    None = 0,
    SystemInfo = 1,
    Notifications = 2,
    Home = 4,
    Overview = 8,
    GlobalActions = 16,
    Keyguard = 32,
    BlockActivityStartInTask = 64,
}

/// <param name="Pinned">This app is the one pinned.</param>
/// <param name="Mode">"none", "locked" or "pinned".</param>
public sealed record OhrKioskState(bool Pinned, string Mode, OhrKioskFeatures Features, bool AppMayExit);

public sealed class OhrKioskModeChangedEventArgs(string mode) : EventArgs
{
    /// <summary>"none", "locked" or "pinned".</summary>
    public string Mode { get; } = mode;
}

public interface IOhrKiosk
{
    Task StartAsync(Android.App.Activity activity, OhrKioskFeatures features = OhrKioskFeatures.GlobalActions | OhrKioskFeatures.SystemInfo);
    Task StopAsync();
    Task<OhrKioskState> GetStateAsync();
    Task RequestExitAsync();
    void Heartbeat(TimeSpan? timeout = null);
    void StopHeartbeat();
    event EventHandler<OhrKioskModeChangedEventArgs>? ModeChanged;
}

/// <summary>
/// Kiosk mode: pins the app with Android's lock task, as the system, so the app can't be left (and can't unpin itself
/// either). Needs the fleet's "kiosk" grant. <see cref="StopAsync"/> works only if the fleet lets kiosk apps leave.
/// </summary>
public static class OhrKiosk
{
    public static IOhrKiosk Current { get; } = new Impl();

    /// <summary>Pins <paramref name="activity"/>'s task, keeping <paramref name="features"/>.</summary>
    public static Task StartAsync(Android.App.Activity activity, OhrKioskFeatures features = OhrKioskFeatures.GlobalActions | OhrKioskFeatures.SystemInfo) =>
        Native.Run(() => Bound.OhrKiosk.Start(activity, (int)features));

    /// <summary>Ends kiosk mode, if the fleet lets kiosk apps leave (<see cref="OhrNotAllowedException"/> otherwise).</summary>
    public static Task StopAsync() => Native.Run(Bound.OhrKiosk.Stop);

    public static Task<OhrKioskState> GetStateAsync() => Native.Run(() =>
    {
        var s = Bound.OhrKiosk.InvokeState();
        return new OhrKioskState(s.Pinned, s.Mode, (OhrKioskFeatures)s.Features, s.AppMayExit);
    });

    /// <summary>Opens the Agent's exit PIN keypad over the app, for staff to leave kiosk mode for a while. The app must be the one pinned.</summary>
    public static Task RequestExitAsync() => Native.Run(Bound.OhrKiosk.RequestExit);

    /// <summary>
    /// Tells the Agent's kiosk watchdog that the app's main thread is alive: the library beats from Android's main
    /// thread (where .NET for Android and MAUI run their UI) a few times per <paramref name="timeout"/> (10 s by
    /// default, 2 s to 10 minutes). If the beats stop for longer, the Agent stops the app and starts it again. Only
    /// while the app is pinned; call it again after <see cref="StartAsync"/>. One per app.
    /// </summary>
    public static void Heartbeat(TimeSpan? timeout = null) =>
        Native.Call(() => Bound.OhrKiosk.Heartbeat((long)(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds));

    /// <summary>Stops the heartbeat (and the Agent's watch on it).</summary>
    public static void StopHeartbeat() => Native.Call(Bound.OhrKiosk.StopHeartbeat);

    /// <summary>The device-wide lock task mode as it changes; <see cref="GetStateAsync"/> says whose.</summary>
    public static event EventHandler<OhrKioskModeChangedEventArgs>? ModeChanged
    {
        add => modeChanged.Add(value);
        remove => modeChanged.Remove(value);
    }

    private static readonly WatchedEvent<OhrKioskModeChangedEventArgs> modeChanged =
        new(nameof(ModeChanged), Bound.OhrKiosk.ListenMode, v => new OhrKioskModeChangedEventArgs(v?.ToString() ?? "none"));

    public static IAsyncEnumerable<string> ModeChanges(CancellationToken cancellationToken = default) =>
        Streams.Watch(Bound.OhrKiosk.ListenMode, v => v?.ToString() ?? "none", cancellationToken);

    private sealed class Impl : IOhrKiosk
    {
        public Task StartAsync(Android.App.Activity activity, OhrKioskFeatures features = OhrKioskFeatures.GlobalActions | OhrKioskFeatures.SystemInfo) =>
            OhrKiosk.StartAsync(activity, features);
        public Task StopAsync() => OhrKiosk.StopAsync();
        public Task<OhrKioskState> GetStateAsync() => OhrKiosk.GetStateAsync();
        public Task RequestExitAsync() => OhrKiosk.RequestExitAsync();
        public void Heartbeat(TimeSpan? timeout = null) => OhrKiosk.Heartbeat(timeout);
        public void StopHeartbeat() => OhrKiosk.StopHeartbeat();
        public event EventHandler<OhrKioskModeChangedEventArgs>? ModeChanged { add => OhrKiosk.ModeChanged += value; remove => OhrKiosk.ModeChanged -= value; }
    }
}

/// <param name="Plugged">"ac", "usb", "wireless", "dock" or null.</param>
/// <param name="Health">"good", "overheat", ...</param>
public sealed record OhrBattery(int? Level, bool Charging, string? Plugged, string Health, double TemperatureC, bool ScreenOn)
{
    internal static OhrBattery Of(Bound.OhrPower.Battery b) => new(b.Level?.IntValue(), b.Charging, b.Plugged, b.Health, b.TemperatureC, b.ScreenOn);
}

/// <summary>A reboot schedule: <paramref name="Time"/> "HH:mm", <paramref name="Days"/> 1-7 (Monday = 1).</summary>
public sealed record OhrRebootSchedule(string Time, IReadOnlySet<int> Days, string Reason);

public sealed class OhrBatteryChangedEventArgs(OhrBattery battery) : EventArgs
{
    public OhrBattery Battery { get; } = battery;
}

public interface IOhrPower
{
    Task<OhrBattery> GetBatteryAsync();
    Task RebootAsync(string reason);
    Task ScheduleRebootAsync(OhrRebootSchedule? schedule);
    Task<OhrRebootSchedule?> GetRebootScheduleAsync();
    Task SetScreenTimeoutAsync(TimeSpan timeout);
    Task SetBrightnessAsync(int percent);
    event EventHandler<OhrBatteryChangedEventArgs>? BatteryChanged;
    IAsyncEnumerable<OhrBattery> BatteryChanges(CancellationToken cancellationToken = default);
}

/// <summary>Battery (always), reboots, screen timeout and brightness (with the fleet's "power" grant).</summary>
public static class OhrPower
{
    public static IOhrPower Current { get; } = new Impl();

    public static Task<OhrBattery> GetBatteryAsync() => Native.Run(() => OhrBattery.Of(Bound.OhrPower.InvokeBattery()));

    /// <summary>Reboots in a few seconds; the hub records <paramref name="reason"/>.</summary>
    public static Task RebootAsync(string reason) => Native.Run(() => Bound.OhrPower.Reboot(reason));

    /// <summary>Reboots on a schedule (the device's one; the last app to set it wins); null stops it.</summary>
    public static Task ScheduleRebootAsync(OhrRebootSchedule? schedule) => Native.Run(() => Bound.OhrPower.ScheduleReboot(schedule is null ? null
        : new Bound.OhrPower.Schedule(schedule.Time, schedule.Days.Order().Select(d => Java.Lang.Integer.ValueOf(d)).ToList(), schedule.Reason)));

    public static Task<OhrRebootSchedule?> GetRebootScheduleAsync() => Native.Run(() => Bound.OhrPower.RebootSchedule() is { } s
        ? new OhrRebootSchedule(s.Time, s.Days.Select(d => d.IntValue()).ToHashSet(), s.Reason) : null);

    /// <summary>Any whole number of seconds, 1 to 2147483 (Android's limit). <see cref="OhrFleetLockedException"/> if the fleet locks it.</summary>
    public static Task SetScreenTimeoutAsync(TimeSpan timeout) => Native.Run(() => Bound.OhrPower.SetScreenTimeout((int)timeout.TotalSeconds));

    /// <summary>0-100 (turns adaptive brightness off). <see cref="OhrFleetLockedException"/> if the fleet locks it.</summary>
    public static Task SetBrightnessAsync(int percent) => Native.Run(() => Bound.OhrPower.SetBrightness(percent));

    public static event EventHandler<OhrBatteryChangedEventArgs>? BatteryChanged
    {
        add => batteryChanged.Add(value);
        remove => batteryChanged.Remove(value);
    }

    private static readonly WatchedEvent<OhrBatteryChangedEventArgs> batteryChanged =
        new(nameof(BatteryChanged), Bound.OhrPower.ListenBattery, v => new OhrBatteryChangedEventArgs(OhrBattery.Of((Bound.OhrPower.Battery)v!)));

    public static IAsyncEnumerable<OhrBattery> BatteryChanges(CancellationToken cancellationToken = default) =>
        Streams.Watch(Bound.OhrPower.ListenBattery, v => OhrBattery.Of((Bound.OhrPower.Battery)v!), cancellationToken);

    private sealed class Impl : IOhrPower
    {
        public Task<OhrBattery> GetBatteryAsync() => OhrPower.GetBatteryAsync();
        public Task RebootAsync(string reason) => OhrPower.RebootAsync(reason);
        public Task ScheduleRebootAsync(OhrRebootSchedule? schedule) => OhrPower.ScheduleRebootAsync(schedule);
        public Task<OhrRebootSchedule?> GetRebootScheduleAsync() => OhrPower.GetRebootScheduleAsync();
        public Task SetScreenTimeoutAsync(TimeSpan timeout) => OhrPower.SetScreenTimeoutAsync(timeout);
        public Task SetBrightnessAsync(int percent) => OhrPower.SetBrightnessAsync(percent);
        public event EventHandler<OhrBatteryChangedEventArgs>? BatteryChanged { add => OhrPower.BatteryChanged += value; remove => OhrPower.BatteryChanged -= value; }
        public IAsyncEnumerable<OhrBattery> BatteryChanges(CancellationToken cancellationToken = default) => OhrPower.BatteryChanges(cancellationToken);
    }
}

/// <param name="Value">The value as JSON (a bool, number, string, array, object or null).</param>
/// <param name="Mode">"unmanaged", "set" or "locked".</param>
public sealed record OhrSetting(string Key, JsonNode? Value, string Mode)
{
    public bool Locked => Mode == "locked";

    internal static OhrSetting Of(Bound.OhrSettings.Setting s) => new(s.Key, Native.Json(s.Value), s.Mode);
}

public sealed class OhrSettingChangedEventArgs(OhrSetting setting) : EventArgs
{
    public OhrSetting Setting { get; } = setting;
}

public interface IOhrSettings
{
    Task<IReadOnlyList<string>> GetKeysAsync();
    Task<OhrSetting> GetAsync(string key);
    Task SetAsync(string key, JsonNode? value);
    event EventHandler<OhrSettingChangedEventArgs>? Changed;
}

/// <summary>
/// The device's settings, by the OhrTech settings catalogue's keys: read always, change with the fleet's "settings"
/// grant. A key the fleet locks refuses (<see cref="OhrFleetLockedException"/>).
/// </summary>
public static class OhrSettings
{
    public static IOhrSettings Current { get; } = new Impl();

    /// <summary>The keys apps may read and change on this device.</summary>
    public static Task<IReadOnlyList<string>> GetKeysAsync() => Native.Run<IReadOnlyList<string>>(() => [.. Bound.OhrSettings.Keys()]);
    public static Task<OhrSetting> GetAsync(string key) => Native.Run(() => OhrSetting.Of(Bound.OhrSettings.Get(key)));

    /// <summary><paramref name="value"/>: a bool, number, string, array, object or null, as JSON (<c>JsonValue.Create(45)</c>).</summary>
    public static Task SetAsync(string key, JsonNode? value) => Native.Run(() => Bound.OhrSettings.Set(key, Native.Java(value)));

    public static event EventHandler<OhrSettingChangedEventArgs>? Changed
    {
        add => changed.Add(value);
        remove => changed.Remove(value);
    }

    private static readonly WatchedEvent<OhrSettingChangedEventArgs> changed =
        new(nameof(Changed), Bound.OhrSettings.Listen, v => new OhrSettingChangedEventArgs(OhrSetting.Of((Bound.OhrSettings.Setting)v!)));

    public static IAsyncEnumerable<OhrSetting> Changes(CancellationToken cancellationToken = default) =>
        Streams.Watch(Bound.OhrSettings.Listen, v => OhrSetting.Of((Bound.OhrSettings.Setting)v!), cancellationToken);

    private sealed class Impl : IOhrSettings
    {
        public Task<IReadOnlyList<string>> GetKeysAsync() => OhrSettings.GetKeysAsync();
        public Task<OhrSetting> GetAsync(string key) => OhrSettings.GetAsync(key);
        public Task SetAsync(string key, JsonNode? value) => OhrSettings.SetAsync(key, value);
        public event EventHandler<OhrSettingChangedEventArgs>? Changed { add => OhrSettings.Changed += value; remove => OhrSettings.Changed -= value; }
    }
}

public sealed class OhrConfigChangedEventArgs(JsonObject values) : EventArgs
{
    public JsonObject Values { get; } = values;
}

/// <summary>
/// The app's managed configuration: the values its fleet (or one device's overrides) sets for the settings the app
/// declares in its own res/xml/app_restrictions.xml. Needs no Agent connection.
/// </summary>
public static class OhrConfig
{
    /// <summary>Every managed value (bundles as objects, bundle arrays as arrays of objects).</summary>
    public static JsonObject GetValues() => Native.Call(() => Json(Bound.OhrConfig.Values()));

    /// <summary>A string, choice or hidden setting: the managed value, else the declared default, else <paramref name="fallback"/>.</summary>
    public static string? GetString(string key, string? fallback = null) => Native.Call(() => Bound.OhrConfig.String(key, fallback));
    public static int GetInt(string key, int fallback = 0) => Native.Call(() => Bound.OhrConfig.GetInt(key, fallback));
    public static bool GetBool(string key, bool fallback = false) => Native.Call(() => Bound.OhrConfig.Bool(key, fallback));

    /// <summary>A multi-select setting.</summary>
    public static IReadOnlyList<string> GetStrings(string key) => Native.Call<IReadOnlyList<string>>(() => [.. Bound.OhrConfig.Strings(key)]);

    /// <summary>Whether the fleet sets <paramref name="key"/>.</summary>
    public static bool IsSet(string key) => Native.Call(() => Bound.OhrConfig.IsSet(key));

    public static event EventHandler<OhrConfigChangedEventArgs>? Changed
    {
        add => changed.Add(value);
        remove => changed.Remove(value);
    }

    private static readonly WatchedEvent<OhrConfigChangedEventArgs> changed =
        new(nameof(Changed), Bound.OhrConfig.Listen, v => new OhrConfigChangedEventArgs(Json((Android.OS.Bundle)v!)));

    /// <summary>A Bundle of managed configuration as JSON.</summary>
    internal static JsonObject Json(Android.OS.Bundle b)
    {
        var o = new JsonObject();
        foreach (var k in b.KeySet()!.Order())
        {
#pragma warning disable CA1422 // Bundle.Get: the values are of any of the restriction types
            var v = b.Get(k);
#pragma warning restore CA1422
            o[k] = v switch
            {
                Android.OS.Bundle n => Json(n),
                null => null,
                // A multi-select setting (String[]) or a bundle-array setting (Parcelable[] of Bundles).
                _ when v.Class.IsArray && v.Class.ComponentType?.Name == "java.lang.String" =>
                    new JsonArray([.. (b.GetStringArray(k) ?? []).Select(x => (JsonNode?)JsonValue.Create(x))]),
#pragma warning disable CA1422
                _ when v.Class.IsArray => new JsonArray([.. (b.GetParcelableArray(k) ?? []).Select(x => x is Android.OS.Bundle n ? (JsonNode?)Json(n) : null)]),
#pragma warning restore CA1422
                _ => Native.Json(v),
            };
        }
        return o;
    }
}

public sealed record OhrUsbDevice(bool Accessory, string? DeviceName, int VendorId, int ProductId, string? Manufacturer, string? Product,
    string? Model, string? Version, string? Serial, string? Name, bool HasPermission)
{
    internal static OhrUsbDevice Of(Bound.OhrUsb.Device d) => new(d.Accessory, d.DeviceName, d.VendorId, d.ProductId, d.Manufacturer, d.Product,
        d.Model, d.Version, d.Serial, d.Name, d.HasPermission);
}

public sealed class OhrUsbChangedEventArgs(bool attached, OhrUsbDevice device) : EventArgs
{
    public bool Attached { get; } = attached;
    public OhrUsbDevice Device { get; } = device;

    internal static OhrUsbChangedEventArgs Of(Java.Lang.Object? v) => v is Bound.OhrUsb.Change c
        ? new(c.Attached, OhrUsbDevice.Of(c.Device)) : throw new InvalidOperationException("not a USB change");
}

/// <summary>The USB devices the fleet pre-approves for this app: no "Allow access?" prompt (Agent 1061+). Open them with UsbManager by <see cref="OhrUsbDevice.DeviceName"/>.</summary>
public static class OhrUsb
{
    public static Task<IReadOnlyList<OhrUsbDevice>> GetDevicesAsync() => Native.Run<IReadOnlyList<OhrUsbDevice>>(() => [.. Bound.OhrUsb.Devices().Select(OhrUsbDevice.Of)]);

    public static event EventHandler<OhrUsbChangedEventArgs>? Changed
    {
        add => changed.Add(value);
        remove => changed.Remove(value);
    }

    private static readonly WatchedEvent<OhrUsbChangedEventArgs> changed = new(nameof(Changed), Bound.OhrUsb.Listen, OhrUsbChangedEventArgs.Of);
}

/// <summary>The app's own diagnostics for the hub (Agent 1061+). Crashes need nothing: the Agent reports them.</summary>
public static class OhrDiagnostics
{
    /// <summary>An event of the app's own ("order_failed"): lower camel or snake case, up to 64 characters; <paramref name="data"/> up to 2 KB as JSON. At most 60 a minute.</summary>
    public static Task EventAsync(string name, JsonObject? data = null) =>
        Native.Run(() => Bound.OhrDiagnostics.Event(name, Native.JavaObject(data ?? [])));

    /// <summary>A log line for log bundles, kept by the Agent (the newest 500).</summary>
    public static Task LogAsync(Android.Util.LogPriority priority, string tag, string message) =>
        Native.Run(() => Bound.OhrDiagnostics.Log((int)priority, tag, message));
}
