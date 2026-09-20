using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Ombi.Core.Senders.Dvr;

namespace Ombi.Core.Tests.Senders.Dvr
{
    [TestFixture]
    public class TvDvrSenderFactoryTests
    {
        private static Mock<ITvDvrSender> CreateSender(int priority, bool enabled)
        {
            var sender = new Mock<ITvDvrSender>();
            sender.SetupGet(x => x.Priority).Returns(priority);
            sender.Setup(x => x.IsEnabledAsync()).ReturnsAsync(enabled);
            return sender;
        }

        [Test]
        public async Task GetEnabledSendersAsync_OrdersByPriority()
        {
            var sickRage = CreateSender(priority: 1, enabled: true);
            var sonarr = CreateSender(priority: 0, enabled: true);
            var factory = new TvDvrSenderFactory(new[] { sickRage.Object, sonarr.Object });

            var result = await factory.GetEnabledSendersAsync();

            Assert.That(result, Is.EqualTo(new[] { sonarr.Object, sickRage.Object }));
        }

        [Test]
        public async Task GetEnabledSendersAsync_ExcludesDisabledSenders()
        {
            var sonarr = CreateSender(priority: 0, enabled: false);
            var sickRage = CreateSender(priority: 1, enabled: true);
            var factory = new TvDvrSenderFactory(new[] { sonarr.Object, sickRage.Object });

            var result = await factory.GetEnabledSendersAsync();

            Assert.That(result, Is.EqualTo(new[] { sickRage.Object }));
        }

        [Test]
        public async Task GetEnabledSendersAsync_NoneEnabled_ReturnsEmpty()
        {
            var sonarr = CreateSender(priority: 0, enabled: false);
            var factory = new TvDvrSenderFactory(new[] { sonarr.Object });

            var result = await factory.GetEnabledSendersAsync();

            Assert.That(result, Is.Empty);
        }
    }
}
