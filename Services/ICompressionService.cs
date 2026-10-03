using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Exdir.Services;

/// <summary>
/// 把选中的文件 / 目录打成一个 zip 压缩包（内置右键菜单「压缩」用）。
///
/// <para>
/// 与 <see cref="IArchiveService" />（只读浏览压缩包）分开：那一块是“读别人的压缩包”，
/// 走随程序分发的原生 7z.dll；这里是“造一个自己的 zip”，走 BCL 的
/// <c>System.IO.Compression</c>，两条路互不干扰（也避免把“只读”那块改成可写）。
/// </para>
/// <para>
/// 生成的压缩包默认落在用户的「下载」文件夹，也可以由设置里的
/// <see cref="Exdir.Models.AppSettings.CompressionOutputDirectory" /> 改成别的目录。
/// </para>
/// </summary>
public interface ICompressionService
{
    /// <summary>
    /// 把 <paramref name="sourcePaths" />（真实路径：文件或目录，目录含整棵子树）打包成一个 zip，
    /// 写进 <paramref name="outputDirectory" />，返回写出的 zip 的完整路径。
    ///
    /// <list type="bullet">
    /// <item>压缩包名由 <paramref name="baseName" /> 派生（调用方按选中项算：单个条目用条目名、
    ///       多个用当前目录名）；同名文件已存在时依次加 <c>(2)(3)…</c>，不覆盖已有文件；</item>
    /// <item>目录会整棵打包（含空目录），符号链接 / 联接点不跟进去（会绕圈、也可能指到目录外）；</item>
    /// <item>失败时把写了一半的 zip 删掉再抛异常（不让下载目录里留个坏包）。</item>
    /// </list>
    /// </summary>
    Task<string> CompressAsync(
        IReadOnlyList<string> sourcePaths,
        string outputDirectory,
        string baseName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 一个 zip 真的写完（文件已落盘）之后触发，参数是 zip 的完整路径。
    /// 宿主据此刷新正开在压缩包所在目录里的标签页 —— 与解压完成后的处理一致。
    /// </summary>
    event EventHandler<string>? ArchiveCreated;
}
