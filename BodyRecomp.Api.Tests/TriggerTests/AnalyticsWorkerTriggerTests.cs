using BodyRecomp.Api.Models;
using BodyRecomp.Api.Triggers;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;

namespace BodyRecomp.Api.Tests.TriggerTests
{
    public class AnalyticsWorkerTriggerTests
    {
        private readonly Mock<Container> _mockContainer;
        private readonly Mock<ILogger<AnalyticsWorkerTrigger>> _mockLogger;
        private readonly AnalyticsWorkerTrigger _sut;

        public AnalyticsWorkerTriggerTests()
        {
            _mockContainer = new Mock<Container>();
            _mockLogger = new Mock<ILogger<AnalyticsWorkerTrigger>>();
            _sut = new AnalyticsWorkerTrigger(_mockContainer.Object, _mockLogger.Object);
        }

        private Mock<FeedIterator<T>> SetupMockFeedIterator<T>(List<T> dataToReturn)
        {
            var mockFeedResponse = new Mock<FeedResponse<T>>();
            mockFeedResponse.As<IEnumerable<T>>().Setup(c => c.GetEnumerator()).Returns(dataToReturn.GetEnumerator());

            var mockFeedIterator = new Mock<FeedIterator<T>>();
            mockFeedIterator.SetupSequence(x => x.HasMoreResults).Returns(true).Returns(false);
            mockFeedIterator.Setup(c => c.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(mockFeedResponse.Object);

            return mockFeedIterator;
        }

        [Fact]
        public async Task Run_WithInvalidPayload_AbortsComputation()
        {
            string badMessage = "{\"userId\":\"\"}";

            var mockContext = new Mock<FunctionContext>();

            await _sut.Run(badMessage, mockContext.Object);

            _mockContainer.Verify(v => v.GetItemQueryIterator<DailyMacroLog>(
                It.IsAny<QueryDefinition>(),
                It.IsAny<string>(),
                It.IsAny<QueryRequestOptions>()), Times.Never);
        }

        [Fact]
        public async Task Run_WithNoDatabaseRecords_AbortsWithoutWriting()
        {
            var command = new AnalyticsQueueMessage { UserId = "test-123", TargetDate = "2026-06-29" };
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string message = JsonSerializer.Serialize(command, options);
            var mockContext = new Mock<FunctionContext>();

            var emptyData = new List<DailyMacroLog>();
            var mockIterator = SetupMockFeedIterator(emptyData);

            _mockContainer.Setup(c => c.GetItemQueryIterator<DailyMacroLog>(
                It.IsAny<QueryDefinition>(),
                null,
                It.IsAny<QueryRequestOptions>()))
                .Returns(mockIterator.Object);

            await _sut.Run(message, mockContext.Object);

            _mockContainer.Verify(v => v.GetItemQueryIterator<DailyMacroLog>(
                It.IsAny<QueryDefinition>(),
                null,
                It.IsAny<QueryRequestOptions>()), Times.Once);

            _mockContainer.Verify(v => v.CreateItemAsync(
                It.IsAny<WeeklyMacroSummary>(),
                It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Never);      
        }

        [Fact]
        public async Task Run_WithValidRecords_ComputesAveragesAndPersistsSummary()
        {
            string expectedUserId = "test-123";
            var command = new AnalyticsQueueMessage { UserId = expectedUserId, TargetDate = "2026-06-29" };
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string message = JsonSerializer.Serialize(command, options);
            var mockContext = new Mock<FunctionContext>();

            var logs = new List<DailyMacroLog>
            {
                new DailyMacroLog { ProteinGrams = 100, FatGrams = 40, CarbGrams = 150 },
                new DailyMacroLog { ProteinGrams = 200, FatGrams = 60, CarbGrams = 250 }
            };

            var mockIterator = SetupMockFeedIterator(logs);

            _mockContainer.Setup(c => c.GetItemQueryIterator<DailyMacroLog>(
                It.IsAny<QueryDefinition>(),
                null,
                It.IsAny<QueryRequestOptions>()))
                .Returns(mockIterator.Object);

            WeeklyMacroSummary? capturedSummary = null;
            var mockItemResponse = new Mock<ItemResponse<WeeklyMacroSummary>>();

            _mockContainer.Setup(c => c.CreateItemAsync(
                It.IsAny<WeeklyMacroSummary>(),
                It.Is<PartitionKey>(pk => pk == new PartitionKey(expectedUserId)),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()))
                .Callback<WeeklyMacroSummary, PartitionKey?, ItemRequestOptions, CancellationToken>((item, pk, opt, ct) =>
                {
                    capturedSummary = item;
                })
                .ReturnsAsync(mockItemResponse.Object);

            await _sut.Run(message, mockContext.Object);

            capturedSummary.Should().NotBeNull();
            capturedSummary!.UserId.Should().Be(expectedUserId);
            capturedSummary.DaysLogged.Should().Be(2);

            capturedSummary.AverageProtein.Should().Be(150);
            capturedSummary.AverageFat.Should().Be(50);
            capturedSummary.AverageCarbs.Should().Be(200);
            capturedSummary.AverageCalories.Should().Be(1850);

            _mockContainer.Verify(v => v.CreateItemAsync(
                It.IsAny<WeeklyMacroSummary>(),
                It.Is<PartitionKey>(pk => pk == new PartitionKey(expectedUserId)),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
