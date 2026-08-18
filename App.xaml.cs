using System.Windows;
using System.Windows.Threading;

namespace DshDesktop;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\DeepSeekHarnessDesktop.Instance";
    private const string ActivationEventName = @"Local\DeepSeekHarnessDesktop.Activate";

    private readonly CancellationTokenSource _shutdown = new();
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private Task? _activationTask;
    private bool _ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        _instanceMutex = new Mutex(
            initiallyOwned: true,
            InstanceMutexName,
            out _ownsInstanceMutex);

        if (!_ownsInstanceMutex)
        {
            _activationEvent.Set();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        _activationTask = Task.Run(WaitForActivation);
    }

    private void WaitForActivation()
    {
        if (_activationEvent is null)
        {
            return;
        }

        var handles = new[] { _activationEvent, _shutdown.Token.WaitHandle };
        while (WaitHandle.WaitAny(handles) == 0)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                () => (MainWindow as MainWindow)?.BringToFront());
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        _activationTask?.Wait(1000);

        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _activationEvent?.Dispose();
        _instanceMutex?.Dispose();
        _shutdown.Dispose();
        base.OnExit(e);
    }
}
