using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DshDesktop;

internal sealed class DshServerManager : IDisposable
{
    public const string HarnessVersion = "0.1.7-alpha.2";

    private const int MaxLogCharacters = 512 * 1024;

    private static readonly string ApplicationBaseDirectory =
        Path.GetDirectoryName(typeof(DshServerManager).Assembly.Location)
        ?? AppContext.BaseDirectory;

    private enum ProbeResult
    {
        Unavailable,
        DeepSeekHarness,
        OtherHttpServer
    }

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(1.5) };
    private readonly StringBuilder _logs = new();
    private readonly object _logLock = new();
    private readonly Uri _preferredAppUri;
    private readonly StreamWriter? _persistentLog;
    private Process? _process;
    private IntPtr _jobHandle;
    private string? _activeDshHomeOverride;

    public DshServerManager(Uri preferredAppUri)
    {
        _preferredAppUri = preferredAppUri;
        AppUri = preferredAppUri;
        (LogFilePath, _persistentLog) = CreatePersistentLog();
        AppendLog("DeepSeek Harness Desktop started.");
    }

    public Uri AppUri { get; private set; }

    public event EventHandler<string>? ProgressChanged;

    public string? LogFilePath { get; }

    public bool OwnsServerProcess => _process is not null || _jobHandle != IntPtr.Zero;

    public bool StartedByClient => OwnsServerProcess;

    public bool IsSafeMode => _activeDshHomeOverride is not null;

    public string Logs
    {
        get
        {
            lock (_logLock)
            {
                return _logs.ToString();
            }
        }
    }

    public async Task EnsureRunningAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        string? dshHomeOverride = null)
    {
        if (!PathsEqualOrBothNull(_activeDshHomeOverride, dshHomeOverride))
        {
            if (OwnsServerProcess)
            {
                StopOwnedServer();
            }

            AppUri = dshHomeOverride is null
                ? _preferredAppUri
                : new Uri($"http://127.0.0.1:{FindAvailableLoopbackPort()}/");
        }

        var probe = await ProbeAsync(cancellationToken);
        if (probe == ProbeResult.DeepSeekHarness)
        {
            AppendLog($"Using the existing DeepSeek Harness server at {AppUri}");
            return;
        }

        if (probe == ProbeResult.OtherHttpServer)
        {
            var previousPort = AppUri.Port;
            AppUri = WithAvailableLoopbackPort();
            AppendLog($"Port {previousPort} is occupied by another HTTP service; using {AppUri.Port} instead.");
        }
        else if (!CanBindLoopbackPort(AppUri.Port))
        {
            var previousPort = AppUri.Port;
            AppUri = WithAvailableLoopbackPort();
            AppendLog(
                $"Port {previousPort} is unavailable to this process (possibly reserved by Windows); " +
                $"using {AppUri.Port} instead.");
        }

        DisposeExitedProcess();
        var startInfo = await BuildStartInfoAsync(
            workingDirectory,
            AppUri.Port,
            dshHomeOverride,
            cancellationToken);
        StartServer(workingDirectory, startInfo, dshHomeOverride);
        try
        {
            var deadline = Stopwatch.StartNew();

            var nextProgressReport = TimeSpan.FromSeconds(10);
            while (deadline.Elapsed < TimeSpan.FromMinutes(3))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_process is { HasExited: true })
                {
                    throw new InvalidOperationException(
                        $"DeepSeek Harness 启动进程已退出（代码 {_process.ExitCode}）。请查看启动日志。");
                }

                probe = await ProbeAsync(cancellationToken);
                if (probe == ProbeResult.DeepSeekHarness)
                {
                    AppendLog($"DeepSeek Harness {HarnessVersion} is ready at {AppUri}");
                    return;
                }

                if (deadline.Elapsed >= nextProgressReport)
                {
                    ReportProgress(
                        $"正在初始化 Harness 配置，" +
                        $"已用时 {FormatElapsed(deadline.Elapsed)}...");
                    nextProgressReport += TimeSpan.FromSeconds(10);
                }

                await Task.Delay(400, cancellationToken);
            }

            throw new TimeoutException("等待 DeepSeek Harness 初始化超时（3 分钟）。请查看启动日志。");
        }
        catch
        {
            StopOwnedServer();
            throw;
        }
    }

    public async Task RestartAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        string? dshHomeOverride = null)
    {
        if (OwnsServerProcess)
        {
            StopOwnedServer();
        }

        AppUri = dshHomeOverride is null
            ? _preferredAppUri
            : new Uri($"http://127.0.0.1:{FindAvailableLoopbackPort()}/");
        await EnsureRunningAsync(workingDirectory, cancellationToken, dshHomeOverride);
    }

    public async Task<bool> IsHarnessReadyAsync(CancellationToken cancellationToken) =>
        await ProbeAsync(cancellationToken) == ProbeResult.DeepSeekHarness;

    public void RecordClientLog(string message) => AppendLog(message);

    private void StartServer(
        string workingDirectory,
        ProcessStartInfo startInfo,
        string? dshHomeOverride)
    {
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{workingDirectory}");
        }

        var arguments = startInfo.ArgumentList.Count > 0
            ? string.Join(' ', startInfo.ArgumentList)
            : startInfo.Arguments;
        AppendLog($"> {startInfo.FileName} {arguments}");
        AppendLog($"Working directory: {workingDirectory}");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var jobHandle = CreateKillOnCloseJob();
        process.OutputDataReceived += (_, args) => HandleServerOutput(args.Data);
        process.ErrorDataReceived += (_, args) => AppendLog(args.Data);
        process.Exited += (_, _) =>
        {
            try
            {
                AppendLog($"DeepSeek Harness process exited with code {process.ExitCode}.");
            }
            catch (InvalidOperationException)
            {
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法创建 DeepSeek Harness 启动进程。");
            }

            if (!AssignProcessToJobObject(jobHandle, process.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将 Harness 进程加入 Windows 作业。");
            }

            _process = process;
            _jobHandle = jobHandle;
            _activeDshHomeOverride = dshHomeOverride;
            jobHandle = IntPtr.Zero;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
            throw;
        }
        finally
        {
            if (jobHandle != IntPtr.Zero)
            {
                CloseHandle(jobHandle);
            }
        }
    }

    private async Task<ProcessStartInfo> BuildStartInfoAsync(
        string workingDirectory,
        int port,
        string? dshHomeOverride,
        CancellationToken cancellationToken)
    {
        var bundledNode = Path.Combine(ApplicationBaseDirectory, "runtime", "node", "node.exe");
        var bundledDsh = Path.Combine(
            ApplicationBaseDirectory,
            "runtime",
            "dsh",
            "node_modules",
            "@deepseek-ai",
            "dsh",
            "lib",
            "bin.js");

        if (File.Exists(bundledNode) && File.Exists(bundledDsh))
        {
            ReportProgress($"正在使用内置 DeepSeek Harness {HarnessVersion}...");
            PrepareProfileFallbackLinks(GetDshPackageDirectory(bundledDsh), dshHomeOverride);
            var bundled = CommonStartInfo(bundledNode, workingDirectory);
            bundled.ArgumentList.Add(bundledDsh);
            AddWebArguments(bundled.ArgumentList, port);
            ApplyDshHomeOverride(bundled, dshHomeOverride);
            return bundled;
        }

        var onlineSpec = OnlineRuntimeSpec.TryLoad(
            Path.Combine(ApplicationBaseDirectory, "runtime-mode.json"),
            HarnessVersion);
        if (onlineSpec is not null)
        {
            var provisioner = new OnlineRuntimeProvisioner(ReportProgress, AppendLog);
            var runtime = await provisioner.EnsureAsync(onlineSpec, cancellationToken);
            PrepareProfileFallbackLinks(GetDshPackageDirectory(runtime.DshEntryPoint), dshHomeOverride);
            var online = CommonStartInfo(runtime.NodeExecutable, workingDirectory);
            online.ArgumentList.Add(runtime.DshEntryPoint);
            AddWebArguments(online.ArgumentList, port);
            ApplyDshHomeOverride(online, dshHomeOverride);
            return online;
        }

        ReportProgress("正在检查系统 Node.js 运行环境...");
        ValidateSystemNode();
        var npxCmd = FindOnPath("npx.cmd")
            ?? throw new FileNotFoundException(
                "找不到 npx。请安装 Node.js 22.19 或 24 及更高版本：https://nodejs.org/");

        PrepareProfileFallbackLinks(expectedDshPackageDirectory: null, dshHomeOverride);

        var system = CmdStartInfo(
            npxCmd,
            workingDirectory,
            "--yes",
            $"@deepseek-ai/dsh@{HarnessVersion}",
            "web",
            "--host",
            "127.0.0.1",
            "--port",
            port.ToString(),
            "--no-open");
        ApplyDshHomeOverride(system, dshHomeOverride);
        return system;
    }

    private void PrepareProfileFallbackLinks(
        string? expectedDshPackageDirectory,
        string? dshHomeOverride)
    {
        var dshHome = dshHomeOverride ?? Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrWhiteSpace(dshHome))
        {
            dshHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh");
        }

        var fallbackDirectory = Path.Combine(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(dshHome)),
            "profiles",
            "node_modules");
        if (!Directory.Exists(fallbackDirectory))
        {
            return;
        }

        var managedDshLink = Path.Combine(fallbackDirectory, "@deepseek-ai", "dsh");
        var previousInstallRoot = GetVerifiedDshInstallRoot(managedDshLink);
        if (previousInstallRoot is null)
        {
            return;
        }

        var currentTarget = new DirectoryInfo(managedDshLink).ResolveLinkTarget(returnFinalTarget: false);
        if (expectedDshPackageDirectory is not null &&
            currentTarget is not null &&
            PathsEqual(currentTarget.FullName, expectedDshPackageDirectory))
        {
            return;
        }

        try
        {
            var removed = RemoveManagedFallbackLinks(fallbackDirectory, previousInstallRoot);
            AppendLog($"Removed {removed} stale DeepSeek Harness profile fallback link(s).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"无法更新 DeepSeek Harness 的旧版依赖链接：{fallbackDirectory}。" +
                "请关闭其他 DeepSeek Harness 进程后重试。",
                ex);
        }
    }

    private static string GetDshPackageDirectory(string entryPoint) =>
        Directory.GetParent(Path.GetDirectoryName(entryPoint)
            ?? throw new InvalidOperationException("DeepSeek Harness 入口路径无效。"))?.FullName
        ?? throw new InvalidOperationException("DeepSeek Harness 包路径无效。");

    private static string? GetVerifiedDshInstallRoot(string managedDshLink)
    {
        DirectoryInfo link;
        try
        {
            link = new DirectoryInfo(managedDshLink);
            if ((link.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        var target = link.ResolveLinkTarget(returnFinalTarget: false);
        if (target is null)
        {
            return null;
        }

        var scopeDirectory = Directory.GetParent(target.FullName);
        var nodeModulesDirectory = scopeDirectory?.Parent;
        if (scopeDirectory?.Name != "@deepseek-ai" ||
            nodeModulesDirectory?.Name != "node_modules")
        {
            return null;
        }

        if (!Directory.Exists(target.FullName))
        {
            return target.Name == "dsh" ? nodeModulesDirectory.FullName : null;
        }

        var manifestPath = Path.Combine(target.FullName, "package.json");
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!manifest.RootElement.TryGetProperty("name", out var name) ||
                name.GetString() != "@deepseek-ai/dsh")
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        return nodeModulesDirectory.FullName;
    }

    private static int RemoveManagedFallbackLinks(string rootDirectory, string previousInstallRoot)
    {
        var removed = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(rootDirectory));
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (entry is DirectoryInfo link)
                    {
                        var target = link.ResolveLinkTarget(returnFinalTarget: false);
                        if (target is not null && IsPathWithin(target.FullName, previousInstallRoot))
                        {
                            Directory.Delete(link.FullName);
                            removed++;
                        }
                    }

                    continue;
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory);
                }
            }
        }

        return removed;
    }

    private static bool IsPathWithin(string candidate, string root)
    {
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void AddWebArguments(ICollection<string> arguments, int port)
    {
        arguments.Add("web");
        arguments.Add("--host");
        arguments.Add("127.0.0.1");
        arguments.Add("--port");
        arguments.Add(port.ToString());
        arguments.Add("--no-open");
    }

    private static void ApplyDshHomeOverride(ProcessStartInfo startInfo, string? dshHomeOverride)
    {
        if (dshHomeOverride is not null)
        {
            startInfo.Environment["DSH_HOME"] = dshHomeOverride;
        }
    }

    private static void ValidateSystemNode()
    {
        var nodePath = FindOnPath("node.exe")
            ?? throw new FileNotFoundException(
                "找不到 Node.js。请安装 Node.js 22.19 或 24 及更高版本：https://nodejs.org/");

        var info = new ProcessStartInfo(nodePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("--version");

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("无法检查 Node.js 版本。");
        var output = process.StandardOutput.ReadToEnd().Trim().TrimStart('v');
        if (!process.WaitForExit(5000) || !Version.TryParse(output, out var version))
        {
            throw new InvalidOperationException("无法读取 Node.js 版本，请重新安装 Node.js。");
        }

        var supported = version.Major >= 24 || version.Major == 22 && version.Minor >= 19;
        if (!supported)
        {
            throw new InvalidOperationException(
                $"当前 Node.js {version} 不受 DeepSeek Harness 支持。请安装 Node.js 22.19 或 24 及更高版本。");
        }
    }

    private static ProcessStartInfo CmdStartInfo(string command, string workingDirectory, params string[] arguments)
    {
        var info = CommonStartInfo("cmd.exe", workingDirectory);
        var commandLine = $"\"{command}\" {string.Join(' ', arguments.Select(QuoteCmdArgument))}";
        info.Arguments = $"/d /s /c \"{commandLine}\"";
        return info;
    }

    private static string QuoteCmdArgument(string value)
    {
        if (value.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static ProcessStartInfo CommonStartInfo(string fileName, string workingDirectory) => new()
    {
        FileName = fileName,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var rawDirectory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed or inaccessible PATH entries.
            }
        }

        return null;
    }

    private static int FindAvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private Uri WithAvailableLoopbackPort() =>
        new($"http://127.0.0.1:{FindAvailableLoopbackPort()}/");

    private static bool CanBindLoopbackPort(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
            : $"{Math.Max(1, elapsed.Seconds)} 秒";

    private async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(AppUri, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return ProbeResult.OtherHttpServer;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return (html.Contains("window.__DSH_BOOT__", StringComparison.Ordinal) ||
                    html.Contains("globalThis[\"__DSH_BOOT__\"]", StringComparison.Ordinal) ||
                    html.Contains("<title>DeepSeek Harness</title>", StringComparison.OrdinalIgnoreCase))
                ? ProbeResult.DeepSeekHarness
                : ProbeResult.OtherHttpServer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Unavailable;
        }
        catch (HttpRequestException)
        {
            return ProbeResult.Unavailable;
        }
    }

    private void HandleServerOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        const string endpointPrefix = "dsh web:";
        if (line.StartsWith(endpointPrefix, StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(line[endpointPrefix.Length..].Trim(), UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttp &&
            IPAddress.TryParse(endpoint.Host, out var address) &&
            IPAddress.IsLoopback(address) &&
            endpoint.Port == AppUri.Port)
        {
            AppUri = endpoint;
            AppendLog($"dsh web: {endpoint.GetLeftPart(UriPartial.Path)}?token=<redacted>");
            return;
        }

        AppendLog(line);
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_logLock)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var formatted = $"[{timestamp}] {line}";
            _logs.AppendLine(formatted);
            try
            {
                _persistentLog?.WriteLine(formatted);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The in-memory log remains available if persistent logging fails mid-run.
            }
            if (_logs.Length > MaxLogCharacters)
            {
                _logs.Remove(0, _logs.Length - MaxLogCharacters * 3 / 4);
            }
        }
    }

    private void ReportProgress(string message)
    {
        AppendLog(message);
        ProgressChanged?.Invoke(this, message);
    }

    private void DisposeExitedProcess()
    {
        if (_process is not { HasExited: true })
        {
            return;
        }

        CloseOwnedJob();
        _process.Dispose();
        _process = null;
        _activeDshHomeOverride = null;
    }

    private void StopOwnedServer()
    {
        var process = _process;
        _process = null;
        _activeDshHomeOverride = null;
        if (process is null)
        {
            return;
        }

        try
        {
            CloseOwnedJob();
            if (!process.HasExited)
            {
                AppendLog("Stopping the DeepSeek Harness process tree.");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void CloseOwnedJob()
    {
        var jobHandle = _jobHandle;
        _jobHandle = IntPtr.Zero;
        if (jobHandle != IntPtr.Zero)
        {
            CloseHandle(jobHandle);
        }
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (jobHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 Windows 作业。");
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(jobHandle, 9, pointer, (uint)length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法配置 Windows 作业。");
            }

            return jobHandle;
        }
        catch
        {
            CloseHandle(jobHandle);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr jobHandle,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr jobHandle, IntPtr processHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public void Dispose()
    {
        StopOwnedServer();
        _httpClient.Dispose();
        lock (_logLock)
        {
            _persistentLog?.Dispose();
        }
    }

    private static bool PathsEqualOrBothNull(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return PathsEqual(left, right);
    }

    private static (string? Path, StreamWriter? Writer) CreatePersistentLog()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekHarness",
                "logs"),
            Path.Combine(Path.GetTempPath(), "DeepSeekHarness", "logs")
        };

        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(
                    directory,
                    $"desktop-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
                return (
                    path,
                    new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                    {
                        AutoFlush = true
                    });
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }

        return (null, null);
    }
}
