using Amiga;

namespace CopperStart.Dos;

/// <summary>Handler-owned classic notifications, including absent path watches.</summary>
internal static class NativeFileSystemNotifications
{
    // Private notification chains must not overlap the SFS journal and
    // recovery state at offsets 52 and 56.
    private const int Watches = 1728, Messages = 1732;
    private const uint WatchBytes = 32;
    private const int Request = 4, Key = 8, Outstanding = 12, Pending = 16, Flags = 20, Target = 24, Signal = 28;
    private static uint R(APTR p, int o) => APTR.ReadUInt32(p, o);
    private static void W(APTR p, int o, uint v) => APTR.WriteUInt32(p, o, v);

    private static uint Resolve(APTR state, APTR request, out DOS.Error error)
    {
        var name = APTR.FromPointer(APTR.ReadUInt32(request, DosNotifyRequestStateFieldsCodec.FullNameOffset)); var length = 0u;
        while (APTR.ReadUInt8(name, (int)length) != 0) length++;
        var p = new NativeFileSystemPlatform(state);
        return AmigaDiskFileSystemCore.Resolve(ref p, p.Geometry.RootBlock, name, length,
            R(state, NativeAmigaFileSystem.DosTypeOffset), NativeAmigaFileSystem.Metadata(state), out error);
    }

    internal static int Add(APTR state, APTR request, out DOS.Error error)
    {
        error = DOS.Error.None;
        var watch = APTR.FromPointer(global::Amiga.Exec.AllocMem(WatchBytes,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (watch.IsNull) { error = DOS.Error.NoFreeStore; return 0; }
        W(watch, Request, request.Raw); W(watch, Flags, APTR.ReadUInt32(request, DosNotifyRequestStateFieldsCodec.FlagsOffset));
        W(watch, Target, APTR.ReadUInt32(request, DosNotifyRequestStateFieldsCodec.TargetOffset));
        W(watch, Signal, APTR.ReadUInt8(request, DosNotifyRequestStateFieldsCodec.SignalNumberOffset));
        var key = Resolve(state, request, out error);
        if (key == 0 && error != DOS.Error.ObjectNotFound && error != DOS.Error.DirectoryNotFound)
        { global::Amiga.Exec.FreeMem(watch.Raw, WatchBytes); return 0; }
        error = DOS.Error.None;
        W(watch, Key, key); W(watch, 0, R(state, Watches)); W(state, Watches, watch.Raw);
        if ((R(watch, Flags) & (uint)DosNotifyFlags.NotifyInitial) != 0 && key != 0 && !Send(state, watch))
        { W(state, Watches, R(watch, 0)); global::Amiga.Exec.FreeMem(watch.Raw, WatchBytes); error = DOS.Error.NoFreeStore; return 0; }
        return -1;
    }

    private static bool Send(APTR state, APTR watch)
    {
        var request = APTR.FromPointer(R(watch, Request)); if (request.IsNull) return false;
        var flags = R(watch, Flags);
        if ((flags & (uint)DosNotifyFlags.SendSignal) != 0)
        { global::Amiga.Exec.Signal(APTR.FromPointer(R(watch, Target)), 1u << (int)R(watch, Signal)); return true; }
        if ((flags & (uint)DosNotifyFlags.WaitReply) != 0 && R(watch, Outstanding) != 0)
        { W(watch, Pending, 1); return true; }
        var message = APTR.FromPointer(global::Amiga.Exec.AllocMem(NotifyMessage.Size,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (message.IsNull) { W(watch, Pending, 1); return false; }
        APTR.WriteUInt32(message, NativeExecFieldCodec.MessageReplyPortOffset, R(state, NativeAmigaFileSystem.PortOffset));
        APTR.WriteUInt16(message, NativeExecFieldCodec.MessageLengthOffset, (ushort)NotifyMessage.Size);
        APTR.WriteUInt32(message, DosNotifyMessageFieldsCodec.ClassOffset, NotifyMessage.Class);
        APTR.WriteUInt16(message, DosNotifyMessageFieldsCodec.CodeOffset, NotifyMessage.Code);
        APTR.WriteUInt32(message, DosNotifyMessageFieldsCodec.RequestOffset, request.Raw);
        APTR.WriteUInt32(message, DosNotifyMessageFieldsCodec.Private0Offset, watch.Raw); APTR.WriteUInt32(message, DosNotifyMessageFieldsCodec.Private1Offset, R(state, Messages));
        W(state, Messages, message.Raw); W(watch, Outstanding, R(watch, Outstanding) + 1); W(watch, Pending, 0);
        APTR.WriteUInt32(request, DosNotifyRequestStateFieldsCodec.MessageCountOffset, APTR.ReadUInt32(request, DosNotifyRequestStateFieldsCodec.MessageCountOffset) + 1);
        global::Amiga.Exec.PutMsg(APTR.FromPointer(R(watch, Target)), message);
        return true;
    }

    // Called only for messages actually removed from this handler's port.
    // Search the owned chain rather than trusting caller Node links/length.
    internal static bool Reply(APTR state, APTR message)
        => APTR.ReadUInt8(message, NativeExecFieldCodec.NodeTypeOffset) == (byte)NodeType.ReplyMessage &&
            RetireOwnedReply(state, message);

    private static bool RetireOwnedReply(APTR state, APTR message)
    {
        var link = APTR.FromPointer(state.Raw + Messages);
        for (var current = APTR.FromPointer(R(link, 0)); current.IsNotNull; current = APTR.FromPointer(R(link, 0)))
        {
            if (current == message)
            {
                W(link, 0, APTR.ReadUInt32(message, DosNotifyMessageFieldsCodec.Private1Offset));
                RetireMessage(state, message); return true;
            }
            link = APTR.FromPointer(current.Raw + DosNotifyMessageFieldsCodec.Private1Offset);
        }
        return false;
    }
    private static void RetireMessage(APTR state, APTR message)
    {
        var watch = APTR.FromPointer(APTR.ReadUInt32(message, DosNotifyMessageFieldsCodec.Private0Offset));
        var count = R(watch, Outstanding) - 1; W(watch, Outstanding, count);
        var request = APTR.FromPointer(R(watch, Request));
        if (request.IsNotNull) APTR.WriteUInt32(request, DosNotifyRequestStateFieldsCodec.MessageCountOffset, count);
        global::Amiga.Exec.FreeMem(message.Raw, NotifyMessage.Size);
        if (count != 0) return;
        if (request.IsNull) global::Amiga.Exec.FreeMem(watch.Raw, WatchBytes);
        else if (R(watch, Pending) != 0) Send(state, watch);
    }

    internal static int Remove(APTR state, APTR request)
    {
        var link = APTR.FromPointer(state.Raw + Watches);
        for (var watch = APTR.FromPointer(R(link, 0)); watch.IsNotNull; watch = APTR.FromPointer(R(link, 0)))
        {
            if (R(watch, Request) != request.Raw) { link = watch; continue; }
            W(link, 0, R(watch, 0)); W(watch, Pending, 0);
            // EndNotify removes queued deliveries. A message already held by
            // the application retains its private owner until its actual reply.
            global::Amiga.Exec.Forbid();
            if ((R(watch, Flags) & (uint)DosNotifyFlags.SendMessage) != 0)
            {
                var port = APTR.FromPointer(R(watch, Target));
                var messageLink = APTR.FromPointer(state.Raw + Messages);
                for (var message = APTR.FromPointer(R(messageLink, 0)); message.IsNotNull; message = APTR.FromPointer(R(messageLink, 0)))
                {
                    var queued = false;
                    if (APTR.ReadUInt32(message, DosNotifyMessageFieldsCodec.Private0Offset) == watch.Raw)
                        for (var node = APTR.ReadUInt32(port, NativeExecFieldCodec.PortHeadOffset); node != (port.Raw + NativeExecFieldCodec.PortMessageListOffset + 4); node = R(APTR.FromPointer(node), 0))
                            if (node == message.Raw) { global::Amiga.Exec.Remove(message); queued = true; break; }
                    if (!queued) { messageLink = APTR.FromPointer(message.Raw + DosNotifyMessageFieldsCodec.Private1Offset); continue; }
                    W(messageLink, 0, APTR.ReadUInt32(message, DosNotifyMessageFieldsCodec.Private1Offset));
                    W(watch, Outstanding, R(watch, Outstanding) - 1); global::Amiga.Exec.FreeMem(message.Raw, NotifyMessage.Size);
                }
            }
            APTR.WriteUInt32(request, DosNotifyRequestStateFieldsCodec.MessageCountOffset, 0); W(watch, Request, 0);
            if (R(watch, Outstanding) == 0) global::Amiga.Exec.FreeMem(watch.Raw, WatchBytes);
            global::Amiga.Exec.Permit(); return -1;
        }
        return -1;
    }

    internal static void Changed(APTR state, uint changed, uint oldParent = 0)
    {
        if (R(state, Watches) == 0) return;
        var p = new NativeFileSystemPlatform(state); var metadata = NativeAmigaFileSystem.Metadata(state);
        var parent = changed != 0 && AmigaDiskFileSystemCore.Header(ref p, changed, metadata)
            ? p.ReadUInt32(metadata, ((int)p.BlockBytes - 512) + 500) : 0;
        for (var watch = APTR.FromPointer(R(state, Watches)); watch.IsNotNull; watch = APTR.FromPointer(R(watch, 0)))
        {
            var previous = R(watch, Key); var current = Resolve(state, APTR.FromPointer(R(watch, Request)), out _);
            W(watch, Key, current);
            if (previous != current || current != 0 && (current == changed || current == parent || current == oldParent)) Send(state, watch);
        }
    }

    private static class WireCodec
    {
        internal const int DosLayout_NotifyMessage_MessageClass = DosLayout.NotifyMessage.MessageClass;
        internal const int DosLayout_NotifyMessage_MessageCode = DosLayout.NotifyMessage.MessageCode;
        internal const int DosLayout_NotifyMessage_Private0 = DosLayout.NotifyMessage.Private0;
        internal const int DosLayout_NotifyMessage_Private1 = DosLayout.NotifyMessage.Private1;
        internal const int DosLayout_NotifyMessage_Request = DosLayout.NotifyMessage.Request;
        internal const int DosLayout_NotifyRequest_Flags = DosLayout.NotifyRequest.Flags;
        internal const int DosLayout_NotifyRequest_FullName = DosLayout.NotifyRequest.FullName;
        internal const int DosLayout_NotifyRequest_MessageCount = DosLayout.NotifyRequest.MessageCount;
        internal const int DosLayout_NotifyRequest_Target = DosLayout.NotifyRequest.Target;
        internal const int ExecLayout_Message_Length = ExecLayout.Message.Length;
        internal const int ExecLayout_Message_ReplyPort = ExecLayout.Message.ReplyPort;
        internal const int ExecLayout_MsgPort_MessageList = ExecLayout.MsgPort.MessageList;
        internal const int ExecLayout_Node_Type = ExecLayout.Node.Type;
    }
}
