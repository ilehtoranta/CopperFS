using Amiga;

namespace CopperStart.Dos;

public static class NativeFileSystemAttributeCore
{
	public static uint RequiredSize(FileSystemQueryAttribute attribute) =>
		attribute == FileSystemQueryAttribute.MaxFileSize ||
		attribute == FileSystemQueryAttribute.NumBlocks ||
		attribute == FileSystemQueryAttribute.NumBlocksUsed
			? DosFileSystemAttributeCodec.QuadSize :
		attribute == FileSystemQueryAttribute.MaxFileNameLength ||
		attribute == FileSystemQueryAttribute.MaxVolumeNameLength ||
		attribute == FileSystemQueryAttribute.IsCaseSensitive ||
		attribute == FileSystemQueryAttribute.DeviceType
			? DosFileSystemAttributeCodec.LongSize : 0u;
}
