using System.Runtime.ExceptionServices;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>Deeply nested HTML for the nesting guard, run on a thread with a small stack: a recursive walk over it would
/// overflow that stack (and end the test process – a stack overflow cannot be caught).</summary>
internal static class SmallStack
{
    /// <summary>The stack of <see cref="Run{T}"/>; far smaller than a thread-pool thread's.</summary>
    public const int StackBytes = 256 * 1024;

    /// <summary><paramref name="depth"/> nested <c>div</c> elements around Polish text.</summary>
    public static string Nested(int depth) => string.Concat(Enumerable.Repeat("<div>", depth)) + "Zażółć gęślą jaźń";

    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            },
            StackBytes);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
