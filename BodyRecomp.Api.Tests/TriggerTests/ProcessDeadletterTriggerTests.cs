using BodyRecomp.Api.Triggers;
using Microsoft.Extensions.Logging;
using Moq;

namespace BodyRecomp.Api.Tests.TriggerTests
{
    public class ProcessDeadletterTriggerTests
    {
        private readonly Mock<ILogger<ProcessDeadletterTrigger>> _mockLogger;
        private readonly ProcessDeadletterTrigger _sut;

        public ProcessDeadletterTriggerTests()
        {
            _mockLogger = new Mock<ILogger<ProcessDeadletterTrigger>>();
            _sut = new ProcessDeadletterTrigger(_mockLogger.Object);
        }

        private void VerifyLog(LogLevel level, string expectedMessage,Times times)
        {
            _mockLogger.Verify(logger => logger.Log(
                It.Is<LogLevel>(l => l == level),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(expectedMessage)),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)
                ), times);
        }

        [Fact]
        public async Task Run_WithNonArrayJson_LogsErrorAndExits()
        {
            string fileName = "deadletter-1.json";

            string jsonPayload = "{\"message\": \"object, not an array\"}";

            await _sut.Run(jsonPayload, fileName);

            VerifyLog(LogLevel.Warning, fileName, Times.Once());
            VerifyLog(LogLevel.Error, "not an array", Times.Once());
        }

        [Fact]
        public async Task Run_WithValidDeadletterArray_ParsesAndLogsFailureDetails()
        {
            string fileName = "deadletter-2.json";

            string jsonPayload = @"
                [
                    {
                        ""deadLetterReason"": ""MaxDeliveryAttemptsExceeded"",
                        ""subject"": ""/blobServices/default/containers/progress-photos/blobs/checkin.jpg""
                    }
                ]";

            await _sut.Run(jsonPayload, fileName);

            VerifyLog(LogLevel.Error, "MaxDeliveryAttemptsExceeded", Times.Once());
            VerifyLog(LogLevel.Error, "checkin.jpg", Times.Once());
        }
    }
}
