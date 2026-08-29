using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public sealed class FishingCommand : ExecuteCommandBase
{
    /// <summary>
    ///     抛竿
    /// </summary>
    public static void Cast() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fishing);

    /// <summary>
    ///     收杆
    /// </summary>
    public static void Quit() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fishing, 1);

    /// <summary>
    ///     提钩
    /// </summary>
    public static void Hook() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fishing, 2);

    /// <summary>
    ///     换饵
    /// </summary>
    public static void ChangeBait(uint itemID) =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fishing, 4, itemID);

    /// <summary>
    ///     坐下
    /// </summary>
    public static void Sit() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fishing, 5);

    /// <summary>
    ///     强力提钩
    /// </summary>
    public static void PowerfulHookset() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fishing, 10);

    /// <summary>
    ///     精准提钩
    /// </summary>
    public static void PrecisionHookset() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fishing, 11);

    /// <summary>
    ///     耐心
    /// </summary>
    public static void Patience() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fishing, 13);

    /// <summary>
    ///     耐心 2
    /// </summary>
    public static void Patience2() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fishing, 14);

    /// <summary>
    ///     熟练妙招
    /// </summary>
    public static void ThaliaksFavor() =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fishing, 24);

    /// <summary>
    ///     游动饵
    /// </summary>
    public static void SwimBait(uint baitIndex) =>
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Fishing, 25, baitIndex);
}
