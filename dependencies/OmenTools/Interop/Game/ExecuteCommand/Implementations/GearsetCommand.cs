using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public sealed class GearsetCommand : ExecuteCommandBase
{
    /// <summary>
    ///     更换套装
    /// </summary>
    public static void Change() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.ChangeGearset);
}
