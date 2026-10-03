using System;
using System.Text;
using Exdir.Diagnostics;
using Exdir.Services.Native;

namespace Exdir.Helpers;

/// <summary>
/// 把敏感字符串（远程位置的密码 / 私钥口令）加密成可以写进 <c>config.json</c> 的 Base64。
///
/// <para>
/// 用 DPAPI 的**当前用户**范围（见 <see cref="DpapiInterop" />）：同一台机器上的同一个 Windows 用户
/// 才能解开，别的用户、别的机器、把文件拷走都解不开 —— 对“凭据跟着用户走”的桌面程序正好，
/// 也不需要主密码或额外的密钥管理。
/// </para>
///
/// <para>
/// 解不开时返回 <c>null</c> 而不是空串：空串表示“本来就没填密码”，
/// 而解不开要提示用户重新填写（见 <c>RemoteFileService</c> 里的处理）。
/// </para>
/// </summary>
public static class SecretProtector
{
    /// <summary>加密成 Base64；空串原样返回（表示没有保存）。失败返回空串并记日志。</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return string.Empty;
        }

        try
        {
            var encrypted = DpapiInterop.Protect(Encoding.UTF8.GetBytes(plain));
            return encrypted is null ? string.Empty : Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            Log.Exception("加密远程位置凭据", ex);
            return string.Empty;
        }
    }

    /// <summary>
    /// 解密。空串 → 空串（没保存过）；解密失败 → <c>null</c>（换用户 / 换机器 / 数据损坏）。
    /// </summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
        {
            return string.Empty;
        }

        try
        {
            var decrypted = DpapiInterop.Unprotect(Convert.FromBase64String(protectedBase64));
            if (decrypted is null)
            {
                Log.Write("远程位置凭据解密失败（可能换了 Windows 用户或换了机器），需要在设置里重新填写");
                return null;
            }

            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            Log.Exception("解密远程位置凭据", ex);
            return null;
        }
    }
}
