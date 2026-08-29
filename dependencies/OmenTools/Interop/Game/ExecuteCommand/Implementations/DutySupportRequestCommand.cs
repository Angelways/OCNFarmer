using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public sealed class DutySupportRequestCommand : ExecuteCommandBase
{
    /// <summary>
    ///     请求亲信战友数据
    /// </summary>
    public static void RequestTrustedFriend() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestTrustedFriend);

    /// <summary>
    ///     请求剧情辅助器数据
    /// </summary>
    public static void RequestDutySupport() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestDutySupport);
}
