using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Abstractions;
using OmenTools.OmenService;

namespace OmenTools.Interop.Game.ExecuteCommand.Implementations;

public unsafe class SalvageCommand : ExecuteCommandBase
{
    public void Execute(InventoryType type, ushort slot) =>
        ExecuteCommandManager.Instance().ExecuteCommand
        (
            ExecuteCommandFlag.EventFrameworkAction,
            0x390000,
            (uint)type,
            slot,
            InventoryManager.Instance()->GetInventorySlot(type, slot)->GetItemId()
        );
}
