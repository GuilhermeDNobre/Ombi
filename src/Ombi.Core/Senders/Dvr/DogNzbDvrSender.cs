using System.Threading.Tasks;
using Ombi.Api.External.ExternalApis.DogNzb;
using Ombi.Core.Settings;
using Ombi.Settings.Settings.Models.External;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Senders.Dvr
{
    public class DogNzbDvrSender : IMovieDvrSender
    {
        public DogNzbDvrSender(IDogNzbApi dogApi, ISettingsService<DogNzbSettings> dogSettings)
        {
            _dogNzbApi = dogApi;
            _dogNzbSettings = dogSettings;
        }

        private readonly IDogNzbApi _dogNzbApi;
        private readonly ISettingsService<DogNzbSettings> _dogNzbSettings;

        public int Priority => 1;

        public async Task<bool> IsEnabledAsync(bool is4K)
        {
            var settings = await _dogNzbSettings.GetSettingsAsync();
            return settings.Enabled;
        }

        public async Task<SenderResult> Send(MovieRequests model, bool is4K)
        {
            var settings = await _dogNzbSettings.GetSettingsAsync();
            // DogNzbMovieAddResult is a raw RSS response with no explicit success/failure field;
            // a non-null response is the only signal the API gives us that the add request went through.
            var result = await _dogNzbApi.AddMovie(settings.ApiKey, model.ImdbId);
            return new SenderResult { Success = result != null, Sent = result != null };
        }
    }
}
