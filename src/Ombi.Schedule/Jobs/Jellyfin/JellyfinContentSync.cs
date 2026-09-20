using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ombi.Helpers;
using Ombi.Schedule.Jobs.MediaServer;
using Ombi.Store.Entities;
using Ombi.Store.Repository;

namespace Ombi.Schedule.Jobs.Jellyfin
{
    public class JellyfinContentSync : MediaServerContentSync<JellyfinContent>, IJellyfinContentSync
    {
        public JellyfinContentSync(
            IMediaServerCatalog<JellyfinContent> catalog,
            IJellyfinContentRepository repo,
            ILogger<JellyfinContentSync> logger):
            base(catalog, repo, logger)
        {
        }

        protected override async Task OnSyncCompleted()
        {
            // Episodes
            await OmbiQuartz.TriggerJob(nameof(IJellyfinEpisodeSync), "Jellyfin");
        }
    }

}
