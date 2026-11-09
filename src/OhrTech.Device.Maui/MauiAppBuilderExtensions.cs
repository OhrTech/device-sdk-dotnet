using Microsoft.Maui.Hosting;

namespace OhrTech.Device.Maui;

/// <summary>Push handlers for <see cref="MauiAppBuilderExtensions.UseOhrTechDevice"/>.</summary>
public sealed class OhrDeviceOptions
{
    internal Func<OhrPushMessage, Task>? OnMessage;
    internal Func<OhrPushToken?, Task>? OnTokenChanged;
    internal Func<string, string, Task>? OnDropped;

    /// <summary>Each push message, verified, opened, in date and not seen before; it runs whether the app is open or not.</summary>
    public OhrDeviceOptions OnPushMessage(Func<OhrPushMessage, Task> handler) { OnMessage = handler; return this; }

    /// <summary>The push token changed or ended (null: register again).</summary>
    public OhrDeviceOptions OnPushTokenChanged(Func<OhrPushToken?, Task> handler) { OnTokenChanged = handler; return this; }

    /// <summary>A push message was dropped: the hub's id and why.</summary>
    public OhrDeviceOptions OnPushDropped(Func<string, string, Task> handler) { OnDropped = handler; return this; }
}

public static class MauiAppBuilderExtensions
{
    /// <summary>
    /// The OhrTech Device SDK in a MAUI app: registers <see cref="OhrWifiView"/>'s handler, the areas' <c>Current</c> instances for
    /// dependency injection (<see cref="IOhrWifi"/>, ...), and the push handlers. MauiProgram runs when the process starts, a
    /// process a push message started included, so the handlers are there in time.
    /// </summary>
    public static MauiAppBuilder UseOhrTechDevice(this MauiAppBuilder builder, Action<OhrDeviceOptions>? configure = null)
    {
        builder.ConfigureMauiHandlers(h => h.AddHandler<OhrWifiView, OhrWifiViewHandler>());
        builder.Services.AddSingleton(OhrAccess.Current);
        builder.Services.AddSingleton(OhrDevice.Current);
        builder.Services.AddSingleton(OhrUpdates.Current);
        builder.Services.AddSingleton(OhrWifi.Current);
        builder.Services.AddSingleton(OhrNetwork.Current);
        builder.Services.AddSingleton(OhrKiosk.Current);
        builder.Services.AddSingleton(OhrPower.Current);
        builder.Services.AddSingleton(OhrSettings.Current);
        builder.Services.AddSingleton(OhrAttestation.Current);
        if (configure is not null)
        {
            var o = new OhrDeviceOptions();
            configure(o);
            if (o.OnMessage is not null) OhrPush.SetHandlers(o.OnMessage, o.OnTokenChanged, o.OnDropped);
        }
        return builder;
    }
}
