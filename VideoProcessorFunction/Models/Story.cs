using System;
using System.Collections.Generic;
using VideoProcessorFunction.Services;

namespace VideoProcessorFunction.Models
{
    public record Story : ICosmosDbEntity
    {
        [Newtonsoft.Json.JsonProperty(PropertyName = "id")]
        public string Id { get; set; }
        [Newtonsoft.Json.JsonProperty(PropertyName = "StationName")]
        public string PartitionKey { get; set; }
        public string VideoName { get; set; }
        public string VideoId { get; set; }
        public DateTime StoryDateTime { get; set; }
        public List<string> Topics { get; set; }
    }
}
