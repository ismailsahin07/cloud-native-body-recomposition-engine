using BodyRecomp.Api.Configuration;
using BodyRecomp.Api.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BodyRecomp.Api.Triggers;

public class AnalyticsWorkerTrigger
{
    private readonly Container _container;
    private readonly ILogger<AnalyticsWorkerTrigger> _logger;

    public AnalyticsWorkerTrigger(
        [FromKeyedServices(CosmosContainerKey.UserData)]Container container, 
        ILogger<AnalyticsWorkerTrigger> logger)
    {
        _container = container;
        _logger = logger;
    }

    [Function(nameof(AnalyticsWorkerTrigger))]
    public async Task Run(
        [ServiceBusTrigger("weekly-analytics", Connection = "ServiceBusConnection")] string message,
        FunctionContext context)
    {
        _logger.LogInformation("Processing analytics computation command from Service Bus...");

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        AnalyticsQueueMessage? command;

        try
        {
            command = JsonSerializer.Deserialize<AnalyticsQueueMessage>(message, options);
        }
        catch(JsonException ex)
        {
            _logger.LogError($"Failed to deserialize message payload: {ex.Message}");
            throw;
        }

        if (command is null || string.IsNullOrEmpty(command.UserId) || string.IsNullOrEmpty(command.TargetDate))
        {
            _logger.LogError("Invalid command payload structure.");
            return;
        }

        DateTime endDate = DateTime.Parse(command.TargetDate).Date;
        DateTime startDate = endDate.AddDays(-6).Date;

        _logger.LogInformation($"Computing averages for user: {command.UserId} from: {startDate:yyyy-MM-dd} to: {endDate:yyyy-MM-dd}");

        var queryDefinition = new QueryDefinition("SELECT * FROM c WHERE c.userId = @userId AND c.type = @docType AND c.logTime >= @startDate AND c.logTime <= @endDate")
            .WithParameter("@userId", command.UserId)
            .WithParameter("@docType", nameof(DailyMacroLog))
            .WithParameter("@startDate", startDate)
            .WithParameter("@endDate", endDate.AddDays(1).AddTicks(-1));

        var queryOptions = new QueryRequestOptions { PartitionKey = new PartitionKey(command.UserId) };
        var logs = new List<DailyMacroLog>();

        using FeedIterator<DailyMacroLog> feedIterator = _container.GetItemQueryIterator<DailyMacroLog>(
            queryDefinition, 
            requestOptions: queryOptions);

        if (feedIterator.HasMoreResults)
        {
            FeedResponse<DailyMacroLog> response = await feedIterator.ReadNextAsync();
            logs.AddRange(response);
        }

        if (!logs.Any())
        {
            _logger.LogWarning("No macro logs discovered for the requested time period. Computation aborted.");
            return;
        }

        var summary = new WeeklyMacroSummary
        {
            UserId = command.UserId,
            WeekEnding = endDate,
            DaysLogged = logs.Count,
            AverageProtein = Math.Round(logs.Average(l => l.ProteinGrams), 1),
            AverageFat = Math.Round(logs.Average(l => l.FatGrams), 1),
            AverageCarbs = Math.Round(logs.Average(l => l.CarbGrams), 1),
            AverageCalories = Math.Round(logs.Average(l => l.TotalCalories), 1)
        };

        try
        {
            ItemResponse<WeeklyMacroSummary> writeSummary = await _container.CreateItemAsync(
                item: summary,
                partitionKey: new PartitionKey(command.UserId));

            _logger.LogInformation($"Successfully computed and persisted the weekly analytics. Request Charge: {writeSummary.RequestCharge} RUs.");
        }
        catch(CosmosException ex)
        {
            _logger.LogError($"Failed to persist weekly analytics summary: {ex.Message}");
            throw;
        }
    }
}