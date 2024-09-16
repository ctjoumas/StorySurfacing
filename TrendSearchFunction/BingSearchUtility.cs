namespace TrendSearchFunction
{
    using Newtonsoft.Json.Linq;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;

    internal class BingSearchUtility
    {
        private string BingSearchSubscriptionKey = Environment.GetEnvironmentVariable("BingSearchSubscriptionKey");
        private string BingApiBaseUrl = Environment.GetEnvironmentVariable("BingTrendsApiBaseUrl");

        // Bing uses the X-MSEdge-ClientID header to provide users with consistent
        // behavior across Bing API calls. See the reference documentation
        // for usage.
        private static string _clientIdHeader = null;

        public async Task<List<string>> GetTrendingNewsTopics()
        {
            List<string> trends = new List<string>();

            HttpResponseMessage response = await MakeRequestAsync();

            _clientIdHeader = response.Headers.GetValues("X-MSEdge-ClientID").FirstOrDefault();

            var contentString = await response.Content.ReadAsStringAsync();
            Dictionary<string, object> searchResponse = JsonConvert.DeserializeObject<Dictionary<string, object>>(contentString);

            if (response.IsSuccessStatusCode)
            {
                var trendingTopics = searchResponse["value"] as JToken;

                foreach (JToken trendingTopic in trendingTopics)
                {
                    Console.WriteLine("Name:\t\t" + trendingTopic["name"]);
                    Console.WriteLine("Query Text:\t" + trendingTopic["query"]["text"]);
                    Console.WriteLine();

                    if (trendingTopic != null)
                    {
                        trends.Add(trendingTopic["query"]["text"].ToString());
                    }
                }
            }
            else
            {
                Console.WriteLine("Error");
            }

            return trends;
        }

        // Makes the request to the News Search endpoint.
        public async Task<HttpResponseMessage> MakeRequestAsync()
        {
            var client = new HttpClient();

            // Request headers. The subscription key is the only required header but you should
            // include User-Agent (especially for mobile), X-MSEdge-ClientID, X-Search-Location
            // and X-MSEdge-ClientIP (especially for local aware queries).
            client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", BingSearchSubscriptionKey);

            return await client.GetAsync(BingApiBaseUrl);
        }
    }
}