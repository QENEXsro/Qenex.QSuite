using Qenex.QSuite.Variables.QVariables;

namespace Qenex.QSuite.Protocols.Protocol;

/// <summary>
/// A protocol whose communicated variables are fed by script writes: the module routes every
/// successful script write of such a variable here (the scripting engine itself knows nothing
/// about protocols). The implementation must not block — the expected shape is
/// enqueue-into-buffer with a consumer loop publishing the notifications.
/// Such protocols keep running during replay, so scripts can be tested over a recorded data log.
/// </summary>
public interface IScriptWriteAwareProtocol
{
    void OnVariableWrittenByScript(IVariableBase variable);

    /// <summary>
    /// Same as above with the time the sample belongs to: the time of the write during a live
    /// session, the time of the last replayed sample during replay (so the script-computed
    /// sample lines up with the recorded ones). Protocols that do not implement it keep
    /// stamping the sample themselves.
    /// </summary>
    void OnVariableWrittenByScript(IVariableBase variable, DateTime timestampUtc)
    {
        OnVariableWrittenByScript(variable);
    }
}
