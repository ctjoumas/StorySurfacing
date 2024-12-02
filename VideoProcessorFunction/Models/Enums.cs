using System.Text.Json.Serialization;

namespace VideoProcessorFunction.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ArmAccessTokenPermission
    {
        Reader,
        Contributor,
        MyAccessAdministrator,
        Owner,
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ArmAccessTokenScope
    {
        Account,
        Project,
        Video
    }

    public enum ProcessingState
    {
        Uploaded,
        Processing,
        Processed,
        Failed
    }
}