using System.Text.Json.Serialization;

namespace VideoProcessorFunction.Models
{
    public class Video
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }
    }
}
