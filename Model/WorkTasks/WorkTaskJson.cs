using System.Text.Json;
using System.Text.Json.Serialization;

namespace Model.WorkTasks;

public static class WorkTaskJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var result=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        result.Converters.Add(new UtcDateTimeConverter());
        return result;
    }
    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var value=reader.GetDateTime();
            return value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value,DateTimeKind.Utc);
        }
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value,DateTimeKind.Utc));
    }
}
