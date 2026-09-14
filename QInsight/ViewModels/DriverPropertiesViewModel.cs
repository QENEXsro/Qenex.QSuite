using System.Windows;
using Qenex.QInsight.Helpers;
using Qenex.QLibs.QUI;
using Qenex.QSuite.Common.CoreComm;
using Qenex.QSuite.Drivers.Driver;
using Qenex.QSuite.LogSystems.LogSystem;

namespace Qenex.QInsight.ViewModels;

public class DriverPropertiesViewModel : PropertyChangedBaseWithValidation, IPropertiesViewModel, IDisposable
{
    private readonly IDriverBase driver;
    private readonly Func<bool> isRuntimeRunning;
    private readonly EventAggregator eventAggregator;

    public DriverPropertiesViewModel(EventAggregator ea, IDriverBase driver, Func<bool>? isRuntimeRunning = null)
    {
        eventAggregator = ea;
        this.driver = driver;
        this.isRuntimeRunning = isRuntimeRunning ?? (() => false);

        // A driver that gave up reconnecting (reconnectAttempts exhausted) or failed to start is
        // Stopped/Faulted while the runtime keeps running on the other drivers. Start brings just
        // this driver (and its protocols) back without stopping the runtime.
        StartCommand = new RelayCommandAsync<object>(_ => StartDriverAsync(), _ => CanStart);

        driver.StateChanged += OnDriverStateChanged;
    }

    public string Name => driver.Specification.Name;
    public string Label => driver.Specification.Label;
    public string Description => driver.Specification.Description;
    public string Version => driver.Specification.Version.ToDisplayString();
    public string Author => driver.Specification.Author ?? string.Empty;
    public string Company => driver.Specification.Company ?? string.Empty;
    public string CreatedOn => driver.Specification.CreatedOn.ToString("yyyy/MM/dd");
    public bool IsEnabled => driver.IsEnabled;
    public string State => driver.State.ToString();
    public string StateMessage => driver.StateMessage ?? string.Empty;
    public int ProtocolCount => driver.Protocols.Count;

    public RelayCommandAsync<object> StartCommand { get; }

    /// <summary>Start is offered only while the runtime runs and this driver is not running.</summary>
    public bool CanStart =>
        isRuntimeRunning()
        && driver.IsEnabled
        && driver.State is CommunicationState.Stopped or CommunicationState.Faulted;

    public void RefreshDriver()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(Author));
        OnPropertyChanged(nameof(Company));
        OnPropertyChanged(nameof(CreatedOn));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateMessage));
        OnPropertyChanged(nameof(ProtocolCount));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.OnCanExecuteChanged();
    }

    public void Dispose()
    {
        driver.StateChanged -= OnDriverStateChanged;
    }

    private async Task StartDriverAsync()
    {
        try
        {
            eventAggregator.Publish(new LogMessage(LogLevel.Info, $"Driver '{driver.Label}' started by the operator."));
            await driver.StartAsync();
        }
        catch (Exception e)
        {
            eventAggregator.Publish(new LogMessage(LogLevel.Error, $"Driver '{driver.Label}' failed to start: {e.Message}"));
        }

        RefreshDriver();
    }

    // Driver state changes come from the driver's own threads; the bindings must be updated on the UI thread.
    private void OnDriverStateChanged(object? sender, CommunicationStateChangedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            RefreshDriver();
        }
        else
        {
            dispatcher.InvokeAsync(RefreshDriver);
        }
    }
}
