using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public sealed class CastCommand : ExecuteCommandBase
{
    /// <summary>
    ///     中断咏唱
    /// </summary>
    public static void Cancel() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.CancelCast);
}
