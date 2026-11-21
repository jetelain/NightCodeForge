using KeySharp;

namespace NightCodeForge.Infrastructure.Security;

internal readonly record struct KeyringReadResult(
    ErrorType ErrorType,
    string? Value,
    string? ErrorMessage,
    Exception? Exception);

internal readonly record struct KeyringWriteResult(
    ErrorType ErrorType,
    string? ErrorMessage,
    Exception? Exception);

internal interface IKeyringBackend
{
    bool IsAvailable();
    KeyringReadResult GetPassword(string package, string service, string username);
    KeyringWriteResult SetPassword(string package, string service, string username, string password);
    KeyringWriteResult DeletePassword(string package, string service, string username);
}

internal sealed class KeySharpKeyringBackend : IKeyringBackend
{
    public static KeySharpKeyringBackend Instance { get; } = new();

    private KeySharpKeyringBackend()
    {
    }

    public bool IsAvailable() => Keyring.IsAvailable();

    public KeyringReadResult GetPassword(string package, string service, string username)
    {
        try
        {
            var value = Keyring.GetPassword(package, service, username);
            return new KeyringReadResult(ErrorType.NoError, value, null, null);
        }
        catch (KeyringException ex)
        {
            return new KeyringReadResult(ex.Type, null, ex.BackendMessage ?? ex.Message, ex);
        }
        catch (Exception ex) when (ex is DllNotFoundException or PlatformNotSupportedException)
        {
            return new KeyringReadResult(ErrorType.Unavailable, null, ex.Message, ex);
        }
    }

    public KeyringWriteResult SetPassword(string package, string service, string username, string password)
    {
        try
        {
            Keyring.SetPassword(package, service, username, password);
            return new KeyringWriteResult(ErrorType.NoError, null, null);
        }
        catch (KeyringException ex)
        {
            return new KeyringWriteResult(ex.Type, ex.BackendMessage ?? ex.Message, ex);
        }
        catch (Exception ex) when (ex is DllNotFoundException or PlatformNotSupportedException)
        {
            return new KeyringWriteResult(ErrorType.Unavailable, ex.Message, ex);
        }
    }

    public KeyringWriteResult DeletePassword(string package, string service, string username)
    {
        try
        {
            Keyring.DeletePassword(package, service, username);
            return new KeyringWriteResult(ErrorType.NoError, null, null);
        }
        catch (KeyringException ex)
        {
            return new KeyringWriteResult(ex.Type, ex.BackendMessage ?? ex.Message, ex);
        }
        catch (Exception ex) when (ex is DllNotFoundException or PlatformNotSupportedException)
        {
            return new KeyringWriteResult(ErrorType.Unavailable, ex.Message, ex);
        }
    }
}
