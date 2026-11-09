using System.Text;

namespace OhrTech.Device.Maui.Sample;

public static class MauiProgram
{
    internal const string Tag = "OhrSdkCheck";

    // Runs when the process starts, a process a push message started included: the handlers are set before the message.
    public static MauiApp CreateMauiApp()
    {
        Android.Util.Log.Info(Tag, "MAUI PROGRAM");
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>()
               .UseOhrTechDevice(o => o.OnPushMessage(OnPushMessage).OnPushTokenChanged(OnPushTokenChanged).OnPushDropped(OnPushDropped));
        builder.Services.AddTransient<MainPage>();
        return builder.Build();
    }

    private static Task OnPushMessage(OhrPushMessage m)
    {
        Android.Util.Log.Info(Tag, $"PUSH RECEIVED {m.Id} ({m.PushId}): {Encoding.UTF8.GetString(m.Data)} main={MainThread.IsMainThread}");
        return Task.CompletedTask;
    }

    private static Task OnPushTokenChanged(OhrPushToken? t)
    {
        Android.Util.Log.Info(Tag, $"PUSH TOKEN now {t?.Token}");
        return Task.CompletedTask;
    }

    private static Task OnPushDropped(string pushId, string reason)
    {
        Android.Util.Log.Info(Tag, $"PUSH DROPPED {pushId}: {reason}");
        return Task.CompletedTask;
    }
}
