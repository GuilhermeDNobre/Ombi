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
    public class MovieSenderTests
    {
        private MovieSender _subject;
        private AutoMocker _mocker;

        [SetUp]
        public void Setup()
        {
            _mocker = new AutoMocker();
            _subject = _mocker.CreateInstance<MovieSender>();
        }

        private static Mock<IMovieDvrSender> CreateSender(bool success, string message = null)
        {
            var sender = new Mock<IMovieDvrSender>();
            sender.Setup(x => x.Send(It.IsAny<MovieRequests>(), It.IsAny<bool>()))
                .ReturnsAsync(new SenderResult { Success = success, Sent = success, Message = message });
            return sender;
        }

        [Test]
        public async Task Send_NoStrategiesEnabled_ReturnsSuccessWithoutSending()
        {
            _mocker.Setup<IMovieDvrSenderFactory, Task<IReadOnlyList<IMovieDvrSender>>>(x => x.GetEnabledSendersAsync(It.IsAny<bool>()))
                .ReturnsAsync(new List<IMovieDvrSender>());

            var result = await _subject.Send(new MovieRequests(), false);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Sent, Is.False);
        }

        [Test]
        public async Task Send_FirstStrategySucceeds_SecondIsNeverCalled()
        {
            var radarr = CreateSender(true);
            var dogNzb = CreateSender(true);
            _mocker.Setup<IMovieDvrSenderFactory, Task<IReadOnlyList<IMovieDvrSender>>>(x => x.GetEnabledSendersAsync(It.IsAny<bool>()))
                .ReturnsAsync(new List<IMovieDvrSender> { radarr.Object, dogNzb.Object });

            var result = await _subject.Send(new MovieRequests(), false);

            Assert.That(result.Success, Is.True);
            dogNzb.Verify(x => x.Send(It.IsAny<MovieRequests>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public async Task Send_RadarrFails_FallsBackToDogNzb()
        {
            var radarr = CreateSender(false, "Radarr failed");
            var dogNzb = CreateSender(true);
            _mocker.Setup<IMovieDvrSenderFactory, Task<IReadOnlyList<IMovieDvrSender>>>(x => x.GetEnabledSendersAsync(It.IsAny<bool>()))
                .ReturnsAsync(new List<IMovieDvrSender> { radarr.Object, dogNzb.Object });

            var result = await _subject.Send(new MovieRequests(), false);

            Assert.That(result.Success, Is.True);
            dogNzb.Verify(x => x.Send(It.IsAny<MovieRequests>(), It.IsAny<bool>()), Times.Once);
        }

        [Test]
        public async Task Send_Is4K_IsForwardedToFactory()
        {
            _mocker.Setup<IMovieDvrSenderFactory, Task<IReadOnlyList<IMovieDvrSender>>>(x => x.GetEnabledSendersAsync(true))
                .ReturnsAsync(new List<IMovieDvrSender>());

            await _subject.Send(new MovieRequests(), true);

            _mocker.Verify<IMovieDvrSenderFactory>(x => x.GetEnabledSendersAsync(true), Times.Once);
        }

        [Test]
        public async Task Send_AllStrategiesFail_AddsToRequestFailureQueue()
        {
            var model = new MovieRequests { Id = 7 };
            var radarr = CreateSender(false, "Radarr failed");
            var couchPotato = CreateSender(false, "CouchPotato failed");
            _mocker.Setup<IMovieDvrSenderFactory, Task<IReadOnlyList<IMovieDvrSender>>>(x => x.GetEnabledSendersAsync(It.IsAny<bool>()))
                .ReturnsAsync(new List<IMovieDvrSender> { radarr.Object, couchPotato.Object });

            var result = await _subject.Send(model, false);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Is.EqualTo("CouchPotato failed"));
        }
    }
}
