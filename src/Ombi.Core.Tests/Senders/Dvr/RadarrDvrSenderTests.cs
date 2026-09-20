using System.Threading.Tasks;
using Moq;
using Moq.AutoMock;
using NUnit.Framework;
using Ombi.Core.Senders.Dvr;
using Ombi.Core.Settings;
using Ombi.Settings.Settings.Models.External;

namespace Ombi.Core.Tests.Senders.Dvr
{
    [TestFixture]
    public class RadarrDvrSenderTests
    {
        private RadarrDvrSender _subject;
        private AutoMocker _mocker;

        [SetUp]
        public void Setup()
        {
            _mocker = new AutoMocker();
            _subject = _mocker.CreateInstance<RadarrDvrSender>();
        }

        [Test]
        public async Task IsEnabledAsync_Is4KRequest_NeverEligible()
        {
            // This instance only handles non-4K requests; the 4K-flavoured request
            // must be routed to Radarr4KDvrSender instead, regardless of settings.
            _mocker.Setup<ISettingsService<RadarrSettings>, Task<RadarrSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new RadarrSettings { Enabled = true });

            Assert.That(await _subject.IsEnabledAsync(is4K: true), Is.False);
        }

        [Test]
        public async Task IsEnabledAsync_NonIs4KRequest_ReflectsSettings()
        {
            _mocker.Setup<ISettingsService<RadarrSettings>, Task<RadarrSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new RadarrSettings { Enabled = true });

            Assert.That(await _subject.IsEnabledAsync(is4K: false), Is.True);
        }
    }

    [TestFixture]
    public class Radarr4KDvrSenderTests
    {
        private Radarr4KDvrSender _subject;
        private AutoMocker _mocker;

        [SetUp]
        public void Setup()
        {
            _mocker = new AutoMocker();
            _subject = _mocker.CreateInstance<Radarr4KDvrSender>();
        }

        [Test]
        public async Task IsEnabledAsync_NonIs4KRequest_NeverEligible()
        {
            _mocker.Setup<ISettingsService<Radarr4KSettings>, Task<Radarr4KSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new Radarr4KSettings { Enabled = true });

            Assert.That(await _subject.IsEnabledAsync(is4K: false), Is.False);
        }

        [Test]
        public async Task IsEnabledAsync_Is4KRequest_ReflectsSettings()
        {
            _mocker.Setup<ISettingsService<Radarr4KSettings>, Task<Radarr4KSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new Radarr4KSettings { Enabled = true });

            Assert.That(await _subject.IsEnabledAsync(is4K: true), Is.True);
        }
    }
}
