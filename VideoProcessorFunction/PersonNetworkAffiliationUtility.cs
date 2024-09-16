namespace VideoProcessorFunction
{
    using Azure.AI.OpenAI;
    using Azure;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using System.Web;

    internal class PersonNetworkAffiliationUtility
    {
        private string AzureOpenAiEndpoint = Environment.GetEnvironmentVariable("CanadaAzureOpenAiEndpoint");
        private string AzureOpenAiKey = Environment.GetEnvironmentVariable("CanadaAzureOpenAiKey");
        private string AzureGptModelDeployment = Environment.GetEnvironmentVariable("AzureGpt4ModelDeployment");

        public async Task<string> SearchNetworkAffiliationUsingChatGpt4(string videoText)
        {
            videoText = HttpUtility.HtmlDecode(videoText);

            OpenAIClient client = new OpenAIClient(
                new Uri(AzureOpenAiEndpoint),
                new AzureKeyCredential(AzureOpenAiKey));

            Response<ChatCompletions> response = await client.GetChatCompletionsAsync(
                AzureGptModelDeployment,
                new ChatCompletionsOptions()
                {
                    Messages =
                    {
                        new ChatMessage(ChatRole.System, @"You are a news outlet AI assistant that identifies the network a person works for."),
                        new ChatMessage(ChatRole.User, @"Find a person's name in this text. The network this person works for is not in the text so please find out which network this person works for and respond with only that network: " + videoText),
                    },
                    Temperature = (float)0.7,
                    MaxTokens = 800,
                    NucleusSamplingFactor = (float)0.95,
                    FrequencyPenalty = 0,
                    PresencePenalty = 0,
                });

            ChatCompletions completions = response.Value;

            string possibleNetworkAffiliatoin = completions.Choices != null ? completions.Choices[0].Message.Content : "Unknown network affiliation";

            return possibleNetworkAffiliatoin;
        }
    }
}
