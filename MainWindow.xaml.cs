using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace DshDesktop;

public partial class MainWindow : Window
{
    private static readonly Uri PreferredAppUri = new("http://127.0.0.1:3080/");

    private readonly DshServerManager _server = new(PreferredAppUri);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private DesktopSettings _settings = DesktopSettings.Load();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _webViewReady;
    private bool _externalBrowserFallback;
    private bool _serviceConnected;
    private bool _isRecovering;
    private bool _isClosing;
    private int _automaticRecoveryAttempts;

    public MainWindow()
    {
        InitializeComponent();
        WorkspaceText.Text = _settings.WorkingDirectory;
        WorkspaceText.ToolTip = _settings.WorkingDirectory;
        _server.ProgressChanged += Server_ProgressChanged;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void Server_ProgressChanged(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isClosing && StartupPanel.Visibility == Visibility.Visible)
            {
                StartupDetail.Text = message;
            }
        });
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await StartAndConnectAsync(restartOwnedServer: false);
        _ = MonitorServerAsync(_lifetime.Token);
    }

    private async Task<bool> StartAndConnectAsync(bool restartOwnedServer)
    {
        if (!await _operationGate.WaitAsync(0))
        {
            return false;
        }

        try
        {
            _serviceConnected = false;
            ShowStartup(
                restartOwnedServer ? "正在重新连接" : "正在启动 DeepSeek Harness",
                "正在检查本地服务...");
            SetStatus("正在启动", "#D99100");

            if (restartOwnedServer && _server.StartedByClient)
            {
                await _server.RestartAsync(_settings.WorkingDirectory, _lifetime.Token);
            }
            else
            {
                await _server.EnsureRunningAsync(_settings.WorkingDirectory, _lifetime.Token);
            }

            StartupDetail.Text = "服务已就绪，正在加载界面...";
            SetStatus("正在连接", "#2F6FED");

            if (Environment.GetEnvironmentVariable("DSH_SHELL") == "1")
            {
                ShowExternalBrowserFallback(
                    "当前进程运行在 Harness 文件沙箱中，内嵌浏览器不可用。请点击下方按钮在系统浏览器中打开聊天界面。");
                _serviceConnected = true;
                return true;
            }

            try
            {
                await EnsureWebViewAsync();
                Browser.Visibility = Visibility.Visible;
                Browser.CoreWebView2.Navigate(_server.AppUri.AbsoluteUri);
            }
            catch (Exception exception)
            {
                ShowExternalBrowserFallback(
                    $"内嵌浏览器初始化失败。请点击下方按钮在系统浏览器中打开聊天界面。\n\n{exception.Message}");
                _serviceConnected = true;
            }

            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception) when (_isClosing)
        {
            return false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady)
        {
            return;
        }

        var userDataFolder = ResolveWritableWebViewDataFolder();
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await Browser.EnsureCoreWebView2Async(environment);

        Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
        Browser.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
        Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        Browser.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.IsUserInitiated)
            {
                OpenExternal(args.Uri);
            }
        };
        Browser.CoreWebView2.ProcessFailed += (_, _) => Dispatcher.Invoke(() =>
        {
            SetStatus("页面进程异常", "#D92D20");
        });
        _webViewReady = true;
    }

    private void ShowStartup(string title, string detail)
    {
        _externalBrowserFallback = false;
        Browser.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        StartupPanel.Visibility = Visibility.Visible;
        StartupTitle.Text = title;
        StartupDetail.Text = detail;
        StartupProgress.IsIndeterminate = true;
    }

    private void ShowError(Exception exception)
    {
        _serviceConnected = false;
        _externalBrowserFallback = false;
        Browser.Visibility = Visibility.Collapsed;
        StartupPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        LogsBox.Visibility = Visibility.Collapsed;
        ErrorTitle.Text = "无法启动 DeepSeek Harness";
        ErrorMessage.Text = exception.Message;
        RetryCommandButton.Content = "重试";
        LogsCommandButton.Visibility = Visibility.Visible;
        LogsBox.Text = _server.Logs;
        LogsBox.ScrollToEnd();
        SetStatus("启动失败", "#D92D20");
    }

    private void ShowExternalBrowserFallback(string message)
    {
        _externalBrowserFallback = true;
        Browser.Visibility = Visibility.Collapsed;
        StartupPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        LogsBox.Visibility = Visibility.Collapsed;
        ErrorTitle.Text = "使用浏览器模式";
        ErrorMessage.Text = message;
        RetryCommandButton.Content = "在浏览器中打开";
        LogsCommandButton.Visibility = Visibility.Collapsed;
        SetStatus("浏览器模式", "#12B76A");
    }

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusIndicator.Fill = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    }

    private void Browser_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var destination) &&
            destination.Scheme != "about" &&
            !IsHarnessUri(destination))
        {
            e.Cancel = true;
            if (e.IsUserInitiated)
            {
                OpenExternal(destination.AbsoluteUri);
            }

            return;
        }

        _serviceConnected = false;
        StartupPanel.Visibility = Visibility.Visible;
        StartupTitle.Text = "正在加载 DeepSeek Harness";
        StartupDetail.Text = _server.AppUri.AbsoluteUri;
        SetStatus("正在加载", "#2F6FED");
    }

    private void Browser_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        BackButton.IsEnabled = Browser.CanGoBack;
        ForwardButton.IsEnabled = Browser.CanGoForward;

        if (e.IsSuccess)
        {
            _serviceConnected = true;
            StartupPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            Browser.Visibility = Visibility.Visible;
            SetStatus(_server.StartedByClient ? "客户端服务已连接" : "已连接现有服务", "#12B76A");
            return;
        }

        _serviceConnected = false;
        ShowError(new InvalidOperationException($"页面加载失败：{e.WebErrorStatus}"));
    }

    private bool IsHarnessUri(Uri uri) =>
        uri.Scheme.Equals(_server.AppUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals(_server.AppUri.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == _server.AppUri.Port;

    private async Task MonitorServerAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
                if (!_serviceConnected || _isRecovering)
                {
                    consecutiveFailures = 0;
                    continue;
                }

                if (await _server.IsHarnessReadyAsync(cancellationToken))
                {
                    consecutiveFailures = 0;
                    continue;
                }

                if (++consecutiveFailures < 2)
                {
                    continue;
                }

                consecutiveFailures = 0;
                var recovery = await Dispatcher.InvokeAsync(HandleServiceUnavailableAsync);
                await recovery;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_isClosing)
        {
        }
    }

    private async Task HandleServiceUnavailableAsync()
    {
        if (_isClosing || _isRecovering)
        {
            return;
        }

        _serviceConnected = false;
        if (_automaticRecoveryAttempts >= 1)
        {
            ShowError(new InvalidOperationException(
                "DeepSeek Harness 服务连接已中断。自动恢复未成功，请查看日志后重试。"));
            return;
        }

        _automaticRecoveryAttempts++;
        _isRecovering = true;
        try
        {
            ShowStartup("服务连接中断", "正在尝试自动恢复...");
            SetStatus("正在恢复", "#D99100");
            await StartAndConnectAsync(restartOwnedServer: _server.OwnsServerProcess);
        }
        finally
        {
            _isRecovering = false;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoBack)
        {
            Browser.GoBack();
        }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoForward)
        {
            Browser.GoForward();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_externalBrowserFallback)
        {
            OpenExternal(_server.AppUri.AbsoluteUri);
        }
        else if (_webViewReady)
        {
            Browser.Reload();
        }
        else
        {
            await StartAndConnectAsync(restartOwnedServer: false);
        }
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        _automaticRecoveryAttempts = 0;
        await StartAndConnectAsync(restartOwnedServer: true);
    }

    private void OpenBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternal(_server.AppUri.AbsoluteUri);
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_externalBrowserFallback)
        {
            OpenExternal(_server.AppUri.AbsoluteUri);
            return;
        }

        _automaticRecoveryAttempts = 0;
        await StartAndConnectAsync(restartOwnedServer: false);
    }

    private void ToggleLogsButton_Click(object sender, RoutedEventArgs e)
    {
        LogsBox.Text = _server.Logs;
        LogsBox.Visibility = LogsBox.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        LogsBox.ScrollToEnd();
    }

    private async void ChooseWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 DeepSeek Harness 工作目录",
            InitialDirectory = _settings.WorkingDirectory,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _settings.WorkingDirectory = dialog.FolderName;
        _settings.Save();
        WorkspaceText.Text = _settings.WorkingDirectory;
        WorkspaceText.ToolTip = _settings.WorkingDirectory;

        if (!_server.StartedByClient)
        {
            MessageBox.Show(
                this,
                "工作目录已保存。当前连接的是外部启动的服务，请先关闭该服务，再使用工具栏中的重新连接按钮。",
                "DeepSeek Harness",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _automaticRecoveryAttempts = 0;
        await StartAndConnectAsync(restartOwnedServer: true);
    }

    private static void OpenExternal(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // The embedded client remains usable when Windows has no URL handler.
        }
    }

    private static string ResolveWritableWebViewDataFolder()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekHarness",
                "WebView2"),
            Path.Combine(Path.GetTempPath(), "DeepSeekHarness", "WebView2")
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probePath = Path.Combine(candidate, $".write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probePath, string.Empty);
                File.Delete(probePath);
                return candidate;
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
            }
        }

        throw new UnauthorizedAccessException("找不到可写的 WebView2 用户数据目录。");
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;
        _serviceConnected = false;
        _lifetime.Cancel();
        _server.Dispose();
    }
}
