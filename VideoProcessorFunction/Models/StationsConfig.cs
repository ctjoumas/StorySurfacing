namespace VideoProcessorFunction.Models
{
    public class Station
    {
        public string ServerAddress { get; set; }
    }

    public class StationsConfig
    {
        public Dictionary<string, Station> Stations { get; set; }
    }
}