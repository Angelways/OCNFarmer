using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public sealed class PartyCommand : ExecuteCommandBase
{
    /// <summary>
    ///     请求加载小队成员角色数据
    /// </summary>
    public static void LoadMember(uint index, uint entityID) =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.LoadPartyMember, index, entityID);
}
