using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AI003.Net
{
    /// <summary>
    /// The JSON envelope shared with the Go gateway and the Unreal client:
    /// <c>{"type":"ping","seq":1,"body":{...}}</c>.
    /// </summary>
    public sealed class Envelope
    {
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("seq")] public long Seq { get; set; }
        [JsonProperty("body")] public JObject Body { get; set; }

        /// <summary>Raw text of the frame, useful for logging.</summary>
        [JsonIgnore] public string Raw { get; set; }
    }
}
