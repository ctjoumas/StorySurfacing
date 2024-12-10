using VideoProcessorFunction.Models;

namespace VideoProcessorFunction.Services
{
    public class StationService
    {
        private readonly StationsConfig _stationsConfig;

        public StationService(StationsConfig stationsConfig)
        {
            _stationsConfig = stationsConfig;
        }

        public string GetServerAddress(string stationName)
        {
            if (_stationsConfig?.Stations?.TryGetValue(stationName, out var station) == true)
            {
                return station.ServerAddress;
            }

            throw new ArgumentNullException(nameof(stationName), "Station not found");
        }
    }
}