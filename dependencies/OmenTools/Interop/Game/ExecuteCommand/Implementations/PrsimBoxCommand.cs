using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public sealed class PrsimBoxCommand : ExecuteCommandBase
{
    /// <summary>
    ///     请求投影台数据
    /// </summary>
    public static void Request() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestPrismBox);

    /// <summary>
    ///     取出投影台物品
    /// </summary>
    public static void Restore(uint prismBoxItemID) =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RestorePrsimBoxItem, prismBoxItemID);

    /// <summary>
    ///     将投影台中的套装物品还原
    /// </summary>
    public static void RestoreSetItem(uint prismBoxIndex, uint slotMaskLow, uint slotMaskHigh) =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RestorePrsimBoxSetItem, prismBoxIndex, slotMaskLow, slotMaskHigh);
}
