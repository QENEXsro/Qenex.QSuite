using Qenex.QSuite.LogSystems.LogSystem;
using Qenex.QSuite.Specifications.ComponentSpecification;
using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Protocols.Protocol;

public class ProtocolVariable : IProtocolVariable
{
    public bool IsCommunicated { get; set; }
    public IVariableBase Variable { get; set; }
    public IProtVariableSpecification ProtocolVariableSpecification { get; set; }

    // Diagnostics: subscriber exceptions (graph, scripts, data logger) used to be swallowed
    // silently (catch {}), hiding logging dropouts. Logger is set by the protocol in ProtocolBase.AddVariable.
    public ILogger? Logger { get; set; }

    public void NotifyValueChanged()
    {
        var handlers = OnValueChanged?.GetInvocationList();
        if (handlers == null)
        {
            return;
        }

        foreach (Action handler in handlers)
        {
            try
            {
                handler();
            }
            catch (Exception e)
            {
                LogSubscriberException(nameof(NotifyValueChanged), e);
            }
        }
    }
    public event Action? OnValueChanged;
    public void SubscribeValueChanged(Action handler)
    {
        OnValueChanged += handler;
    }
    public void UnsubscribeValueChanged(Action handler)
    {
        OnValueChanged -= handler;
    }

    public async Task NotifyValueChangedAsync()
    {
        var handlers = OnValueChangedAsync?.GetInvocationList();
        if (handlers == null)
        {
            return;
        }

        var tasks = handlers
            .Cast<Func<IProtocolVariable, Task>>()
            .Select(NotifySubscriberAsync)
            .ToList();

        await Task.WhenAll(tasks);
    }
    public event Func<IProtocolVariable, Task>? OnValueChangedAsync;
    public void SubscribeAsyncValueChanged(Func<IProtocolVariable, Task> handler)
    {
        OnValueChangedAsync += handler;
    }
    public void UnsubscribeAsyncValueChanged(Func<IProtocolVariable, Task> handler)
    {
        OnValueChangedAsync -= handler;
    }

    /// <summary>
    /// On-request read: the handlers (module → command driver → protocol) run one after another
    /// and their exceptions are NOT swallowed — a failed device read must reach the requesting
    /// control. No handler subscribed (variable not served by a read-capable protocol, or the
    /// module is not running) is reported as an InvalidOperationException for the same reason.
    /// </summary>
    public async Task RequestReadAsync(CancellationToken ct = default)
    {
        var handlers = OnReadRequestedAsync?.GetInvocationList();
        if (handlers == null || handlers.Length == 0)
        {
            throw new InvalidOperationException(
                $"Variable '{Variable?.Name}' has no read handler — the protocol is not running or does not support on-request reads.");
        }

        foreach (Func<IProtocolVariable, CancellationToken, Task> handler in handlers)
        {
            await handler(this, ct);
        }
    }
    public event Func<IProtocolVariable, CancellationToken, Task>? OnReadRequestedAsync;
    public void SubscribeAsyncReadRequested(Func<IProtocolVariable, CancellationToken, Task> handler)
    {
        OnReadRequestedAsync += handler;
    }
    public void UnsubscribeAsyncReadRequested(Func<IProtocolVariable, CancellationToken, Task> handler)
    {
        OnReadRequestedAsync -= handler;
    }

    private async Task NotifySubscriberAsync(Func<IProtocolVariable, Task> handler)
    {
        try
        {
            await handler(this);
        }
        catch (Exception e)
        {
            LogSubscriberException(nameof(NotifyValueChangedAsync), e);
        }
    }

    private void LogSubscriberException(string source, Exception e)
    {
        var message =
            $"Value-changed subscriber threw in {source} for variable '{Variable?.Name}' (Id {Variable?.Id}): {e.Message}";
        Logger?.Log(LogLevel.Error, message, e);
    }
}
