﻿using Azure.Identity;
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
        private static string CosmosDatabaseName = Environment.GetEnvironmentVariable("CosmosDatabaseName");
        private static string CosmosContainerName = Environment.GetEnvironmentVariable("CosmosContainerName");
        private static string CosmosAccountUri = Environment.GetEnvironmentVariable("CosmosAccountUri");
        private static string TenantId = Environment.GetEnvironmentVariable("TenantId");
        private static string TopicsTimeframeThreshold = Environment.GetEnvironmentVariable("TopicsTimeframeThreshold");

        public CosmosDbService()
        {
            CosmosClient cosmosClient = new(
                accountEndpoint: CosmosAccountUri,
                tokenCredential: new DefaultAzureCredential(
                    new DefaultAzureCredentialOptions
                    {
                        TenantId = TenantId,
                        ExcludeEnvironmentCredential = true
                    })
            );

            var database = cosmosClient.GetDatabase(CosmosDatabaseName);
            _container = cosmosClient.GetContainer(CosmosDatabaseName, CosmosContainerName);
        }

        public async Task<T> CreateItemAsync(T item)
        {
            var videoName = item.GetType().GetProperty("VideoName")?.GetValue(item, null);
            var queryDefinition = new QueryDefinition(
                    "SELECT * FROM c WHERE c.stationName = @partitionKey AND c.VideoName = @videoName")
                .WithParameter("@partitionKey", item.PartitionKey)
                .WithParameter("@videoName", videoName);

            var queryIterator = _container.GetItemQueryIterator<T>(
                queryDefinition,
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(item.PartitionKey)
                }
            );

            var existingItem = await queryIterator.ReadNextAsync();

            if (existingItem.Any())
            {
                // Item already exists, return the existing item
                return existingItem.First();
            }

            // Item doesn't exist, proceed to create the new item
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
            // We only want to pull topics from stations from the past x number of days, as specified in
            // the TopicsTimeframeThreshold environment variable
            var intThresholdTimeframe = int.Parse(TopicsTimeframeThreshold);
            TimeZoneInfo easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

            // get the current time minus the specified threshold in EST
            DateTime thresholdDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.AddDays(-intThresholdTimeframe), easternZone);
            var query = string.IsNullOrEmpty(excludedStation)
                ? $"SELECT * FROM c WHERE ARRAY_LENGTH(c.Topics) > 0 and c.EnpsVideoTimestamp >= '{thresholdDate}'"
                : $"SELECT * FROM c WHERE c.StationName != @excludedStation AND ARRAY_LENGTH(c.Topics) > 0 and c.EnpsVideoTimestamp >= '{thresholdDate}'";

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
