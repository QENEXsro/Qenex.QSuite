using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Protocols.Protocol;

public interface IProtocolVariable
{
    bool IsCommunicated { get; set; }
    IVariableBase Variable { get; set; }
    IProtVariableSpecification ProtocolVariableSpecification { get; set; }
    
    void NotifyValueChanged();
    event Action? OnValueChanged;
    void SubscribeValueChanged(Action handler);
    void UnsubscribeValueChanged(Action handler);
    
    Task NotifyValueChangedAsync();
    event Func<IProtocolVariable, Task>? OnValueChangedAsync;
    void SubscribeAsyncValueChanged(Func<IProtocolVariable, Task> handler);
    void UnsubscribeAsyncValueChanged(Func<IProtocolVariable, Task> handler);

    /// <summary>
    /// Asks the owning protocol to read this variable from the device once (On Request event).
    /// The module wires the command drivers to this notification the same way as to
    /// NotifyValueChangedAsync for writes. Unlike the value-changed notification the handlers'
    /// exceptions propagate to the caller, so the UI can show a failed read.
    /// </summary>
    Task RequestReadAsync(CancellationToken ct = default);
    event Func<IProtocolVariable, CancellationToken, Task>? OnReadRequestedAsync;
    void SubscribeAsyncReadRequested(Func<IProtocolVariable, CancellationToken, Task> handler);
    void UnsubscribeAsyncReadRequested(Func<IProtocolVariable, CancellationToken, Task> handler);
}