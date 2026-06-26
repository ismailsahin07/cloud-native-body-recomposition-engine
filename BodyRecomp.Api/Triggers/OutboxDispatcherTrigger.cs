using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BodyRecomp.Api.Triggers;

public class OutboxDispatcherTrigger
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ILogger<OutboxDispatcherTrigger> _logger;

    public OutboxDispatcherTrigger(ServiceBusClient serviceBusClient ,ILogger<OutboxDispatcherTrigger> logger)
    {
        _serviceBusClient = serviceBusClient;
        _logger = logger;
    }

    [Function(nameof(OutboxDispatcherTrigger))]
    public async Task Run([CosmosDBTrigger(
        databaseName: "NutritionFitnessDb",
        containerName: "UserData",
        Connection = "CosmosConnectionString",
        LeaseContainerName = "leases",
        CreateLeaseContainerIfNotExists = true)] IReadOnlyList<JsonElement> input) 
    {
        if (input is null || input.Count is 0) return;

        _logger.LogInformation($"CosmosDB Change Feed Triggered with {input.Count} modified documents.");

        await using ServiceBusSender sender = _serviceBusClient.CreateSender("weekly-analytics");

        foreach(JsonElement document in input)
        {
            if (!document.TryGetProperty("type", out JsonElement typeElement)
                && typeElement.GetString() is not "OutboxMessage") continue;

            string id = document.GetProperty("id").GetString() ?? "UnknownID";
            string eventType = document.GetProperty("eventType").GetString() ?? "UnknownType";
            string payload = document.GetProperty("payload").GetString() ?? "{}";

            _logger.LogInformation($"Intercepted OutboxMessage [{id}] of type [{eventType}]. Dispatching to Service Bus.");

            try
            {
                ServiceBusMessage message = new(payload);

                await sender.SendMessageAsync(message);

                _logger.LogInformation($"Successfully dispatched OutboxMessage [{id}] to the weekly-analysis queue.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to dispatch OutboxMessage [{id}]: {ex.Message}");
                throw;
            }
        }
    }
}
