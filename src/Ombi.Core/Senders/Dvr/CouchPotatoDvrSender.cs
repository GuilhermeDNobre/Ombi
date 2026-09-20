using System.Threading.Tasks;
using Ombi.Api.External.ExternalApis.CouchPotato;
using Ombi.Core.Settings;
using Ombi.Settings.Settings.Models.External;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Senders.Dvr
{
    public class CouchPotatoDvrSender : IMovieDvrSender
    {
        public CouchPotatoDvrSender(ICouchPotatoApi cpApi, ISettingsService<CouchPotatoSettings> cpSettings)
        {
            _couchPotatoApi = cpApi;
            _couchPotatoSettings = cpSettings;
        }

        private readonly ICouchPotatoApi _couchPotatoApi;
        private readonly ISettingsService<CouchPotatoSettings> _couchPotatoSettings;

        public int Priority => 2;

        public async Task<bool> IsEnabledAsync(bool is4K)
        {
            var settings = await _couchPotatoSettings.GetSettingsAsync();
            return settings.Enabled;
        }

        public async Task<SenderResult> Send(MovieRequests model, bool is4K)
        {
            var settings = await _couchPotatoSettings.GetSettingsAsync();
            var result = await _couchPotatoApi.AddMovie(model.ImdbId, settings.ApiKey, model.Title, settings.FullUri, settings.DefaultProfileId);
            return new SenderResult { Success = result, Sent = true };
        }
    }
}
