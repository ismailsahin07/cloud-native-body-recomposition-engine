using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace BodyRecomp.Api.Triggers;

public class ProcessDeadletterTrigger
{
    private readonly ILogger<ProcessDeadletterTrigger> _logger;

    public ProcessDeadletterTrigger(ILogger<ProcessDeadletterTrigger> logger)
    {
        _logger = logger;
    }

    [Function(nameof(ProcessDeadletterTrigger))]
    public async Task Run(
        [BlobTrigger("eventgrid-deadletter515/{name}", Connection = "AzureWebJobsStorage")] string deadLetterJson,
        string name)
    {
        _logger.LogWarning($"Alert: A storage event has permanently failed delivery and been dead-lettered. File name: {name}");

        JsonNode deadLetterNode = JsonNode.Parse(deadLetterJson)!;
        
        if(deadLetterNode is not JsonArray jsonArray)
        {
            _logger.LogError("Dead-letter JSON structure was not an array as expected.");
            return;
        }

        foreach(JsonNode? eventNode in jsonArray)
        {
            if (eventNode == null) continue;

            string reason = eventNode["deadLetterReason"]?.ToString() ?? "Unknown Reason";
            string failedBlob = eventNode["subject"]?.ToString() ?? "Unknown Blob Target";

            _logger.LogError($"Failed resource: {failedBlob} | Reason: {reason}");
        }
    }
}
