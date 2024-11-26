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

        /// <summary>
        /// Slug of the story/video, which we get from ENPS
        /// </summary>
        public string EnpsSlug { get; set; }
        /// <summary>
        /// XML details from ENPS
        /// </summary>
        public string EnpsMediaObject { get; set; }

        /// <summary>
        /// From person pulled from ENPS
        /// </summary>
        public string EnpsFromPerson { get; set; }

        /// <summary>
        /// Timestamp of the video creation, from ENPS
        /// </summary>
        public string EnpsVideoTimestamp { get; set; }

        /// <summary>
        /// Determines whether the story is set to be shared with all stations or not.
        /// </summary>
        public bool EnpsHearstShare { get; set; }
    }
}
