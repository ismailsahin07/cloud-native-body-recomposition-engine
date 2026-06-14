using BodyRecomp.Api.Models;
using BodyRecomp.Api.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Text.Json;

namespace BodyRecomp.Api.Endpoints;

public class MacrosEndpoint
{
    private readonly Container _container;
    private readonly ILogger<MacrosEndpoint> _logger;

    public MacrosEndpoint([FromKeyedServices(CosmosContainerKey.UserData)] Container container, ILogger<MacrosEndpoint> logger)
    {
        _container = container;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/macros - Logs a daily macronutrient snapshot
    /// </summary>
    [Function(nameof(LogMacros))]
    public async Task<IActionResult> LogMacros([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "macros")] HttpRequest req)
    {
        _logger.LogInformation("Processing a daily macro logging request...");

        var user = req.HttpContext.User;
        string? userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("oid")?.Value;

        if (string.IsNullOrEmpty(userId))
            return new UnauthorizedResult();

        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        DailyMacroLog macroLog;

        try
        {
            macroLog = JsonSerializer.Deserialize<DailyMacroLog>(requestBody, options)!;
            if (macroLog == null)
                return new BadRequestObjectResult("Invalid JSON payload structure.");
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult("Malformed JSON payload.");
        }

        macroLog.UserId = userId;
        macroLog.LogTime = DateTime.UtcNow.Date;

        try
        {
            ItemResponse<DailyMacroLog> response = await _container.CreateItemAsync(
                item: macroLog,
                partitionKey: new PartitionKey(userId)
            );

            _logger.LogInformation($"Successfully saved macro log. Charge: {response.RequestCharge} RUs.");
            return new OkObjectResult(response.Resource);
        }
        catch (CosmosException ex)
        {
            _logger.LogError($"CosmosDB tracking write failure encountered: {ex.Message}");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// GET /api/macros - Retrieves historical macro entries strictly for the authenticated user
    /// </summary>
    [Function(nameof(GetMacroHistory))]
    public async Task<IActionResult> GetMacroHistory([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "macros")] HttpRequest req)
    {
        _logger.LogInformation("Processing macro historical query request...");

        var user = req.HttpContext.User;
        string? userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("oid")?.Value;

        if (string.IsNullOrEmpty(userId))
            return new UnauthorizedResult();

        var queryDefinition = new QueryDefinition("SELECT * FROM c WHERE c.userId = @userId AND c.type = @docType")
            .WithParameter("@userId", userId)
            .WithParameter("@docType", nameof(DailyMacroLog));

        var queryOptions = new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(userId),
            MaxItemCount = 100
        };

        var results = new List<DailyMacroLog>();

        using FeedIterator<DailyMacroLog> feedIterator = _container.GetItemQueryIterator<DailyMacroLog>(
                queryDefinition,
                requestOptions: queryOptions
            );

        while (feedIterator.HasMoreResults)
        {
            FeedResponse<DailyMacroLog> response = await feedIterator.ReadNextAsync();
            results.AddRange(response);
        }

        return new OkObjectResult(results);
    }
}