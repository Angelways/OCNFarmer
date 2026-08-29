using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public sealed class MobHuntCommand : ExecuteCommandBase
{
    /// <summary>
    ///     接受怪物狩猎通缉令
    /// </summary>
    public static void AcceptBill(uint availableMarkIndex, uint markID) =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.AcceptMobHuntBill, availableMarkIndex, markID);

    /// <summary>
    ///     放弃怪物狩猎通缉令
    /// </summary>
    public static void AbandonBill(uint availableMarkIndex, uint markID) =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.AbandonMobHuntBill, availableMarkIndex, markID);
}
