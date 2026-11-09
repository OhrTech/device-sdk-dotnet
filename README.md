# OhrTech Device SDK for .NET

`OhrTech.Device`: what an app on an OhrTech device can ask the OhrTech Agent for, from .NET for Android and .NET MAUI.
Who the device is, the app's own updates, Wi-Fi (with Android's Wi-Fi screen embedded in your layout), mobile data and
airplane mode, kiosk mode, power, settings, managed configuration, KEM keys and attestation in the device's TEE, client
certificates, pre-approved USB devices, its own events and logs, and push from your own server, end to end.

**Documentation: <https://docs.ohrtech.net/device-sdk/>**

```sh
dotnet add package OhrTech.Device
dotnet add package OhrTech.Device.Maui   # MAUI apps: the Wi-Fi screen as a control, UseOhrTechDevice()
```

`net10.0-android`, Android 13 (API 33) and up (`<SupportedOSPlatformVersion>33</SupportedOSPlatformVersion>`), on OhrTech
devices with the OhrTech Agent 1059 or later. Trim- and NativeAOT-safe. It wraps the Kotlin library
([net.ohrtech:device-sdk](https://github.com/OhrTech/device-sdk-android)), so the Agent protocol and its checks are the
same as for Kotlin apps; the binding is internal, and the API is plain C#.

## Who may use what

The Agent answers apps your organisation installed through OhrTech; others get `OhrNotManagedException`. Reading is mostly
always allowed. Changing something needs the fleet's grant for that area, set in the portal under the fleet's apps: `wifi`,
`network`, `kiosk`, `power`, `settings`. Settings and networks the fleet locks stay locked (`OhrFleetLockedException`).

## Use

```csharp
using OhrTech.Device;

var info = await OhrDevice.GetInfoAsync();                 // DeviceId "dev_…", fleet, org, OS, Agent
if (await OhrAccess.MayUseAsync("kiosk")) await OhrKiosk.StartAsync(activity);

OhrWifi.StateChanged += OnWifiStateChanged;               // raised on the thread that subscribed (the UI thread)
void OnWifiStateChanged(object? sender, OhrWifiStateChangedEventArgs e) => label.Text = e.State.Ssid;

await foreach (var battery in OhrPower.BatteryChanges(cancellationToken)) { … }
```

Each area is a static class; there is nothing to connect or close. The library binds the Agent on the first call and keeps
the binding; when the Agent stops (a crash, its own update) it binds again, and every event and stream goes on.
`OhrDeviceSdk.ConnectionChanged` says how it stands.

| Class | |
|---|---|
| `OhrDevice` | `GetInfoAsync()`: device id, serial, fleet, org, OS version and patch, Agent version, online |
| `OhrUpdates` | `GetAvailableAsync()`, `InstallAsync(whenIdle)`, `Changed`: the app's own updates the hub offers |
| `OhrWifi` | state, networks around, saved networks, `SaveAsync`, `ConnectAsync`, `ForgetAsync`, `SetEnabledAsync`, `ConnectQrAsync` |
| `OhrNetwork` | `GetStateAsync()`, `SetMobileDataAsync`, `SetAirplaneModeAsync`, `SetDataRoamingAsync` |
| `OhrKiosk` | `StartAsync(activity)`, `StopAsync()`, `GetStateAsync()`, `RequestExitAsync()`, `Heartbeat()` |
| `OhrPower` | `GetBatteryAsync()`, `RebootAsync`, `ScheduleRebootAsync`, `SetScreenTimeoutAsync`, `SetBrightnessAsync` |
| `OhrSettings` | `GetKeysAsync()`, `GetAsync(key)`, `SetAsync(key, value)`: the device settings apps may change |
| `OhrConfig` | the app's managed configuration, as its fleet or device sets it |
| `OhrAttestation` | `AttestAsync(challenge)`, `GetTokenAsync(audience)`: proof for your server of this app on this device |
| `OhrCertificates` | client certificates made on the device or imported; `CreateHttpHandler()` for HttpClient |
| `OhrUsb` | the USB devices the fleet pre-approves for the app: no permission prompt |
| `OhrDiagnostics` | the app's own events and log lines for the hub; crashes are reported without it |
| `OhrKeys` | TEE KEM keys: ML-KEM, X-Wing, X25519 for HPKE |
| `OhrPush` | end-to-end push: see below |

Changes come as static events (the first handler watches the Agent, the last one removed stops; remove handlers when a page
goes away, since a static event holds them) and as `IAsyncEnumerable` streams, ended by their cancellation token. For
dependency injection and test fakes, each area's `Current` is an interface (`IOhrWifi`, ...).

Every call can throw an `OhrException`: `OhrNotManagedException`, `OhrNotAllowedException` (`Area`),
`OhrFleetLockedException` (`Key`), `OhrUnsupportedException` (`Reason`, `NeedsAgent`), `OhrInvalidConfigException`
(`Field`, `Reason`), `OhrRefusedException` or `OhrFailedException`.

## MAUI

```csharp
builder.UseMauiApp<App>()
       .UseOhrTechDevice(o => o.OnPushMessage(OnPushMessage));
```

```xml
<ohr:OhrWifiView xmlns:ohr="clr-namespace:OhrTech.Device.Maui;assembly=OhrTech.Device.Maui"
                 Connected="OnWifiConnected" Error="OnWifiError" />
```

The Agent draws Android's Wi-Fi screen inside the control: no other activity or window opens, so a kiosk app stays a kiosk.
In .NET for Android, `OhrTech.Device.OhrWifiView` is the same as an Android view.

## Kiosk heartbeat

`OhrKiosk.Heartbeat()` beats from Android's main thread, where .NET for Android and MAUI run their UI: if it hangs for longer
than the timeout, the Agent stops the app and starts it again. Call it after `OhrKiosk.StartAsync()`.

## Push

Messages from your server, sealed to the device's TEE key and signed by your sender key; the OhrTech hub only carries them.
The library declares the push service; set the handlers when the process starts (`UseOhrTechDevice`, or your Application's
`OnCreate`): the Agent starts the app for a message, and the handlers run whether the app is open or not.

```csharp
OhrPush.SetHandlers(onMessage: async message => { /* message.Data: verified, opened, in date, not seen before */ });
var token = await OhrPush.RegisterAsync([OhrSenderKey.FromBase64(ed25519PublicKey)]);
// Send token.Token and token.PublicKeyBase64 to your server.
```

## Client certificates

The keys stay in the device's Android Keystore, so requests that need the app's client certificate go through Android's TLS:

```csharp
using var http = new HttpClient(OhrCertificates.CreateHttpHandler("orders"));
var orders = await http.GetStringAsync("https://api.example.com/orders");
```

It also trusts the private CAs the fleet gives the app.

## License

MIT
