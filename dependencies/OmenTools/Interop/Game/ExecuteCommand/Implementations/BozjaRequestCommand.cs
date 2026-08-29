using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public sealed class BozjaRequestCommand : ExecuteCommandBase
{
    /// <summary>
    ///     请求博兹雅战果记录数据更新
    /// </summary>
    public static void RequestWarResultNotebook() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestBozjaWarResultNotebook);

    /// <summary>
    ///     在博兹雅或高原副本区域以外地区查看失传技能库
    /// </summary>
    public static void RequestHolsterOutside() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestBozjaHolsterOutside);
}
