using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Ombi.Core.Senders.Dvr;

namespace Ombi.Core.Tests.Senders.Dvr
{
    [TestFixture]
    public class MovieDvrSenderFactoryTests
    {
        private static Mock<IMovieDvrSender> CreateSender(int priority, bool enabled)
        {
            var sender = new Mock<IMovieDvrSender>();
            sender.SetupGet(x => x.Priority).Returns(priority);
            sender.Setup(x => x.IsEnabledAsync(It.IsAny<bool>())).ReturnsAsync(enabled);
            return sender;
        }

        [Test]
        public async Task GetEnabledSendersAsync_OrdersByPriority()
        {
            var couchPotato = CreateSender(priority: 2, enabled: true);
            var radarr = CreateSender(priority: 0, enabled: true);
            var dogNzb = CreateSender(priority: 1, enabled: true);
            var factory = new MovieDvrSenderFactory(new[] { couchPotato.Object, dogNzb.Object, radarr.Object });

            var result = await factory.GetEnabledSendersAsync(false);

            Assert.That(result, Is.EqualTo(new[] { radarr.Object, dogNzb.Object, couchPotato.Object }));
        }

        [Test]
        public async Task GetEnabledSendersAsync_ExcludesDisabledSenders()
        {
            var radarr = CreateSender(priority: 0, enabled: false);
            var dogNzb = CreateSender(priority: 1, enabled: true);
            var factory = new MovieDvrSenderFactory(new[] { radarr.Object, dogNzb.Object });

            var result = await factory.GetEnabledSendersAsync(false);

            Assert.That(result, Is.EqualTo(new[] { dogNzb.Object }));
        }

        [Test]
        public async Task GetEnabledSendersAsync_PassesIs4KToEachSender()
        {
            var radarr4K = new Mock<IMovieDvrSender>();
            radarr4K.SetupGet(x => x.Priority).Returns(0);
            radarr4K.Setup(x => x.IsEnabledAsync(true)).ReturnsAsync(true);
            var factory = new MovieDvrSenderFactory(new[] { radarr4K.Object });

            var result = await factory.GetEnabledSendersAsync(true);

            Assert.That(result, Is.EqualTo(new[] { radarr4K.Object }));
            radarr4K.Verify(x => x.IsEnabledAsync(true), Times.Once);
        }
    }
}
