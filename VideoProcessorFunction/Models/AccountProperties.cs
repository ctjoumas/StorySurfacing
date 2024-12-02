using System.Text.Json.Serialization;

namespace VideoProcessorFunction.Models
{
    public class AccountProperties
    {
        [JsonPropertyName("accountId")]
        public string Id { get; set; }
    }
}