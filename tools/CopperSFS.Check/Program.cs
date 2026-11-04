using System.Text.Json;
using CopperSFS.Check;

if (args.Length is < 1 or > 3)
{
    Console.Error.WriteLine("Usage: CopperSFS.Check <raw-partition-image> [block-bytes=512] [reserved-blocks=1]");
    return 2;
}
try
{
    using var input = File.OpenRead(args[0]);
    var report = SfsImageChecker.Check(input, args.Length > 1 ? int.Parse(args[1]) : 512,
        args.Length > 2 ? uint.Parse(args[2]) : 1);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
