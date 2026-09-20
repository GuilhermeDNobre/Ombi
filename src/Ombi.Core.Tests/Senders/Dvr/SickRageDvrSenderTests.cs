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
    public class SickRageDvrSenderTests
    {
        private SickRageDvrSender _subject;
        private AutoMocker _mocker;

        [SetUp]
        public void Setup()
        {
            _mocker = new AutoMocker();
            _subject = _mocker.CreateInstance<SickRageDvrSender>();
        }

        [Test]
        public async Task IsEnabledAsync_ReflectsSettings()
        {
            _mocker.Setup<ISettingsService<SickRageSettings>, Task<SickRageSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new SickRageSettings { Enabled = true });

            Assert.That(await _subject.IsEnabledAsync(), Is.True);
        }

        [Test]
        public async Task IsEnabledAsync_Disabled_ReturnsFalse()
        {
            _mocker.Setup<ISettingsService<SickRageSettings>, Task<SickRageSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new SickRageSettings { Enabled = false });

            Assert.That(await _subject.IsEnabledAsync(), Is.False);
        }
    }
}
