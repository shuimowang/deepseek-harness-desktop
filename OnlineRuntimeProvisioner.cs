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
    private const int FileOperationAttempts = 10;

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
        var stagingRoot = Path.Combine(runtimeRoot, "staging");
        Directory.CreateDirectory(stagingRoot);
        CleanupStagingDirectories(stagingRoot, $"dsh-{spec.HarnessVersion}");

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

        _progress($"正在准备 DeepSeek Harness {spec.HarnessVersion}...");
        var stagingDirectory = Path.Combine(
            runtimeRoot,
            "staging",
            $"dsh-{spec.HarnessVersion}");
        var installLogPath = Path.Combine(runtimeRoot, "logs", "install.log");

        if (IsExpectedDshVersion(stagingDirectory, spec.HarnessVersion))
        {
            _progress("发现上次已完成的安装，正在恢复...");
            AppendInstallLog(runtimeRoot, $"Recovering completed staging directory: {stagingDirectory}");
            await CommitStagingDirectoryAsync(
                stagingDirectory,
                dshDirectory,
                runtimeRoot,
                cancellationToken);
            return;
        }

        await DeleteDirectoryWithRetryAsync(
            stagingDirectory,
            "旧的未完成安装目录",
            runtimeRoot,
            cancellationToken);
        Directory.CreateDirectory(stagingDirectory);

        var runtimePackageDirectory = Path.Combine(AppContext.BaseDirectory, "runtime", "dsh-package");
        var packageJson = Path.Combine(runtimePackageDirectory, "package.json");
        var packageLock = Path.Combine(runtimePackageDirectory, "package-lock.json");
        if (!File.Exists(packageJson) || !File.Exists(packageLock))
        {
            throw new FileNotFoundException("在线安装清单缺失，请重新下载完整的在线轻量版压缩包。");
        }

        if (!IsExpectedRuntimePackage(packageJson, packageLock, spec.HarnessVersion))
        {
            throw new InvalidDataException("在线安装清单与客户端要求的 DeepSeek Harness 版本不一致。");
        }

        File.Copy(packageJson, Path.Combine(stagingDirectory, "package.json"), overwrite: true);
        File.Copy(packageLock, Path.Combine(stagingDirectory, "package-lock.json"), overwrite: true);
        AppendInstallLog(
            runtimeRoot,
            $"Starting locked install for DSH {spec.HarnessVersion}. Staging: {stagingDirectory}");

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
        startInfo.ArgumentList.Add("ci");
        startInfo.ArgumentList.Add("--prefix");
        startInfo.ArgumentList.Add(stagingDirectory);
        startInfo.ArgumentList.Add("--omit=dev");
        startInfo.ArgumentList.Add("--no-audit");
        startInfo.ArgumentList.Add("--no-fund");
        startInfo.ArgumentList.Add("--prefer-offline");
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
            var elapsed = Stopwatch.StartNew();
            var exitTask = process.WaitForExitAsync(cancellationToken);
            while (!exitTask.IsCompleted)
            {
                var delayTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                if (await Task.WhenAny(exitTask, delayTask) == delayTask)
                {
                    _progress(
                        $"正在下载安装已锁定的依赖，已用时 {FormatElapsed(elapsed.Elapsed)}...");
                }
            }

            await exitTask;
            process.WaitForExit();
            AppendInstallLog(runtimeRoot, $"npm ci exited with code {process.ExitCode}.");
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

            _progress("正在校验 DeepSeek Harness 安装...");
            if (!IsExpectedDshVersion(stagingDirectory, spec.HarnessVersion))
            {
                throw new InvalidDataException("npm 安装完成，但 DeepSeek Harness 版本校验失败。");
            }

            _progress("正在提交 DeepSeek Harness 运行时...");
            await CommitStagingDirectoryAsync(
                stagingDirectory,
                dshDirectory,
                runtimeRoot,
                cancellationToken);
            AppendInstallLog(runtimeRoot, $"Installed DSH {spec.HarnessVersion}: {dshDirectory}");
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process);
            AppendInstallLog(runtimeRoot, "Installation was cancelled; staging was preserved for retry.");
            throw;
        }
        catch (Exception exception)
        {
            await StopProcessAsync(process);
            AppendInstallLog(runtimeRoot, $"Installation failed; staging was preserved. {exception}");
            throw new InvalidOperationException(
                $"{exception.Message}\n\n安装文件已保留，下次重试会自动恢复。\n安装日志：{installLogPath}",
                exception);
        }
    }

    private static async Task StopProcessAsync(Process process)
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
    }

    private async Task CommitStagingDirectoryAsync(
        string stagingDirectory,
        string destinationDirectory,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        if (IsExpectedDshVersion(destinationDirectory, Path.GetFileName(destinationDirectory)))
        {
            TryDeleteDirectory(stagingDirectory);
            return;
        }

        await DeleteDirectoryWithRetryAsync(
            destinationDirectory,
            "旧的 DeepSeek Harness 运行时",
            runtimeRoot,
            cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationDirectory)!);

        Exception? lastException = null;
        for (var attempt = 1; attempt <= FileOperationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(stagingDirectory, destinationDirectory);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                lastException = exception;
                AppendInstallLog(
                    runtimeRoot,
                    $"Commit attempt {attempt}/{FileOperationAttempts} failed: {exception.Message}");
                if (attempt < FileOperationAttempts)
                {
                    _progress($"运行时文件暂时被占用，正在重试（{attempt}/{FileOperationAttempts}）...");
                    await Task.Delay(GetRetryDelay(attempt), cancellationToken);
                }
            }
        }

        throw new IOException(
            $"无法提交 DeepSeek Harness 运行时，已重试 {FileOperationAttempts} 次。" +
            $" 安装文件已保留在：{stagingDirectory}",
            lastException);
    }

    private static async Task DeleteDirectoryWithRetryAsync(
        string path,
        string description,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Exception? lastException = null;
        for (var attempt = 1; attempt <= FileOperationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                lastException = exception;
                AppendInstallLog(
                    runtimeRoot,
                    $"Delete attempt {attempt}/{FileOperationAttempts} for {description} failed: {exception.Message}");
                if (attempt < FileOperationAttempts)
                {
                    await Task.Delay(GetRetryDelay(attempt), cancellationToken);
                }
            }
        }

        throw new IOException($"无法清理{description}：{path}", lastException);
    }

    private static TimeSpan GetRetryDelay(int attempt)
    {
        return TimeSpan.FromMilliseconds(Math.Min(250 * Math.Pow(2, attempt - 1), 4000));
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
            : $"{Math.Max(1, elapsed.Seconds)} 秒";
    }

    private static bool IsExpectedRuntimePackage(
        string packageJsonPath,
        string packageLockPath,
        string expectedVersion)
    {
        try
        {
            using var packageDocument = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            var packageVersion = packageDocument.RootElement
                .GetProperty("dependencies")
                .GetProperty("@deepseek-ai/dsh")
                .GetString();

            using var lockDocument = JsonDocument.Parse(File.ReadAllText(packageLockPath));
            var lockVersion = lockDocument.RootElement
                .GetProperty("packages")
                .GetProperty(string.Empty)
                .GetProperty("dependencies")
                .GetProperty("@deepseek-ai/dsh")
                .GetString();
            return packageVersion == expectedVersion && lockVersion == expectedVersion;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static void AppendInstallLog(string runtimeRoot, string message)
    {
        try
        {
            var logDirectory = Path.Combine(runtimeRoot, "logs");
            Directory.CreateDirectory(logDirectory);
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}";
            File.AppendAllText(
                Path.Combine(logDirectory, "install.log"),
                line,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
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

    private static void CleanupStagingDirectories(string stagingRoot, string preservedDirectoryName)
    {
        if (!Directory.Exists(stagingRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
        {
            if (Path.GetFileName(directory).Equals(
                preservedDirectoryName,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
