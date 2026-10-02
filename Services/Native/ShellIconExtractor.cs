using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Exdir.Services.Native;

/// <summary>
/// 从一个路径取“系统外壳图标”的像素（资源管理器列里显示的那一套）。
///
/// 为什么不直接用 <c>ExtractIconEx</c>：那只认 exe/dll/ico 里的图标资源，拿不到
/// 文件夹、快捷方式（含它指向的目标图标 + 小箭头覆盖层）、以及注册表里登记的关联图标。
/// 所以主路径是 <c>SHGetFileInfo</c> + <c>SHGFI_ICON</c>，语义与资源管理器完全一致，
/// 只有当外壳给不出 HICON 时才退回读文件自身的图标资源（见 <see cref="Extract"/>）。
///
/// 要的是 <b>小图标</b>：列表里的图标按 16 DIP 显示，而 <c>SM_CXSMICON</c> 正好就是
/// “16 DIP 在当前 DPI 下的物理像素数”（100% 是 16、150% 是 24、200% 是 32），所以任何缩放下都是 1:1，
/// 既不会糊也不会白拿一张 4 倍大的位图。
///
/// 拿到的 HICON 用 <c>GetDIBits</c> 读成 32bpp BGRA：外壳给的图标本来就是 32bpp 的 DIB section，
/// 直接读能原样保留 alpha 通道（<c>DrawIconEx</c> 画到 DC 上的路子在带 alpha 的图标上会丢 alpha）。
/// 老式 24bpp / 单色图标的 alpha 全是 0，这时退回用 AND 掩码位算透明度。
/// </summary>
internal static class ShellIconExtractor
{
    /// <summary>读出来的图标：像素是预乘 alpha 的 BGRA，自上而下、逐行紧密排列。</summary>
    public readonly record struct Result(int Width, int Height, byte[] Pixels, int ContentHash);

    // SHGetFileInfo 的 uFlags / dwFileAttributes
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiSmallIcon = 0x00000001;
    private const uint ShgfiLinkOverlay = 0x00008000;
    private const uint ShgfiUseFileAttributes = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;

    private const uint DibRgbColors = 0;

    /// <summary>
    /// 提取外壳图标时的串行闸门。
    ///
    /// 外壳的“系统图标列表”是<b>进程级共享</b>的：多个线程同时用 <c>SHGetFileInfo</c> 往里添图标时，
    /// 实测偶尔会出现“返回了图标索引，却没给出 HICON”（在一堆行并行提取时，第一个碰上的那个
    /// 文件会命中，例如 .txt / calc.exe），串行化之后就没再出现过。
    /// 单次提取本来就只有零点几毫秒，串行不影响观感（提取本来就在后台线程）。
    /// </summary>
    private static readonly object Gate = new();

    /// <summary>
    /// 取图标；没有图标（或读不出来）返回 <c>null</c>，<paramref name="failure"/> 里会写明原因（便于排查）。
    /// </summary>
    /// <param name="path">条目路径。</param>
    /// <param name="useFileAttributes">
    /// true = 不去看文件本身，只按扩展名 + <c>FILE_ATTRIBUTE_NORMAL</c> 推断图标（快，且文件不存在也能用）。
    /// </param>
    /// <param name="linkOverlay">是否叠加“快捷方式小箭头”（.lnk / .url 用）。</param>
    /// <param name="directoryAttributes">
    /// <paramref name="useFileAttributes" /> 为 true 时用“目录属性”而不是“普通文件属性”询问壳。
    /// 压缩包里的目录在磁盘上并不存在，只能这样拿到通用文件夹图标。
    /// </param>
    /// <param name="failure">失败原因（成功时为空串）。</param>
    public static Result? Extract(string path, bool useFileAttributes, bool linkOverlay, out string failure, bool directoryAttributes = false)
    {
        failure = string.Empty;

        if (string.IsNullOrEmpty(path))
        {
            failure = "路径为空";
            return null;
        }

        var flags = ShgfiIcon | ShgfiSmallIcon;
        if (linkOverlay)
        {
            flags |= ShgfiLinkOverlay;
        }

        // dwFileAttributes 只在 SHGFI_USEFILEATTRIBUTES 下才有意义（那时外壳不去看文件本身）
        var attributes = 0u;
        if (useFileAttributes)
        {
            flags |= ShgfiUseFileAttributes;
            attributes = directoryAttributes ? FileAttributeDirectory : FileAttributeNormal;
        }

        IntPtr shellIcon;
        IntPtr returned;
        SHFILEINFO fileInfo;

        // 只有“问外壳要 HICON”这一步需要串行（外壳的系统图标列表是进程级共享的，
        // 并发往里添图标时会偶发“只给索引不给 HICON”）；读像素各用各的位图，可以并行
        lock (Gate)
        {
            shellIcon = GetShellIcon(path, attributes, flags, out returned, out fileInfo);

            // 没拿到就再试一次：上面那种“只给索引不给 HICON”的情况重试一次几乎总能成功
            if (shellIcon == IntPtr.Zero)
            {
                shellIcon = GetShellIcon(path, attributes, flags, out returned, out fileInfo);
            }
        }

        if (shellIcon != IntPtr.Zero)
        {
            try
            {
                return FromIcon(shellIcon, out failure);
            }
            finally
            {
                DestroyIcon(shellIcon);
            }
        }

        // 少数情况下外壳就是给不出 HICON：例如 calc.exe —— 这个名字撞上了 Windows 的
        // “应用执行别名”，外壳转去从 AppX 包里取图标，非打包进程里这一步会失败
        // （同一个文件改个名字就正常）。此时直接读文件自身的图标资源。
        var fallback = ExtractFromResources(path);
        if (fallback != IntPtr.Zero)
        {
            try
            {
                failure = string.Empty;
                return FromIcon(fallback, out failure);
            }
            finally
            {
                DestroyIcon(fallback);
            }
        }

        failure = returned == IntPtr.Zero
            ? $"SHGetFileInfo 失败（flags=0x{flags:X} attrs=0x{attributes:X}）"
            : $"SHGetFileInfo 没有返回图标（iIcon={fileInfo.iIcon} flags=0x{flags:X} attrs=0x{attributes:X}）";
        return null;
    }

    /// <summary>问外壳要一张 HICON（调用方负责 <c>DestroyIcon</c>）；拿不到返回 0。</summary>
    private static IntPtr GetShellIcon(
        string path,
        uint attributes,
        uint flags,
        out IntPtr returned,
        out SHFILEINFO fileInfo)
    {
        fileInfo = default;
        returned = SHGetFileInfo(path, attributes, ref fileInfo, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        return fileInfo.hIcon;
    }

    /// <summary>从文件自身的图标资源里取第一个图标（不是文件、没有图标资源都返回 0）。</summary>
    private static IntPtr ExtractFromResources(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return IntPtr.Zero;
            }

            if (ExtractIconEx(path, 0, out var large, out var small, 1) == 0)
            {
                return IntPtr.Zero;
            }

            // 优先小图标：与 SHGetFileInfo 的小图标同尺寸（SM_CXSMICON），免得同一个列表里
            // 有的图标是 32px、有的是 64px（虽然视觉上都正常，但没必要多占内存）
            if (small != IntPtr.Zero)
            {
                if (large != IntPtr.Zero)
                {
                    DestroyIcon(large);
                }

                return small;
            }

            return large;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static Result? FromIcon(IntPtr hIcon, out string failure)
    {
        failure = string.Empty;

        if (!GetIconInfo(hIcon, out var icon))
        {
            failure = "GetIconInfo 失败";
            return null;
        }

        var dc = CreateCompatibleDC(IntPtr.Zero);

        try
        {
            // 颜色位图缺失时（极老的单色图标）hbmMask 里其实是 AND + OR 两张位图，高度要减半
            var measure = icon.hbmColor != IntPtr.Zero ? icon.hbmColor : icon.hbmMask;
            if (measure == IntPtr.Zero)
            {
                failure = "图标没有位图";
                return null;
            }

            var bitmap = default(BITMAP);
            if (GetObject(measure, Marshal.SizeOf<BITMAP>(), ref bitmap) == 0)
            {
                failure = "GetObject 失败";
                return null;
            }

            var width = bitmap.bmWidth;
            var height = icon.hbmColor != IntPtr.Zero ? bitmap.bmHeight : bitmap.bmHeight / 2;

            // 保护上限：正常外壳图标不超过 256（SHIL_JUMBO），异常值直接放弃
            if (dc == IntPtr.Zero || width <= 0 || height <= 0 || width > 256 || height > 256)
            {
                failure = $"位图尺寸异常（{width}×{height}，{bitmap.bmBitsPixel}bpp，hbmColor={icon.hbmColor != IntPtr.Zero}）";
                return null;
            }

            var pixels = ReadColor(dc, icon.hbmColor, width, height);
            if (pixels is null)
            {
                failure = $"GetDIBits 失败（{width}×{height}，{bitmap.bmBitsPixel}bpp）";
                return null;
            }

            if (!HasAlpha(pixels) && !ApplyMaskAlpha(dc, icon.hbmMask, width, height, pixels))
            {
                // 既没有 alpha 又读不到掩码：宁可不透明可见，也不要整块隐形
                SetOpaque(pixels);
            }

            Premultiply(pixels);
            return new Result(width, height, pixels, ComputeHash(pixels, width, height));
        }
        finally
        {
            if (dc != IntPtr.Zero)
            {
                DeleteDC(dc);
            }

            // GetIconInfo 交出来的两个位图由调用方释放
            if (icon.hbmColor != IntPtr.Zero)
            {
                DeleteObject(icon.hbmColor);
            }

            if (icon.hbmMask != IntPtr.Zero)
            {
                DeleteObject(icon.hbmMask);
            }
        }
    }

    /// <summary>读颜色位图；返回自上而下的 BGRA。</summary>
    private static byte[]? ReadColor(IntPtr dc, IntPtr hbmColor, int width, int height)
    {
        if (hbmColor == IntPtr.Zero)
        {
            return null;
        }

        // 按“自下而上”请求（DIB 的原生方向），再自己翻过来：
        // 不依赖 GDI 对 DIB section 做“换方向”的转换能力，行为最可预期
        var bottomUp = new byte[width * height * 4];
        var header = CreateHeader(width, height, 32);

        return GetDIBits(dc, hbmColor, 0, (uint)height, bottomUp, ref header, DibRgbColors) == 0
            ? null
            : FlipToTopDown(bottomUp, width, height);
    }

    /// <summary>用 AND 掩码位补 alpha（掩码位为 1 表示该像素透明）。返回是否读到掩码。</summary>
    private static bool ApplyMaskAlpha(IntPtr dc, IntPtr hbmMask, int width, int height, byte[] pixels)
    {
        if (hbmMask == IntPtr.Zero)
        {
            return false;
        }

        var stride = ((width + 31) / 32) * 4;
        var bottomUp = new byte[stride * height];

        // 1bpp 的 DIB 要留出 2 项调色板的位置，否则 GetDIBits 会把它们写到栈外面
        var info = new BITMAPINFO_MONO
        {
            Header = CreateHeader(width, height, 1),
            Colors = new uint[2],
        };

        if (GetDIBits(dc, hbmMask, 0, (uint)height, bottomUp, ref info, DibRgbColors) == 0)
        {
            return false;
        }

        for (var y = 0; y < height; y++)
        {
            // 自下而上 → 自上而下
            var row = bottomUp.AsSpan((height - 1 - y) * stride, stride);
            var offset = y * width * 4;

            for (var x = 0; x < width; x++)
            {
                var transparent = ((row[x / 8] >> (7 - (x % 8))) & 1) == 1;
                pixels[offset + (x * 4) + 3] = transparent ? (byte)0 : (byte)255;
            }
        }

        return true;
    }

    private static BITMAPINFOHEADER CreateHeader(int width, int height, ushort bitsPerPixel) => new()
    {
        Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
        Width = width,
        Height = height,
        Planes = 1,
        BitCount = bitsPerPixel,
        Compression = 0,
        SizeImage = (uint)(width * height * (bitsPerPixel / 8)),
    };

    private static byte[] FlipToTopDown(byte[] bottomUp, int width, int height)
    {
        var stride = width * 4;
        var topDown = new byte[bottomUp.Length];

        for (var y = 0; y < height; y++)
        {
            Array.Copy(bottomUp, (height - 1 - y) * stride, topDown, y * stride, stride);
        }

        return topDown;
    }

    private static bool HasAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void SetOpaque(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }
    }

    /// <summary>BGRA 直通 alpha 要转成预乘（WriteableBitmap 的 PixelBuffer 是预乘格式），否则边缘会发白。</summary>
    private static void Premultiply(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 255)
            {
                continue;
            }

            if (alpha == 0)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                continue;
            }

            pixels[i] = (byte)(pixels[i] * alpha / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
        }
    }

    /// <summary>FNV-1a：用来判断两个图标内容是否完全一样（列表里成百上千个普通文件夹只留一份）。</summary>
    private static int ComputeHash(byte[] pixels, int width, int height)
    {
        var hash = unchecked((uint)2166136261);
        hash = (hash ^ (uint)width) * 16777619;
        hash = (hash ^ (uint)height) * 16777619;

        foreach (var b in pixels)
        {
            hash = (hash ^ b) * 16777619;
        }

        return unchecked((int)hash);
    }

    // ------------------------------------------------------------------ 互操作

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    /// <summary>1bpp 的 GetDIBits 会回写调色板，结构体里必须留出位置。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO_MONO
    {
        public BITMAPINFOHEADER Header;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public uint[] Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool IsIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>从文件自身的图标资源里取图标（大小是系统大/小图标尺寸，由 SM_CXICON / SM_CXSMICON 决定）。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", CharSet = CharSet.Unicode)]
    private static extern int GetObject(IntPtr hObject, int cchBuffer, ref BITMAP buffer);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint start,
        uint cLines,
        [Out] byte[] lpvBits,
        ref BITMAPINFOHEADER lpbmi,
        uint usage);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint start,
        uint cLines,
        [Out] byte[] lpvBits,
        ref BITMAPINFO_MONO lpbmi,
        uint usage);
}
