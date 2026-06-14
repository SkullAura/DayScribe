using System.Text.RegularExpressions;
using Windows.Storage;

namespace ProjectCal_Client.Services;

public static partial class ClientAppData
{
    private static readonly string FallbackRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectCal",
        "Client");

    public static string LocalFolderPath
    {
        get
        {
            try
            {
                return ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                Directory.CreateDirectory(FallbackRoot);
                return FallbackRoot;
            }
        }
    }

    public static string DataPath
    {
        get
        {
            var path = Path.Combine(LocalFolderPath, "ProjectCal");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string RecordingsPath
    {
        get
        {
            var path = Path.Combine(LocalFolderPath, "ProjectCalRecordings");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string? GetString(string key)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values[key] as string;
        }
        catch
        {
            var path = GetSettingPath(key);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }

    public static bool? GetBool(string key)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values[key] is bool value ? value : null;
        }
        catch
        {
            var value = GetString(key);
            return bool.TryParse(value, out var parsed) ? parsed : null;
        }
    }

    public static void Set(string key, string value)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
        }
        catch
        {
            File.WriteAllText(GetSettingPath(key), value);
        }
    }

    public static void Set(string key, bool value)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
        }
        catch
        {
            File.WriteAllText(GetSettingPath(key), value.ToString());
        }
    }

    public static void Remove(string key)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values.Remove(key);
        }
        catch
        {
            var path = GetSettingPath(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string GetSettingPath(string key)
    {
        var settingsPath = Path.Combine(LocalFolderPath, "settings");
        Directory.CreateDirectory(settingsPath);
        return Path.Combine(settingsPath, $"{SafeSettingFileName().Replace(key, "_")}.txt");
    }

    [GeneratedRegex("[^a-zA-Z0-9_.-]")]
    private static partial Regex SafeSettingFileName();
}
