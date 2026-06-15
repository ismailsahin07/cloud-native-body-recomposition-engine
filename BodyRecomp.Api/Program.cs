using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Storage.Blobs;
using BodyRecomp.Api.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddSingleton<CosmosClient>(serviceProvider =>
{
    string connectionString = Environment.GetEnvironmentVariable("CosmosConnectionString")
        ?? throw new InvalidOperationException("CosmosConnectionString is missing from the configuration.");

    return new CosmosClient(connectionString, new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions()
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
        }
    });
});

builder.Services.AddKeyedSingleton<Container>(CosmosContainerKey.UserData, (sp, key) =>
{
    var cosmosClient = sp.GetRequiredService<CosmosClient>();

    string dbName = "NutritionFitnessDb";
    string containerName = "UserData";

    return cosmosClient.GetContainer(dbName, containerName);
});

builder.Services.AddSingleton<BlobServiceClient>(serviceProvider =>
{
    string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
        ?? throw new InvalidOperationException("Storage connection string is not configured.");
    return new BlobServiceClient(connectionString);
});

builder.Services.AddKeyedSingleton<BlobContainerClient>(StorageContainerKey.ProgressPhotos, (sp, key) =>
{
    var blobServiceClient = sp.GetRequiredService<BlobServiceClient>();
    return blobServiceClient.GetBlobContainerClient("progress-photos");
});

builder.Services.AddKeyedSingleton<BlobContainerClient>(StorageContainerKey.Thumbnails, (sp, key) =>
{
    var blobServiceClient = sp.GetRequiredService<BlobServiceClient>();
    return blobServiceClient.GetBlobContainerClient("thumbnails");
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        string tenantId = Environment.GetEnvironmentVariable("AzureAdTenantId")
            ?? throw new InvalidOperationException("AzureAdTenantId is missing from the configuration.");
        string clientId = Environment.GetEnvironmentVariable("AzureAdClientId")
            ?? throw new InvalidOperationException("AzureAdClientId is missing from the configuration.");

        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.Audience = $"api://{clientId}";

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(5)
        };
    });

builder.Services.AddAuthorizationBuilder();

builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .UseAzureMonitorExporter();

builder.Build().Run();