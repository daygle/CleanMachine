using Microsoft.Win32;

namespace CleanMachine.Windows;

public static class StartupRegistration
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CleanMachine";
    public static void SetEnabled(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunPath);
        if (enabled) key.SetValue(ValueName, $"\"{executablePath}\" --background"); else key.DeleteValue(ValueName, false);
    }
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: false); return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }
}
