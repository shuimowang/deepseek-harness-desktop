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
    private const string InstallationMarkerName = ".install-complete.json";
    private const string PreferredNpmRegistry = "https://registry.npmmirror.com";
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    private static readonly string ApplicationBaseDirectory =
        Path.GetDirectoryName(typeof(OnlineRuntimeProvisioner).Assembly.Location)
        ?? AppContext.BaseDirectory;

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
        CleanupStagingDirectories(stagingRoot, preservedDirectoryName: string.Empty);

        if (!File.Exists(nodeExecutable))
        {
            await InstallNodeAsync(spec, runtimeRoot, nodeDirectory, cancellationToken);
        }

        if (!IsCompleteDshInstallation(dshDirectory, spec.HarnessVersion))
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
        var corepackCli = Path.Combine(nodeDirectory, "node_modules", "corepack", "dist", "corepack.js");
        if (!File.Exists(nodeExecutable) || !File.Exists(corepackCli))
        {
            throw new FileNotFoundException("Node.js 安装中缺少 Corepack，无法安装 DeepSeek Harness。");
        }

        _progress(
            $"正在准备 DeepSeek Harness {spec.HarnessVersion}，" +
            "首次运行将下载并安装约 500 个依赖...");
        var installLogPath = Path.Combine(runtimeRoot, "logs", "install.log");
        Directory.CreateDirectory(dshDirectory);
        TryDeleteFile(Path.Combine(dshDirectory, InstallationMarkerName));

        var runtimePackageDirectory = Path.Combine(ApplicationBaseDirectory, "runtime", "dsh-package");
        var packageJson = Path.Combine(runtimePackageDirectory, "package.json");
        var packageLock = Path.Combine(runtimePackageDirectory, "pnpm-lock.yaml");
        var workspaceManifest = Path.Combine(runtimePackageDirectory, "pnpm-workspace.yaml");
        var packageArchiveDirectory = Path.Combine(runtimePackageDirectory, "packages");
        if (!File.Exists(packageJson) ||
            !File.Exists(packageLock) ||
            !File.Exists(workspaceManifest) ||
            !Directory.Exists(packageArchiveDirectory))
        {
            throw new FileNotFoundException("在线安装清单缺失，请重新下载完整的在线轻量版压缩包。");
        }

        if (!IsExpectedRuntimePackage(runtimePackageDirectory, spec.HarnessVersion))
        {
            throw new InvalidDataException("在线安装清单与客户端要求的 DeepSeek Harness 版本不一致。");
        }

        CopyRuntimePackageDirectory(runtimePackageDirectory, dshDirectory);
        AppendInstallLog(
            runtimeRoot,
            $"Starting or resuming locked install for DSH {spec.HarnessVersion}: {dshDirectory}");

        var startInfo = new ProcessStartInfo(nodeExecutable)
        {
            WorkingDirectory = dshDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(corepackCli);
        startInfo.ArgumentList.Add("pnpm");
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--frozen-lockfile");
        startInfo.ArgumentList.Add("--prod");
        startInfo.ArgumentList.Add("--reporter");
        startInfo.ArgumentList.Add("append-only");
        startInfo.ArgumentList.Add("--registry");
        startInfo.ArgumentList.Add(PreferredNpmRegistry);
        startInfo.ArgumentList.Add("--fetch-timeout");
        startInfo.ArgumentList.Add("120000");
        startInfo.ArgumentList.Add("--fetch-retries");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("--fetch-retry-mintimeout");
        startInfo.ArgumentList.Add("1000");
        startInfo.ArgumentList.Add("--fetch-retry-maxtimeout");
        startInfo.ArgumentList.Add("10000");
        startInfo.ArgumentList.Add("--store-dir");
        startInfo.ArgumentList.Add(Path.Combine(runtimeRoot, "pnpm-store"));
        startInfo.Environment["PATH"] = nodeDirectory + Path.PathSeparator +
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        startInfo.Environment["COREPACK_HOME"] = Path.Combine(runtimeRoot, "corepack-cache");
        startInfo.Environment["COREPACK_NPM_REGISTRY"] = PreferredNpmRegistry;
        startInfo.Environment["PNPM_HOME"] = Path.Combine(runtimeRoot, "pnpm-home");
        startInfo.Environment["CI"] = "true";
        startInfo.Environment["NPM_CONFIG_REGISTRY"] = PreferredNpmRegistry;

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
                throw new InvalidOperationException("无法启动 pnpm 安装进程。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var elapsed = Stopwatch.StartNew();
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(InstallTimeout, cancellationToken);
            while (true)
            {
                var delayTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                var completed = await Task.WhenAny(exitTask, delayTask, timeoutTask);
                if (completed == exitTask)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (completed == timeoutTask)
                {
                    throw new TimeoutException(
                        $"依赖安装超过 {InstallTimeout.TotalMinutes:0} 分钟，已停止本次安装。" +
                        "请检查网络后点击“重试”；已下载的内容会保留。");
                }

                if (completed == delayTask)
                {
                    _progress(
                        $"正在下载安装已锁定的依赖，已用时 {FormatElapsed(elapsed.Elapsed)}。" +
                        "首次运行通常需要几分钟，请保持窗口打开...");
                }
            }

            await exitTask;
            process.WaitForExit();
            AppendInstallLog(runtimeRoot, $"pnpm install exited with code {process.ExitCode}.");
            if (process.ExitCode != 0)
            {
                string details;
                lock (tailLock)
                {
                    details = string.Join(Environment.NewLine, tail);
                }

                throw new InvalidOperationException(
                    $"DeepSeek Harness 安装失败（pnpm 代码 {process.ExitCode}）。\n{details}");
            }

            _progress("正在校验 DeepSeek Harness 安装...");
            if (!IsExpectedDshVersion(dshDirectory, spec.HarnessVersion))
            {
                throw new InvalidDataException("pnpm 安装完成，但 DeepSeek Harness 版本校验失败。");
            }

            _progress("正在完成 DeepSeek Harness 运行时安装...");
            await WriteInstallationMarkerAsync(dshDirectory, spec.HarnessVersion, cancellationToken);
            AppendInstallLog(runtimeRoot, $"Installed DSH {spec.HarnessVersion}: {dshDirectory}");
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process);
            AppendInstallLog(runtimeRoot, "Installation was cancelled; the partial runtime was preserved for retry.");
            throw;
        }
        catch (Exception exception)
        {
            await StopProcessAsync(process);
            AppendInstallLog(runtimeRoot, $"Installation failed; the partial runtime was preserved. {exception}");
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

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
            : $"{Math.Max(1, elapsed.Seconds)} 秒";
    }

    private static bool IsExpectedRuntimePackage(
        string runtimePackageDirectory,
        string expectedVersion)
    {
        try
        {
            var packageJsonPath = Path.Combine(runtimePackageDirectory, "package.json");
            var packageLockPath = Path.Combine(runtimePackageDirectory, "pnpm-lock.yaml");
            var workspaceManifestPath = Path.Combine(runtimePackageDirectory, "pnpm-workspace.yaml");
            var sourceManifestPath = Path.Combine(runtimePackageDirectory, "SOURCE.json");
            using var packageDocument = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            var dependencies = packageDocument.RootElement.GetProperty("dependencies");
            var packageSpec = dependencies.GetProperty("@deepseek-ai/dsh").GetString();
            var packageManager = packageDocument.RootElement.GetProperty("packageManager").GetString();
            using var sourceDocument = JsonDocument.Parse(File.ReadAllText(sourceManifestPath));
            var sourceVersion = sourceDocument.RootElement.GetProperty("harnessVersion").GetString();
            var lockText = File.ReadAllText(packageLockPath);
            var workspaceText = File.ReadAllText(workspaceManifestPath);

            if (packageSpec is null ||
                !packageSpec.StartsWith("file:packages/", StringComparison.Ordinal) ||
                !packageSpec.Contains(expectedVersion, StringComparison.Ordinal) ||
                packageManager != "pnpm@11.7.0" ||
                sourceVersion != expectedVersion ||
                !lockText.Contains(packageSpec, StringComparison.Ordinal) ||
                !workspaceText.Contains($"'@deepseek-ai/dsh': '{packageSpec}'", StringComparison.Ordinal))
            {
                return false;
            }

            var runtimeRoot = Path.GetFullPath(runtimePackageDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var dependency in dependencies.EnumerateObject())
            {
                var dependencySpec = dependency.Value.GetString();
                if (dependencySpec is null ||
                    !dependencySpec.StartsWith("file:packages/", StringComparison.Ordinal))
                {
                    return false;
                }

                var archivePath = Path.GetFullPath(Path.Combine(
                    runtimePackageDirectory,
                    dependencySpec["file:".Length..].Replace('/', Path.DirectorySeparatorChar)));
                if (!archivePath.StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(archivePath))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void CopyRuntimePackageDirectory(string sourceDirectory, string destinationDirectory)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(
            sourceDirectory,
            "*",
            SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
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

    private static bool IsCompleteDshInstallation(string installDirectory, string expectedVersion)
    {
        if (!IsExpectedDshVersion(installDirectory, expectedVersion))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(installDirectory, InstallationMarkerName)));
            return document.RootElement.GetProperty("harnessVersion").GetString() == expectedVersion &&
                document.RootElement.GetProperty("packageManager").GetString() == "pnpm@11.7.0";
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static async Task WriteInstallationMarkerAsync(
        string installDirectory,
        string harnessVersion,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(installDirectory, InstallationMarkerName);
        var temporaryPath = markerPath + ".tmp";
        var json = JsonSerializer.Serialize(
            new
            {
                harnessVersion,
                packageManager = "pnpm@11.7.0",
                installedAtUtc = DateTime.UtcNow.ToString("O")
            },
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, markerPath, overwrite: true);
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
