using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;

namespace CS2_Tags;

public class CS2_TagsConfig : BasePluginConfig
{
    [JsonPropertyName("ApiUrl")]
    public string ApiUrl { get; set; } = "https://api.nowaygamers.pt";

    [JsonPropertyName("UpdateIntervalSeconds")]
    public int UpdateIntervalSeconds { get; set; } = 10;

    [JsonPropertyName("PrefixEnabled")]
    public bool PrefixEnabled { get; set; } = true;

    [JsonPropertyName("PlayerCustomFont")]
    public string PlayerCustomFont { get; set; } = "{Grey}";

    [JsonPropertyName("PrefixSeparator")]
    public string PrefixSeparator { get; set; } = " | ";

    [JsonPropertyName("TagPrefix")]
    public string TagPrefix { get; set; } = "";

    [JsonPropertyName("TagSuffix")]
    public string TagSuffix { get; set; } = "";
}
