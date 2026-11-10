using Android.App;
using Android.Content;
using Android.Util;
using BoundPush = Net.Ohrtech.Device.Push;

namespace OhrTech.Device;

/// <summary>The push suite of a token: <see cref="X25519"/> is small (about 190 bytes over the data), the default; <see cref="XWingMlDsa65"/> is post-quantum (about 4.6 KB over).</summary>
public enum OhrPushSuite
{
    X25519,
    XWingMlDsa65,
}

public static class OhrPushSuiteNames
{
    extension(OhrPushSuite suite)
    {
        /// <summary>The suite's name on the wire and in the Management API: "x25519" or "xwing-mldsa65".</summary>
        public string WireName => suite == OhrPushSuite.X25519 ? "x25519" : "xwing-mldsa65";
    }

    /// <summary>The suite named <paramref name="wireName"/> ("x25519" or "xwing-mldsa65"), or null.</summary>
    public static OhrPushSuite? FromWireName(string wireName) => wireName switch
    {
        "x25519" => OhrPushSuite.X25519,
        "xwing-mldsa65" => OhrPushSuite.XWingMlDsa65,
        _ => null,
    };
}

/// <summary>A sender's public keys, as your server's OhrTech SDK gives them: <paramref name="Ed25519"/> (32 bytes) and, for the xwing-mldsa65 suite, <paramref name="MlDsa65"/> (1,952 bytes).</summary>
public sealed record OhrSenderKey(byte[] Ed25519, byte[]? MlDsa65 = null)
{
    /// <summary>From base64 (standard alphabet, as the Management API and server SDKs print them).</summary>
    public static OhrSenderKey FromBase64(string ed25519, string? mlDsa65 = null) =>
        new(Convert.FromBase64String(ed25519), mlDsa65 is null ? null : Convert.FromBase64String(mlDsa65));

    internal BoundPush.SenderKey Bound() => new(Ed25519, MlDsa65);
    internal static OhrSenderKey Of(BoundPush.SenderKey k) => new(k.GetEd25519(), k.GetMlDsa65());
}

/// <summary>What your server needs to push to this app on this device: <paramref name="Token"/> ("psh_…") and <paramref name="PublicKey"/>.</summary>
public sealed record OhrPushToken(string Token, OhrPushSuite Suite, byte[] PublicKey)
{
    public string PublicKeyBase64 => Convert.ToBase64String(PublicKey);

    internal static OhrPushToken Of(BoundPush.PushToken t) =>
        new(t.Token, OhrPushSuiteNames.FromWireName(t.Suite.WireName) ?? OhrPushSuite.XWingMlDsa65, t.GetPublicKey());
}

/// <summary>A message your server sent, verified and opened. <paramref name="Id"/>: your server's id; <paramref name="Data"/> its bytes (at most 4,096); <paramref name="PushId"/> the hub's id ("pmsg_…").</summary>
public sealed record OhrPushMessage(string Id, byte[] Data, DateTimeOffset ExpiresAt, bool HighPriority, string PushId);

/// <summary>
/// OhrTech push: messages from the app's own server, end to end. The server seals each message to the device's push key
/// (in the TEE) and signs it with its sender key; the OhrTech hub only carries it.
/// <list type="number">
/// <item><see cref="SetHandlers"/> when the app's process starts (MauiProgram, or the Application's OnCreate): the Agent starts
/// the app for a message, and the handlers run then, whether the app is open or not.</item>
/// <item><see cref="RegisterAsync"/> once (and when the token handler gets null), with your server's sender public keys. Send the
/// token (token and public key) to your server.</item>
/// <item>Your server seals and sends through the Management API (<c>POST /v1/push</c>).</item>
/// </list>
/// </summary>
public static class OhrPush
{
    /// <summary>Makes the app's push key in the TEE, has the hub issue a token, and keeps <paramref name="senderKeys"/>. Waits for the hub (up to 30 s).</summary>
    public static Task<OhrPushToken> RegisterAsync(IReadOnlyList<OhrSenderKey> senderKeys, OhrPushSuite suite = OhrPushSuite.X25519) => Native.Run(() =>
        OhrPushToken.Of(BoundPush.OhrPush.Register([.. senderKeys.Select(k => k.Bound())],
            suite == OhrPushSuite.X25519 ? BoundPush.OhrPush.Suite.X25519! : BoundPush.OhrPush.Suite.XwingMldsa65!)));

    /// <summary>The current token, or null if the app isn't registered (or its token ended).</summary>
    public static Task<OhrPushToken?> GetTokenAsync() => Native.Run(() => BoundPush.OhrPush.Token() is { } t ? OhrPushToken.Of(t) : null);

    /// <summary>Ends the token and deletes the push key.</summary>
    public static Task UnregisterAsync() => Native.Run(BoundPush.OhrPush.Unregister);

    /// <summary>Replaces the sender keys messages are checked against (e.g. when your server rotates them).</summary>
    public static void SetSenderKeys(IReadOnlyList<OhrSenderKey> keys) => Native.Call(() => BoundPush.OhrPush.SetSenderKeys([.. keys.Select(k => k.Bound())]));

    public static IReadOnlyList<OhrSenderKey> GetSenderKeys() => Native.Call<IReadOnlyList<OhrSenderKey>>(() => [.. BoundPush.OhrPush.SenderKeys().Select(OhrSenderKey.Of)]);

    internal static Func<OhrPushMessage, Task>? OnMessage;
    internal static Func<OhrPushToken?, Task>? OnTokenChanged;
    internal static Func<string, string, Task>? OnDropped;

    /// <summary>
    /// The app's handlers. Set them when the process starts, before anything else needs them: a message can start the app
    /// with no UI. Each message reaches <paramref name="onMessage"/> verified, opened, in date and not seen before;
    /// <paramref name="onTokenChanged"/> gets the new token (null: register again); <paramref name="onDropped"/> the hub's id and
    /// why a message was dropped ("signature", "expired", "duplicate", ...).
    /// </summary>
    public static void SetHandlers(Func<OhrPushMessage, Task> onMessage, Func<OhrPushToken?, Task>? onTokenChanged = null,
        Func<string, string, Task>? onDropped = null)
    {
        OnMessage = onMessage;
        OnTokenChanged = onTokenChanged;
        OnDropped = onDropped;
    }
}

/// <summary>
/// The library's push service: the Kotlin library checks and opens each message, then the app's handler runs (and the
/// service waits for it, so the process stays up while it works). Only the OhrTech Agent holds RECEIVE_PUSH.
/// </summary>
[Service(Name = "net.ohrtech.device.dotnet.OhrDevicePushService", Exported = true, Permission = "net.ohrtech.device.permission.RECEIVE_PUSH")]
[IntentFilter(["net.ohrtech.device.action.PUSH"])]
internal sealed class OhrDevicePushService : BoundPush.OhrPushService
{
    private const string LogTag = "OhrDevicePush";
    private static readonly TimeSpan HandlerTime = TimeSpan.FromSeconds(25);

    public override void OnMessage(BoundPush.OhrPushMessage message)
    {
        var m = new OhrPushMessage(message.Id, message.GetData(), DateTimeOffset.FromUnixTimeMilliseconds(message.ExpiresAtMs), message.HighPriority, message.PushId);
        // A message the handler didn't take: the library reports it to the hub as dropped, and the sender may send it again.
        if (Run("message", OhrPush.OnMessage is { } h ? () => h(m) : null) is { } failed)
            throw new BoundPush.OhrPushService.NotHandled(failed);
    }

    public override void OnTokenChanged(BoundPush.PushToken? token)
    {
        var t = token is null ? null : OhrPushToken.Of(token);
        if (OhrPush.OnTokenChanged is { } h) Run("token", () => h(t));
    }

    public override void OnDropped(string pushId, string reason)
    {
        if (OhrPush.OnDropped is { } h) Run("dropped", () => h(pushId, reason));
    }

    /// <summary>Runs the handler and waits for it: null when it ran, else why not ("no_handler", "handler_timeout" or "handler").</summary>
    private static string? Run(string what, Func<Task>? handler)
    {
        if (handler is null)
        {
            Log.Warn(LogTag, $"Push {what}: the app set no handler (OhrPush.SetHandlers when the process starts)");
            return "no_handler";
        }
        try
        {
            if (handler().Wait(HandlerTime)) return null;
            Log.Warn(LogTag, $"Push {what}: the handler took longer than {HandlerTime.TotalSeconds} s");
            return "handler_timeout";
        }
        catch (Exception e)
        {
            Log.Error(LogTag, $"Push {what}: the handler threw: {e}");
            return "handler";
        }
    }
}
