namespace NightCodeForge.Installer.Core.Abstractions;

/// <summary>
/// Abstraction over user-scoped (<c>HKCU</c>) Windows Registry operations.
/// </summary>
public interface IWindowsRegistryAccessor
{
    string? ReadCurrentUserStringValue(string subKeyPath, string valueName);
    void WriteCurrentUserValues(string subKeyPath, IReadOnlyDictionary<string, object> values);
    void DeleteCurrentUserSubKeyTree(string subKeyPath);
}
