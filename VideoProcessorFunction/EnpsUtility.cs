namespace VideoProcessorFunction
{
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Threading.Tasks;

    internal class EnpsUtility
    {
        private string EnspDevKey = Environment.GetEnvironmentVariable("EnpsDevKey");
        private string StaffUserId = Environment.GetEnvironmentVariable("EnpsStaffUserId");
        private string DomainUserId = Environment.GetEnvironmentVariable("EnpsDomainUserId");
        private string ApPassword = Environment.GetEnvironmentVariable("EnpsApPassword");
        private string IClientType = Environment.GetEnvironmentVariable("EnpsIClientType");
        private string EnpsApiBaseUrl = Environment.GetEnvironmentVariable("EnpsApiBaseUrl");

        // The ENPS Session ID we need from the login endpoint which is used for subsequent search requests
        private string EnpsSessionId;

        // The GUID of the story/video, used in the BasicContent API
        private string VideoGuid;

        // The modified date of the story
        public DateTime StoryDateTime { get; set; }

        // The path to the story/video on the ENPS server, used in the BasicContent API
        public string VideoPath { get; set; }

        // The overview text of the video
        public string VideoOverviewText { get; set; }

        // The slug of the video (i.e. in "UAW Strike-PKG", it would be UAW Strike)
        public string Slug { get; set; }

        /// <summary>
        /// The MOS XML field from ENPS
        /// </summary>
        public string MediaObject { get; set; }

        /// <summary>
        /// The ModBy field from ENPS
        /// </summary>
        public string FromPerson { get; set; }

        /// <summary>
        /// The ModTime field from ENPS
        /// </summary>
        public string VideoTimestamp { get; set; }

        // THIS WILL BE AN APP CONFIG VALUE FOR THE ENPS SERVER
        private string ServerAddress = "http://WESH-CONT1.companynet.org:10456/proxy/";

        /// <summary>
        /// Logs into the ENPS Server in order to retrieve the SessionID, which will be used for the subsequent search requests
        /// </summary>
        /// <returns>ENPS SessionID</returns>
        public async Task Login(ILogger log)
        {
            Dictionary<string, string> loginBodyContent = new Dictionary<string, string>
            {
                { "staffUserId", StaffUserId },
                { "domainuserId", DomainUserId },
                { "password", ApPassword },
                { "domainName", DomainUserId },
                { "devKey", EnspDevKey },
                { "iClientType", IClientType }
            };

            foreach (var key in loginBodyContent.Keys)
            {
                log.LogInformation($"Key: {key} ---- Value: {loginBodyContent[key]}");
            }

            var client = new HttpClient();

            HttpResponseMessage ipResponse = await client.GetAsync("http://checkip.dyndns.org");

            var ipContentString = await ipResponse.Content.ReadAsStringAsync();
            log.LogInformation(ipContentString);

            FormUrlEncodedContent content = new FormUrlEncodedContent(loginBodyContent);

            log.LogInformation("Attempting to login to ENPS server");

            HttpResponseMessage response = await client.PostAsync(EnpsApiBaseUrl + "/Logon", content);

            log.LogInformation("Request to Logon endpoit sent.");
            log.LogInformation($"Status code: {response.StatusCode}");

            var contentString = await response.Content.ReadAsStringAsync();
            Dictionary<string, object> logonResponse = JsonConvert.DeserializeObject<Dictionary<string, object>>(contentString);

            if (response.IsSuccessStatusCode)
            {
                EnpsSessionId = logonResponse["SessionID"] as string;
            }
            else
            {
                log.LogInformation("Error");
            }
        }

        /// <summary>
        /// Calls the ENPS Search API to find details on the video, which is done using the ENPS
        /// Server address concatenated with the proxy video name, which can be seen in the
        /// bodyContent QueryTerms below.
        /// </summary>
        /// <returns>true if the video is a story (type 3) and also a PKG</returns>
        public async Task<bool> Search(string videoProxyName, ILogger log)
        {
            bool isVideoStoryAndPkg = false;

            string bodyContent = "" +
                "{" +
                " \"Database\": \"ENPS\"," +
                " \"ExactMatch\": true," +
                " \"MaxRows\": 200," +
                " \"NOMContentDates\": { \"All\": true }," +
                " \"NOMContentTypes\": { \"Scripts\": true }," +
                " \"NOMLocations\": [ {" +
                "     \"BasePath\": \"P_SYSTEM\\\\\"," +
                "     \"SearchArchives\": false," +
                "     \"SearchTrash\": false," +
                "     \"SearchWIP\": true" +
                "   }]," +
                " \"QueryTerms\": \"" + ServerAddress + videoProxyName + "\"," +
                " \"SortByRank\": false," +
                " \"SearchWires\": false," +
                " \"zFields\": []" +
                "}";

            var client = new HttpClient();

            StringContent content = new StringContent(bodyContent, Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Add("X-ENPS-TOKEN", EnpsSessionId);

            HttpResponseMessage response = await client.PostAsync(EnpsApiBaseUrl + "/Search", content);

            if (response.IsSuccessStatusCode)
            {
                var contentString = await response.Content.ReadAsStringAsync();

                JObject searchResultsJsonObject = JObject.Parse(contentString);

                JToken objectPropertiesTokens = searchResultsJsonObject.SelectToken($"SearchResults[0].ObjectProperties");

                if (objectPropertiesTokens != null)
                {
                    bool isStoryType = false;
                    bool isPkg = false;

                    // The ObjectProperties collection has FieldName and FieldValue properties where we want to check
                    // that this video is a Type 3 (meaning it is a story), get the modification datetime, the path
                    // where the video is located on the server, and the title which will also contain PKG if it is
                    // indeed a PKG. Example:
                    // {
                    //   "FieldName": "Guid",
                    //   "FieldValue": "3DB9E7E8-04FD-499C-93D2-4FC7D7E3565C"
                    // }
                    // We will need to save the story title (without -PKG), path, and GUID to run the next BasicContent query
                    foreach (var objectPropertyToken in objectPropertiesTokens)
                    {
                        string fieldName = objectPropertyToken["FieldName"].ToString();
                        string fieldValue = objectPropertyToken["FieldValue"].ToString();

                        switch (fieldName.ToLower())
                        {
                            case "guid":
                                VideoGuid = fieldValue;

                                log.LogInformation($"GUID: {fieldValue}");

                                break;

                            case "type":
                                if (int.Parse(fieldValue) == 3)
                                {
                                    isStoryType = true;
                                }

                                log.LogInformation($"Type: {fieldValue}");

                                break;

                            case "modtime":
                                StoryDateTime = DateTime.Parse(fieldValue);

                                log.LogInformation($"Date Modified: {DateTime.Parse(fieldValue)}");

                                break;

                            case "path":
                                VideoPath = fieldValue.Replace(@"\", @"\\");

                                log.LogInformation($"Story Path on ENPS: {fieldValue}");

                                break;

                            case "title":
                                int indexOfDash = fieldValue.IndexOf("-");
                                Slug = fieldValue.Substring(0, indexOfDash);
                                string type = fieldValue.Substring(indexOfDash + 1, fieldValue.Length - (indexOfDash + 1));

                                if (type.Equals("PKG"))
                                {
                                    isPkg = true;
                                }

                                log.LogInformation($"Slug: {Slug} (of type {type})");

                                break;
                        }
                    }

                    // the video is a story and PKG, meaning it's a video we want to process if it's both
                    // a story type (type 3) and a PKG
                    if (isStoryType && isPkg)
                    {
                        isVideoStoryAndPkg = true;
                    }
                }
            }
            else
            {
                log.LogInformation("Error");
            }

            return isVideoStoryAndPkg;
        }

        /// <summary>
        /// Using the ENPS video path and GUID we received from the Search API, we'll find the text giving
        /// an overview of what the video is about in the results of the BasicContent API.
        /// </summary>
        /// <returns>Text description of what the video is about</returns>
        public async Task GetBasicContent(ILogger log)
        {
            string bodyContent =
                "[{" +
                " \"database\": \"ENPS\"," +
                " \"path\": \"" + VideoPath + "\"," +
                " \"guid\": \"" + VideoGuid + "\"," +
                " \"hitHighlightTerm\": \"\"," +
                " \"returnTextLevel\": \"FULL\"" +
                "}]";

            var client = new HttpClient();

            StringContent content = new StringContent(bodyContent, Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Add("X-ENPS-TOKEN", EnpsSessionId);

            HttpResponseMessage response = await client.PostAsync(EnpsApiBaseUrl + "/BasicContent", content);

            if (response.IsSuccessStatusCode)
            {
                var contentString = await response.Content.ReadAsStringAsync();

                // The JSON returned by this API is an array with a single element, so we will pull out the first element
                // and parse that for the text content
                JArray jArray = JArray.Parse(contentString);
                JObject basicContentJsonObject = (JObject)jArray.First();

                JToken objectPropertiesTokens = basicContentJsonObject.SelectToken("ObjectProperties");

                if (objectPropertiesTokens != null)
                {
                    foreach (var objectPropertyToken in objectPropertiesTokens)
                    {
                        string fieldName = objectPropertyToken["FieldName"].ToString();

                        if (fieldName.ToLower().Equals("text"))
                        {
                            VideoOverviewText = objectPropertyToken["FieldValue"].ToString();
                        }
                        else if (fieldName.ToLower().Equals("textcommands"))
                        {
                            MediaObject = ((JValue)((JProperty)objectPropertyToken["FieldValue"].First).Value).Value.ToString();
                        }
                        else if (fieldName.ToLower().Equals("creator"))
                        {
                            FromPerson = objectPropertyToken["FieldValue"].ToString();
                        }
                        else if (fieldName.ToLower().Equals("modtime"))
                        {
                            VideoTimestamp = objectPropertyToken["FieldValue"].ToString();
                        }
                    }
                }
            }
            else
            {
                log.LogInformation("Error");
            }
        }
    }
}