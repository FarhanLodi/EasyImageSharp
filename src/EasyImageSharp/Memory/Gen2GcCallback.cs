using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;

namespace EasyImageSharp.Memory;

/// <summary>
/// Runs a callback after (roughly) every full garbage collection, for as long as its target is alive. The
/// object is unreachable from the moment it is created, so the collector finalizes it; the finalizer runs the
/// callback and re-registers itself. Once the object has been promoted to generation 2 - after the first one or
/// two collections - only a full collection finalizes it again.
/// </summary>
/// <remarks>
/// The target is held through a weak handle, so registering a callback never keeps the target alive. The
/// callback runs on the finalizer thread and must be quick and must not throw.
/// </remarks>
internal sealed class Gen2GcCallback : CriticalFinalizerObject
{
    private readonly Func<object, bool> callback;
    private GCHandle target;

    private Gen2GcCallback(Func<object, bool> callback, object target)
    {
        this.callback = callback;
        this.target = GCHandle.Alloc(target, GCHandleType.Weak);
    }

    /// <summary>
    /// Schedules <paramref name="callback"/> to run with <paramref name="target"/> after full collections until
    /// the target has been collected or the callback returns <see langword="false"/>.
    /// </summary>
    public static void Register(Func<object, bool> callback, object target) => _ = new Gen2GcCallback(callback, target);

    ~Gen2GcCallback()
    {
        object? current = this.target.Target;
        if (current is null)
        {
            this.target.Free();
            return;
        }

        bool keep;
        try
        {
            keep = this.callback(current);
        }
        catch (Exception)
        {
            // An exception escaping a finalizer tears the process down; trimming a pool is never worth that.
            keep = true;
        }

        if (keep)
        {
            GC.ReRegisterForFinalize(this);
        }
        else
        {
            this.target.Free();
        }
    }
}
