namespace CopperStart.Dos;

/// <summary>Narrow notification layout; state writes leave caller-owned fields untouched.</summary>
internal static class DosNotifyRequestStateFieldsCodec
{
    internal const int NameOffset = 0, FullNameOffset = 4, UserDataOffset = 8, FlagsOffset = 12;
    internal const int TargetOffset = 16, SignalNumberOffset = 20;
    internal const int Reserved0Offset = 24, Reserved1Offset = 28, Reserved2Offset = 32, Reserved3Offset = 36;
    internal const int MessageCountOffset = 40, HandlerOffset = 44;
}

internal static class DosNotifyMessageFieldsCodec
{
    internal const int ClassOffset = 20, CodeOffset = 24, RequestOffset = 26;
    internal const int Private0Offset = 30, Private1Offset = 34;
}

internal static class DosFileLockTaskCodec
{
    internal const int TaskOffset = 12;
}
