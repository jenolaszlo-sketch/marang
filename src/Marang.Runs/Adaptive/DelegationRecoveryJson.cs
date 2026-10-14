using System.Text.Json;
using System.Text.Json.Serialization;
using Penghou.Qingniao;

namespace Marang.Runs.Adaptive;

/// <summary>Versioned host journal codecs for immutable Qingniao value objects; all reads validate constructors.</summary>
public static class DelegationRecoveryJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new StructuralNodeConverter());
        options.Converters.Add(new GenerationConverter());
        options.Converters.Add(new ContentConverter());
        options.Converters.Add(new CandidateConverter());
        options.Converters.Add(new ResultIdConverter());
        return options;
    }
    private sealed class StructuralNodeConverter : JsonConverter<StructuralNodeReference>
    {
        public override StructuralNodeReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        { using var doc = JsonDocument.ParseValue(ref reader); return new(doc.RootElement.GetProperty("Identifier").GetString()!); }
        public override void Write(Utf8JsonWriter writer, StructuralNodeReference value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new { value.Identifier }, options);
    }
    private sealed class GenerationConverter : JsonConverter<NodeGenerationId>
    {
        public override NodeGenerationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        { using var doc = JsonDocument.ParseValue(ref reader); return new(doc.RootElement.GetProperty("Value").GetGuid()); }
        public override void Write(Utf8JsonWriter writer, NodeGenerationId value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new { value.Value }, options);
    }
    private sealed class ContentConverter : JsonConverter<ArtifactContentIdentity>
    {
        public override ArtifactContentIdentity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        { using var doc = JsonDocument.ParseValue(ref reader); return new(doc.RootElement.GetProperty("ContractVersion").GetString()!, doc.RootElement.GetProperty("Hash").GetString()!); }
        public override void Write(Utf8JsonWriter writer, ArtifactContentIdentity value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new { value.ContractVersion, value.Hash }, options);
    }
    private sealed class CandidateConverter : JsonConverter<CandidateId>
    {
        public override CandidateId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        { using var doc = JsonDocument.ParseValue(ref reader); return new(doc.RootElement.GetProperty("Value").GetGuid()); }
        public override void Write(Utf8JsonWriter writer, CandidateId value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new { value.Value }, options);
    }
    private sealed class ResultIdConverter : JsonConverter<DelegationResultId>
    {
        public override DelegationResultId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        { using var doc = JsonDocument.ParseValue(ref reader); return new(doc.RootElement.GetProperty("Value").GetGuid()); }
        public override void Write(Utf8JsonWriter writer, DelegationResultId value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new { value.Value }, options);
    }
}
