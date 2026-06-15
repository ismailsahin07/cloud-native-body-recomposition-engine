using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using BodyRecomp.Api.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace BodyRecomp.Api.Endpoints;

public class PhotosEndpoint
{
    private readonly ILogger<PhotosEndpoint> _logger;
    private readonly BlobContainerClient _blobContainerClient;

    public PhotosEndpoint([FromKeyedServices(StorageContainerKey.ProgressPhotos)] BlobContainerClient blobContainerClient,ILogger<PhotosEndpoint> logger)
    {
        _blobContainerClient = blobContainerClient;
        _logger = logger;
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
            var blobClient = _blobContainerClient.GetBlobClient(blobName);

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = _blobContainerClient.Name,
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