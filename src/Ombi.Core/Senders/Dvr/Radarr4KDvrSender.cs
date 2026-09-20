using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.ExternalApis.Radarr;
using Ombi.Core.Settings;
using Ombi.Settings.Settings.Models.External;
using Ombi.Store.Entities;
using Ombi.Store.Repository;

namespace Ombi.Core.Senders.Dvr
{
    public sealed class Radarr4KDvrSender : RadarrDvrSenderBase
    {
        public Radarr4KDvrSender(IRadarrV3Api radarrV3Api, ILogger<RadarrDvrSenderBase> log,
            IRepository<UserQualityProfiles> userProfiles, ISettingsService<Radarr4KSettings> radarr4KSettings)
            : base(radarrV3Api, log, userProfiles)
        {
            _radarr4KSettings = radarr4KSettings;
        }

        private readonly ISettingsService<Radarr4KSettings> _radarr4KSettings;

        protected override bool IsFor4K => true;

        protected override async Task<RadarrSettings> GetSettingsAsync() => await _radarr4KSettings.GetSettingsAsync();
    }
}
