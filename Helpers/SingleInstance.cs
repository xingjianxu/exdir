using System;
using System.Threading;
using Exdir.Diagnostics;
using Microsoft.UI.Dispatching;

namespace Exdir.Helpers;

/// <summary>
/// 单实例闸门。
///
/// exdir 关窗口只是“隐藏到托盘”，进程一直驻留，所以用户第二次双击 exe 时**不能**再起一个进程
/// （那会多出一个托盘图标，两个实例还会交替覆盖同一份 settings.json 会话）。
/// 这里用**一个命名内核事件**同时承担两件事：
///   * 事件对象是否存在（<c>EventWaitHandle</c> 的 createdNew）就是“我是不是第一个实例”；
///     不需要再单独拿一个命名互斥体（多一个内核对象、多一处忘 ReleaseMutex 的机会）。
///   * 第一个实例起一条守护线程等这个事件，第二个实例置位它就等于“把主窗口叫出来”。
///
/// 名字带 <c>Local\</c> 前缀：只在当前登录会话内可见（多用户/多会话各自一个 exdir，互不打扰）。
/// </summary>
internal static class SingleInstance
{
    private const string EventName = @"Local\exdir.activate";

    private static readonly object Gate = new();

    private static EventWaitHandle? _event;
    private static DispatcherQueue? _dispatcher;
    private static Action? _onActivated;
    private static bool _pending;

    /// <summary>
    /// 尝试成为主实例。返回 <c>false</c> 表示已经有一个 exdir 在运行（多半正驻留在托盘里），
    /// 此时已经把“显示主窗口”的请求发给它了，调用方应该直接结束进程。
    /// </summary>
    public static bool TryClaim()
    {
        try
        {
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out var createdNew);

            if (!createdNew)
            {
                // 置位即“请把主窗口显示出来”；它是自动重置事件，最多唤醒一个等待者
                _event.Set();
                _event.Dispose();
                _event = null;

                Log.Write("已有实例在运行：已请求它唤回主窗口，本进程退出");
                return false;
            }

            // 守护线程：即使一直没人再来点 exe，也不能影响进程退出
            var waiter = new Thread(WaitForActivation)
            {
                IsBackground = true,
                Name = "exdir-单实例等待",
            };
            waiter.Start();

            Log.Write("单实例闸门已就位");
            return true;
        }
        catch (Exception ex)
        {
            // 内核对象都建不出来（极少见）时退回普通“多实例”启动：不能因为闸门故障拦住用户
            Log.Exception("单实例闸门", ex);
            return true;
        }
    }

    /// <summary>
    /// 主实例：告诉闸门把“显示主窗口”投递到哪个 UI 线程。
    /// 必须在 <c>Application.Start</c> 之后调用（那之前拿不到 XAML 的 DispatcherQueue）。
    /// </summary>
    public static void BindDispatcher(DispatcherQueue dispatcher)
    {
        lock (Gate)
        {
            _dispatcher = dispatcher;
        }

        Flush();
    }

    /// <summary>主实例：注册被唤醒时执行的动作（显示主窗口）。</summary>
    public static void Listen(Action onActivated)
    {
        lock (Gate)
        {
            _onActivated = onActivated;
        }

        Flush();
    }

    private static void WaitForActivation()
    {
        var handle = _event;
        if (handle is null)
        {
            return;
        }

        while (true)
        {
            handle.WaitOne();

            lock (Gate)
            {
                _pending = true;
            }

            Flush();
        }
    }

    /// <summary>
    /// 信号到了、UI 线程与回调也都就绪时才真正投递。
    /// 三者到齐的顺序不固定（用户在启动过程中再点一次 exe 就赶不上），所以用 <see cref="_pending" />
    /// 记一笔，等最后到齐的那一方来补投。
    /// </summary>
    private static void Flush()
    {
        DispatcherQueue? dispatcher;
        Action? action;

        lock (Gate)
        {
            if (!_pending || _dispatcher is null || _onActivated is null)
            {
                return;
            }

            _pending = false;
            dispatcher = _dispatcher;
            action = _onActivated;
        }

        Log.Write("收到其它实例的请求：唤回主窗口");
        dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Exception("唤回主窗口", ex);
            }
        });
    }
}
