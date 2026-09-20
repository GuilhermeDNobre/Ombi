using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.ExternalApis.Radarr;
using Ombi.Core.Settings;
using Ombi.Settings.Settings.Models.External;
using Ombi.Store.Entities;
using Ombi.Store.Repository;

namespace Ombi.Core.Senders.Dvr
{
    public sealed class RadarrDvrSender : RadarrDvrSenderBase
    {
        public RadarrDvrSender(IRadarrV3Api radarrV3Api, ILogger<RadarrDvrSenderBase> log,
            IRepository<UserQualityProfiles> userProfiles, ISettingsService<RadarrSettings> radarrSettings)
            : base(radarrV3Api, log, userProfiles)
        {
            _radarrSettings = radarrSettings;
        }

        private readonly ISettingsService<RadarrSettings> _radarrSettings;

        protected override bool IsFor4K => false;

        protected override Task<RadarrSettings> GetSettingsAsync() => _radarrSettings.GetSettingsAsync();
    }
}
