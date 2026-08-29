using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public sealed class LeveCommand : ExecuteCommandBase
{
    /// <summary>
    ///     放弃理符任务
    /// </summary>
    /// <seealso cref="Leve" />
    public static void Abandon(uint leveID) =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.AbandonLeveQuest, leveID);

    /// <summary>
    ///     开始理符任务
    /// </summary>
    public static void Start(uint levequestID, uint levelToIncrease) =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.StartLeveQuest, levequestID, levelToIncrease);

    /// <summary>
    ///     刷新理符任务状态
    /// </summary>
    public static void Refresh() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RefreshLeveQuest);

    /// <summary>
    ///     标记理符任务可被再次接取
    /// </summary>
    public static void MarkReadyToAccept(uint leveID) =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.MarkLeveReadyToAccept, leveID);
}
