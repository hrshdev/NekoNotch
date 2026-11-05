using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace NekoNotch.Services;

/// <summary>
/// JSON persistence under %LOCALAPPDATA%\NekoNotch. ORIXNOTCH_DATA_DIR overrides the folder
/// (used by end-to-end tests so they never touch real data).
/// </summary>
public static class Storage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Root { get; } = InitializeRoot();

    private static string InitializeRoot()
    {
        if (Environment.GetEnvironmentVariable("NEKONOTCH_DATA_DIR") is { Length: > 0 } custom)
            return custom;
        if (Environment.GetEnvironmentVariable("ORIXNOTCH_DATA_DIR") is { Length: > 0 } legacyCustom)
            return legacyCustom;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var newDir = Path.Combine(localAppData, "NekoNotch");
        var oldDir = Path.Combine(localAppData, "OrixNotch");

        if (!Directory.Exists(newDir) && Directory.Exists(oldDir))
        {
            try
            {
                CopyDirectory(oldDir, newDir);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
        return newDir;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists) return;

        Directory.CreateDirectory(destinationDir);

        foreach (var file in dir.GetFiles())
        {
            var targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath, true);
        }

        foreach (var subDir in dir.GetDirectories())
        {
            var newSubDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectory(subDir.FullName, newSubDir);
        }
    }

    static Storage() => Directory.CreateDirectory(Root);

    public static string PathOf(string name) => Path.Combine(Root, name);

    public static string Dir(string name)
    {
        var dir = PathOf(name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static T Load<T>(string name) where T : new()
    {
        try
        {
            var path = PathOf(name);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return new T();
    }

    public static void Save<T>(string name, T value)
    {
        try
        {
            var path = PathOf(name);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}

public static class Http
{
    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) NekoNotch/0.1");
        return client;
    }
}
