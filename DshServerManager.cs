using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace DshDesktop;

internal sealed class DshServerManager : IDisposable
{
    public const string HarnessVersion = "0.1.0-rc.7";

    private const int MaxLogCharacters = 512 * 1024;

    private enum ProbeResult
    {
        Unavailable,
        DeepSeekHarness,
        OtherHttpServer
    }

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(1.5) };
    private readonly StringBuilder _logs = new();
    private readonly object _logLock = new();
    private Process? _process;
    private IntPtr _jobHandle;

    public DshServerManager(Uri preferredAppUri)
    {
        AppUri = preferredAppUri;
    }

    public Uri AppUri { get; private set; }

    public event EventHandler<string>? ProgressChanged;

    public bool OwnsServerProcess => _process is not null || _jobHandle != IntPtr.Zero;

    public bool StartedByClient => OwnsServerProcess;

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

    public async Task EnsureRunningAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(cancellationToken);
        if (probe == ProbeResult.DeepSeekHarness)
        {
            AppendLog($"Using the existing DeepSeek Harness server at {AppUri}");
            return;
        }

        if (probe == ProbeResult.OtherHttpServer)
        {
            var previousPort = AppUri.Port;
            AppUri = new Uri($"http://127.0.0.1:{FindAvailableLoopbackPort()}/");
            AppendLog($"Port {previousPort} is occupied by another HTTP service; using {AppUri.Port} instead.");
        }

        DisposeExitedProcess();
        var startInfo = await BuildStartInfoAsync(workingDirectory, AppUri.Port, cancellationToken);
        StartServer(workingDirectory, startInfo);
        try
        {
            var deadline = Stopwatch.StartNew();

            while (deadline.Elapsed < TimeSpan.FromSeconds(120))
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

                if (probe == ProbeResult.OtherHttpServer)
                {
                    throw new InvalidOperationException(
                        $"端口 {AppUri.Port} 返回了非 DeepSeek Harness 页面。请查看启动日志。");
                }

                await Task.Delay(400, cancellationToken);
            }

            throw new TimeoutException("等待 DeepSeek Harness 首次安装或启动超时（120 秒）。请查看启动日志。");
        }
        catch
        {
            StopOwnedServer();
            throw;
        }
    }

    public async Task RestartAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        if (OwnsServerProcess)
        {
            StopOwnedServer();
        }

        await EnsureRunningAsync(workingDirectory, cancellationToken);
    }

    public async Task<bool> IsHarnessReadyAsync(CancellationToken cancellationToken) =>
        await ProbeAsync(cancellationToken) == ProbeResult.DeepSeekHarness;

    private void StartServer(string workingDirectory, ProcessStartInfo startInfo)
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
        process.OutputDataReceived += (_, args) => AppendLog(args.Data);
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
        CancellationToken cancellationToken)
    {
        var bundledNode = Path.Combine(AppContext.BaseDirectory, "runtime", "node", "node.exe");
        var bundledDsh = Path.Combine(
            AppContext.BaseDirectory,
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
            var bundled = CommonStartInfo(bundledNode, workingDirectory);
            bundled.ArgumentList.Add(bundledDsh);
            AddWebArguments(bundled.ArgumentList, port);
            return bundled;
        }

        var onlineSpec = OnlineRuntimeSpec.TryLoad(
            Path.Combine(AppContext.BaseDirectory, "runtime-mode.json"),
            HarnessVersion);
        if (onlineSpec is not null)
        {
            var provisioner = new OnlineRuntimeProvisioner(ReportProgress, AppendLog);
            var runtime = await provisioner.EnsureAsync(onlineSpec, cancellationToken);
            var online = CommonStartInfo(runtime.NodeExecutable, workingDirectory);
            online.ArgumentList.Add(runtime.DshEntryPoint);
            AddWebArguments(online.ArgumentList, port);
            return online;
        }

        ReportProgress("正在检查系统 Node.js 运行环境...");
        ValidateSystemNode();
        var npxCmd = FindOnPath("npx.cmd")
            ?? throw new FileNotFoundException(
                "找不到 npx。请安装 Node.js 22.19 或 24 及更高版本：https://nodejs.org/");

        return CmdStartInfo(
            npxCmd,
            workingDirectory,
            "--yes",
            $"@deepseek-ai/dsh@{HarnessVersion}",
            "web",
            "--host",
            "127.0.0.1",
            "--port",
            port.ToString());
    }

    private static void AddWebArguments(ICollection<string> arguments, int port)
    {
        arguments.Add("web");
        arguments.Add("--host");
        arguments.Add("127.0.0.1");
        arguments.Add("--port");
        arguments.Add(port.ToString());
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
            return html.Contains("window.__DSH_BOOT__", StringComparison.Ordinal)
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

    private void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_logLock)
        {
            _logs.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(line);
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
    }

    private void StopOwnedServer()
    {
        var process = _process;
        _process = null;
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
    }
}
