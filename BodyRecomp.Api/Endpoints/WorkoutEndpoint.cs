using BodyRecomp.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Claims;
using System.Text.Json;

namespace BodyRecomp.Api.Endpoints;

public class WorkoutEndpoint
{
    private readonly Container _container;
    private readonly ILogger<WorkoutEndpoint> _logger;

    public WorkoutEndpoint(CosmosClient cosmosClient, ILogger<WorkoutEndpoint> logger)
    {
        _container = cosmosClient.GetContainer("NutritionFitnessDb", "UserData");
        _logger = logger;
    }

    /// <summary>
    /// POST /api/workouts - Creates or completely updates the user's active 5-day split routine
    /// </summary>
    [Function(nameof(SaveWorkoutSplit))]
    public async Task<IActionResult> SaveWorkoutSplit([HttpTrigger(AuthorizationLevel.Anonymous, "post", "workouts")] HttpRequest req)
    {
        _logger.LogInformation("Processing a workout split upsert request...");

        var user = req.HttpContext.User;
        string? userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("oid")?.Value;

        if (string.IsNullOrEmpty(userId))
            return new UnauthorizedResult();

        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        WorkoutSplit workoutSplit;

        try
        {
            workoutSplit = JsonSerializer.Deserialize<WorkoutSplit>(requestBody, options)!;
            if (workoutSplit == null)
                return new BadRequestObjectResult("Invalid payload mapping.");
        }
        catch(JsonException)
        {
            return new BadRequestObjectResult("Malformed JSON payload.");
        }

        workoutSplit.UserId = userId;
        workoutSplit.Id = $"split-{userId}";
        workoutSplit.Type = nameof(WorkoutSplit);
        workoutSplit.CreatedAt = DateTime.UtcNow.Date;

        try
        {
            ItemResponse<WorkoutSplit> response = await _container.UpsertItemAsync(
                workoutSplit, 
                partitionKey: new PartitionKey(userId));

            _logger.LogInformation($"Workout split processed via Upsert. Request Charge: {response.RequestCharge}");
            return new OkObjectResult(response.Resource);
        }
        catch(CosmosException ex)
        {
            _logger.LogError($"CosmosDB tracking upsert failure: {ex.Message}");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// GET /api/workouts - High-Performance Point Read lookup for the current active split routine
    /// </summary>
    [Function(nameof(GetActiveWorkoutSplit))]
    public async Task<IActionResult> GetActiveWorkoutSplit([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "workouts")] HttpRequest req)
    {
        _logger.LogInformation("Retrieving active workout split configuration...");

        var user = req.HttpContext.User;
        string? userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("oid")?.Value;

        if (string.IsNullOrEmpty(userId))
            return new UnauthorizedResult();

        string documentId = $"split-{userId}";

        try
        {
            ItemResponse<WorkoutSplit> response = await _container.ReadItemAsync<WorkoutSplit>(
                id: documentId, 
                partitionKey: new PartitionKey(userId));

            return new OkObjectResult(response.Resource);
        }
        catch(CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning($"No active split template records located for user: {userId}");
            return new NotFoundObjectResult("No active training split template discovered. Please create one.");
        }
        catch(CosmosException ex)
        {
            _logger.LogError($"Error executing point read sequence: {ex.Message}");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}