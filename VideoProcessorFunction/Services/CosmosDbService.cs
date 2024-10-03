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
            try
            {
                var query = new QueryDefinition($"SELECT * FROM c WHERE c.{propertyName} = @propertyValue")
                                .WithParameter("@propertyValue", value);

                using FeedIterator<T> resultSetIterator = _container.GetItemQueryIterator<T>(query);

                List<T> results = new List<T>();
                while (resultSetIterator.HasMoreResults)
                {
                    FeedResponse<T> response = await resultSetIterator.ReadNextAsync();
                    results.AddRange(response);
                }

                var item = results.FirstOrDefault();

                return item;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
               
            }

            return default;
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

        // Method to query items
        public async Task<IEnumerable<T>> QueryItemsAsync(string query)
        {
            var queryDefinition = new QueryDefinition(query);
            var queryIterator = _container.GetItemQueryIterator<T>(queryDefinition);

            List<T> results = new List<T>();

            while (queryIterator.HasMoreResults)
            {
                var currentResultSet = await queryIterator.ReadNextAsync();
                foreach (var item in currentResultSet)
                {
                    results.Add(item);
                }
            }

            return results;
        }
    }
}
