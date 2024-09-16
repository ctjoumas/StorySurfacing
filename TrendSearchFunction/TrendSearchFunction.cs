namespace TrendSearchFunction
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Threading.Tasks;
    using Azure;
    using Azure.AI.OpenAI;
    using Azure.Search.Documents;
    using Azure.Search.Documents.Indexes;
    using Azure.Search.Documents.Models;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;

    public class TrendSearchFunction
    {
        private string ServiceEndpoint = Environment.GetEnvironmentVariable("ServiceEndpoint");
        private string IndexName = Environment.GetEnvironmentVariable("IndexName");
        private string SearchServiceKey = Environment.GetEnvironmentVariable("SearchServiceKey");
        private string SemanticSearchConfigName = Environment.GetEnvironmentVariable("SemanticSearchConfigName");
        private string OpenAiApiKey = Environment.GetEnvironmentVariable("OpenAiApiKey");
        private string OpenAiEndpoint = Environment.GetEnvironmentVariable("OpenAiEndpoint");
        private string ModelDeployment = Environment.GetEnvironmentVariable("ModelDeployment");
        private string SendResultsLogicAppEndpoint = Environment.GetEnvironmentVariable("SendResultsLogicAppEndpoint");

        // Executes at 9am, 4pm, and 9pm M-F
        [FunctionName("TrendSearchWeekday")]
        public async Task Run([TimerTrigger("0 0 9,16,21 * * MON-FRI")]TimerInfo myTimer, ILogger log)
        {
            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            BingSearchUtility bingSearchUtility = new BingSearchUtility();

            List<string> trends = await bingSearchUtility.GetTrendingNewsTopics();

            // Initialize OpenAI client      
            var credential = new AzureKeyCredential(OpenAiApiKey);
            var openAIClient = new OpenAIClient(new Uri(OpenAiEndpoint), credential);

            // Initialize Azure Cognitive Search clients      
            var searchCredential = new AzureKeyCredential(SearchServiceKey);
            var indexClient = new SearchIndexClient(new Uri(ServiceEndpoint), searchCredential);
            var searchClient = indexClient.GetSearchClient(IndexName);

            TimeZoneInfo easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

            DateTime oneWeekEarlierDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.AddDays(-7), easternZone);
            string filter = $"DatePublished ge '{oneWeekEarlierDate.ToString("yyyy-MM-dd")}'";

            ArrayList searchResultList = new ArrayList();

            // TODO: decide on how many trends to search and search all trends
            for (int i = 0; i < 5; i++)
            {
                List<SearchResultDetails> trendSearchResults = await SemanticHybridSearch(searchClient, openAIClient, trends[i], filter, log);

                SearchResult searchResult = new SearchResult();
                searchResult.Trend = trends[i];
                searchResult.Details = trendSearchResults.ToArray();

                searchResultList.Add(searchResult);
            }

            await sendSearchResults(searchResultList, log);
        }

        /*internal async Task SingleVectorSearch(SearchClient searchClient, OpenAIClient openAIClient, string query, string filter, ILogger log, int k = 3)
        {
            // Generate the embedding for the query      
            var queryEmbeddings = await GenerateEmbeddings(query, openAIClient);

            // Perform the vector similarity search
            var vector = new SearchQueryVector { KNearestNeighborsCount = 3, Fields = { "TextVector" }, Value = queryEmbeddings.ToArray() };
            var searchOptions = new SearchOptions
            {
                Vectors = { vector },
                Size = k,
                //Filter = filter,
                Select = { "VideoName", "DatePublished", "Text" },
            };

            SearchResults<SearchDocument> response = await searchClient.SearchAsync<SearchDocument>(query, searchOptions);

            int count = 0;
            await foreach (SearchResult<SearchDocument> result in response.GetResultsAsync())
            {
                count++;
                log.LogInformation($"Video Name: {result.Document["VideoName"]}");
                log.LogInformation($"Date Published: {result.Document["DatePublished"]}");
                log.LogInformation($"Score: {result.Score}\n");
                log.LogInformation($"Content: {result.Document["Text"]}");
            }
            log.LogInformation($"Total Results: {count}");
        }*/

        internal async Task<List<SearchResultDetails>> SemanticHybridSearch(SearchClient searchClient, OpenAIClient openAIClient, string query, string filter, ILogger log)
        {
            log.LogInformation($"Searching trend: {query}");

            // Generate the embedding for the query  
            var queryEmbeddings = await GenerateEmbeddings(query, openAIClient);

            // Perform the vector similarity search  
            var vector = new SearchQueryVector { KNearestNeighborsCount = 3, Fields = { "TextVector" }, Value = queryEmbeddings.ToArray() };
            var searchOptions = new SearchOptions
            {
                Vectors = { vector },
                Size = 10,
                //Filter = filter,
                QueryType = SearchQueryType.Semantic,
                QueryLanguage = QueryLanguage.EnUs,
                SemanticConfigurationName = SemanticSearchConfigName,
                QueryCaption = QueryCaptionType.Extractive,
                QueryAnswer = QueryAnswerType.Extractive,
                QueryCaptionHighlightEnabled = true,
                Select = { "VideoName", "EnpsVideoPath", "EnpsVideoOverviewText", "PossibleNetworkAffiliation", "DatePublished", "Text" },
            };

            SearchResults<SearchDocument> response = await searchClient.SearchAsync<SearchDocument>(query, searchOptions);

            List<SearchResultDetails> searchResultDetailsList = new List<SearchResultDetails>();

            int count = 0;
            await foreach (SearchResult<SearchDocument> result in response.GetResultsAsync())
            {
                count++;

                log.LogInformation($"Video Name: {result.Document["VideoName"]}");
                log.LogInformation($"ENPS Video Path: {result.Document["EnpsVideoPath"]}");
                log.LogInformation($"ENPS Video Overview Text: {result.Document["EnpsVideoOverviewText"]}");
                log.LogInformation($"Possible Network Affiliation: {result.Document["PossibleNetworkAffiliation"]}");
                log.LogInformation($"Date Published: {result.Document["DatePublished"]}");
                log.LogInformation($"Score: {result.Score}");
                log.LogInformation($"Reranker Score: {result.RerankerScore}\n");
                log.LogInformation($"Content: {result.Document["Text"]}\n");

                SearchResultDetails searchResultDetails = new SearchResultDetails();
                searchResultDetails.VideoName = (string)result.Document["VideoName"];
                searchResultDetails.EnpsVideoPath = (string)result.Document["EnpsVideoPath"];
                searchResultDetails.EnpsVideoOverviewText = (string)result.Document["EnpsVideoOverviewText"];
                searchResultDetails.PossibleNetworkAffiliation = (string)result.Document["PossibleNetworkAffiliation"];
                searchResultDetails.DatePublished = DateTime.Parse(result.Document["DatePublished"].ToString());
                searchResultDetails.Score = (double)result.Score;
                searchResultDetails.RerankerScore = (double)result.RerankerScore;

                searchResultDetailsList.Add(searchResultDetails);
            }

            log.LogInformation($"Total Results: {count}");

            return searchResultDetailsList;
        }

        // Function to generate embeddings      
        private async Task<IReadOnlyList<float>> GenerateEmbeddings(string text, OpenAIClient openAIClient)
        {
            var response = await openAIClient.GetEmbeddingsAsync(ModelDeployment, new EmbeddingsOptions(text));

            return response.Value.Data[0].Embedding;
        }

        /// <summary>
        /// Sends search results to the logic app endpointw which will format and email the results.
        /// </summary>
        /// <param name="searchResults"></param>
        /// <returns></returns>
        private async Task sendSearchResults(ArrayList searchResults, ILogger log)
        {
            string jsonResults = JsonConvert.SerializeObject(searchResults);

            var client = new HttpClient();

            HttpResponseMessage result = await client.PostAsync(
                SendResultsLogicAppEndpoint,
                new StringContent(jsonResults, Encoding.UTF8, "application/json"));

            //var statusCode = result.StatusCode.ToString();

            log.LogInformation("Successfully sent search results to LogicApp");
        }
    }
}