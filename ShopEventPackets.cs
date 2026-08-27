using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace NorthIslandChestPlugin;

/// <summary>远程 EventStart / EventComplete，避免 InteractWithObject 后未结束事件导致 UI 锁死。</summary>
internal static unsafe class ShopEventPackets
{
    private const uint SendPacketFlag = 0x9876543;

    private delegate void SendPacketFn(NetworkModuleProxy* module, byte* packet, uint a3, uint a4);

    private static SendPacketFn? sendPacket;

    internal static int EventStartOpcode { get; private set; }
    internal static int EventCompleteOpcode { get; private set; }
    internal static bool Ready => sendPacket != null && EventStartOpcode != 0 && EventCompleteOpcode != 0;

    internal static void Initialize(ISigScanner scanner, IPluginLog log)
    {
        try
        {
            var startText = scanner.ScanText(
                "C7 44 24 ?? ?? ?? ?? ?? 48 C7 44 24 ?? ?? ?? ?? ?? 89 5C 24 ?? 0F 85");
            EventStartOpcode = Marshal.ReadInt32(startText + 4);

            var completeText = scanner.ScanText("E8 ?? ?? ?? ?? EB 10 48 8B 0D ?? ?? ?? ??");
            EventCompleteOpcode = Marshal.ReadInt32(completeText + 0x117);

            var sendText = scanner.ScanText(
                "E8 ?? ?? ?? ?? 48 8B D6 48 8B CF E8 ?? ?? ?? ?? 48 8B 8C 24");
            sendPacket = Marshal.GetDelegateForFunctionPointer<SendPacketFn>(ResolveRelativeCall(sendText));

            log.Information(
                $"ShopEventPackets 就绪 EventStart={EventStartOpcode} EventComplete={EventCompleteOpcode}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "ShopEventPackets 初始化失败，自动买固定剂将无法正确结束 NPC 事件");
        }
    }

    internal static void SendEventStart(ulong objectId, uint eventId)
    {
        if (!Ready)
            return;

        var packet = new EventStartPacket
        {
            Opcode = EventStartOpcode,
            Length = 32,
            EventObjectId = objectId,
            EventId = eventId,
        };
        Send(in packet);
    }

    internal static void SendEventComplete(uint eventId)
    {
        if (!Ready || eventId == 0)
            return;

        var packet = new EventCompletePacket
        {
            Opcode = EventCompleteOpcode,
            Length = 32,
            EventId = eventId,
        };
        Send(in packet);
    }

    private static void Send<T>(in T packet) where T : unmanaged
    {
        if (sendPacket == null)
            return;

        var framework = Framework.Instance();
        if (framework == null)
            return;

        fixed (T* ptr = &packet)
            sendPacket(framework->NetworkModuleProxy, (byte*)ptr, 0, SendPacketFlag);
    }

    private static nint ResolveRelativeCall(nint address)
    {
        var relative = Marshal.ReadInt32(address + 1);
        return address + 5 + relative;
    }

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct EventStartPacket
    {
        [FieldOffset(0)] public int Opcode;
        [FieldOffset(8)] public uint Length;
        [FieldOffset(32)] public ulong EventObjectId;
        [FieldOffset(40)] public uint EventId;
        [FieldOffset(44)] public uint Category;
        [FieldOffset(48)] public uint Param;
    }

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct EventCompletePacket
    {
        [FieldOffset(0)] public int Opcode;
        [FieldOffset(8)] public uint Length;
        [FieldOffset(32)] public uint EventId;
        [FieldOffset(36)] public uint Category;
        [FieldOffset(40)] public uint Param0;
        [FieldOffset(44)] public uint Param1;
        [FieldOffset(48)] public uint Param2;
    }
}
