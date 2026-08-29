using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public sealed class ContentInventoryCommand : ExecuteCommandBase
{
    /// <summary>
    ///     请求副本物品栏
    /// </summary>
    public static void Request(uint providerField) =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestContentInventory, providerField);
}
