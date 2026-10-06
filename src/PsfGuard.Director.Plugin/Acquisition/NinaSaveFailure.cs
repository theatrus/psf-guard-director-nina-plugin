using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace PsfGuard.Director.Plugin.Acquisition;

internal sealed class ImageSaveFailureException(Exception error) : IOException("NINA image save failed.", error);

internal static class NinaSaveFailure
{
    internal static IDisposable? Observe(object source, Func<object, Task> callback)
    {
        var signal = source.GetType().GetEvent("ImageSaveFailed");
        if (signal is null) return null;
        var signature = signal.EventHandlerType!.GetMethod("Invoke")!;
        var parameters = signature.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
        if (parameters.Length != 2 || signature.ReturnType != typeof(Task))
            throw new NotSupportedException("Unsupported NINA save-failure event signature.");
        var body = Expression.Invoke(Expression.Constant(callback), Expression.Convert(parameters[1], typeof(object)));
        var handler = Expression.Lambda(signal.EventHandlerType, body, parameters).Compile();
        try { signal.AddEventHandler(source, handler); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        return new Subscription(source, signal, handler);
    }

    private sealed class Subscription(object source, EventInfo signal, Delegate handler) : IDisposable
    {
        private int detached;
        public void Dispose() { if (Interlocked.Exchange(ref detached, 1) == 0) signal.RemoveEventHandler(source, handler); }
    }
}
