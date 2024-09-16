namespace VideoProcessorFunction
{
    using Azure;
    using Azure.AI.OpenAI;
    using Azure.Search.Documents.Indexes;
    using Azure.Search.Documents.Indexes.Models;
    using Azure.Search.Documents.Models;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    internal class SearchServiceUtility
    {
        private const int MODEL_DIMENSIONS = 1536;

        private string ServiceEndpoint = Environment.GetEnvironmentVariable("ServiceEndpoint");
        private string IndexName = Environment.GetEnvironmentVariable("IndexName");
        private string SearchServiceKey = Environment.GetEnvironmentVariable("SearchServiceKey");
        private string OpenAiApiKey = Environment.GetEnvironmentVariable("OpenAiApiKey");
        private string OpenAiEndpoint = Environment.GetEnvironmentVariable("OpenAiEndpoint");
        private string ModelDeployment = Environment.GetEnvironmentVariable("ModelDeployment");
        private string SemanticSearchConfigName = Environment.GetEnvironmentVariable("SemanticSearchConfigName");

        /// <summary>
        /// Topics for the video, which will be used to construct the XML file feeding back into ENPS
        /// </summary>
        public string Topics { get; set; }

        /// <summary>
        /// Keywords from video indexer insights, including faces detected
        /// </summary>
        public string Keywords { get; set; }

        /// <summary>
        /// Parses the relevant pieces of data from the Video Indexer metadata from a video and indexes it into the cognitive
        /// search vector store.
        /// </summary>
        /// <param name="videoMetadataJson">The JSON returned from the video indexer for the video which was uploaded</param>
        /// <param name="log">Logger for logging to the console</param>
        public async Task IndexVideoMetadata(string videoMetadataJson, string enpsVideoPath, string enpsVideoOverviewText, string possibleNetworkAffiliation, ILogger log)
        {
            // Initialize OpenAI client      
            var credential = new AzureKeyCredential(OpenAiApiKey);
            var openAIClient = new OpenAIClient(new Uri(OpenAiEndpoint), credential);

            // Initialize Azure Cognitive Search clients      
            var searchCredential = new AzureKeyCredential(SearchServiceKey);
            var indexClient = new SearchIndexClient(new Uri(ServiceEndpoint), searchCredential);
            var searchClient = indexClient.GetSearchClient(IndexName);

            // if the index doesn't exist, create it
            try
            {
                Response<SearchIndex> response = indexClient.GetIndex(IndexName);
            }
            catch (RequestFailedException ex)
            {
                if (ex.Status == 404)
                {
                    Console.WriteLine("Creating index...");

                    // Create the search index      
                    indexClient.CreateOrUpdateIndex(CreateIndex(IndexName));
                }
            }

            JObject videoIndexerJsonObject = JObject.Parse(videoMetadataJson);

            // Grab name of video and it's ID so it can be added to cognitive search document for each entry / document
            string videoName = videoIndexerJsonObject.SelectToken("name").ToString();
            string videoId = videoIndexerJsonObject.SelectToken("videos[0].id").ToString();

            log.LogInformation($"Indexing video: {videoName} with videoId: {videoId}");

            /*
               Create one single text vector to include:
               [Video context]
               Topics of this video are: Politics, Sports
               Celebrities appearing in this video: LeBron James, MJ
               OCR: MUSEUM, 
               Transcript: ...
             */

            StringBuilder sbTextVector = new StringBuilder();

            // We are going to create embeddings from topics, labels, keywords, and faces
            string topics = GetMetadataFromVideoIndexer(videoIndexerJsonObject, "topics", "name");
            if (topics.Length > 0)
            {
                sbTextVector.Append("Topics of this video are: ");
                sbTextVector.Append(topics);

                Topics += sbTextVector.ToString();
            }

            string faces = GetMetadataFromVideoIndexer(videoIndexerJsonObject, "faces", "name");
            if (faces.Length > 0)
            {
                sbTextVector.Append("Celebrities appearing in this video: ");
                sbTextVector.Append(faces);

                Keywords += sbTextVector.ToString();
            }

            string keywords = GetMetadataFromVideoIndexer(videoIndexerJsonObject, "keywords", "name");
            if (keywords.Length > 0)
            {
                Keywords += sbTextVector.ToString();
            }

            string ocr = GetMetadataFromVideoIndexer(videoIndexerJsonObject, "ocr", "text");
            if (ocr.Length > 0)
            {
                sbTextVector.Append("OCR: ");
                sbTextVector.Append(ocr);
            }

            string transcript = GetMetadataFromVideoIndexer(videoIndexerJsonObject, "transcript", "text");
            if (transcript.Length > 0)
            {
                sbTextVector.Append("Transcript: ");
                sbTextVector.Append(transcript);
            }

            log.LogInformation("Indexing the following content for this video:");
            log.LogInformation(sbTextVector.ToString());

            // Generate the embedding for the video content
            var textEmbeddings = await GenerateEmbeddings(sbTextVector.ToString(), openAIClient);

            // TODO: If the created/published date of the video exists in ENPS, we will pull that datetime from there
            TimeZoneInfo easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            DateTime currentTimeEst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, easternZone);

            IndexDocumentsBatch<IndexedVideo> batch = IndexDocumentsBatch.Create(IndexDocumentsAction.Upload(
                new IndexedVideo()
                {
                    ID = videoId,
                    VideoName = videoName,
                    EnpsVideoPath = enpsVideoPath,
                    EnpsVideoOverviewText = enpsVideoOverviewText,
                    PossibleNetworkAffiliation = possibleNetworkAffiliation,
                    DatePublished = currentTimeEst,
                    Text = sbTextVector.ToString(),
                    TextVector = textEmbeddings.ToArray(),
                }));

            try
            {
                IndexDocumentsResult result = searchClient.IndexDocuments(batch);
            }
            catch (Exception)
            {
                // If for some reason any documents are dropped during indexing, you can compensate by delaying and
                // retrying. This simple demo just logs the failed document keys and continues.
                log.LogInformation("Failed to index some of the documents: {0}");
            }
        }

        /// <summary>
        /// Creates the index in the cognitive store.
        /// </summary>
        /// <param name="name">The name of the index we are creating</param>
        /// <returns></returns>
        internal SearchIndex CreateIndex(string name)
        {
            /*
             * Create one single text vector to include:
               [Video context]
               Topics of this video are: Politics, Sports
               Celebrities appearing in this video: LeBron James, MJ
               OCR: MUSEUM, 
               Transcript: ...

               The video name will be the key
             */

            string vectorSearchConfigName = "my-vector-config";

            // Only setting DatePublished field to be filterable. By default, the .NET SDK sets the filterable is off by default and
            // fitlerable fields will cause the index to be larger:
            // https://learn.microsoft.com/en-us/azure/search/search-filters#field-requirements-for-filtering

            SearchIndex searchIndex = new(name)
            {
                VectorSearch = new()
                {
                    AlgorithmConfigurations =
                    {
                        new HnswVectorSearchAlgorithmConfiguration(vectorSearchConfigName)
                    }
                },
                SemanticSettings = new()
                {
                    Configurations =
                    {
                        new SemanticConfiguration(SemanticSearchConfigName, new()
                        {
                            ContentFields =
                            {
                                new() { FieldName = "Text" }
                            }
                        })
                    }
                },
                Fields =
                {
                    new SimpleField("ID", SearchFieldDataType.String) { IsKey = true },
                    new SearchableField("VideoName") { IsSortable = true },
                    new SimpleField("EnpsVideoPath", SearchFieldDataType.String) { },
                    new SimpleField("EnpsVideoOverviewText", SearchFieldDataType.String) { },
                    new SimpleField("PossibleNetworkAffiliation", SearchFieldDataType.String) { },
                    new SearchableField("DatePublished") { IsFilterable = true, IsSortable = true },
                    new SearchableField("Text") { },
                    new SearchField("TextVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                    {
                        IsSearchable = true,
                        VectorSearchDimensions = MODEL_DIMENSIONS,
                        VectorSearchConfiguration = vectorSearchConfigName
                    }
                }
            };

            return searchIndex;
        }

        /// <summary>
        /// Pull out each metadata type in the Video Indexer JSON and concatenates it in a spaced string. E.g., for
        /// topics metadata, we would return something like "Music Entertainment Songs"
        /// </summary>
        /// <param name="videoIndexerJsonObject">The Video Indexer JSON</param>
        /// <param name="metadataType">The metadata type we are pulling from the JSON</param>
        /// <param name="metadataField">The name of the metadata type we are interested in</param>
        /// <returns>The metadata type data, separated by spaces</returns>
        private string GetMetadataFromVideoIndexer(JObject videoIndexerJsonObject, string metadataType, string metadataField)
        {
            StringBuilder sbMetadataType = new StringBuilder();
            JToken metadataTypeTokens = videoIndexerJsonObject.SelectToken($"videos[0].insights.{metadataType}");

            if (metadataTypeTokens != null)
            {
                foreach (var metadataTypeToken in metadataTypeTokens)
                {
                    // if we are not looking for recognized faces or we are looking for recognized faces and the face
                    // is a known person, add the data
                    if (!metadataType.Equals("faces") ||
                        (metadataType.Equals("faces") && !metadataTypeToken[metadataField].ToString().Contains("Unknown")))
                    {
                        sbMetadataType.Append(metadataTypeToken[metadataField]);
                        sbMetadataType.Append(" ");
                    }
                }
            }

            return sbMetadataType.ToString();
        }

        /// <summary>
        /// Generats embeddings based on a given text, which will generate a float array of values as a vector representation
        /// of the text.
        /// </summary>
        /// <param name="text"></param>
        /// <param name="openAIClient"></param>
        /// <returns></returns>
        private async Task<IReadOnlyList<float>> GenerateEmbeddings(string text, OpenAIClient openAIClient)
        {
            var response = await openAIClient.GetEmbeddingsAsync(ModelDeployment, new EmbeddingsOptions(text));

            return response.Value.Data[0].Embedding;
        }
    }

    /// <summary>
    /// Represents an indexed video in our cognitive search data store.
    /// </summary>
    public partial class IndexedVideo
    {
        /*
         * new SimpleField("EnpsVideoPath", SearchFieldDataType.String) { },
                    new SimpleField("EnpsVideoOverviewText", SearchFieldDataType.String) { },
                    new SimpleField("PossibleNetworkAffiliation", SearchFieldDataType.String) { },
        */
        [SimpleField(IsKey = true)]
        public string ID { get; set; }

        [SearchableField(IsSortable = true)]
        public string VideoName { get; set; }

        [SimpleField]
        public string EnpsVideoPath { get; set; }

        [SimpleField]
        public string EnpsVideoOverviewText { get; set; }

        [SimpleField]
        public string PossibleNetworkAffiliation { get; set; }

        [SearchableField(IsFilterable = true, IsSortable = true)]
        public DateTime DatePublished { get; set; }

        [SearchableField()]
        public string Text { get; set; }

        [SearchableField()]
        public float[] TextVector { get; set; }
    }
}