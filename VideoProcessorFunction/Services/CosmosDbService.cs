using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VideoProcessorFunction.Services
{
    public interface ICosmosDbEntity
    {
        string Id { get; set; }
        string PartitionKey { get; set; }
    }

    public class CosmosDbService<T> where T : ICosmosDbEntity
    {
        private readonly Container _container;
        private static string CosmosDbConnectionString = Environment.GetEnvironmentVariable("CosmosDbConnectionString");
        private static string CosmosDatabaseName = Environment.GetEnvironmentVariable("CosmosDatabaseName");
        private static string CosmosContainerName = Environment.GetEnvironmentVariable("CosmosContainerName");

        public CosmosDbService()
        {
            var cosmosClient = new CosmosClient(CosmosDbConnectionString);
            var database = cosmosClient.GetDatabase(CosmosDatabaseName);
            _container = cosmosClient.GetContainer(CosmosDatabaseName, CosmosContainerName);
        }

        // Method to create a new item
        public async Task<T> CreateItemAsync(T item)
        {
            var response = await _container.CreateItemAsync(item, new PartitionKey(item.PartitionKey));
            return response.Resource;
        }

        public async Task<T> GetItemAsync(string propertyName, string value)
        {
            var queryDefinition = new QueryDefinition($"SELECT * FROM c WHERE c.{propertyName} = @propertyValue")
                            .WithParameter("@propertyValue", value);

            var queryIterator = _container.GetItemQueryIterator<T>(queryDefinition);

            List<T> results = new List<T>();

            while (queryIterator.HasMoreResults)
            {
                var currentResultSet = await queryIterator.ReadNextAsync();
                results.AddRange(currentResultSet);
            }

            var item = results.FirstOrDefault();

            return item;
        }

        public async Task<T> UpdateItemAsync(T item)
        {
            var response = await _container.ReplaceItemAsync(item, item.Id, new PartitionKey(item.PartitionKey));
            return response.Resource;
        }

        // Method to delete an item by id and partition key
        public async Task DeleteItemAsync(string id, string partitionKey)
        {
            await _container.DeleteItemAsync<T>(id, new PartitionKey(partitionKey));
        }

        public async Task<IEnumerable<T>> QueryItemsAsync(QueryDefinition queryDefinition)
        {
            var queryIterator = _container.GetItemQueryIterator<T>(queryDefinition);

            List<T> results = new List<T>();

            while (queryIterator.HasMoreResults)
            {
                var currentResultSet = await queryIterator.ReadNextAsync();
                results.AddRange(currentResultSet);
            }

            return results;
        }

        public async Task<IEnumerable<T>> GetStationTopicsAsync(string excludedStation = null)
        {
            var query = string.IsNullOrEmpty(excludedStation)
                ? "SELECT * FROM c WHERE ARRAY_LENGTH(c.Topics) > 0"
                : "SELECT * FROM c WHERE c.StationName != @excludedStation AND ARRAY_LENGTH(c.Topics) > 0";

            var queryDefinition = new QueryDefinition(query);

            if (!string.IsNullOrEmpty(excludedStation))
            {
                queryDefinition.WithParameter("@excludedStation", excludedStation);
            }

            var items = await QueryItemsAsync(queryDefinition);

            return items;
        }
    }
}
