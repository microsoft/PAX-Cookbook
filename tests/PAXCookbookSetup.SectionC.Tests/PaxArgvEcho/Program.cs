using System.Text.Json;

using var output = Console.OpenStandardOutput();
using var writer = new Utf8JsonWriter(output);

writer.WriteStartObject();
writer.WriteNumber("count", args.Length);
writer.WriteStartArray("args");

foreach (var argument in args)
{
    writer.WriteStringValue(argument);
}

writer.WriteEndArray();
writer.WriteEndObject();
writer.Flush();

return args.Length == 0 ? 2 : 0;