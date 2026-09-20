using System.Threading.Tasks;
using Moq;
using Moq.AutoMock;
using NUnit.Framework;
using Ombi.Api.External.ExternalApis.CouchPotato;
using Ombi.Core.Senders.Dvr;
using Ombi.Core.Settings;
using Ombi.Settings.Settings.Models.External;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Tests.Senders.Dvr
{
    [TestFixture]
    public class CouchPotatoDvrSenderTests
    {
        private CouchPotatoDvrSender _subject;
        private AutoMocker _mocker;

        [SetUp]
        public void Setup()
        {
            _mocker = new AutoMocker();
            _subject = _mocker.CreateInstance<CouchPotatoDvrSender>();
        }

        [Test]
        public async Task IsEnabledAsync_ReflectsSettings()
        {
            _mocker.Setup<ISettingsService<CouchPotatoSettings>, Task<CouchPotatoSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new CouchPotatoSettings { Enabled = false });

            Assert.That(await _subject.IsEnabledAsync(false), Is.False);
        }

        [Test]
        public async Task Send_ReflectsApiResult()
        {
            _mocker.Setup<ISettingsService<CouchPotatoSettings>, Task<CouchPotatoSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new CouchPotatoSettings { Enabled = true, ApiKey = "key", DefaultProfileId = "1", Ip = "localhost" });
            _mocker.Setup<ICouchPotatoApi, Task<bool>>(x => x.AddMovie("tt1", "key", "Title", It.IsAny<string>(), "1"))
                .ReturnsAsync(true);

            var result = await _subject.Send(new MovieRequests { ImdbId = "tt1", Title = "Title" }, false);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Sent, Is.True);
        }
    }
}
