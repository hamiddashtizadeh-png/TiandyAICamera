using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace MehrganSurveillance.Services;

/// <summary>
/// محافظت از مسیرها در برابر حملات عبور از دایرکتوری (Directory Traversal) و پیوندهای نمادین/Junctionها در ویندوز
/// </summary>
public static class WindowsSecurePath
{
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint FILE_SHARE_DELETE = 4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const int FileAttributeTagInfoClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int FileInformationClass,
        out FILE_ATTRIBUTE_TAG_INFO lpFileInformation,
        uint dwBufferSize);

    /// <summary>
    /// اعتبارسنجی سلسله‌مراتبی از ریشه تا برگ با استفاده از هندل مستقیم و فلگ FILE_FLAG_OPEN_REPARSE_POINT
    /// </summary>
    public static void AssertNoReparsePointsInHierarchy(string targetPath, string canonicalRoot)
    {
        string normRoot = Path.GetFullPath(canonicalRoot).TrimEnd('\\') + "\\";
        string normTarget = Path.GetFullPath(targetPath);

        // ۱. بررسی انطباق قطعی ریشه (بدون حساسیت به حروف بزرگ و کوچک)
        if (!normTarget.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException($"تلاش برای عبور از دایرکتوری شناسایی و مسدود شد: {normTarget}");

        // در سیستم‌های غیرویندوزی (مانند محیط تست لینوکس) از بررسی تکمیلی P/Invoke صرف‌نظر می‌شود
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // ۲. اعتبارسنجی خود ریشه
        VerifyHandleNotReparse(normRoot.TrimEnd('\\'));

        // ۳. بررسی متوالی هر جزء از مسیر تا برگ
        string current = normRoot.TrimEnd('\\');
        string relative = normTarget.Substring(normRoot.Length);
        string[] segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
                break;

            VerifyHandleNotReparse(current);
        }
    }

    private static void VerifyHandleNotReparse(string path)
    {
        using var handle = CreateFileW(
            path,
            0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            // اگر هنوز ایجاد نشده است، بررسی در زمان ایجاد انجام می‌شود
            if (error == 2 || error == 3) return;
            throw new Win32Exception(error, $"عدم امکان باز کردن هندل برای بررسی ویژگی‌های امنیتی: {path}");
        }

        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var tagInfo, (uint)Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFO>()))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"عدم امکان استعلام برچسب ویژگی‌ها برای مسیر: {path}");
        }

        if ((tagInfo.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new SecurityException($"پیوند نمادین یا Junction غیرمجاز در مسیر شناسایی و مسدود گردید: {path} (Tag: 0x{tagInfo.ReparseTag:X8})");
        }
    }
}
