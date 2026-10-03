using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Exdir.Diagnostics;
using Exdir.Helpers;
using Exdir.Models;

namespace Exdir.Services.Remote;

/// <summary>
/// 远程连接的池子：每个“连接身份”（见 <see cref="RemoteLocation.ConnectionKey" />）留一条连接，
/// 反复浏览同一个位置不用每次重新握手（SFTP 握手 + 认证要好几百毫秒）。
///
/// <para>
/// 两条规则：
/// <list type="bullet">
/// <item>同一条连接用信号量串起来 —— SSH.NET / FluentFTP 都要求同一时刻只有一个操作；</item>
/// <item>操作失败就把连接丢掉（<see cref="RemoteSession.Reset" />）并从池里摘掉，
///       下一次自动重连。否则断网 / 服务器踢人之后用户得重启程序才能恢复。</item>
/// </list>
/// </para>
///
/// <para>
/// 空闲超过 <see cref="IdleTimeout" /> 的连接会被回收（不能一直挂着：SFTP 服务器通常有空闲超时，
/// 而且用户可能换了网络）。设置里改了连接配置时由 <see cref="ResetAll" /> 全部丢掉。
/// </para>
/// </summary>
internal sealed partial class RemoteSessionPool : IDisposable
{
    /// <summary>空闲多久回收（用户切走了就让它自己断，别一直占着服务器的会话）。</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>池子里最多留几条连接（同时开很多位置时别把服务器的连接数占满）。</summary>
    private const int MaxSessions = 8;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    /// <summary>按位置取一条连接，串行执行 <paramref name="action" />。</summary>
    public Task UseAsync(
        RemoteLocation location,
        Func<RemoteSession, CancellationToken, Task> action,
        CancellationToken cancellationToken)
        => UseAsync<bool>(
            location,
            async (session, ct) =>
            {
                await action(session, ct).ConfigureAwait(true);
                return true;
            },
            cancellationToken);

    /// <summary>按位置取一条连接，串行执行 <paramref name="action" />。</summary>
    public async Task<T> UseAsync<T>(
        RemoteLocation location,
        Func<RemoteSession, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var entry = Acquire(location);

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            entry.Touch();
            await entry.Session.ConnectAsync(cancellationToken).ConfigureAwait(true);

            var result = await action(entry.Session, cancellationToken).ConfigureAwait(true);
            entry.Touch();
            return result;
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消不算“连接坏了”，连接留着下次接着用
            throw;
        }
        catch (Exception)
        {
            // 连接状态已经不可信：丢掉它，下一次调用会重连（见类注释）
            Discard(entry);
            throw;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>设置里改了连接配置（增删改）之后调用：已有的连接全部作废。</summary>
    public void ResetAll()
    {
        List<Entry> entries;
        lock (_gate)
        {
            entries = new List<Entry>(_sessions.Values);
            _sessions.Clear();
        }

        foreach (var entry in entries)
        {
            entry.Session.Reset();
        }
    }

    public void Dispose() => ResetAll();

    private Entry Acquire(RemoteLocation location)
    {
        var key = location.ConnectionKey;
        var now = DateTime.UtcNow;

        List<Entry> evicted = new();

        lock (_gate)
        {
            // 空闲太久的先收掉
            foreach (var pair in new List<KeyValuePair<string, Entry>>(_sessions))
            {
                if (now - pair.Value.LastUsedUtc > IdleTimeout)
                {
                    _sessions.Remove(pair.Key);
                    evicted.Add(pair.Value);
                }
            }

            if (_sessions.TryGetValue(key, out var existing))
            {
                return existing;
            }

            // 超过上限时按最久没用过的踢一个（正在被用的那条也会被踢，但它的调用还在跑，
            // 只是从池里摘掉 —— 那条连接跑完就断，不影响正在进行的操作）
            if (_sessions.Count >= MaxSessions)
            {
                var oldestKey = default(string);
                var oldest = DateTime.MaxValue;

                foreach (var pair in _sessions)
                {
                    if (pair.Value.LastUsedUtc < oldest)
                    {
                        oldest = pair.Value.LastUsedUtc;
                        oldestKey = pair.Key;
                    }
                }

                if (oldestKey is not null && _sessions.Remove(oldestKey, out var evictedEntry))
                {
                    evicted.Add(evictedEntry);
                }
            }

            var created = new Entry(key, Create(location));
            _sessions[key] = created;

            foreach (var entry in evicted)
            {
                entry.Session.Reset();
            }

            return created;
        }
    }

    private void Discard(Entry entry)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                _sessions.Remove(entry.Key);
            }
        }

        entry.Session.Reset();
    }

    private static RemoteSession Create(RemoteLocation location)
    {
        // 凭据在这一步解密：解不开（换了用户 / 换了机器）就当没填，并给出一条明确的日志
        var password = SecretProtector.Unprotect(location.ProtectedPassword);
        if (password is null)
        {
            password = string.Empty;
            Log.Write($"远程位置“{location.DisplayName}”的密码解不开（换了 Windows 用户或换了机器），按空密码处理");
        }

        var passphrase = SecretProtector.Unprotect(location.ProtectedPassphrase);
        if (passphrase is null)
        {
            passphrase = string.Empty;
            Log.Write($"远程位置“{location.DisplayName}”的私钥口令解不开，按无口令处理");
        }

        return location.Protocol == RemoteProtocol.Sftp
            ? new SftpSession(location, password, passphrase)
            : new FtpSession(location, password);
    }

    private sealed class Entry
    {
        public Entry(string key, RemoteSession session)
        {
            Key = key;
            Session = session;
        }

        public string Key { get; }

        public RemoteSession Session { get; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

        public void Touch() => LastUsedUtc = DateTime.UtcNow;
    }
}
