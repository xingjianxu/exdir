using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Exdir.Diagnostics;
using Microsoft.UI.Dispatching;

namespace Exdir.Helpers;

/// <summary>
/// 单实例闸门 + 命令行请求转发。
///
/// exdir 关窗口只是“隐藏到托盘”，进程一直驻留，所以用户第二次双击 exe 时**不能**再起一个进程
/// （那会多出一个托盘图标，两个实例还会交替覆盖同一份 settings.json 会话）。
/// 这里用两个内核对象各管一件事：
///   * **命名事件**（<c>Local\exdir.activate</c>）：<c>EventWaitHandle</c> 的 createdNew 就是
///     “我是不是第一个实例”。这一句是原子的，所以不需要额外的互斥体。
///   * **命名管道**（<c>exdir.activate</c>）：“请打开这个目录”这句话的通道 —— 第二个实例把
///     <c>CommandLine.Request</c> 写进去就结束，第一个实例收下后交给主窗口。
///     光有事件传不了参数（内核事件没有 lParam 可带），所以载荷走管道。
///
/// 名字带 <c>Local\</c> 前缀：只在当前登录会话内可见（多用户 / 多会话各自一个 exdir，互不打扰）。
/// </summary>
internal static class SingleInstance
{
    private const string EventName = @"Local\exdir.activate";
    private const string PipeName = "exdir.activate";

    /// <summary>第二个实例最多等这么久：第一个实例可能正在启动，管道还没建出来。</summary>
    private const int SendTimeoutMs = 5000;

    private static readonly object Gate = new();

    private static EventWaitHandle? _event;
    private static NamedPipeServerStream? _pipe;
    private static DispatcherQueue? _dispatcher;
    private static Action<string>? _onActivated;
    private static string? _pendingRequest;

    /// <summary>
    /// 尝试成为主实例。<paramref name="request" /> 是要打开的路径（空串 = 只唤回窗口，
    /// 见 <see cref="CommandLine" />）。
    /// 返回 <c>false</c> 表示已经有一个 exdir 在运行（请求已转交给它），调用方应该直接结束进程。
    /// </summary>
    public static bool TryClaim(string request)
    {
        try
        {
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out var createdNew);

            if (!createdNew)
            {
                // 已有实例在跑：把请求写进它的管道，然后本进程就没事了
                _event.Dispose();
                _event = null;

                var delivered = SendRequest(request);
                Log.Write(delivered
                    ? $"已有实例在运行：已把请求（{Describe(request)}）转交给它，本进程退出"
                    : $"已有实例在运行：但请求（{Describe(request)}）没能转交（管道连不上，见上一行日志），本进程退出");
                return false;
            }

            // 先在本线程把管道建出来、再交给后台线程收：这样第二个实例连上来时管道名字一定已经存在
            _pipe = CreatePipe();

            var server = new Thread(RunPipeServer)
            {
                IsBackground = true,
                Name = "exdir-单实例通道",
            };
            server.Start();

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
    /// 主实例：告诉闸门把请求投递到哪个 UI 线程。
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

    /// <summary>主实例：注册收到请求时执行的动作（把路径交给主窗口 / 唤回窗口）。</summary>
    public static void Listen(Action<string> onActivated)
    {
        lock (Gate)
        {
            _onActivated = onActivated;
        }

        Flush();
    }

    // ------------------------------------------------------------------ 服务端（第一个实例）

    /// <summary>
    /// 收请求的后台线程：一个常驻的服务实例反复 <c>WaitForConnection</c> → 读一条 → <c>Disconnect</c>。
    /// 复用同一个实例（而不是每条请求新建一个）是有意的：实例一直处在等待连接的状态，
    /// 管道名字就一直存在，第二个实例不会因为“名字暂时不在”而连不上。
    /// </summary>
    private static void RunPipeServer()
    {
        while (true)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                lock (Gate)
                {
                    pipe = _pipe ??= CreatePipe();
                }

                pipe.WaitForConnection();

                var request = ReadRequest(pipe);
                Log.Write($"收到其它实例的请求：{Describe(request)}");

                lock (Gate)
                {
                    _pendingRequest = request;
                }

                Flush();

                pipe.Disconnect();
            }
            catch (Exception ex)
            {
                // 连不上 / 读到一半对面挂了：丢掉这一个实例重建一个继续等（这条线程绝不能因为异常退出，
                // 否则第二个实例永远只能唤起前台、再也传不进路径）
                Log.Exception("单实例通道", ex);

                if (pipe is not null)
                {
                    lock (Gate)
                    {
                        if (ReferenceEquals(_pipe, pipe))
                        {
                            _pipe = null;
                        }
                    }

                    try
                    {
                        pipe.Dispose();
                    }
                    catch (Exception)
                    {
                        // 关不上也没关系，下面会重建一个新的
                    }
                }

                Thread.Sleep(500);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
        => new(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);

    /// <summary>一条请求最多收这么多字节（路径长度上限就是 32767 个字符，UTF-8 下最多 3~4 字节/字符）。</summary>
    private const int MaxRequestBytes = 32 * 1024;

    /// <summary>一个连接一条请求：客户端写完就关，所以读到 EOF 就是全文。</summary>
    private static string ReadRequest(NamedPipeServerStream pipe)
    {
        // 不用 StreamReader：直接读固定大小的缓冲区，本地随便哪个进程往里灌数据最多也就占这么多内存
        var buffer = new byte[MaxRequestBytes];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = pipe.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total).Trim();
    }

    // ------------------------------------------------------------------ 客户端（第二个实例）

    /// <summary>
    /// 把请求写进主实例的管道。
    /// 连不上就退一步重试：第一个实例可能还在启动（管道还没建出来）、也可能正在处理上一条请求
    /// （服务实例一时半会儿不可用）。整段最多花 <see cref="SendTimeoutMs" /> 毫秒。
    /// </summary>
    private static bool SendRequest(string request)
    {
        var deadline = Environment.TickCount64 + SendTimeoutMs;
        Exception? lastError = null;

        while (Environment.TickCount64 < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(500);

                // 直接写字节（不用 StreamWriter + UTF8Encoding）：
                // 公开的 UTF8Encoding 类型在裁剪版里可能被整条裁掉，一用就是 JIT 期的
                // FileNotFoundException（见 Diagnostics/Log.cs 里的同一个坑）。
                var payload = Encoding.UTF8.GetBytes(request);
                client.Write(payload, 0, payload.Length);
                client.Flush();
                return true;
            }
            catch (Exception ex)
            {
                // 失败是正常路径（正在启动 / 正忙），所以只在最后彻底失败时记一笔，不逐次刷日志
                lastError = ex;
                Thread.Sleep(150);
            }
        }

        if (lastError is not null)
        {
            Log.Exception("转交命令行请求", lastError);
        }

        return false;
    }

    // ------------------------------------------------------------------ 投递

    /// <summary>
    /// 请求到了、UI 线程与回调也都就绪时才真正投递。
    /// 三者到齐的顺序不固定（用户在启动过程中又敲了一次 exdir 就赶不上），所以先把请求存起来，
    /// 等最后到齐的那一方来补投。
    /// </summary>
    private static void Flush()
    {
        DispatcherQueue? dispatcher;
        Action<string>? action;
        string request;

        lock (Gate)
        {
            if (_pendingRequest is null || _dispatcher is null || _onActivated is null)
            {
                return;
            }

            request = _pendingRequest;
            _pendingRequest = null;
            dispatcher = _dispatcher;
            action = _onActivated;
        }

        dispatcher.TryEnqueue(() =>
        {
            try
            {
                action(request);
            }
            catch (Exception ex)
            {
                Log.Exception("处理其它实例的请求", ex);
            }
        });
    }

    private static string Describe(string request)
        => string.IsNullOrEmpty(request) ? "唤回主窗口" : $"打开 {request}";
}
