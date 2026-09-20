using System.Threading.Tasks;
using Moq;
using Moq.AutoMock;
using NUnit.Framework;
using Ombi.Api.External.ExternalApis.DogNzb;
using Ombi.Api.External.ExternalApis.DogNzb.Models;
using Ombi.Core.Senders.Dvr;
using Ombi.Core.Settings;
using Ombi.Settings.Settings.Models.External;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Tests.Senders.Dvr
{
    [TestFixture]
    public class DogNzbDvrSenderTests
    {
        private DogNzbDvrSender _subject;
        private AutoMocker _mocker;

        [SetUp]
        public void Setup()
        {
            _mocker = new AutoMocker();
            _subject = _mocker.CreateInstance<DogNzbDvrSender>();
        }

        [Test]
        public async Task IsEnabledAsync_ReflectsSettings()
        {
            _mocker.Setup<ISettingsService<DogNzbSettings>, Task<DogNzbSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new DogNzbSettings { Enabled = true });

            Assert.That(await _subject.IsEnabledAsync(false), Is.True);
        }

        [Test]
        public async Task Send_ApiReturnsResult_ReportsSuccess()
        {
            _mocker.Setup<ISettingsService<DogNzbSettings>, Task<DogNzbSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new DogNzbSettings { Enabled = true, ApiKey = "key" });
            _mocker.Setup<IDogNzbApi, Task<DogNzbMovieAddResult>>(x => x.AddMovie("key", "tt123"))
                .ReturnsAsync(new DogNzbMovieAddResult());

            var result = await _subject.Send(new MovieRequests { ImdbId = "tt123" }, false);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Sent, Is.True);
        }

        [Test]
        public async Task Send_ApiReturnsNull_ReportsFailure_InsteadOfForcingSuccess()
        {
            // Regression test for the bug where MovieSender used to discard SendToDogNzb's
            // result and always report Success = true, even when the add call failed.
            _mocker.Setup<ISettingsService<DogNzbSettings>, Task<DogNzbSettings>>(x => x.GetSettingsAsync())
                .ReturnsAsync(new DogNzbSettings { Enabled = true, ApiKey = "key" });
            _mocker.Setup<IDogNzbApi, Task<DogNzbMovieAddResult>>(x => x.AddMovie("key", "tt123"))
                .ReturnsAsync((DogNzbMovieAddResult)null);

            var result = await _subject.Send(new MovieRequests { ImdbId = "tt123" }, false);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Sent, Is.False);
        }
    }
}
