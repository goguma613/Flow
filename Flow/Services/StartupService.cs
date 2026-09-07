using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Flow.Services;

/// <summary>부팅 시 자동 실행 등록. 레지스트리 Run 키만 건드린다.</summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Flow";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var path = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(path)) key.SetValue(ValueName, $"\"{path}\"");
        }
        catch (Exception)
        {
            // 권한 문제로 실패할 수 있다. 자동 시작은 부가 기능이므로 조용히 넘어간다.
        }
    }
}
