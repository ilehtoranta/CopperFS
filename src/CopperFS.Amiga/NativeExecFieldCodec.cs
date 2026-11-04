using Amiga;

namespace CopperStart.Dos;

internal static class NativeExecFieldCodec
{
    internal const int IoCommandOffset = ExecLayout.IORequest.Command;
    internal const int IoFlagsOffset = ExecLayout.IORequest.Flags;
    internal const int IoErrorOffset = ExecLayout.IORequest.Error;
    internal const int IoDeviceOffset = ExecLayout.IORequest.Device;
    internal const int IoDataOffset = ExecLayout.IOStdReq.Data;
    internal const int IoLengthOffset = ExecLayout.IOStdReq.Length;
    internal const int IoByteOffset = ExecLayout.IOStdReq.Offset;
    internal const int IoActualOffset = ExecLayout.IOStdReq.Actual;
    internal const int NodeNameOffset = ExecLayout.Node.Name;
    internal const int NodeTypeOffset = ExecLayout.Node.Type;
    internal const int NodeSuccessorOffset = ExecLayout.Node.Successor;
    internal const int ListHeadOffset = ExecLayout.List.Head;
    internal const int MemoryLowerOffset = ExecLayout.MemHeader.Lower;
    internal const int MemoryUpperOffset = ExecLayout.MemHeader.Upper;
    internal const int LibraryOpenCountOffset = ExecLayout.Library.OpenCount;
    internal const int MessageReplyPortOffset = ExecLayout.Message.ReplyPort;
    internal const int MessageLengthOffset = ExecLayout.Message.Length;
    internal const int PortHeadOffset = ExecLayout.MsgPort.MessageList;
    internal const int MemoryListOffset = ExecLayout.ExecBase.MemList;
    internal const int ListTailOffset = ExecLayout.List.Tail;
    internal const int PortMessageListOffset = ExecLayout.MsgPort.MessageList;
}
