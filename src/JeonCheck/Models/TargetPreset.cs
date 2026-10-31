using System.Text.Json.Serialization;

namespace JeonCheck.Models;

public class TargetPresetList
{
    [JsonPropertyName("targets")]
    public List<TargetPreset> Targets { get; set; } = new();
}

public class TargetPreset
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("vendor")]
    public string Vendor { get; set; } = string.Empty;

    [JsonPropertyName("architecture")]
    public string Architecture { get; set; } = string.Empty;

    [JsonPropertyName("endian")]
    public string Endian { get; set; } = "little";

    [JsonPropertyName("pointerBits")]
    public int PointerBits { get; set; } = 32;

    [JsonPropertyName("intBits")]
    public int IntBits { get; set; } = 32;

    [JsonPropertyName("longBits")]
    public int LongBits { get; set; } = 32;

    [JsonPropertyName("platformXml")]
    public string PlatformXml { get; set; } = string.Empty;

    [JsonPropertyName("clangTarget")]
    public string ClangTarget { get; set; } = "armv7r-none-eabi";

    [JsonPropertyName("defaultStd")]
    public string DefaultStd { get; set; } = "c99";

    [JsonPropertyName("defines")]
    public List<string> Defines { get; set; } = new();

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}
