namespace TrendSearchFunction
{
    using System;
   
    internal class SearchResult
    {
        public string Trend { get; set; }

        public SearchResultDetails[] Details { get; set; }
    }

    class SearchResultDetails
    {
        public string VideoName { get; set; }

        public string EnpsVideoPath { get; set; }

        public string EnpsVideoOverviewText { get; set; }

        public string PossibleNetworkAffiliation { get; set; }

        public DateTime DatePublished { get; set; }

        public double Score { get; set; }

        public double RerankerScore { get; set; }
    }
}