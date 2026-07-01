using BodyRecomp.Api.Configuration;
using BodyRecomp.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using System.Net;
using System.Security.Claims;
using System.Text.Json;

namespace BodyRecomp.Api.Endpoints;

public class WorkoutEndpoint
{
    private readonly Container _container;
    private readonly ILogger<WorkoutEndpoint> _logger;

    public WorkoutEndpoint(
        [FromKeyedServices(CosmosContainerKey.UserData)] Container container,
        ILogger<WorkoutEndpoint> logger)
    {
        _container = container;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/workouts - Creates or completely updates the user's active 5-day split routine
    /// </summary>
    [Function(nameof(SaveWorkoutSplit))]
    
    [OpenApiOperation(operationId: nameof(SaveWorkoutSplit), 
        tags: new[] { "workouts" }, 
        Summary = "Create or update split routine", 
        Description = "Creates or completely updates the user's active 5-day split routine.")]
    
    [OpenApiRequestBody(contentType: "application/json", 
        bodyType: typeof(WorkoutSplit), 
        Required = true, 
        Description = "The split routine payload")]
    
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, 
        contentType: "application/json", 
        bodyType: typeof(WorkoutSplit), 
        Description = "Successfully processed user's split.")]
    
    [OpenApiResponseWithoutBody(statusCode: HttpStatusCode.Unauthorized, 
        Description = "Missing or invalid JWT token.")]
    
    [OpenApiResponseWithoutBody(statusCode: HttpStatusCode.BadRequest, 
        Description = "Malformed JSON payload.")]
    public async Task<IActionResult> SaveWorkoutSplit([HttpTrigger(AuthorizationLevel.Function, "post", "workouts")] HttpRequest req)
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
        catch (JsonException)
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
        catch (CosmosException ex)
        {
            _logger.LogError($"CosmosDB tracking upsert failure: {ex.Message}");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// GET /api/workouts - High-Performance Point Read lookup for the current active split routine
    /// </summary>
    [Function(nameof(GetActiveWorkoutSplit))]
    
    [OpenApiOperation(operationId: nameof(GetActiveWorkoutSplit), 
        tags: new[] { "workouts" }, 
        Summary = "Retrieve active workout split", 
        Description = "High-Performance Point Read lookup for the current active split routine.")]
    
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, 
        contentType: "application/json", 
        bodyType: typeof(WorkoutSplit), 
        Description = "Successfully retrieved active workout split.")]
    
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.NotFound, 
        contentType: "text/plain", typeof(string), 
        Description = "No active split template records located.")]
    
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, 
        contentType: "text/plain", typeof(string), 
        Description = "Error executing point read sequence")]
    
    [OpenApiResponseWithoutBody(statusCode: HttpStatusCode.Unauthorized, 
        Description = "Missing or invalid JWT token.")]
    public async Task<IActionResult> GetActiveWorkoutSplit([HttpTrigger(AuthorizationLevel.Function, "get", Route = "workouts")] HttpRequest req)
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
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning($"No active split template records located for user: {userId}");
            return new NotFoundObjectResult("No active training split template discovered. Please create one.");
        }
        catch (CosmosException ex)
        {
            _logger.LogError($"Error executing point read sequence: {ex.Message}");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// POST /api/workouts/session - Logs a completed workout day and triggers analytics if day-5 is reached
    /// </summary>
    [Function(nameof(LogWorkoutSession))]
    [OpenApiOperation(operationId: nameof(LogWorkoutSession), 
        tags: new[] {"workouts/session"}, 
        Summary = "Log a completed workout session",
        Description = "Logs a completed workout day and triggers analytics if day-5 is reached.")]
    
    [OpenApiRequestBody(contentType: "application/json", 
        bodyType: typeof(WorkoutSessionLog), 
        Required = true, 
        Description = "The workout logging payload.")]
    
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, 
        contentType: "application/json", 
        bodyType: typeof(WorkoutSessionLog), 
        Description = "Workout session (and potential outbox message) saved atomically.")]
    
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, 
        contentType: "text/plain", 
        bodyType: typeof(string) ,
        Description = "Malformed JSON payload.")]
    
    [OpenApiResponseWithoutBody(statusCode: HttpStatusCode.Unauthorized, 
        Description = "Missing or invalid JWT token.")]
    
    [OpenApiResponseWithoutBody(statusCode: HttpStatusCode.Conflict, 
        Summary = "Conflict", 
        Description = "A workout for this ID/Date already exists in the database.")]
    
    [OpenApiResponseWithoutBody(statusCode: HttpStatusCode.TooManyRequests, 
        Summary = "Rate Limited", 
        Description = "Cosmos DB RU limit exceeded. Client should retry.")]
    
    [OpenApiResponseWithoutBody(statusCode: HttpStatusCode.InternalServerError, 
        Summary = "Internal Server Error", 
        Description = "Database transaction failed or unexpected Cosmos exception.")]

    public async Task<IActionResult> LogWorkoutSession(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "workouts/session")] HttpRequest req)
    {
        _logger.LogInformation("Logging a completed workout session...");

        var user = req.HttpContext.User;
        string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("oid");

        if (string.IsNullOrEmpty(userId))
            return new UnauthorizedResult();

        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        WorkoutSessionLog sessionLog;

        try
        {
            sessionLog = JsonSerializer.Deserialize<WorkoutSessionLog>(requestBody, options);
            if (sessionLog is null)
                return new BadRequestObjectResult("Invalid payload mapping.");
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult("Malformed JSON payload.");
        }

        sessionLog.UserId = userId;
        sessionLog.CompletedAt = DateTime.UtcNow;

        try
        {
            TransactionalBatch batch = _container.CreateTransactionalBatch(new PartitionKey(userId));
            
            batch.CreateItem(sessionLog);
            
            if(sessionLog.DayNumber is 5)
            {
                _logger.LogInformation($"Day 5 detected. Attaching analytics command to the database transaction batch.");

                var payload = new AnalyticsQueueMessage
                {
                    UserId = userId,
                    TargetDate = sessionLog.CompletedAt.ToString("yyyy-MM-dd")
                };

                var outboxMessage = new OutboxMessage
                {
                    UserId = userId,
                    EventType = "WeeklyAnalyticsRequested",
                    Payload = JsonSerializer.Serialize(payload, options)
                };

                batch.CreateItem(outboxMessage);
            }

            using TransactionalBatchResponse batchResponse = await batch.ExecuteAsync();

            if (!batchResponse.IsSuccessStatusCode)
            {
                _logger.LogError($"Transactional Batch failed with status code: {batchResponse.StatusCode} | Error: {batchResponse.ErrorMessage}");
                return new StatusCodeResult((int)batchResponse.StatusCode);
            }

            _logger.LogInformation($"Workout session (and potential outbox message) saved atomically. Charge: {batchResponse.RequestCharge} RUs");
            return new OkObjectResult(sessionLog);
        }
        catch(CosmosException ex)
        {
            _logger.LogError($"CosmosDB write failure: {ex.Message}");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}