using Microsoft.Extensions.Logging;
using System.Web;
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
using Newtonsoft.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.AspNetCore.Mvc;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;

namespace VideoProcessorFunction
{
    public class StationVideoUploadProcessorFunction
    {
        private readonly ILogger _logger;

        private const string AzureResourceManager = "https://management.azure.com";

        private const string ApiUrl = "https://api.videoindexer.ai";

        private const int TIME_THRESHOLD = 10;

        // Create a single, static Http Client, which we will not dispose of so it can be used for the duration of the application (not just when the function ends)
        // https://learn.microsoft.com/en-us/azure/azure-functions/manage-connections?tabs=csharp#http-requests
        //private static HttpClient client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false } );

        // Connection string to the storage account
        private static string StorageAccountConnectionString = Environment.GetEnvironmentVariable("StorageConnectionString");
        private readonly StationService _stationService;

        public StationVideoUploadProcessorFunction(
            ILoggerFactory loggerFactory, 
            StationService stationService)
        {
            _logger = loggerFactory.CreateLogger<StationVideoUploadProcessorFunction>();
            _stationService = stationService;
        }

        [Function("TestEnpsConnectivity")]
        public async Task EnpsConnectivityTest([HttpTrigger(authLevel:AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req)
        {
            var enpsUtility = new EnpsUtility();

            _logger.LogInformation("Attempting to log into ENPS Server on VM...");

            await enpsUtility.Login(_logger);

            _logger.LogInformation("Done ENPS login");
        }

        [Function("LlmResponseTest")]
        public static async Task<IActionResult> LlmResponseTest([HttpTrigger(authLevel: AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req)
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
                        ""stationName"": ""WMUR"",
                        ""topics"": [
                            ""Sports"",
                            ""Crime"",
                            ""Politics""
                        ]
                    },
                    {
                        ""stationName"": ""KCRA"",
                        ""topics"": [
                            ""Weather"",
                            ""Shopping"",
                            ""Entertainment""
                        ]
                    },
                    {
                        ""stationName"": ""WMUR"",
                        ""topics"": [
                            ""Entertainment"",
                            ""Crime"",
                            ""Stock Market""
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

            var azureOpenAIService = new AzureOpenAIService();
            var response = await azureOpenAIService.MatchStationsToVideoTopicsAsync(allStationTopics, videoTopics);

            // The response will come back from the LLM as the following JSON:
            // { "interestedStations":["WESH", "WMUR", "KCRA"]}
            // Deserialize the JSON string into a dictionary
            var jsonObject = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(response);

            // extract the interestedStations array
            List<string> interestedStations = jsonObject["interestedStations"];

            string commaSeparatedStations = string.Join(",", interestedStations);

            return new OkObjectResult(response);
        }

        /// <summary>
        /// Endpoint for testing the indexing of a video. If testing a video which exists in ENPS and in VI, the processing state and video ID can be supplied
        /// as query parameters.
        /// </summary>
        /// <param name="req"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        [Function("TestIndexVideo")]
        public async Task IndexVideoTest([HttpTrigger(authLevel: AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req)
        {
            // The stations available are:
            // - WESH
            // - WMUR
            // - WCVB
            // - KCRA
            //
            // The video name and the Video Indexer video id of this video are passed into the request. The four available vidoes to test with are:
            // - 4525002_US_NY_Diddy_Court_AP_Explains_CR__x040n.mp4
            // - 4525038_US_IL_Bird_Migration_Building_Collisions_CR__x040n.mp4  (this video has the hearst share set)
            // - 4524569_US_IL_Ex_House_Speaker_Trial_AP_Explains_CR__x040n.mp4
            // - 4524674_US_Climate_Hurricane_Milton_AP_Explains_CR__x040n.mp4
            //
            // You will need to check your Video Indexer resource for the corresponding video ids

            string stationName = req.Query["stationName"];
            string videoName = req.Query["videoName"];
            string videoId = req.Query["videoId"];

            EnpsUtility enpsUtility = new EnpsUtility();
            await enpsUtility.Login(_logger);
            var serverAddress = _stationService.GetServerAddress(stationName);
            // call search to populate ENPS Video Path, slug, and other pieces of information needed for the XML file
            await enpsUtility.Search(videoName,serverAddress, _logger);

            DateTime storyModifiedDate = enpsUtility.StoryDateTime;

            // calls the ENPS BasicContent endpoint which will get the text overview of the video
            await enpsUtility.GetBasicContent(_logger);

            Console.WriteLine(enpsUtility.HearstShare);

            var cosmosDbService = new CosmosDbService<Story>();

            // This mimics when a video is first uploaded, before it is processed by Video Indexer, adding the Cosmos DB entry
            // with an empty topics list. The topics list will get updated after the Video Indexer callback, which we'll mimic below
            var story = new Story
            {
                Id = Guid.NewGuid().ToString(),
                PartitionKey = stationName,
                VideoName = videoName,
                Topics = new List<string> { "", "", "" },
                VideoId = videoId,
                StoryDateTime = enpsUtility.StoryDateTime,
                EnpsVideoTimestamp = enpsUtility.VideoTimestamp,
                EnpsSlug = enpsUtility.Slug,
                EnpsMediaObject = enpsUtility.MediaObject,
                EnpsFromPerson = enpsUtility.FromPerson,
                EnpsHearstShare = enpsUtility.HearstShare,
                VideoOverviewText = enpsUtility.VideoOverviewText
            };

            await cosmosDbService.CreateItemAsync(story);
            //await IndexVideoMetadata(req.Query["state"], req.Query["id"], log);
            await ProcessVideo(videoId);
            
            //await cosmosDbService.CreateItemAsync(story);

            //await cosmosDbService.GetStationTopicsAsync();
        }

        /// <summary>
        /// This is the callback URL that Video Indexer posts to where we can get the Video ID. We use this in order to avoid having
        /// to continously poll Video Indexer after uploading a video to determine when it has finished processing. Once we have this
        /// callback, we will make a call to get the metadata, pull out the video name from that payload in order to call ENPS and get
        /// the data we need about the video, and then parse and index the video's payload.
        /// </summary>
        /// <param name="req">POST request from Video Indexer</param>
        /// <param name="log">Logger</param>
        [Function("GetVideoStatus")]
        public async Task ReceiveVideoIndexerStateUpdate([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            try
            {
                _logger.LogInformation($"Received Video Indexer status update - Video ID: {req.Query["id"]} \t Processing State: {req.Query["state"]}");

                // If video is processed
                if (req.Query["state"].Equals(ProcessingState.Processed.ToString()))
                {
                    await ProcessVideo(req.Query["id"]);
                }
                else if (req.Query["state"].Equals(ProcessingState.Failed.ToString()))
                {
                    _logger.LogInformation($"\nThe video index failed for video ID {req.Query["id"]}.");
                    var service = new CosmosDbService<Story>();
                    var story = await service.GetItemAsync("VideoId", req.Query["id"]);
                    await service.DeleteItemAsync(story.Id, story.PartitionKey);
                }
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.ToString());
            }
        }

        [Function("WeshUploadTrigger")]
        public async Task WeshUploadTrigger(
            [BlobTrigger("wesh/{name}",
            Connection = "StorageConnectionString")] Stream videoBlob,
            string name,
            Uri uri,
            BlobProperties properties)
        {
            try
            {
                _logger.LogInformation("Start WeshUploadTrigger");
                await TriggerHandler(name, uri, properties);
                _logger.LogInformation("End WeshUploadTrigger");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.ToString());
            }
        }

        [Function("WcvbUploadTrigger")]
        public async Task WcvbUploadTrigger(
            [BlobTrigger("wcvb/{name}",
            Connection = "StorageConnectionString")] Stream videoBlob,
            string name,
            Uri uri,
            BlobProperties properties)
        {
            try
            {
                _logger.LogInformation("Start WcvbUploadTrigger");
                await TriggerHandler(name, uri, properties);
                _logger.LogInformation("End WcvbUploadTrigger");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.ToString());
            }
        }

        [Function("KcraUploadTrigger")]
        public async Task KcraUploadTrigger(
            [BlobTrigger("kcra/{name}",
            Connection = "StorageConnectionString")] Stream videoBlob,
            string name,
            Uri uri,
            BlobProperties properties)
        {
            try
            {
                _logger.LogInformation("Start KcraUploadTrigger");
                await TriggerHandler(name, uri, properties);
                _logger.LogInformation("End KcraUploadTrigger");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.ToString());
            }
        }

        [Function("WmurUploadTrigger")]
        public async Task WmurUploadTrigger(
            [BlobTrigger("wmur/{name}",
            Connection = "StorageConnectionString")] Stream videoBlob,
            string name,
            Uri uri,
            BlobProperties properties)
        {
            try
            {
                _logger.LogInformation("Start WmurUploadTrigger");
                await TriggerHandler(name, uri, properties);
                _logger.LogInformation("End WmurUploadTrigger");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.ToString());
            }
        }

        [Function("WxiiUploadTrigger")]
        public async Task WxiiUploadTrigger(
            [BlobTrigger("wxii/{name}",
            Connection = "StorageConnectionString")] Stream videoBlob,
            string name,
            Uri uri,
            BlobProperties properties)
        {
            try
            {
                _logger.LogInformation("Start WxiiUploadTrigger");
                await TriggerHandler(name, uri, properties);
                _logger.LogInformation("End WxiiUploadTrigger");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.ToString());
            }
        }

        [Function("KcciUploadTrigger")]
        public async Task KcciUploadTrigger(
            [BlobTrigger("kcci/{name}",
            Connection = "StorageConnectionString")] Stream videoBlob,
            string name,
            Uri uri,
            BlobProperties properties)
        {
            try
            {
                _logger.LogInformation("Start KcciUploadTrigger");
                await TriggerHandler(name, uri, properties);
                _logger.LogInformation("End KcciUploadTrigger");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.ToString());
            }
        }

        private async Task TriggerHandler(string name, Uri uri, BlobProperties properties)
        {
            // we first need to check ENPS to ensure this is a PKG and return back the pieces of information we need to include in
            // the database so when videos are pulled up from trend search results, it will have the path to the video on the ENPS
            // server as well as the overview text of the video, including any possible network affiliation if an anchor's name
            // exists in the overview text
            var stationName = new BlobUriBuilder(uri).BlobContainerName;
            var serverAddress = _stationService.GetServerAddress(stationName);
            var enpsUtility = new EnpsUtility();
            await enpsUtility.Login(_logger);
            bool processVideo = await enpsUtility.Search(name, serverAddress, _logger);

            // The above processVideo determines if the video is not more than a day old and is a PKG; but if Hearst determines
            // that the video should be shared to all stations, the HearstShare property will be set to true and the video will be
            // processed regardless of the above conditions. We get the HearstShare property from the ENPS BasicContent call
            await enpsUtility.GetBasicContent(_logger);

            bool forceShare = enpsUtility.HearstShare;

            // if the video is set to be shared to all stations or, if not, it is a PKG and less than a day old, we will process the video
            if (forceShare || processVideo)
            {
                TimeZoneInfo easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

                // get the current time minus the specified threshold and blob created time in EST
                DateTime currentTimeMinusTenMinutesEst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.AddMinutes(-TIME_THRESHOLD), easternZone);
                DateTime blobCreatedDateTimeEst = TimeZoneInfo.ConvertTimeFromUtc(properties.CreatedOn.DateTime, easternZone);

                _logger.LogInformation($"Blob created on: {blobCreatedDateTimeEst}  ====== Current time minus {TIME_THRESHOLD} mins: {currentTimeMinusTenMinutesEst}");

                if (!forceShare || (blobCreatedDateTimeEst < currentTimeMinusTenMinutesEst))
                {
                    _logger.LogInformation($"Blob trigger function for Station A SKIPPING blob\n Name: {name} because it was uploaded more than {TIME_THRESHOLD} minutes ago.");
                }
                else
                {
                    _logger.LogInformation($"Blob trigger function for Station A processed blob\n Name: {name} from path: {uri}.");

                    await ProcessBlobTrigger(name, stationName, _logger);

                    var cosmosDbService = new CosmosDbService<Story>();

                    var story = new Story
                    {
                        Id = Guid.NewGuid().ToString(),
                        PartitionKey = stationName,
                        VideoName = name,
                        StoryDateTime = enpsUtility.StoryDateTime,
                        EnpsSlug = enpsUtility.Slug,
                        EnpsMediaObject = enpsUtility.MediaObject,
                        EnpsFromPerson = enpsUtility.FromPerson,
                        EnpsVideoTimestamp = enpsUtility.VideoTimestamp,
                        VideoOverviewText = enpsUtility.VideoOverviewText
                    };

                    _logger.LogInformation($"Creating item in Cosmos DB for video {name} from station {stationName}");
                    await cosmosDbService.CreateItemAsync(story);
                }
            }
        }

        /// <summary>
        /// Processes the uploaded blob (video) by pulling it from Azure Storage and uploading it to the Video Indexer.
        /// </summary>
        /// <param name="name">The name of the video / blob</param>
        /// <param name="functionCallbackUrl">The video indexer callback URL which is called when the video completes processing</param>
        /// <param name="log"></param>
        /// <returns></returns>
        private static async Task ProcessBlobTrigger(string name, string containerName, ILogger log)
        {
            BlobClient blobClient = new BlobClient(StorageAccountConnectionString, containerName, name);

            BlobSasBuilder sasBuilder = new BlobSasBuilder()
            {
                BlobContainerName = containerName,
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
                log.LogError(ex.ToString());
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
        private async Task ProcessVideo(string videoId)
        {
            // we don't have the video name and will need to get it from Video Indexer, so let's do that first
            // Build Azure Video Indexer resource provider client that has access token throuhg ARM
            var videoIndexerResourceProviderClient = await VideoIndexerResourceProviderClient.BuildVideoIndexerResourceProviderClient();

            // Get account details
            var account = await videoIndexerResourceProviderClient.GetAccount(_logger);
            var accountLocation = account.Location;
            var accountId = account.Properties.Id;

            // Get account level access token for Azure Video Indexer 
            var accountAccessToken = await videoIndexerResourceProviderClient.GetAccessToken(ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Account, _logger);

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

            var cosmosDbService = new CosmosDbService<Story>();
            Story story = await cosmosDbService.GetItemAsync("VideoId", videoId);
            string videoName = story.VideoName;

            _logger.LogInformation($"Here is the full JSON of the indexed video for video ID {videoId}: \n{videoGetIndexResult}");

            if (!string.IsNullOrWhiteSpace(story.VideoOverviewText))
            {
                // ask GPT-4 to see if a name is embedded in the video overview text and return any network affiliation
                //var azureOpenAIService = new AzureOpenAIService();
                //var possibleNetworkAffiliation = await azureOpenAIService.SearchNetworkAffiliationAsync(story.VideoOverviewText);
            }

            var stationName = story.PartitionKey;

            // once the video is processed, we no longer need it in the storage account - TODO: ADD CONTAINER NAME FOR VIDEOS TO APP CONFIG
            BlobClient blobClient = new BlobClient(StorageAccountConnectionString, stationName, videoName);
            if (blobClient.Exists())
            {
                await blobClient.DeleteAsync();

                _logger.LogInformation($"Video {videoName} deleted from storage account.");
            }            

            // Now that we have the full JSON from Video Indexer, extract the topics and keywords for the XML file
            videoIndexerResourceProviderClient.ProcessMetadata(videoGetIndexResult, _logger);
          
            // create the XML document that will feed back into ENPS
            string topics = videoIndexerResourceProviderClient.Topics;

            if (!string.IsNullOrEmpty(topics))
            {
                await UpdateStationTopics(videoId, videoName, topics.Trim());
            }

            string keywords = videoIndexerResourceProviderClient.Keywords;
            //string mosXml = "<mos><itemID>2</itemID><itemSlug>UAW STRIKE-PKG_WESH-NEWS-WSE1X_drobinson02_20230918_104756.mxf</itemSlug><objID>fae8d129-2374-4aa3-bfa0-51532fbc076c</objID><mosID>BC.PRECIS2.WESH.HEARST.MOS</mosID><mosAbstract>UAW STRIKE-PKG_WESH-NEWS-WSE1X_drobinson02_20230918_104756.mxf</mosAbstract><abstract>UAW STRIKE-PKG_WESH-NEWS-WSE1X_drobinson02_20230918_104756.mxf</abstract><objDur>5580</objDur><objTB>60</objTB><objSlug>UAW STRIKE-PKG_WESH-NEWS-WSE1X_drobinson02_20230918_104756.mxf</objSlug><objType>VIDEO</objType><objPaths><objPath>https://WESH-CONT1.companynet.org:10456/broadcast/fae8d129-2374-4aa3-bfa0-51532fbc076c.mxf</objPath><objProxyPath techDescription=\"Proxy\">https://WESH-CONT1.companynet.org:10456/proxy/fae8d129-2374-4aa3-bfa0-51532fbc076cProxy.mp4</objProxyPath><objProxyPath techDescription=\"JPG\">https://WESH-CONT1.companynet.org:10456/still/fae8d129-2374-4aa3-bfa0-51532fbc076c.jpg</objProxyPath></objPaths><mosExternalMetadata><mosScope>STORY</mosScope><mosSchema>http://bitcentral.com/schemas/mos/2.0</mosSchema><mosPayload /></mosExternalMetadata><itemChannel>X</itemChannel><objAir>NOT READY</objAir></mos>";
            //string videoTimestamp = DateTime.Now.ToString();
            await CreateEnpsXmlDocument(story.EnpsHearstShare, stationName, topics, keywords, story.EnpsSlug, story.EnpsMediaObject, stationName, story.EnpsFromPerson, story.EnpsVideoTimestamp);
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
        /// <param name="forceShare">From the ENPS system for this video, signifying if this video should be shared regardless of other conditions</param>
        /// </summary>
        private async Task CreateEnpsXmlDocument(
            bool forceShare, 
            string stationName, 
            string topics, 
            string keywords, 
            string slug, 
            string mosXml, 
            string fromStation, 
            string fromPerson, 
            string videoTimestamp)
        {
            _logger.LogInformation($"Creating Hearst XML for station {stationName}");
            XmlDocument doc = new XmlDocument();

            XmlNode newNode = doc.CreateElement("hearstXML");
            XmlNode rootNode = doc.AppendChild(newNode);

            // Message ID needs to be 7 characters, so generate SHA256 and truncate it. We'll base this
            // off of the slug
            string messageId = string.Empty;
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

            newNode.InnerXml = mosXml;
            rootNode.AppendChild(newNode);

            // TODO: If HearstShare is true, this may not be a PKG so we may need to add the type to Cosmos when video is first uploaded
            // if we need this, this might be in the title case of the EnpsUtility
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
            string ofInterestToStations = string.Empty;            
            if (!forceShare)
            {
                string allStationTopics = await GetStationTopicsAsync(stationName);

                AzureOpenAIService azureOpenAIService = new AzureOpenAIService();
                var response = await azureOpenAIService.MatchStationsToVideoTopicsAsync(allStationTopics, topics);

                // The response will come back from the LLM as the following JSON:
                // { "interestedStations":["WESH", "WMUR", "KCRA"]}
                // Deserialize the JSON string into a dictionary
                var jsonObject = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(response);

                // extract the interestedStations array
                List<string> interestedStations = jsonObject["interestedStations"];

                // remove the current station from this list because this station originated the video and is sharing with all over stations
                interestedStations.Remove(stationName);

                ofInterestToStations = string.Join(",", interestedStations);
            }
            else
            {
                var stations = _stationService.GetStations();
                var filtered = stations.Where(s => s.Key != stationName).Select(s => s.Key).ToList();
                ofInterestToStations = string.Join(",", filtered);
            }

            newNode = doc.CreateElement("ofInterestTo");
            newNode.InnerText = ofInterestToStations;
            rootNode.AppendChild(newNode);

            _logger.LogInformation($"End creating Hearst XML for station {stationName}");
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
                    using var writeStream = await ftpClient.OpenFileWriteStreamAsync($"{messageId}.xml");
                    doc.Save(writeStream);
                    _logger.LogInformation($"XML document {messageId}.xml uploaded to FTP server");
                }
            }
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

            List<string> topicsList = topicsPart.Split('|')
                                                .Select(topic => topic.Trim())
                                                .Where(topic => !string.IsNullOrEmpty(topic))
                                                .ToList();
            
            var service = new CosmosDbService<Story>();
            var story = await service.GetItemAsync("VideoName", videoName);
            story.VideoId = videoId;
            story.Topics = topicsList;
            await service.UpdateItemAsync(story);
        }
    }
}