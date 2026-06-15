using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace BodyRecomp.Api.Endpoints;

public class PhotosEndpoint
{
    private readonly ILogger<PhotosEndpoint> _logger;
    private readonly string _storageConnectionString;

    public PhotosEndpoint(ILogger<PhotosEndpoint> logger)
    {
        _logger = logger;

        _storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
            ?? throw new InvalidOperationException("Storage connection string is not configured.");
    }

    /// <summary>
    /// GET  /api/photos/upload-token - Generates a short-lived, secure SAS URI for direct blob uploads
    /// </summary>
    [Function(nameof(GetUploadToken))]
    public async Task<IActionResult> GetUploadToken([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "photos/upload-token")] HttpRequest req)
    {
        _logger.LogInformation("Processing a request for secure SAS upload token serialization...");

        var user = req.HttpContext.User;
        string? userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("oid")?.Value;

        if(string.IsNullOrEmpty(userId))
            return new UnauthorizedResult();

        string timeStamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss");
        string blobName = $"{userId}/{timeStamp}-checkin.jpg";

        try
        {
            var blobServiceClient = new BlobServiceClient(_storageConnectionString);
            var blobContainerClient = blobServiceClient.GetBlobContainerClient("progress-photos");
            var blobClient = blobContainerClient.GetBlobClient(blobName);

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = blobContainerClient.Name,
                BlobName = blobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(15)
            };

            sasBuilder.SetPermissions(BlobAccountSasPermissions.Write);

            Uri uploadUri = blobClient.GenerateSasUri(sasBuilder);

            var responsePayload = new
            {
                TargetBlobName = blobName,
                UploadUri = uploadUri.ToString()
            };

            return new OkObjectResult(responsePayload);
        }
        catch(Exception ex)
        {
            _logger.LogError($"Cryptographic SAS generation pipeline failure: {ex.Message}");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}