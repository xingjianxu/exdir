using System;
using System.Collections.Generic;
using System.Linq;
using Exdir.Diagnostics;
using Exdir.Services.Native;

namespace Exdir.Services;

/// <inheritdoc cref="IClipboardService" />
public sealed class ClipboardService : IClipboardService
{
    public bool SetFiles(IReadOnlyList<string> paths, bool move)
    {
        var distinct = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count == 0)
        {
            return false;
        }

        try
        {
            var ok = ClipboardInterop.SetFileDrop(distinct, move);
            Log.Write($"剪贴板：{(move ? "剪切" : "复制")} {distinct.Count} 项{(ok ? string.Empty : "（写入失败）")}");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Exception("写入剪贴板", ex);
            return false;
        }
    }

    public ClipboardFiles? GetFiles()
    {
        try
        {
            var snapshot = ClipboardInterop.GetFileDrop();
            if (snapshot is not { } value || value.Paths.Count == 0)
            {
                return null;
            }

            return new ClipboardFiles(value.Paths, value.Move);
        }
        catch (Exception ex)
        {
            Log.Exception("读取剪贴板", ex);
            return null;
        }
    }

    public bool HasFiles()
    {
        try
        {
            return ClipboardInterop.HasFileDrop();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Clear()
    {
        try
        {
            ClipboardInterop.Clear();
        }
        catch (Exception ex)
        {
            Log.Exception("清空剪贴板", ex);
        }
    }
}
