using System.Windows;
using System.Windows.Threading;

namespace VisionMotionAlignment.Infrastructure;

/// <summary>
/// UI 线程调度助手。
/// <para>线程安全 T1/T5 的核心基础设施：所有跨线程（相机回调、力值轮询、运动控制卡状态）的 UI 属性变更
/// 必须经此 helper 切回 UI 线程，避免 <c>[ObservableProperty]</c> 非线程安全导致的异常或 UI 错乱。</para>
/// <para>契约：调用返回时，action 一定在 UI 线程上执行（无论调用方当前是否在 UI 线程）。
/// 若已在 UI 线程则同步执行；否则异步切回 UI 线程。</para>
/// </summary>
public static class DispatcherHelper
{
    /// <summary>当前 UI Dispatcher（主线程）。</summary>
    public static Dispatcher Dispatcher =>
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <summary>当前调用是否在 UI 线程上。</summary>
    public static bool CheckAccess() => Dispatcher.CheckAccess();

    /// <summary>
    /// 在 UI 线程上执行 <paramref name="action"/>。
    /// 若已在 UI 线程则同步执行；否则异步切回 UI 线程。
    /// </summary>
    /// <param name="action">要执行的操作。</param>
    /// <returns>表示执行的任务（已在 UI 线程时返回 <see cref="Task.CompletedTask"/>）。</returns>
    public static Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.InvokeAsync(action).Task;
    }

    /// <summary>
    /// 在 UI 线程上执行 <paramref name="func"/> 并返回结果。
    /// 若已在 UI 线程则同步执行；否则异步切回 UI 线程。
    /// </summary>
    /// <typeparam name="T">返回类型。</typeparam>
    /// <param name="func">要执行的函数。</param>
    /// <returns>函数返回值（已在 UI 线程时用 <see cref="Task.FromResult"/> 包装）。</returns>
    public static Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (CheckAccess())
        {
            return Task.FromResult(func());
        }
        return Dispatcher.InvokeAsync(func).Task;
    }

    /// <summary>
    /// 在 UI 线程上执行 <paramref name="action"/>（异步重载，支持 await 内部操作）。
    /// 若已在 UI 线程则直接返回 action 的任务；否则异步切回 UI 线程。
    /// </summary>
    /// <param name="action">要执行的异步操作。</param>
    /// <returns>表示异步执行的任务。</returns>
    public static Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            return action();
        }
        return Dispatcher.InvokeAsync(action).Task.Unwrap();
    }
}
