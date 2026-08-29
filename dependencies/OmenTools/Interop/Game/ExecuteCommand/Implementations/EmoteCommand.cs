using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public sealed class EmoteCommand : ExecuteCommandBase
{
    /// <summary>
    ///     打断当前正在进行的情感动作
    /// </summary>
    public static void Interrupt() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.InterruptEmote);

    /// <summary>
    ///     打断当前正在进行的特殊情感动作
    /// </summary>
    public static void InterruptSpecial() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.InterruptEmoteSpecial);
}
