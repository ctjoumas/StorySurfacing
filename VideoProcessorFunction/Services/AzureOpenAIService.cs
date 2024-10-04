namespace VideoProcessorFunction.Services
{
    using Newtonsoft.Json.Linq;
    using Newtonsoft.Json;
    using Polly;
    using Polly.Retry;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using VideoProcessorFunction.Schemas;

    public class AzureOpenAIService
    {
        private string SystemPrompt = @"
        You are an AI assistant that analyzes topics from a video and compares them to topics that a broadcasting company is interested in.
        You will be given the topics of interest for each broadcasting company and the video topics, both of which will be in structured JSON format.
        The topics of interest for each broadcasting company will be in the following format:
        ""stationTopics"": [
            {
              ""stationName"": ""WESH"",
              ""topics"": [
                ""politics"",
                ""weather"",
                ""sports""
              ]
            },
            {
              ""stationName"": ""NYC"",
              ""topics"": [
                ""politics"",
                ""crime"",
                ""entertainment""
              ]
            }
        ]

        Each node represents a station and the topics they are interested in.

        The video topics will be in the following format:
        ""videoTopics"": [
              ""politics"",
              ""weather"",
              ""sports""
        ]

        You need to compare the video topics with the topics of interest for each station and return a list of station names that that has at least one topic in common. The topics do not have to match word for word so you will use
        your judgement to determine a match. For example, if a topic of the video is ""baseball"", this would match a station topic of ""sports"" or ""camden yards"".

        
        instruction: Using the topics of interest for each station and the video topics, return a list of station names that are interested in the video topics.
        all_station_topics: {all_staton_topics}
        video_topics: {video_topics}

        Only return properly structured JSON as the response.
        ";

        private readonly string _azureOpenAIUrl;
        private readonly string _apiKey;
        //private readonly IHttpClientFactory _httpClientFactory;
        private readonly ISchemaLoader _schemaLoader;
        private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(5); // Limit concurrency to 5 requests
        private readonly HttpClient _retryClient;

        // Retry policy with exponential backoff using Polly
        private static readonly AsyncRetryPolicy<HttpResponseMessage> retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(response => response.StatusCode == (System.Net.HttpStatusCode)429) // Handle 429 responses
            .WaitAndRetryAsync(
                retryCount: 5, // Retry up to 5 times
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // Exponential backoff
                onRetry: (outcome, timespan, retryAttempt, context) =>
                {
                    Console.WriteLine($"Retrying... Attempt: {retryAttempt} after {timespan.Seconds} seconds due to {outcome.Result.StatusCode}");
                }
            );

        public AzureOpenAIService()//IHttpClientFactory httpClientFactory)
        {
            _apiKey = Environment.GetEnvironmentVariable("AzureOpenAIKey", EnvironmentVariableTarget.Process);
            var endPoint = Environment.GetEnvironmentVariable("AzureOpenAIEndpoint", EnvironmentVariableTarget.Process);
            var modelName = Environment.GetEnvironmentVariable("ModelName", EnvironmentVariableTarget.Process);
            var apiVersion = Environment.GetEnvironmentVariable("ApiVersion", EnvironmentVariableTarget.Process);
            //_httpClientFactory = httpClientFactory;
            _azureOpenAIUrl = $"{endPoint}{modelName}/chat/completions?api-version={apiVersion}";
            _schemaLoader = new SchemaLoader(@".\Schemas");

            //using HttpClient _retryClient = _httpClientFactory.CreateClient();
            using HttpClient _retryClient = new HttpClient();
            _retryClient.DefaultRequestHeaders.Add("api-key", _apiKey);
            _retryClient.Timeout = TimeSpan.FromSeconds(60);
        }

        /// <summary>
        /// Uses the LLM to scan all station's interested topics and compare them to the topics of a video in order to determine
        /// which stations would be interested in this video.
        /// </summary>
        /// <param name="allStationTopics">JSON list of stations and the topics they have been broadcasting recently</param>
        /// <param name="videoTopics">JSON list of topics present in the given video</param>
        /// <returns>A JSON list of stations interested in the given video topics</returns>
        public async Task<string> GetChatResponseWithRetryAsync(string allStationTopics, string videoTopics)
        {
            // Ensure semaphore is in place for controlling concurrency
            await semaphore.WaitAsync();

            try
            {
                // Wrap the API call with the retry policy to handle transient errors
                var httpResponse = await retryPolicy.ExecuteAsync(async () =>
                {
                    //using HttpClient client = _httpClientFactory.CreateClient();
                    using HttpClient client = new HttpClient();
                    client.DefaultRequestHeaders.Add("api-key", _apiKey);

                    SystemPrompt = SystemPrompt.Replace("{all_staton_topics}", allStationTopics);
                    SystemPrompt = SystemPrompt.Replace("{video_topics}", videoTopics);

                    // Define the request payload for a chat completion
                    var requestPayload = new
                    {
                        messages = new[]
                        {
                        new { role = "system", content = SystemPrompt },
                        new { role = "user", content = "Using the provided JSON list of all station topics and the JSON list of topics from the given video, please identify all stations which have topics similar to the topics of the video." }
                        },
                        max_tokens = 4096,  // Define the maximum number of tokens
                        temperature = 0.7,  // Optional, controls randomness of the response
                        response_format = new
                        {
                            type = "json_schema",
                            json_schema = JObject.Parse(_schemaLoader.LoadSchema("InterestedStations.json"))
                        },
                    };

                    // Serialize the payload to JSON
                    var jsonPayload = JsonConvert.SerializeObject(requestPayload);

                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    // Send POST request
                    var response = await client.PostAsync(_azureOpenAIUrl, content);

                    return response;
                });

                // Get the response content
                var responseContent = await httpResponse.Content.ReadAsStringAsync();

                // Parse the JSON response
                JObject jsonResponse = JObject.Parse(responseContent);

                // Extract the content from the message inside choices[0]
                var messageContent = jsonResponse["choices"]?[0]?["message"]?["content"]?.ToString();

                return messageContent;
            }
            finally
            {
                // Release the semaphore once the request is complete
                semaphore.Release();
            }
        }
    }
}