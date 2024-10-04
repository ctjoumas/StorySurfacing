namespace VideoProcessorFunction
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Extensions.Logging;
    using System.Net.Http;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using System.Web;
    using Azure.Storage.Blobs;
    using Azure.Storage.Sas;
    using Azure.Storage.Blobs.Models;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.AspNetCore.Http;
    using Azure.Core;
    using Azure.Identity;
    using System.Net.Http.Headers;
    using Newtonsoft.Json.Linq;
    using System.Xml;
    using System.Security.Cryptography;
    using System.Text;
    using System.Globalization;
    using CoreFtp;
    using VideoProcessorFunction.Models;
    using VideoProcessorFunction.Services;
    using System.Linq;
    using Newtonsoft.Json;

    public static class StationVideoUploadProcessorFunction
    {
        private const string AzureResourceManager = "https://management.azure.com";

        private const string ApiUrl = "https://api.videoindexer.ai";

        private const int TIME_THRESHOLD = 10;

        // Create a single, static Http Client, which we will not dispose of so it can be used for the duration of the application (not just when the function ends)
        // https://learn.microsoft.com/en-us/azure/azure-functions/manage-connections?tabs=csharp#http-requests
        //private static HttpClient client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false } );

        // Connection string to the storage account
        private static string StorageAccountConnectionString = Environment.GetEnvironmentVariable("StorageConnectionString");

        private static string StationAContainerName = Environment.GetEnvironmentVariable("StationAContainerName");

        [FunctionName("TestEnpsConnectivity")]
        public static async Task EnpsConnectivityTest([HttpTrigger(authLevel:AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req, ILogger log)
        {
            string allStationTopics = @"
                {
                ""stationTopics"": [
                    {
                        ""stationName"": ""WESH"",
                        ""topics"": [
                            ""Sports"",
                            ""Weather"",
                            ""Fishing""
                        ]
                    },
                    {
                        ""stationName"": ""NYC"",
                        ""topics"": [
                            ""Sports"",
                            ""Crime"",
                            ""Politics""
                        ]
                    }
                ]
            }
            ";
            string videoTopics = @"
                {
                    ""videoTopics"": [
                        ""Sports"",
                        ""Weather"",
                        ""Fishing""
                    ]
                }
            ";

            AzureOpenAIService azureOpenAIService = new AzureOpenAIService();
            var response = await azureOpenAIService.GetChatResponseWithRetryAsync(allStationTopics, videoTopics);

            EnpsUtility enpsUtility = new EnpsUtility();

            log.LogInformation("Attempting to log into ENPS Server on VM...");

            await enpsUtility.Login(log);

            log.LogInformation("Done ENPS login");
        }

        /// <summary>
        /// Endpoint for testing the indexing of a video. If testing a video which exists in ENPS and in VI, the processing state and video ID can be supplied
        /// as query parameters.
        /// </summary>
        /// <param name="req"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        [FunctionName("TestIndexVideo")]
        public static async Task IndexVideoTest([HttpTrigger(authLevel: AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req, ILogger log)
        {
            //await IndexVideoMetadata(req.Query["state"], req.Query["id"], log);
            await ProcessVideo(req.Query["state"], req.Query["id"], log);
        }

        /// <summary>
        /// This is the callback URL that Video Indexer posts to where we can get the Video ID. We use this in order to avoid having
        /// to continously poll Video Indexer after uploading a video to determine when it has finished processing. Once we have this
        /// callback, we will make a call to get the metadata, pull out the video name from that payload in order to call ENPS and get
        /// the data we need about the video, and then parse and index the video's payload.
        /// </summary>
        /// <param name="req">POST request from Video Indexer</param>
        /// <param name="log">Logger</param>
        [FunctionName("GetVideoStatus")]
        public static async Task ReceiveVideoIndexerStateUpdate([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req, ILogger log)
        {
            log.LogInformation($"Received Video Indexer status update - Video ID: {req.Query["id"]} \t Processing State: {req.Query["state"]}");

            // If video is processed
            if (req.Query["state"].Equals(ProcessingState.Processed.ToString()))
            {
                //await IndexVideoMetadata(req.Query["state"], req.Query["id"], log);
                await ProcessVideo(req.Query["state"], req.Query["id"], log);
            }
            else if (req.Query["state"].Equals(ProcessingState.Failed.ToString()))
            {
                log.LogInformation($"\nThe video index failed for video ID {req.Query["id"]}.");
                var service = new CosmosDbService<Story>();
                var story = await service.GetItemAsync("VideoId", req.Query["id"]);
                await service.DeleteItemAsync(story.Id, story.PartitionKey);
            }
        }

        [FunctionName("StationAVideoUploadTrigger")]
        public static async Task RunStationAVideo(
            [BlobTrigger("station-a/{name}", 
            Connection = "StorageConnectionString")] Stream videoBlob, 
            string name, 
            Uri uri, 
            ILogger log, 
            BlobProperties properties)
        {
            // we first need to check ENPS to ensure this is a PKG and return back the pieces of information we need to include in
            // the database so when videos are pulled up from trend search results, it will have the path to the video on the ENPS
            // server as well as the overview text of the video, including any possible network affiliation if an anchor's name
            // exists in the overview text
            /*EnpsUtility enpsUtility = new EnpsUtility();
            await enpsUtility.Login(log);
            bool processVideo = await enpsUtility.Search(name, log);
            
            // if this video is found to be a story and a PKG, we'll process it
            if (processVideo)*/
            {
                TimeZoneInfo easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

                // get the current time minus the specified threshold and blob created time in EST
                DateTime currentTimeMinusTenMinutesEst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.AddMinutes(-TIME_THRESHOLD), easternZone);
                DateTime blobCreatedDateTimeEst = TimeZoneInfo.ConvertTimeFromUtc(properties.CreatedOn.DateTime, easternZone);

                log.LogInformation($"Blob created on: {blobCreatedDateTimeEst}  ====== Current time minus {TIME_THRESHOLD} mins: {currentTimeMinusTenMinutesEst}");

                if (blobCreatedDateTimeEst < currentTimeMinusTenMinutesEst)
                {
                    log.LogInformation($"Blob trigger function for Station A SKIPPING blob\n Name: {name} because it was uploaded more than {TIME_THRESHOLD} minutes ago.");
                }
                else
                {
                    log.LogInformation($"Blob trigger function for Station A processed blob\n Name: {name} from path: {uri}.");

                    await ProcessBlobTrigger(name, log);

                    var cosmosDbService = new CosmosDbService<Story>();
                    var station = new BlobUriBuilder(uri).BlobContainerName;

                    var story = new Story
                    {
                        Id = Guid.NewGuid().ToString(),
                        PartitionKey = station,
                        VideoName = name,
                        StoryDateTime = enpsUtility.StoryDateTime
                    };

                    await cosmosDbService.CreateItemAsync(story);
                }
            }

            log.LogInformation("End Storage A Video Upload Trigger");
        }

        /// <summary>
        /// Processes the uploaded blob (video) by pulling it from Azure Storage and uploading it to the Video Indexer.
        /// </summary>
        /// <param name="name">The name of the video / blob</param>
        /// <param name="functionCallbackUrl">The video indexer callback URL which is called when the video completes processing</param>
        /// <param name="log"></param>
        /// <returns></returns>
        private static async Task ProcessBlobTrigger(string name, ILogger log)
        {
            BlobClient blobClient = new BlobClient(StorageAccountConnectionString, StationAContainerName, name);

            BlobSasBuilder sasBuilder = new BlobSasBuilder()
            {
                BlobContainerName = StationAContainerName,
                BlobName = name,
                Resource = "b",
            };

            sasBuilder.ExpiresOn = DateTimeOffset.UtcNow.AddDays(1);
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            Uri uri = blobClient.GenerateSasUri(sasBuilder);

            log.LogInformation($"SAS URI for blob is {uri}");

            // Create the http client
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            var client = new HttpClient(handler);

            // Build Azure Video Indexer resource provider client that has access token throuhg ARM
            var videoIndexerResourceProviderClient = await VideoIndexerResourceProviderClient.BuildVideoIndexerResourceProviderClient();

            // Get account details
            var account = await videoIndexerResourceProviderClient.GetAccount(log);
            var accountLocation = account.Location;
            var accountId = account.Properties.Id;

            // Get account level access token for Azure Video Indexer 
            var accountAccessToken = await videoIndexerResourceProviderClient.GetAccessToken(/*client, */ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Account, log);

            // Upload the video
            await UploadVideo(name, uri, accountLocation, accountId, accountAccessToken, client, log);
        }

        /// <summary>
        /// Uploads the video from a station to the Video Indexer.
        /// </summary>
        /// <param name="videoName"></param>
        /// <param name="videoUri"></param>
        /// <param name="accountLocation"></param>
        /// <param name="accountId"></param>
        /// <param name="accountAccessToken"></param>
        /// <param name="client"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        private static async Task UploadVideo(string videoName, Uri videoUri, string accountLocation, string accountId, string accountAccessToken, HttpClient client, ILogger log)
        {
            log.LogInformation($"Video is starting to upload with video name: {videoName}, videoUri: {videoUri}");

            var content = new MultipartFormDataContent();

            var env = Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT");

            var functionCallbackUrl = string.Empty;

            if (env == "Development")
            {
                functionCallbackUrl = Environment.GetEnvironmentVariable("CallbackFunctionName");
            }
            else
            {
                functionCallbackUrl = await GetFunctionCallbackUrl();
            }
            
            try
            {
                var queryParams = CreateQueryString(
                new Dictionary<string, string>()
                {
                    {"accessToken", accountAccessToken},
                    {"name", videoName},
                    //{"description", "video_description"},
                    {"privacy", "Private"},
                    //{"partition", "partition"},
                    {"videoUrl", videoUri.ToString()},
                    {"callbackUrl", functionCallbackUrl },
                });

                log.LogInformation($"API Call: {ApiUrl}/{accountLocation}/Accounts/{accountId}/Videos?{queryParams}");

                var uploadRequestResult = await client.PostAsync($"{ApiUrl}/{accountLocation}/Accounts/{accountId}/Videos?{queryParams}", content);

                VerifyStatus(uploadRequestResult, System.Net.HttpStatusCode.OK);

                var uploadResult = await uploadRequestResult.Content.ReadAsStringAsync();

                // Get the video ID from the upload result
                var videoId = System.Text.Json.JsonSerializer.Deserialize<Video>(uploadResult).Id;
                log.LogInformation($"\nVideo ID {videoId} was uploaded successfully");
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// This function will utilize the Azure Function / Web App APIs to construct the callback function URL which
        /// is sent to the Video Indexer upload call so that Video Indexer will notify us when there is a state change and
        /// we don't have to continuously poll Video Indexer for the state and risk the function timing out:
        /// https://learn.microsoft.com/en-us/azure/azure-video-indexer/considerations-when-use-at-scale#use-callback-url
        /// </summary>
        /// <returns></returns>
        private static async Task<string> GetFunctionCallbackUrl()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            // generate the ARM access token in order to make the requests to get the callback function for the GetVideoStatus function as
            // well as the request to get the function code for authorization
            var tokenRequestContext = new TokenRequestContext(new[] { $"{AzureResourceManager}/.default" });
            var tokenRequestResult = await new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeEnvironmentCredential = true }).GetTokenAsync(tokenRequestContext);

            var armAccessToken = tokenRequestResult.Token;

            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", armAccessToken);

            // in order to make the call for the callback URL, we need the resource group name, the name of the Azure Function and the Azure Function function name
            var subscriptionId = Environment.GetEnvironmentVariable("SubscriptionId");
            var resourceGroupName = Environment.GetEnvironmentVariable("ResourceGroup");
            var azureFunctionName = Environment.GetEnvironmentVariable("AzureFunctionName");
            var callbackFunctionName = Environment.GetEnvironmentVariable("CallbackFunctionName");

            // call the API to get the URL for the callback function based on the callback function name
            var getVideoStatusFunctionUrlRequestResult = await client.GetAsync($"{AzureResourceManager}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Web/sites/{azureFunctionName}/functions/{callbackFunctionName}?api-version=2023-01-01");

            var jsonResponseBody = await getVideoStatusFunctionUrlRequestResult.Content.ReadAsStringAsync();

            JObject getVideoStatusFunctionJsonObject = JObject.Parse(jsonResponseBody);

            // extract the function URL from the API results
            string functionUrl = getVideoStatusFunctionJsonObject.SelectToken("properties.invoke_url_template").ToString();

            // call the API to get the function key which is the code needed on the callback function URL which is sent to video indexer
            var getFunctionKeyRequestResult = await client.PostAsync($"{AzureResourceManager}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Web/sites/{azureFunctionName}/host/default/listkeys?api-version=2023-01-01", null);

            jsonResponseBody = await getFunctionKeyRequestResult.Content.ReadAsStringAsync();

            JObject getFunctionKeyJsonObject = JObject.Parse(jsonResponseBody);

            // extract the function key which will be appended to the callback URL
            string funtionKey = getFunctionKeyJsonObject.SelectToken("functionKeys.default").ToString();

            var queryParams = CreateQueryString(
                new Dictionary<string, string>()
                {
                    {"code", funtionKey},
                });

            // if there are special characters such as "=", we want to ensure it doesn't return in the coded character
            string unencodedCodeString = HttpUtility.UrlDecode(queryParams.ToString());

            return functionUrl + "?" + unencodedCodeString;
        }

        public static void VerifyStatus(HttpResponseMessage response, System.Net.HttpStatusCode expectedStatusCode)
        {
            if (response.StatusCode != expectedStatusCode)
            {
                throw new Exception(response.ToString());
            }
        }

        /// <summary>
        /// Once a video is determined to be a PKG and published within the threshold (i.e. 24 hours), we will process the video
        /// by saving the topics into the cosmos db and then creating the XML document which will be uploaded to an FTP server
        /// for processing.
        /// </summary>
        /// <returns></returns>
        private static async Task ProcessVideo(string processingState, string videoId, ILogger log)
        {
            // we don't have the video name and will need to get it from Video Indexer, so let's do that first
            // Build Azure Video Indexer resource provider client that has access token throuhg ARM
            var videoIndexerResourceProviderClient = await VideoIndexerResourceProviderClient.BuildVideoIndexerResourceProviderClient();

            // Get account details
            var account = await videoIndexerResourceProviderClient.GetAccount(log);
            var accountLocation = account.Location;
            var accountId = account.Properties.Id;

            // Get account level access token for Azure Video Indexer 
            var accountAccessToken = await videoIndexerResourceProviderClient.GetAccessToken(ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Account, log);

            string queryParams = CreateQueryString(
                new Dictionary<string, string>()
                {
                    { "accessToken", accountAccessToken },
                    { "language", "English" },
                });

            // Create the http client in order to get the JSON Insights of the video
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            var client = new HttpClient(handler);

            var videoGetIndexRequestResult = await client.GetAsync($"{ApiUrl}/{accountLocation}/Accounts/{accountId}/Videos/{videoId}/Index?{queryParams}");

            VerifyStatus(videoGetIndexRequestResult, System.Net.HttpStatusCode.OK);

            var videoGetIndexResult = await videoGetIndexRequestResult.Content.ReadAsStringAsync();

            string videoName = System.Text.Json.JsonSerializer.Deserialize<Video>(videoGetIndexResult).Name;

            log.LogInformation($"Here is the full JSON of the indexed video for video ID {videoId}: \n{videoGetIndexResult}");

            // when the video was first processed by the RunStationAVideo trigger, we checked ENPS to ensure this video is a PKG but we
            // still need to use the ENPS client to return back the pieces of information we need to include in
            // the XML file which include the path to the video on the ENPS server as well as the overview text of the video, including
            // any possible network affiliation (future) if an anchor's name exists in the overview text
            EnpsUtility enpsUtility = new EnpsUtility();
            await enpsUtility.Login(log);

            // testing - remove after testing
            string videoNameTest = System.Text.Json.JsonSerializer.Deserialize<Video>(videoGetIndexResult).Name;

            // call search to populate ENPS Video Path, slug, and other pieces of information needed for the XML file
            await enpsUtility.Search(videoName, log);

            DateTime storyModifiedDate = enpsUtility.StoryDateTime;

            // calls the ENPS BasicContent endpoint which will get the text overview of the video
            await enpsUtility.GetBasicContent(log);

            // ask GPT-4 to see if a name is embedded in the video overview text and return any network affiliation
            // COMMENTING OUT FOR TESTING WITHOUT ENPS SERVER
            //PersonNetworkAffiliationUtility personNetworkAffiliationUtility = new PersonNetworkAffiliationUtility();
            //string possibleNetworkAffiliation = await personNetworkAffiliationUtility.SearchNetworkAffiliationUsingChatGpt4(enpsUtility.VideoOverviewText);*/
            string possibleNetworkAffiliation = "NBC";

            // TODO: Add code to update the entry in Cosmos DB for this video with the topics. Save Topics to Cosmos DB. Update Cosmos DB entry with Topics for existing video.
            // This call should return the station name so we know to not pull this back when creating the XML document (we are only pulling topics from all other stations when checking "interest of")
            string stationName = string.Empty;
          
            // once the video is processed, we no longer need it in the storage account - TODO: ADD CONTAINER NAME FOR VIDEOS TO APP CONFIG
            BlobClient blobClient = new BlobClient(StorageAccountConnectionString, "station-a", videoName);
            if (blobClient.Exists())
            {
                await blobClient.DeleteAsync();
            }

            log.LogInformation($"Video {videoName} deleted from storage account.");

            // Now that we have the full JSON from Video Indexer, extract the topics and keywords for the XML file
            videoIndexerResourceProviderClient.ProcessMetadata(videoGetIndexResult, log);
          
            // create the XML document that will feed back into ENPS
            string topics = videoIndexerResourceProviderClient.Topics;

            if (!string.IsNullOrEmpty(topics))
            {
                await UpdateStationTopics(videoId, videoName, topics.Trim());
            }

            string keywords = videoIndexerResourceProviderClient.Keywords;
            string slug = enpsUtility.Slug;
            //string slug = "slug";
            string mosXml = enpsUtility.MediaObject;
            //string mosXml = "<mos><itemID>2</itemID><itemSlug>UAW STRIKE-PKG_WESH-NEWS-WSE1X_drobinson02_20230918_104756.mxf</itemSlug><objID>fae8d129-2374-4aa3-bfa0-51532fbc076c</objID><mosID>BC.PRECIS2.WESH.HEARST.MOS</mosID><mosAbstract>UAW STRIKE-PKG_WESH-NEWS-WSE1X_drobinson02_20230918_104756.mxf</mosAbstract><abstract>UAW STRIKE-PKG_WESH-NEWS-WSE1X_drobinson02_20230918_104756.mxf</abstract><objDur>5580</objDur><objTB>60</objTB><objSlug>UAW STRIKE-PKG_WESH-NEWS-WSE1X_drobinson02_20230918_104756.mxf</objSlug><objType>VIDEO</objType><objPaths><objPath>https://WESH-CONT1.companynet.org:10456/broadcast/fae8d129-2374-4aa3-bfa0-51532fbc076c.mxf</objPath><objProxyPath techDescription=\"Proxy\">https://WESH-CONT1.companynet.org:10456/proxy/fae8d129-2374-4aa3-bfa0-51532fbc076cProxy.mp4</objProxyPath><objProxyPath techDescription=\"JPG\">https://WESH-CONT1.companynet.org:10456/still/fae8d129-2374-4aa3-bfa0-51532fbc076c.jpg</objProxyPath></objPaths><mosExternalMetadata><mosScope>STORY</mosScope><mosSchema>http://bitcentral.com/schemas/mos/2.0</mosSchema><mosPayload /></mosExternalMetadata><itemChannel>X</itemChannel><objAir>NOT READY</objAir></mos>";
            string fromStation = "wesh";
            string fromPerson = enpsUtility.FromPerson;
            //string fromPerson = "from Person";
            string videoTimestamp = enpsUtility.VideoTimestamp;
            //string videoTimestamp = DateTime.Now.ToString();
            await CreateEnpsXmlDocument(stationName, topics, keywords, slug, mosXml, fromStation, fromPerson, videoTimestamp);
        }

        /// <summary>
        /// THIS PREVIOUSLY INDEXED THE VIDEO METADATA INTO THE COGNITIVE SEARCH INDEX VECTOR STORE, BUT NOW WILL
        /// BE REPLACED WITH THE ABOVE "ProcessVideo" METHOD TO ONLY STORE TOPICS OF THE VIDEO.
        /// </summary>
        /// <param name="processingState"></param>
        /// <param name="videoId"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        private static async Task IndexVideoMetadata(string processingState, string videoId, ILogger log)
        {
            // we don't have the video name and will need to get it from Video Indexer, so let's do that first
            // Build Azure Video Indexer resource provider client that has access token throuhg ARM
            var videoIndexerResourceProviderClient = await VideoIndexerResourceProviderClient.BuildVideoIndexerResourceProviderClient();

            // Get account details
            var account = await videoIndexerResourceProviderClient.GetAccount(log);
            var accountLocation = account.Location;
            var accountId = account.Properties.Id;

            // Get account level access token for Azure Video Indexer 
            var accountAccessToken = await videoIndexerResourceProviderClient.GetAccessToken(ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Account, log);

            string queryParams = CreateQueryString(
                new Dictionary<string, string>()
                {
                    { "accessToken", accountAccessToken },
                    { "language", "English" },
                });

            // Create the http client in order to get the JSON Insights of the video
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            var client = new HttpClient(handler);

            var videoGetIndexRequestResult = await client.GetAsync($"{ApiUrl}/{accountLocation}/Accounts/{accountId}/Videos/{videoId}/Index?{queryParams}");

            VerifyStatus(videoGetIndexRequestResult, System.Net.HttpStatusCode.OK);
            
            var videoGetIndexResult = await videoGetIndexRequestResult.Content.ReadAsStringAsync();

            log.LogInformation($"Here is the full JSON of the indexed video for video ID {videoId}: \n{videoGetIndexResult}");

            // when the video was first processed by the RunStationAVideo trigger, we checked ENPS to ensure this video is a PKG but we
            // still need to use the ENPS client to return back the pieces of information we need to include in
            // the database so when videos are pulled up from trend search results, it will have the path to the video on the ENPS
            // server as well as the overview text of the video, including any possible network affiliation if an anchor's name
            // exists in the overview text
            EnpsUtility enpsUtility = new EnpsUtility();
            await enpsUtility.Login(log);
            
            string videoName = System.Text.Json.JsonSerializer.Deserialize<Video>(videoGetIndexResult).Name;

            // call search to populate ENPS Video Path, which will tell us if it's a story as well as a PKG
            bool isStoryAndPkg = await enpsUtility.Search(videoName, log);

            DateTime storyModifiedDate = enpsUtility.StoryDateTime;

            TimeZoneInfo easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            DateTime currentDateEst = TimeZoneInfo.ConvertTime(DateTime.Now, easternZone);

            // only process the video if it's a story and a PKG and the date is the same as today's date since we do not want older stories
            if (isStoryAndPkg && storyModifiedDate >= currentDateEst)
            {
                // calls the ENPS BasicContent endpoint which will get the text overview of the video
                await enpsUtility.GetBasicContent(log);

                // ask GPT-4 to see if a name is embedded in the video overview text and return any network affiliation
                // COMMENTING OUT FOR TESTING WITHOUT ENPS SERVER
                //PersonNetworkAffiliationUtility personNetworkAffiliationUtility = new PersonNetworkAffiliationUtility();
                //string possibleNetworkAffiliation = await personNetworkAffiliationUtility.SearchNetworkAffiliationUsingChatGpt4(enpsUtility.VideoOverviewText);*/
                string possibleNetworkAffiliation = "NBC";

                // once the video is processed, we no longer need it in the storage account - TODO: ADD CONTAINER NAME FOR VIDEOS TO APP CONFIG
                BlobClient blobClient = new BlobClient(StorageAccountConnectionString, "station-a", videoName);
                if (blobClient.Exists())
                {
                    await blobClient.DeleteAsync();
                }

                log.LogInformation($"Video {videoName} deleted from storage account.");

                // Now that we have the full JSON from Video Indexer, we are going to index this into our Cognitive Search index vector store
                SearchServiceUtility searchServiceUtility = new SearchServiceUtility();
                await searchServiceUtility.IndexVideoMetadata(videoGetIndexResult, enpsUtility.VideoPath, enpsUtility.VideoOverviewText, possibleNetworkAffiliation, log);
                //await searchServiceUtility.IndexVideoMetadata(videoGetIndexResult, "ENPS/Video/Path", "This is the overview video text which will be taken from ENPS", possibleNetworkAffiliation, log);

                // create the XML document that will feed back into ENPS
                string topics = searchServiceUtility.Topics;
                string keywords = searchServiceUtility.Keywords;
                string slug = enpsUtility.Slug;
                //string slug = "slug";
                string mosXml = enpsUtility.MediaObject;
                //string mosXml = "<mos><itemID>2</itemID><itemSlug>UAW STRIKE-PKG_WESH-NEWS-WSE1X_drobinson02_20230918_104756.mxf</itemSlug><objID>fae8d129-2374-4aa3-bfa0-51532fbc076c</objID><mosID>BC.PRECIS2.WESH.HEARST.MOS</mosID><mosAbstract>UAW STRIKE-PKG_WESH-NEWS-WSE1X_drobinson02_20230918_104756.mxf</mosAbstract><abstract>UAW STRIKE-PKG_WESH-NEWS-WSE1X_drobinson02_20230918_104756.mxf</abstract><objDur>5580</objDur><objTB>60</objTB><objSlug>UAW STRIKE-PKG_WESH-NEWS-WSE1X_drobinson02_20230918_104756.mxf</objSlug><objType>VIDEO</objType><objPaths><objPath>https://WESH-CONT1.companynet.org:10456/broadcast/fae8d129-2374-4aa3-bfa0-51532fbc076c.mxf</objPath><objProxyPath techDescription=\"Proxy\">https://WESH-CONT1.companynet.org:10456/proxy/fae8d129-2374-4aa3-bfa0-51532fbc076cProxy.mp4</objProxyPath><objProxyPath techDescription=\"JPG\">https://WESH-CONT1.companynet.org:10456/still/fae8d129-2374-4aa3-bfa0-51532fbc076c.jpg</objProxyPath></objPaths><mosExternalMetadata><mosScope>STORY</mosScope><mosSchema>http://bitcentral.com/schemas/mos/2.0</mosSchema><mosPayload /></mosExternalMetadata><itemChannel>X</itemChannel><objAir>NOT READY</objAir></mos>";
                string fromStation = "wesh";
                string fromPerson = enpsUtility.FromPerson;
                //string fromPerson = "from Person";
                string videoTimestamp = enpsUtility.VideoTimestamp;
                //string videoTimestamp = DateTime.Now.ToString();
                await CreateEnpsXmlDocument(topics, keywords, slug, mosXml, fromStation, fromPerson, videoTimestamp);
            }
        }

        /// <summary>
        /// Creates the XML file which will be uploaded to a location which can be accessed and then
        /// ingested into ENPS. The format of the XML file will be:
        /// <hearstXML>
        ///     <messageID>MMDDXXX</messageID>
        ///     <slug>UAW Strike</slug> this excludes "PKG"
        ///     <mediaObject>Block of XML that opens and closes with MOS tags</mediaObject>
        ///     <videoGenre>PKG</videoGenre> For now we are only working with PKGs
        ///     <fromStation>wesh</fromStation> For now we are only working with wesh, but in the future this will be configurable for each station
        ///     <fromPerson>ModBy field in ENPS</fromPerson>
        ///     <videoTimestamp>ModTime field in ENPS</videoTimestamp>
        ///     <subject>Topics from Video Indexer</subject>
        ///     <keywords>Keywords and faces from Video Indexer</keywords>
        /// </hearstXML>
        /// </summary>
        private static async Task CreateEnpsXmlDocument(string stationName, string topics, string keywords, string slug, string mosXml, string fromStation, string fromPerson, string videoTimestamp)
        {
            XmlDocument doc = new XmlDocument();

            XmlNode newNode = doc.CreateElement("hearstXML");
            XmlNode rootNode = doc.AppendChild(newNode);

            // Message ID needs to be 7 characters, so generate SHA256 and truncate it. We'll base this
            // off of the slug
            string messageId = "";
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(slug));
                string hexDigest = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
                messageId = hexDigest.Substring(0, 7);
            }
            newNode = doc.CreateElement("messageID");
            newNode.InnerText = messageId;
            rootNode.AppendChild(newNode);

            newNode = doc.CreateElement("slug");
            newNode.InnerText = slug;
            rootNode.AppendChild(newNode);

            newNode = doc.CreateElement("mediaObject");
            // this will be enclosed in [], so we want to remove the first and last character
            mosXml = mosXml.Substring(1, mosXml.Length - 2);
            // there seem to be non-breaking spaces, specifically the character with a hex value of 0xA0, so we need to remove this
            mosXml = mosXml.Replace("\u00A0", " ");
            //mosXml = System.Text.RegularExpressions.Regex.Unescape(mosXml);

            try
            {
                newNode.InnerXml = mosXml;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }

            rootNode.AppendChild(newNode);

            newNode = doc.CreateElement("videoGenre");
            newNode.InnerText = "PKG";
            rootNode.AppendChild(newNode);

            newNode = doc.CreateElement("fromStation");
            newNode.InnerText = fromStation;
            rootNode.AppendChild(newNode);

            newNode = doc.CreateElement("fromPerson");
            newNode.InnerText = fromPerson;
            rootNode.AppendChild(newNode);

            newNode = doc.CreateElement("videoTimestamp");
            DateTime dtVideoTimestamp = DateTime.ParseExact(videoTimestamp, "M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture);
            newNode.InnerText = dtVideoTimestamp.ToString("yyyyMMdd HH:mm");
            rootNode.AppendChild(newNode);

            // testing current EST
            newNode = doc.CreateElement("AzureProcessTimeStamp");
            TimeZoneInfo easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            DateTime currentEstTime = TimeZoneInfo.ConvertTime(new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, DateTime.Now.Hour, DateTime.Now.Minute, DateTime.Now.Second), easternZone);
            newNode.InnerText = currentEstTime.ToString("yyyyMMdd HH:mm");
            rootNode.AppendChild(newNode);

            newNode = doc.CreateElement("subject");
            newNode.InnerText = topics;
            rootNode.AppendChild(newNode);

            newNode = doc.CreateElement("keywords");
            newNode.InnerText = keywords;
            rootNode.AppendChild(newNode);

            // AI topic comparison to each station AI index
            // TODO: make COSMOS call to get all station topics from all other stations except for this station
            string allStationTopics = string.Empty;
            AzureOpenAIService azureOpenAIService = new AzureOpenAIService();
            var response = await azureOpenAIService.GetChatResponseWithRetryAsync(allStationTopics, topics);

            // this is hardcoded now for WESH, but this will be updated in the future to account for other stations
            // based on their location and the area of interest / location of the story
            newNode = doc.CreateElement("ofInterestTo");
            newNode.InnerText = "WESH WCVB WBAL";
            rootNode.AppendChild(newNode);

            using (MemoryStream xmlStream = new MemoryStream())
            {
                using (var ftpClient = new FtpClient(new FtpClientConfiguration
                {
                    Host = "bncftp.ap.org",
                    Username = "hearst-test",
                    Password = "Fried^Pickle^Chips97",
                    IgnoreCertificateErrors = true

                }))
                {
                    await ftpClient.LoginAsync();

                    using (var writeStream = await ftpClient.OpenFileWriteStreamAsync($"{messageId}.xml"))
                    {
                        doc.Save(writeStream);
                    }
                }
            }

            //doc.Save("C:\\Rapid Innovation\\Story Surfacing\\enps.xml");
        }

        static string CreateQueryString(IDictionary<string, string> parameters)
        {
            var queryParameters = HttpUtility.ParseQueryString(string.Empty);
            foreach (var parameter in parameters)
            {
                queryParameters[parameter.Key] = parameter.Value;
            }

            return queryParameters.ToString();
        }

        static async Task<string> GetStationTopicsAsync(string excludedStation = null)
        {
            var service = new CosmosDbService<Story>();

            var items = await service.GetStationTopicsAsync(excludedStation);

            var projectedItems = items.Select(story => new
            {
                stationName = story.PartitionKey,
                topics = story.Topics
            }).ToList();

            var rootObject = new
            {
                stationTopics = projectedItems
            };

            var json = JsonConvert.SerializeObject(rootObject, Newtonsoft.Json.Formatting.Indented);

            return json;
        }

        static async Task UpdateStationTopics(string videoId, string videoName, string topics)
        {
            var topicsPart = topics.Split(':')[1].Trim();

            List<string> topicsList = topicsPart.Split(' ')
                                                .Select(topic => topic.Trim())
                                                .ToList();
            
            var service = new CosmosDbService<Story>();
            var story = await service.GetItemAsync("VideoName", videoName);
            story.VideoId = videoId;
            story.Topics = topicsList;
            await service.UpdateItemAsync(story);
        }
    }

    public class AccessTokenRequest
    {
        [JsonPropertyName("permissionType")]
        public ArmAccessTokenPermission PermissionType { get; set; }

        [JsonPropertyName("scope")]
        public ArmAccessTokenScope Scope { get; set; }

        /*[JsonPropertyName("projectId")]
        public string ProjectId { get; set; }

        [JsonPropertyName("videoId")]
        public string VideoId { get; set; }*/
    }

    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ArmAccessTokenPermission
    {
        Reader,
        Contributor,
        MyAccessAdministrator,
        Owner,
    }

    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ArmAccessTokenScope
    {
        Account,
        Project,
        Video
    }

    public class Video
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    public enum ProcessingState
    {
        Uploaded,
        Processing,
        Processed,
        Failed
    }
}