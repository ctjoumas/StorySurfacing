using Azure.Core;
using Azure.Identity;
using System;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using VideoProcessorFunction.Models;

namespace VideoProcessorFunction
{
    internal class VideoIndexerResourceProviderClient
    {
        private const string AzureResourceManager = "https://management.azure.com";
        private const string ApiVersion = "2022-08-01";
        private string ResourceGroup = Environment.GetEnvironmentVariable("ResourceGroup");
        private string SubscriptionId = Environment.GetEnvironmentVariable("SubscriptionId");
        private string AccountName = Environment.GetEnvironmentVariable("AccountName");
        private readonly string armAccessToken;

        public string Topics { get; set; }
        public string Faces { get; set; }
        public string Keywords { get; set; }
        public string Ocr { get; set; }
        public string Transcript { get; set; }

        /// <summary>
        /// Builds the Video Indexer Resource Provider Client with the proper token for authorization.
        /// </summary>
        /// <returns></returns>
        async public static Task<VideoIndexerResourceProviderClient> BuildVideoIndexerResourceProviderClient()
        {
            var tokenRequestContext = new TokenRequestContext(new[] { $"{AzureResourceManager}/.default" });
            var tokenRequestResult = await new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeEnvironmentCredential = true }).GetTokenAsync(tokenRequestContext);

            return new VideoIndexerResourceProviderClient(tokenRequestResult.Token);
        }

        public VideoIndexerResourceProviderClient(string armAaccessToken)
        {
            this.armAccessToken = armAaccessToken;
        }

        /// <summary>
        /// Generates an access token. Calls the generateAccessToken API  (https://github.com/Azure/azure-rest-api-specs/blob/main/specification/vi/resource-manager/Microsoft.VideoIndexer/stable/2022-08-01/vi.json#:~:text=%22/subscriptions/%7BsubscriptionId%7D/resourceGroups/%7BresourceGroupName%7D/providers/Microsoft.VideoIndexer/accounts/%7BaccountName%7D/generateAccessToken%22%3A%20%7B)
        /// </summary>
        /// <param name="permission"> The permission for the access token</param>
        /// <param name="scope"> The scope of the access token </param>
        /// <param name="videoId"> if the scope is video, this is the video Id </param>
        /// <param name="projectId"> If the scope is project, this is the project Id </param>
        /// <returns> The access token, otherwise throws an exception</returns>
        public async Task<string> GetAccessToken(/*HttpClient client, */ArmAccessTokenPermission permission, ArmAccessTokenScope scope, /*string videoId = null, string projectId = null, */ILogger log)
        {
            var accessTokenRequest = new AccessTokenRequest
            {
                PermissionType = permission,
                Scope = scope/*,
                VideoId = videoId,
                ProjectId = projectId*/
            };

            log.LogInformation($"\nGetting access token: {System.Text.Json.JsonSerializer.Serialize(accessTokenRequest)}");

            // Set the generateAccessToken (from video indexer) http request content
            try
            {
                var jsonRequestBody = System.Text.Json.JsonSerializer.Serialize(accessTokenRequest);
                var httpContent = new StringContent(jsonRequestBody, Encoding.UTF8, "application/json");

                // Set request uri
                var requestUri = $"{AzureResourceManager}/subscriptions/{SubscriptionId}/resourcegroups/{ResourceGroup}/providers/Microsoft.VideoIndexer/accounts/{AccountName}/generateAccessToken?api-version={ApiVersion}";
                var client = new HttpClient(new HttpClientHandler());
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", armAccessToken);

                var result = await client.PostAsync(requestUri, httpContent);

                VerifyStatus(result, System.Net.HttpStatusCode.OK);
                var jsonResponseBody = await result.Content.ReadAsStringAsync();

                log.LogInformation($"Got access token: {scope}, {permission}");

                return System.Text.Json.JsonSerializer.Deserialize<GenerateAccessTokenResponse>(jsonResponseBody).AccessToken;
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// Gets an account. Calls the getAccount API (https://github.com/Azure/azure-rest-api-specs/blob/main/specification/vi/resource-manager/Microsoft.VideoIndexer/stable/2022-08-01/vi.json#:~:text=%22/subscriptions/%7BsubscriptionId%7D/resourceGroups/%7BresourceGroupName%7D/providers/Microsoft.VideoIndexer/accounts/%7BaccountName%7D%22%3A%20%7B)
        /// </summary>
        /// <returns> The Account, otherwise throws an exception</returns>
        public async Task<Account> GetAccount(ILogger log)
        {
            log.LogInformation($"Getting account {AccountName}.");
            Account account;
            try
            {
                // Set request uri
                var requestUri = $"{AzureResourceManager}/subscriptions/{SubscriptionId}/resourcegroups/{ResourceGroup}/providers/Microsoft.VideoIndexer/accounts/{AccountName}?api-version={ApiVersion}";
                log.LogInformation($"Requesting Video Indexer Account Name: {requestUri}");
                var client = new HttpClient(new HttpClientHandler());
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", armAccessToken);

                var result = await client.GetAsync(requestUri);

                VerifyStatus(result, System.Net.HttpStatusCode.OK);
                var jsonResponseBody = await result.Content.ReadAsStringAsync();
                account = System.Text.Json.JsonSerializer.Deserialize<Account>(jsonResponseBody);
                VerifyValidAccount(account, log);
                log.LogInformation($"The account ID is {account.Properties.Id}");
                log.LogInformation($"The account location is {account.Location}");
                return account;
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.ToString());
                throw;
            }
        }

        public void ProcessMetadata(string videoMetadataJson, ILogger log)
        {
            JObject videoIndexerJsonObject = JObject.Parse(videoMetadataJson);

            // Grab name of video and it's ID so it can be added to cognitive search document for each entry / document
            //string videoName = videoIndexerJsonObject.SelectToken("name").ToString();
            //string videoId = videoIndexerJsonObject.SelectToken("videos[0].id").ToString();

            /*
               If creating a text vector, include:
               [Video context]
               Topics of this video are: Politics, Sports
               Celebrities appearing in this video: LeBron James, MJ
               OCR: MUSEUM, 
               Transcript: ...
             */

            string topics = GetMetadataFromVideoIndexer(videoIndexerJsonObject, "topics", "name");
            if (topics.Length > 0)
            {
                Topics = topics;
            }

            string faces = GetMetadataFromVideoIndexer(videoIndexerJsonObject, "faces", "name");
            if (faces.Length > 0)
            {
                // remove Unknown faces (?) - TEST
                Faces = faces;
            }

            string keywords = GetMetadataFromVideoIndexer(videoIndexerJsonObject, "keywords", "text");
            if (keywords.Length > 0)
            {
                Keywords = keywords;
            }

            string ocr = GetMetadataFromVideoIndexer(videoIndexerJsonObject, "ocr", "text");
            if (ocr.Length > 0)
            {
                Ocr = ocr;
            }

            string transcript = GetMetadataFromVideoIndexer(videoIndexerJsonObject, "transcript", "text");
            if (transcript.Length > 0)
            {
                Transcript = transcript;
            }
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
                        sbMetadataType.Append("|");
                    }
                }
            }

            return sbMetadataType.ToString();
        }

        private void VerifyValidAccount(Account account, ILogger log)
        {
            if (string.IsNullOrWhiteSpace(account.Location) || account.Properties == null || string.IsNullOrWhiteSpace(account.Properties.Id))
            {
                log.LogInformation($"{nameof(AccountName)} {AccountName} not found. Check {nameof(SubscriptionId)}, {nameof(ResourceGroup)}, {nameof(AccountName)} ar valid.");
                throw new Exception($"Account {AccountName} not found.");
            }
        }

        public static void VerifyStatus(HttpResponseMessage response, System.Net.HttpStatusCode excpectedStatusCode)
        {
            if (response.StatusCode != excpectedStatusCode)
            {
                throw new Exception(response.ToString());
            }
        }
    }
}