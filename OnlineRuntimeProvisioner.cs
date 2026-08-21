using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshDesktop;

internal sealed class OnlineRuntimeSpec
{
    public string Mode { get; set; } = string.Empty;

    public string NodeVersion { get; set; } = string.Empty;

    public string NodeArchiveSha256 { get; set; } = string.Empty;

    public string HarnessVersion { get; set; } = string.Empty;

    public static OnlineRuntimeSpec? TryLoad(string path, string expectedHarnessVersion)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        OnlineRuntimeSpec spec;
        try
        {
            spec = JsonSerializer.Deserialize<OnlineRuntimeSpec>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("在线运行时清单为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("在线运行时清单格式无效。", exception);
        }

        if (!spec.Mode.Equals("online", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Version.TryParse(spec.NodeVersion, out var nodeVersion) || nodeVersion.Major < 24)
        {
            throw new InvalidDataException($"在线运行时清单中的 Node.js 版本无效：{spec.NodeVersion}");
        }

        if (!spec.HarnessVersion.Equals(expectedHarnessVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"在线运行时清单要求 DSH {spec.HarnessVersion}，客户端要求 {expectedHarnessVersion}。");
        }

        if (!Regex.IsMatch(spec.NodeArchiveSha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("在线运行时清单中的 Node.js SHA-256 无效。");
        }

        spec.NodeArchiveSha256 = spec.NodeArchiveSha256.ToUpperInvariant();
        return spec;
    }
}

internal sealed record OnlineRuntimePaths(string NodeExecutable, string DshEntryPoint);

internal sealed class OnlineRuntimeProvisioner
{
    private static readonly HttpClient DownloadClient = new()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private readonly Action<string> _progress;
    private readonly Action<string?> _log;

    public OnlineRuntimeProvisioner(Action<string> progress, Action<string?> log)
    {
        _progress = progress;
        _log = log;
    }

    public async Task<OnlineRuntimePaths> EnsureAsync(
        OnlineRuntimeSpec spec,
        CancellationToken cancellationToken)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new DirectoryNotFoundException("找不到当前用户的本地应用数据目录。");
        }

        var runtimeRoot = Path.Combine(localAppData, "DeepSeekHarness", "Runtime");
        var nodeDirectory = Path.Combine(runtimeRoot, "node", spec.NodeVersion);
        var dshDirectory = Path.Combine(runtimeRoot, "dsh", spec.HarnessVersion);
        var nodeExecutable = Path.Combine(nodeDirectory, "node.exe");
        var dshEntryPoint = Path.Combine(
            dshDirectory,
            "node_modules",
            "@deepseek-ai",
            "dsh",
            "lib",
            "bin.js");

        Directory.CreateDirectory(runtimeRoot);
        CleanupStagingDirectories(Path.Combine(runtimeRoot, "staging"));

        if (!File.Exists(nodeExecutable))
        {
            await InstallNodeAsync(spec, runtimeRoot, nodeDirectory, cancellationToken);
        }

        if (!IsExpectedDshVersion(dshDirectory, spec.HarnessVersion))
        {
            await InstallHarnessAsync(
                spec,
                runtimeRoot,
                nodeDirectory,
                dshDirectory,
                cancellationToken);
        }

        if (!File.Exists(nodeExecutable) || !File.Exists(dshEntryPoint))
        {
            throw new InvalidDataException("在线运行时安装完成，但关键文件缺失。");
        }

        await WriteManifestAsync(runtimeRoot, spec, cancellationToken);
        _progress($"运行时已就绪：Node.js {spec.NodeVersion} / DSH {spec.HarnessVersion}");
        return new OnlineRuntimePaths(nodeExecutable, dshEntryPoint);
    }

    private async Task InstallNodeAsync(
        OnlineRuntimeSpec spec,
        string runtimeRoot,
        string nodeDirectory,
        CancellationToken cancellationToken)
    {
        var archiveName = $"node-v{spec.NodeVersion}-win-x64.zip";
        var downloadsDirectory = Path.Combine(runtimeRoot, "downloads");
        var archivePath = Path.Combine(downloadsDirectory, archiveName);
        var partialPath = archivePath + ".partial";
        Directory.CreateDirectory(downloadsDirectory);

        if (!File.Exists(archivePath) || !HasExpectedHash(archivePath, spec.NodeArchiveSha256))
        {
            TryDeleteFile(archivePath);
            TryDeleteFile(partialPath);
            var uri = new Uri($"https://nodejs.org/dist/v{spec.NodeVersion}/{archiveName}");
            await DownloadAsync(uri, partialPath, spec.NodeVersion, cancellationToken);

            if (!HasExpectedHash(partialPath, spec.NodeArchiveSha256))
            {
                TryDeleteFile(partialPath);
                throw new InvalidDataException("Node.js 下载文件 SHA-256 校验失败，已拒绝安装。");
            }

            File.Move(partialPath, archivePath, overwrite: true);
        }

        _progress("正在解压 Node.js 运行时...");
        var stagingRoot = Path.Combine(runtimeRoot, "staging", $"node-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            ZipFile.ExtractToDirectory(archivePath, stagingRoot);
            var extractedDirectory = Path.Combine(stagingRoot, $"node-v{spec.NodeVersion}-win-x64");
            if (!File.Exists(Path.Combine(extractedDirectory, "node.exe")))
            {
                throw new InvalidDataException("Node.js 压缩包结构无效。");
            }

            TryDeleteDirectory(nodeDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(nodeDirectory)!);
            Directory.Move(extractedDirectory, nodeDirectory);
            TryDeleteFile(archivePath);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private async Task DownloadAsync(
        Uri uri,
        string destination,
        string version,
        CancellationToken cancellationToken)
    {
        _progress($"正在下载 Node.js {version}（约 33 MB）...");
        using var response = await DownloadClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalLength = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[1024 * 128];
        long downloaded = 0;
        var lastReported = -10;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            if (totalLength is > 0)
            {
                var percentage = (int)(downloaded * 100 / totalLength.Value);
                if (percentage >= lastReported + 10)
                {
                    lastReported = percentage;
                    _progress($"正在下载 Node.js {version}：{percentage}%");
                }
            }
        }
    }

    private async Task InstallHarnessAsync(
        OnlineRuntimeSpec spec,
        string runtimeRoot,
        string nodeDirectory,
        string dshDirectory,
        CancellationToken cancellationToken)
    {
        var nodeExecutable = Path.Combine(nodeDirectory, "node.exe");
        var npmCli = Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(nodeExecutable) || !File.Exists(npmCli))
        {
            throw new FileNotFoundException("Node.js 安装中缺少 npm，无法安装 DeepSeek Harness。");
        }

        _progress($"正在安装 DeepSeek Harness {spec.HarnessVersion}，首次运行可能需要数分钟...");
        var stagingDirectory = Path.Combine(
            runtimeRoot,
            "staging",
            $"dsh-{spec.HarnessVersion}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        var startInfo = new ProcessStartInfo(nodeExecutable)
        {
            WorkingDirectory = runtimeRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(npmCli);
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--prefix");
        startInfo.ArgumentList.Add(stagingDirectory);
        startInfo.ArgumentList.Add("--no-save");
        startInfo.ArgumentList.Add("--no-package-lock");
        startInfo.ArgumentList.Add($"@deepseek-ai/dsh@{spec.HarnessVersion}");
        startInfo.ArgumentList.Add("--omit=dev");
        startInfo.ArgumentList.Add("--no-audit");
        startInfo.ArgumentList.Add("--no-fund");
        startInfo.Environment["PATH"] = nodeDirectory + Path.PathSeparator +
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        startInfo.Environment["npm_config_cache"] = Path.Combine(runtimeRoot, "npm-cache");

        var tail = new Queue<string>();
        var tailLock = new object();
        void Capture(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            _log(line);
            lock (tailLock)
            {
                tail.Enqueue(line);
                while (tail.Count > 20)
                {
                    tail.Dequeue();
                }
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, args) => Capture(args.Data);
        process.ErrorDataReceived += (_, args) => Capture(args.Data);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 npm 安装进程。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                string details;
                lock (tailLock)
                {
                    details = string.Join(Environment.NewLine, tail);
                }

                throw new InvalidOperationException(
                    $"DeepSeek Harness 安装失败（npm 代码 {process.ExitCode}）。\n{details}");
            }

            if (!IsExpectedDshVersion(stagingDirectory, spec.HarnessVersion))
            {
                throw new InvalidDataException("npm 安装完成，但 DeepSeek Harness 版本校验失败。");
            }

            TryDeleteDirectory(dshDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(dshDirectory)!);
            Directory.Move(stagingDirectory, dshDirectory);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static bool IsExpectedDshVersion(string installDirectory, string expectedVersion)
    {
        var packagePath = Path.Combine(
            installDirectory,
            "node_modules",
            "@deepseek-ai",
            "dsh",
            "package.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packagePath));
            return document.RootElement.GetProperty("version").GetString() == expectedVersion;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool HasExpectedHash(string path, string expectedHash)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream))
                .Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task WriteManifestAsync(
        string runtimeRoot,
        OnlineRuntimeSpec spec,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(runtimeRoot, "runtime-manifest.json");
        var temporaryPath = manifestPath + ".tmp";
        var json = JsonSerializer.Serialize(
            new
            {
                node = spec.NodeVersion,
                deepSeekHarness = spec.HarnessVersion,
                installedAtUtc = DateTime.UtcNow.ToString("O")
            },
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, manifestPath, overwrite: true);
    }

    private static void CleanupStagingDirectories(string stagingRoot)
    {
        if (!Directory.Exists(stagingRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
        {
            TryDeleteDirectory(directory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }
}
