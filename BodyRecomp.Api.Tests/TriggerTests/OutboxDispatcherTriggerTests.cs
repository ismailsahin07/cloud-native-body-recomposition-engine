using Azure.Messaging.ServiceBus;
using BodyRecomp.Api.Triggers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;

namespace BodyRecomp.Api.Tests.TriggerTests
{
    public class OutboxDispatcherTriggerTests
    {
        private readonly Mock<ServiceBusClient> _mockServiceBusClient;
        private readonly Mock<ServiceBusSender> _mockServiceBusSender;
        private readonly Mock<ILogger<OutboxDispatcherTrigger>> _mockLogger;
        private readonly OutboxDispatcherTrigger _sut;

        public OutboxDispatcherTriggerTests()
        {
            _mockServiceBusClient = new Mock<ServiceBusClient>();
            _mockServiceBusSender = new Mock<ServiceBusSender>();
            _mockLogger = new Mock<ILogger<OutboxDispatcherTrigger>>();

            _mockServiceBusClient.Setup(c => c.CreateSender("weekly-analytics"))
                .Returns(_mockServiceBusSender.Object);

            _sut = new OutboxDispatcherTrigger(_mockServiceBusClient.Object, _mockLogger.Object);
        }

        private IReadOnlyList<JsonElement> GenerateChangeFeedBatch(params object[] documents)
        {
            string jsonArray = JsonSerializer.Serialize(documents);

            JsonDocument parsedDocument = JsonDocument.Parse(jsonArray);

            return parsedDocument.RootElement.EnumerateArray().ToList();
        }

        [Fact]
        public async Task Run_WithEmptyInput_SafelyExistsWithoutConnectionToServiceBus()
        {
            var emptyInput = new List<JsonElement>();

            await _sut.Run(emptyInput);

            _mockServiceBusClient.Verify(c => c.CreateSender(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Run_WithMixedDocuments_FiltersCorrectlyAndDispatchesOnlyOutboxMessages()
        {
            var standardLog = new
            {
                id = "log-1",
                type = "WorkoutSessionLog",
                dayNumber = 3
            };

            var validOutboxMessage = new
            {
                id = "outbox-1",
                type = "OutboxMessage",
                eventType = "WeeklyAnalyticsRequested",
                payload = "{\"userId\":\"test-123\", \"targetDate\":\"2026-06-28\"}"
            };

            var input = GenerateChangeFeedBatch(standardLog, validOutboxMessage);

            await _sut.Run(input);

            _mockServiceBusClient.Verify(v => v.CreateSender("weekly-analytics"), Times.Once);

            _mockServiceBusSender.Verify(v => v.SendMessageAsync(
                It.Is<ServiceBusMessage>(msg => msg.Body.ToString().Contains("test-123")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Run_OnNetworkFailure_BubblesExceptionToPreventLeaseUpdate()
        {
            var validOutboxMessage = new
            {
                id = "outbox-1",
                type = "OutboxMessage",
                eventType = "WeeklyAnalyticsRequested",
                payload = "{}"
            };

            var input = GenerateChangeFeedBatch(validOutboxMessage);
            var expectedException = new ServiceBusException("Simulated Network Timeout", ServiceBusFailureReason.ServiceTimeout);

            _mockServiceBusSender.Setup(c => c.SendMessageAsync(
                It.IsAny<ServiceBusMessage>(), 
                It.IsAny<CancellationToken>()))
                .ThrowsAsync(expectedException);

            Func<Task> action = async () => await _sut.Run(input);

            await action.Should().ThrowAsync<ServiceBusException>().WithMessage("*Simulated Network Timeout*");

            _mockServiceBusSender.Verify(v => v.SendMessageAsync(
                It.IsAny<ServiceBusMessage>(), 
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
