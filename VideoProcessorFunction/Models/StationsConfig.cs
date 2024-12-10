namespace VideoProcessorFunction.Models
{
    public class Station
    {
        public string ServerAddress { get; set; }
        public string Database { get; set; }
        public string Basepath { get; set; }
    }

    public class StationsConfig
    {
        public Dictionary<string, Station> Stations { get; set; }
    }
}