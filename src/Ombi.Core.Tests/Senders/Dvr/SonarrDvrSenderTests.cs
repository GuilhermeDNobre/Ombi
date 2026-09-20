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
    public class SonarrDvrSenderTests
    {
        private SonarrDvrSender _subject;
        private AutoMocker _mocker;

        [SetUp]
        public void Setup()
        {
            _mocker = new AutoMocker();
            _subject = _mocker.CreateInstance<SonarrDvrSender>();
        }

        [Test]
        public async Task IsEnabledAsync_EnabledWithApiKey_ReturnsTrue()
        {
            _mocker.Setup<ISettingsService<SonarrSettings>, Task<SonarrSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new SonarrSettings { Enabled = true, ApiKey = "key" });

            Assert.That(await _subject.IsEnabledAsync(), Is.True);
        }

        [Test]
        public async Task IsEnabledAsync_EnabledButNoApiKey_ReturnsFalse()
        {
            // Regression test: the monolithic TvSender used to treat "Enabled but no ApiKey"
            // as a silent no-op that fell through to the next DVR in the chain. The factory
            // now needs IsEnabledAsync to report false for that same case up front.
            _mocker.Setup<ISettingsService<SonarrSettings>, Task<SonarrSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new SonarrSettings { Enabled = true, ApiKey = "" });

            Assert.That(await _subject.IsEnabledAsync(), Is.False);
        }

        [Test]
        public async Task IsEnabledAsync_Disabled_ReturnsFalse()
        {
            _mocker.Setup<ISettingsService<SonarrSettings>, Task<SonarrSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new SonarrSettings { Enabled = false, ApiKey = "key" });

            Assert.That(await _subject.IsEnabledAsync(), Is.False);
        }
    }
}
