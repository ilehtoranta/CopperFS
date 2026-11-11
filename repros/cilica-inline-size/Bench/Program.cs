using InlineSize;
using System.Diagnostics;
using System.Text.Json;

int iterations = args.Length > 0 ? int.Parse(args[0]) : 3_000_000;
if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
var words = new[] { 42, 1, 2, 3, 4, 5, 6, 7 };
var device = new Device(words);
var state = new State();
for (var i = 0; i < 20_000; i++)
    if (!Filesystem.Write(ref device, ref state, 42)) throw new InvalidOperationException("Warmup failed.");
long before = Stopwatch.GetTimestamp();
for (var i = 0; i < iterations; i++)
    if (!Filesystem.Write(ref device, ref state, 42)) throw new InvalidOperationException("Operation failed.");
long elapsed = Stopwatch.GetTimestamp() - before;
Console.WriteLine(JsonSerializer.Serialize(new {
    iterations, elapsedTicks = elapsed, ticksPerSecond = Stopwatch.Frequency,
    nsPerOperation = elapsed * 1_000_000_000.0 / Stopwatch.Frequency / iterations,
    state.Error, words
}));
