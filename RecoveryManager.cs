using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DshDesktop;

internal sealed class RecoveryManager
{
    private static readonly SnapshotFile[] SnapshotFiles =
    [
        new("profile-package.json", Path.Combine("profiles", "web", "package.json")),
        new("profile-patch.yml", Path.Combine("profiles", "web", "cordis.patch.yml")),
        new("profile-lock.yaml", Path.Combine("profiles", "web", "pnpm-lock.yaml")),
        new("profile-workspace.yaml", Path.Combine("profiles", "web", "pnpm-workspace.yaml")),
        new("home-patch.yml", "cordis.patch.yml")
    ];

    private const string SafeProfileManifest = """
        {
          "name": "dsh-profile-web-safe",
          "private": true,
          "dsh": {
            "profile": {
              "bundles": [
                "@deepseek-ai/dsh-base",
                "@deepseek-ai/dsh-web-app"
              ]
            }
          }
        }
        """;

    public RecoveryManager()
        : this(dshHome: null, recoveryRoot: null, safeDshHome: null)
    {
    }

    internal RecoveryManager(string? dshHome, string? recoveryRoot, string? safeDshHome)
    {
        DshHome = Path.GetFullPath(dshHome ?? ResolveDshHome());
        RecoveryRoot = Path.GetFullPath(recoveryRoot ?? ResolveWritableRecoveryRoot());
        SafeDshHome = Path.GetFullPath(safeDshHome ?? Path.Combine(
                Path.GetTempPath(),
                "DeepSeekHarness",
                "safe-mode",
                Environment.ProcessId.ToString()));
    }

    public string DshHome { get; }

    public string ProfileDirectory => Path.Combine(DshHome, "profiles", "web");

    public string RecoveryRoot { get; }

    public string SafeDshHome { get; }

    private string LastGoodDirectory => Path.Combine(RecoveryRoot, "last-good");

    private string LastGoodManifestPath => Path.Combine(LastGoodDirectory, "manifest.json");

    public bool HasLastKnownGood
    {
        get
        {
            var manifest = ReadManifest(LastGoodManifestPath);
            try
            {
                return manifest is not null && PathsEqual(manifest.DshHome, DshHome);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }

    public string GetCurrentFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in SnapshotFiles)
        {
            var target = TargetPath(file);
            hash.AppendData(Encoding.UTF8.GetBytes(file.SnapshotName));
            if (!File.Exists(target))
            {
                hash.AppendData([0]);
                continue;
            }

            hash.AppendData([1]);
            try
            {
                hash.AppendData(File.ReadAllBytes(target));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(exception.GetType().Name));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void SaveLastKnownGood()
    {
        if (!File.Exists(Path.Combine(ProfileDirectory, "package.json")))
        {
            return;
        }

        Directory.CreateDirectory(LastGoodDirectory);
        var presentFiles = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SnapshotFiles)
        {
            var source = TargetPath(file);
            var snapshot = Path.Combine(LastGoodDirectory, file.SnapshotName);
            var exists = File.Exists(source);
            presentFiles[file.SnapshotName] = exists;
            if (exists)
            {
                File.Copy(source, snapshot, overwrite: true);
            }
            else if (File.Exists(snapshot))
            {
                File.Delete(snapshot);
            }
        }

        var manifest = new SnapshotManifest
        {
            DshHome = DshHome,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Files = presentFiles
        };
        var temporaryManifest = LastGoodManifestPath + ".tmp";
        File.WriteAllText(
            temporaryManifest,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryManifest, LastGoodManifestPath, overwrite: true);
    }

    public string RestoreLastKnownGood()
    {
        var manifest = ReadManifest(LastGoodManifestPath)
            ?? throw new InvalidOperationException("尚未保存可恢复的配置。请先使用安全模式启动并检查插件目录。");
        if (!PathsEqual(manifest.DshHome, DshHome))
        {
            throw new InvalidOperationException("备份属于另一个 DSH_HOME，已拒绝恢复。当前数据不会被修改。");
        }

        foreach (var file in SnapshotFiles)
        {
            var shouldExist = manifest.Files.TryGetValue(file.SnapshotName, out var existed) && existed;
            if (shouldExist && !File.Exists(Path.Combine(LastGoodDirectory, file.SnapshotName)))
            {
                throw new InvalidOperationException($"恢复文件缺失：{file.SnapshotName}");
            }
        }

        var incidentDirectory = Path.Combine(
            RecoveryRoot,
            "before-restore",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(incidentDirectory);

        foreach (var file in SnapshotFiles)
        {
            var target = TargetPath(file);
            var currentBackup = Path.Combine(incidentDirectory, file.SnapshotName);
            if (File.Exists(target))
            {
                File.Copy(target, currentBackup, overwrite: true);
            }

            var shouldExist = manifest.Files.TryGetValue(file.SnapshotName, out var existed) && existed;
            if (shouldExist)
            {
                var snapshot = Path.Combine(LastGoodDirectory, file.SnapshotName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(snapshot, target, overwrite: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }

        File.WriteAllText(
            Path.Combine(incidentDirectory, "manifest.json"),
            JsonSerializer.Serialize(
                new SnapshotManifest
                {
                    DshHome = DshHome,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Files = SnapshotFiles.ToDictionary(
                        file => file.SnapshotName,
                        file => File.Exists(Path.Combine(incidentDirectory, file.SnapshotName)),
                        StringComparer.OrdinalIgnoreCase)
                },
                new JsonSerializerOptions { WriteIndented = true }));

        return incidentDirectory;
    }

    public string PrepareSafeHome()
    {
        var profileDirectory = Path.Combine(SafeDshHome, "profiles", "web");
        Directory.CreateDirectory(profileDirectory);
        File.WriteAllText(
            Path.Combine(profileDirectory, "package.json"),
            SafeProfileManifest,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            Path.Combine(profileDirectory, "cordis.patch.yml"),
            "[]\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            Path.Combine(SafeDshHome, "cordis.patch.yml"),
            "[]\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return SafeDshHome;
    }

    private string TargetPath(SnapshotFile file) => Path.Combine(DshHome, file.RelativeTargetPath);

    private static SnapshotManifest? ReadManifest(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(path))
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string ResolveDshHome()
    {
        var configured = Environment.GetEnvironmentVariable("DSH_HOME");
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
            : Environment.ExpandEnvironmentVariables(configured);
        return Path.GetFullPath(path);
    }

    private static string ResolveWritableRecoveryRoot()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekHarness",
                "Recovery"),
            Path.Combine(Path.GetTempPath(), "DeepSeekHarness", "Recovery")
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }

        throw new UnauthorizedAccessException("找不到可写的恢复数据目录。");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private sealed record SnapshotFile(string SnapshotName, string RelativeTargetPath);

    private sealed class SnapshotManifest
    {
        public string DshHome { get; set; } = string.Empty;

        public DateTimeOffset CreatedAtUtc { get; set; }

        public Dictionary<string, bool> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
