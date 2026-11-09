using Microsoft.Maui.Handlers;

namespace OhrTech.Device.Maui;

/// <summary>
/// The Wi-Fi screen of Android's Settings, drawn by the OhrTech Agent inside the page: the network list, joining every
/// type of network, saved networks, forgetting. It opens no other activity or window, so a kiosk app stays a kiosk.
/// Needs the fleet's "wifi" grant (<see cref="Error"/> says if not). Register it with <c>builder.UseOhrTechDevice()</c>.
/// </summary>
public class OhrWifiView : View
{
    public static readonly BindableProperty ThemeProperty =
        BindableProperty.Create(nameof(Theme), typeof(OhrWifiTheme), typeof(OhrWifiView), new OhrWifiTheme());

    public static readonly BindableProperty OptionsProperty =
        BindableProperty.Create(nameof(Options), typeof(OhrWifiOptions), typeof(OhrWifiView), new OhrWifiOptions());

    /// <summary>Changes apply at once.</summary>
    public OhrWifiTheme Theme
    {
        get => (OhrWifiTheme)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    /// <summary>Read when the screen opens.</summary>
    public OhrWifiOptions Options
    {
        get => (OhrWifiOptions)GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    /// <summary>The device joined the network named.</summary>
    public event EventHandler<OhrWifiConnectedEventArgs>? Connected;

    /// <summary>The screen couldn't open or stopped.</summary>
    public event EventHandler<OhrWifiErrorEventArgs>? Error;

    internal void RaiseConnected(OhrWifiConnectedEventArgs e) => Connected?.Invoke(this, e);
    internal void RaiseError(OhrWifiErrorEventArgs e) => Error?.Invoke(this, e);
}

/// <summary>Shows <see cref="OhrWifiView"/> as the library's Android view.</summary>
public sealed class OhrWifiViewHandler() : ViewHandler<OhrWifiView, OhrTech.Device.OhrWifiView>(Mapper)
{
    public static readonly IPropertyMapper<OhrWifiView, OhrWifiViewHandler> Mapper = new PropertyMapper<OhrWifiView, OhrWifiViewHandler>(ViewMapper)
    {
        [nameof(OhrWifiView.Theme)] = (h, v) => h.PlatformView.Theme = v.Theme,
        [nameof(OhrWifiView.Options)] = (h, v) => h.PlatformView.Options = v.Options,
    };

    protected override OhrTech.Device.OhrWifiView CreatePlatformView() => new(Context);

    protected override void ConnectHandler(OhrTech.Device.OhrWifiView platformView)
    {
        base.ConnectHandler(platformView);
        platformView.Connected += OnConnected;
        platformView.Error += OnError;
    }

    protected override void DisconnectHandler(OhrTech.Device.OhrWifiView platformView)
    {
        platformView.Connected -= OnConnected;
        platformView.Error -= OnError;
        base.DisconnectHandler(platformView);
    }

    private void OnConnected(object? sender, OhrWifiConnectedEventArgs e) => VirtualView?.RaiseConnected(e);
    private void OnError(object? sender, OhrWifiErrorEventArgs e) => VirtualView?.RaiseError(e);
}
