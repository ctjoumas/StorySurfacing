using System.Text.Json.Serialization;

namespace VideoProcessorFunction.Models
{
    public class GenerateAccessTokenResponse
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; }
    }
}