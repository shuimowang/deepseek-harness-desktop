using System.IO;
using System.Text.Json;

namespace DshDesktop;

internal sealed class DesktopSettings
{
    public string WorkingDirectory { get; set; } = DefaultWorkingDirectory();

    private static IEnumerable<string> SettingsPaths()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekHarness",
            "desktop-settings.json");
        yield return Path.Combine(
            Path.GetTempPath(),
            "DeepSeekHarness",
            "desktop-settings.json");
    }

    public static DesktopSettings Load()
    {
        foreach (var path in SettingsPaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path));
                if (settings is not null && Directory.Exists(settings.WorkingDirectory))
                {
                    return settings;
                }
            }
            catch
            {
                // A damaged or inaccessible preference file should not block startup.
            }
        }

        return new DesktopSettings();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        foreach (var path in SettingsPaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
                return;
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
            }
        }

        throw new UnauthorizedAccessException("找不到可写的客户端设置目录。");
    }

    private static string DefaultWorkingDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("DSH_DESKTOP_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "DeepSeek Harness Workspace"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekHarness",
                "Workspace")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
