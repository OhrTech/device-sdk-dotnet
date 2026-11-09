using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Java.Util.Concurrent;
using Bound = Net.Ohrtech.Device;

namespace OhrTech.Device;

/// <summary>Why the OhrTech Agent refused or couldn't do what the app asked. Every call can throw one of the subclasses.</summary>
public abstract class OhrException : Exception
{
    private protected OhrException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>This app is not one the organisation installed on this device through OhrTech.</summary>
public sealed class OhrNotManagedException(string message, Exception? inner = null) : OhrException(message, inner);

/// <summary>The fleet doesn't let this app use <see cref="Area"/>.</summary>
public sealed class OhrNotAllowedException(string message, string area, Exception? inner = null) : OhrException(message, inner)
{
    public string Area { get; } = area;
}

/// <summary>The fleet locks the setting or network <see cref="Key"/>.</summary>
public sealed class OhrFleetLockedException(string message, string key, Exception? inner = null) : OhrException(message, inner)
{
    public string Key { get; } = key;
}

/// <summary>This device, OS or Agent can't do it. <see cref="NeedsAgent"/>: the Agent version that can, when the Agent is only too old.</summary>
public sealed class OhrUnsupportedException(string message, string reason, long? needsAgent, Exception? inner = null) : OhrException(message, inner)
{
    public string Reason { get; } = reason;
    public long? NeedsAgent { get; } = needsAgent;
}

/// <summary>A value the app passed is invalid: <see cref="Field"/>, and why.</summary>
public sealed class OhrInvalidConfigException(string message, string field, string reason, Exception? inner = null) : OhrException(message, inner)
{
    public string Field { get; } = field;
    public string Reason { get; } = reason;
}

/// <summary>The hub refused a request it checks itself (a certificate: <see cref="Reason"/> says why).</summary>
public sealed class OhrRefusedException(string message, string reason, Exception? inner = null) : OhrException(message, inner)
{
    public string Reason { get; } = reason;
}

/// <summary>The Agent tried and it failed, or the Agent can't be reached.</summary>
public sealed class OhrFailedException(string message, string reason, Exception? inner = null) : OhrException(message, inner)
{
    public string Reason { get; } = reason;
}

/// <summary>Calls into the Kotlin library, with its errors as <see cref="OhrException"/>.</summary>
internal static class Native
{
    public static Exception Map(Exception e) => e switch
    {
        OhrException => e,
        Bound.OhrException.NotManaged x => new OhrNotManagedException(x.Message ?? "not managed", x),
        Bound.OhrException.NotAllowed x => new OhrNotAllowedException(x.Message ?? x.Area, x.Area, x),
        Bound.OhrException.FleetLocked x => new OhrFleetLockedException(x.Message ?? x.Key, x.Key, x),
        Bound.OhrException.Unsupported x => new OhrUnsupportedException(x.Message ?? x.Reason, x.Reason, x.NeedsAgent?.LongValue(), x),
        Bound.OhrException.InvalidConfig x => new OhrInvalidConfigException(x.Message ?? x.Field, x.Field, x.Reason, x),
        Bound.OhrException.Refused x => new OhrRefusedException(x.Message ?? x.Reason, x.Reason, x),
        Bound.OhrException.Failed x => new OhrFailedException(x.Message ?? x.Reason, x.Reason, x),
        Java.Lang.Throwable t => new OhrFailedException(t.Message ?? t.ToString(), t.Message ?? t.ToString(), t),
        _ => e,
    };

    /// <summary>A blocking library call, here.</summary>
    public static T Call<T>(Func<T> f)
    {
        try
        {
            return f();
        }
        catch (Exception e) when (Map(e) is var m && !ReferenceEquals(m, e))
        {
            throw m;
        }
    }

    public static void Call(Action f) => Call(() => { f(); return true; });

    /// <summary>A blocking library call, on the thread pool (binder calls into the Agent).</summary>
    public static Task<T> Run<T>(Func<T> f) => Task.Run(() => Call(f));

    public static Task Run(Action f) => Task.Run(() => Call(f));

    /// <summary>Runs Java's Executor jobs where they are posted (the library's binder threads).</summary>
    public static readonly IExecutor Direct = new DirectExecutor();

    private sealed class DirectExecutor : Java.Lang.Object, IExecutor
    {
        public void Execute(Java.Lang.IRunnable? command) => command?.Run();
    }

    /// <summary>The library's listener, as a C# callback.</summary>
    public sealed class Listener(Action<Java.Lang.Object?> onChange) : Java.Lang.Object, Bound.IOhrListener
    {
        public void OnChange(Java.Lang.Object? value) => onChange(value);
    }

    /// <summary>A Java value of org.json's kinds (or a boxed primitive) as JSON.</summary>
    public static JsonNode? Json(Java.Lang.Object? value) =>
        value is null ? null : JsonNode.Parse(new Org.Json.JSONArray().Put(value)!.ToString())![0]?.DeepClone();

    public static JsonObject Json(Org.Json.JSONObject value) => JsonNode.Parse(value.ToString())!.AsObject();

    /// <summary>JSON as the Java value org.json makes of it (null for null).</summary>
    public static Java.Lang.Object? Java(JsonNode? value)
    {
        if (value is null) return null;
        var v = new Org.Json.JSONTokener(value.ToJsonString()).NextValue();
        return v is null || v.Equals(Org.Json.JSONObject.Null) ? null : v;
    }

    public static Org.Json.JSONObject JavaObject(JsonObject value) => new(value.ToJsonString());
}

/// <summary>
/// A static event over one of the library's listen(executor, listener) calls: the first handler subscribes, the
/// last one removed unsubscribes. Each handler is raised on the synchronization context it subscribed from (the UI
/// thread in MAUI), else on the thread pool.
/// </summary>
internal sealed class WatchedEvent<TArgs>(string name, Func<IExecutor, Bound.IOhrListener, Java.IO.ICloseable> listen, Func<Java.Lang.Object?, TArgs> map)
    where TArgs : EventArgs
{
    private readonly object gate = new();
    private readonly List<(EventHandler<TArgs> Handler, SynchronizationContext? Context)> handlers = [];
    private Java.IO.ICloseable? subscription;
    private int generation;

    public void Add(EventHandler<TArgs>? handler)
    {
        if (handler is null) return;
        bool first;
        int mine;
        lock (gate)
        {
            handlers.Add((handler, SynchronizationContext.Current));
            first = handlers.Count == 1;
            mine = first ? ++generation : generation;
        }
        if (!first) return;
        // The first subscription is a binder call (and maybe the first bind): off the subscriber's thread.
        Task.Run(() =>
        {
            try
            {
                var s = Native.Call(() => listen(Native.Direct, new Native.Listener(Raise)));
                lock (gate)
                {
                    if (generation == mine && handlers.Count > 0) { subscription = s; return; }
                }
                s.Close();
            }
            catch (Exception e)
            {
                OhrDeviceSdk.RaiseEventFailed(name, e);
            }
        });
    }

    public void Remove(EventHandler<TArgs>? handler)
    {
        if (handler is null) return;
        Java.IO.ICloseable? end = null;
        lock (gate)
        {
            var i = handlers.FindLastIndex(h => h.Handler == handler);
            if (i < 0) return;
            handlers.RemoveAt(i);
            if (handlers.Count == 0)
            {
                generation++;
                end = subscription;
                subscription = null;
            }
        }
        if (end is not null) Task.Run(() => { try { end.Close(); } catch (Exception) { } });
    }

    private void Raise(Java.Lang.Object? value)
    {
        var args = map(value);
        (EventHandler<TArgs> Handler, SynchronizationContext? Context)[] now;
        lock (gate) now = [.. handlers];
        foreach (var (handler, context) in now)
        {
            if (context is not null) context.Post(_ => handler(null, args), null);
            else ThreadPool.QueueUserWorkItem(_ => handler(null, args));
        }
    }
}

internal static class Streams
{
    /// <summary>One subscription of its own for as long as the enumeration runs; cancelling ends it.</summary>
    public static async IAsyncEnumerable<T> Watch<T>(Func<IExecutor, Bound.IOhrListener, Java.IO.ICloseable> listen, Func<Java.Lang.Object?, T> map,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true });
        var subscription = await Native.Run(() => listen(Native.Direct, new Native.Listener(v => channel.Writer.TryWrite(map(v))))).ConfigureAwait(false);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            _ = Task.Run(() => { try { subscription.Close(); } catch (Exception) { } });
        }
    }
}
