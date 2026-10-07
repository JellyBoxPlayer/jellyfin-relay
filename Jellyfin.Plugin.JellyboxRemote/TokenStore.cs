namespace Jellyfin.Plugin.JellyboxRemote;

public sealed class TokenStore(string path)
{
    private readonly object _gate = new();
    private string? _cached;

    public string Current
    {
        get
        {
            lock (_gate)
            {
                if (_cached is null)
                {
                    _cached = File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
                }

                return _cached;
            }
        }
    }

    public bool IsSet => Current.Length > 0;

    public void Set(string token)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, token.Trim());
            Restrict();
            _cached = token.Trim();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            File.Delete(path);
            _cached = string.Empty;
        }
    }

    private void Restrict()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
