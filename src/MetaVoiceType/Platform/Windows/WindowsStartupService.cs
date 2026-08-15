using MetaVoiceType.Core.Interfaces;
using Microsoft.Win32;

namespace MetaVoiceType.Platform.Windows;

public sealed class WindowsStartupService : IStartupService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MetaVoiceType";
    public bool IsEnabled { get { using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue(ValueName) is string; } }
    public void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --startup", RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
    }
}
