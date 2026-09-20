using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using Moq.AutoMock;
using NUnit.Framework;
using Ombi.Core.Senders;
using Ombi.Core.Senders.Dvr;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Tests.Senders
{
    [TestFixture]
    public class TvSenderTests
    {
        private TvSender _subject;
        private AutoMocker _mocker;

        [SetUp]
        public void Setup()
        {
            _mocker = new AutoMocker();
            _subject = _mocker.CreateInstance<TvSender>();
        }

        private static Mock<ITvDvrSender> CreateSender(bool success, string message = null)
        {
            var sender = new Mock<ITvDvrSender>();
            sender.Setup(x => x.Send(It.IsAny<ChildRequests>()))
                .ReturnsAsync(new SenderResult { Success = success, Sent = success, Message = message });
            return sender;
        }

        [Test]
        public async Task Send_NoStrategiesEnabled_ReturnsSuccessWithoutSending()
        {
            _mocker.Setup<ITvDvrSenderFactory, Task<IReadOnlyList<ITvDvrSender>>>(x => x.GetEnabledSendersAsync())
                .ReturnsAsync(new List<ITvDvrSender>());

            var result = await _subject.Send(new ChildRequests());

            Assert.That(result.Success, Is.True);
            Assert.That(result.Sent, Is.False);
        }

        [Test]
        public async Task Send_FirstStrategySucceeds_SecondIsNeverCalled()
        {
            var first = CreateSender(true);
            var second = CreateSender(true);
            _mocker.Setup<ITvDvrSenderFactory, Task<IReadOnlyList<ITvDvrSender>>>(x => x.GetEnabledSendersAsync())
                .ReturnsAsync(new List<ITvDvrSender> { first.Object, second.Object });

            var result = await _subject.Send(new ChildRequests());

            Assert.That(result.Success, Is.True);
            second.Verify(x => x.Send(It.IsAny<ChildRequests>()), Times.Never);
        }

        [Test]
        public async Task Send_FirstStrategyFails_FallsBackToSecond()
        {
            var first = CreateSender(false, "Sonarr failed");
            var second = CreateSender(true);
            _mocker.Setup<ITvDvrSenderFactory, Task<IReadOnlyList<ITvDvrSender>>>(x => x.GetEnabledSendersAsync())
                .ReturnsAsync(new List<ITvDvrSender> { first.Object, second.Object });

            var result = await _subject.Send(new ChildRequests());

            Assert.That(result.Success, Is.True);
            second.Verify(x => x.Send(It.IsAny<ChildRequests>()), Times.Once);
        }

        [Test]
        public async Task Send_FirstStrategyThrows_SecondIsStillAttempted()
        {
            var first = new Mock<ITvDvrSender>();
            first.Setup(x => x.Send(It.IsAny<ChildRequests>())).ThrowsAsync(new Exception("Sonarr is down"));
            var second = CreateSender(true);
            _mocker.Setup<ITvDvrSenderFactory, Task<IReadOnlyList<ITvDvrSender>>>(x => x.GetEnabledSendersAsync())
                .ReturnsAsync(new List<ITvDvrSender> { first.Object, second.Object });

            var result = await _subject.Send(new ChildRequests());

            Assert.That(result.Success, Is.True);
            second.Verify(x => x.Send(It.IsAny<ChildRequests>()), Times.Once);
        }

        [Test]
        public async Task Send_AllStrategiesFail_AddsToRequestFailureQueue()
        {
            var model = new ChildRequests { Id = 42 };
            var first = CreateSender(false, "Sonarr failed");
            var second = CreateSender(false, "SickRage failed");
            _mocker.Setup<ITvDvrSenderFactory, Task<IReadOnlyList<ITvDvrSender>>>(x => x.GetEnabledSendersAsync())
                .ReturnsAsync(new List<ITvDvrSender> { first.Object, second.Object });

            var result = await _subject.Send(model);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Is.EqualTo("SickRage failed"));
        }
    }
}
