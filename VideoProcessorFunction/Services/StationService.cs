using System.Security.Permissions;
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

        public Station GetStationConfig(string stationName)
        {
            if (_stationsConfig?.Stations?.TryGetValue(stationName, out var station) == true)
            {
                return station;
            }
            throw new ArgumentNullException(nameof(stationName), "Station not found");
        }

        public Dictionary<string, Station> GetStations() => _stationsConfig?.Stations;

        public string GetDatabase(string stationName)
        {
            if (_stationsConfig?.Stations?.TryGetValue(stationName, out var station) == true)
            {
                return station.Database;
            }

            throw new ArgumentNullException(nameof(stationName), "Station not found");
        }

        public string GetBasepath(string stationName)
        {
            if (_stationsConfig?.Stations?.TryGetValue(stationName, out var station) == true)
            {
                return station.Basepath;
            }

            throw new ArgumentNullException(nameof(stationName), "Station not found");
        }
    }
}