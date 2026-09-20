using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ombi.Core.Services;
using Ombi.Helpers;
using Ombi.Schedule.Jobs.MediaServer;
using Ombi.Settings.Settings.Models;
using Ombi.Store.Entities;
using Ombi.Store.Repository;
using Quartz;
using MediaType = Ombi.Store.Entities.MediaType;

namespace Ombi.Schedule.Jobs.Emby
{
    public class EmbyContentSync : MediaServerContentSync<EmbyContent>, IEmbyContentSync
    {
        public EmbyContentSync(
            IMediaServerCatalog<EmbyContent> catalog,
            IEmbyContentRepository repo,
            ILogger<EmbyContentSync> logger,
            IFeatureService feature):
            base(catalog, repo, logger)
        {
            _repo = repo;
            _feature = feature;
        }

        private readonly IEmbyContentRepository _repo;
        private readonly IFeatureService _feature;

        private const int DeleteBatchSize = 1000;

        // Every EmbyId the server reported during this run, used to work out which
        // database records no longer exist on the server.
        private readonly HashSet<string> _seenEmbyIds = new HashSet<string>();

        protected override bool StopOnEmptyPage => true;

        protected override bool AddSeriesWithoutProviderIds => true;

        protected override bool ToleratesMissingProviderIds => true;

        protected override bool RunSyncCompletedWhenDisabled => true;

        protected override void OnSyncStarting(IJobExecutionContext context)
        {
            // The set is run-scoped; clear it in case the same job instance is reused
            _seenEmbyIds.Clear();

            JobDataMap dataMap = context.MergedJobDataMap;
            if (dataMap.TryGetValue(JobDataKeys.EmbyRecentlyAddedSearch, out var recentlyAddedObj))
            {
                recentlyAdded = Convert.ToBoolean(recentlyAddedObj);
            }
        }

        protected override void OnContentSeen(string mediaServerId)
        {
            _seenEmbyIds.Add(mediaServerId);
        }

        protected override async Task OnSyncCompleted()
        {
            // A full sync has seen everything on the server, so anything in our database
            // that was not reported no longer exists in Emby (or lives in a library that
            // is not enabled) and would otherwise produce incorrect availability results.
            if (!recentlyAdded)
            {
                await RemoveStaleContent();
            }

            // Episodes
            await OmbiQuartz.Scheduler.TriggerJob(new JobKey(nameof(IEmbyEpisodeSync), "Emby"), new JobDataMap(new Dictionary<string, string> { { JobDataKeys.EmbyRecentlyAddedSearch, recentlyAdded.ToString() } }));

            // Played state
            var isPlayedSyncEnabled = await _feature.FeatureEnabled(FeatureNames.PlayedSync);
            if(isPlayedSyncEnabled)
            {
                await OmbiQuartz.Scheduler.TriggerJob(new JobKey(nameof(IEmbyPlayedSync), "Emby"), new JobDataMap(new Dictionary<string, string> { { JobDataKeys.EmbyRecentlyAddedSearch, recentlyAdded.ToString() } }));
            }
        }

        /// <summary>
        /// Removes content records (and their episodes) that were not reported by the
        /// server during this run. This clears out ghosts left behind by removed or
        /// reidentified items, which otherwise keep matching availability lookups forever.
        /// Only runs after a complete full sync so we never delete based on partial data.
        /// </summary>
        private async Task RemoveStaleContent()
        {
            if (syncIncomplete)
            {
                Logger.LogWarning("Skipping the stale Emby content cleanup because the sync did not fully complete. Removing records based on a partial sync could delete content that still exists on the server.");
                return;
            }

            if (!_seenEmbyIds.Any())
            {
                Logger.LogInformation("Skipping the stale Emby content cleanup because the server reported no content.");
                return;
            }

            var dbContent = await _repo.GetAllContentIdentifiers();
            var staleContent = dbContent.Where(x => string.IsNullOrEmpty(x.EmbyId) || !_seenEmbyIds.Contains(x.EmbyId)).ToList();
            if (!staleContent.Any())
            {
                return;
            }

            Logger.LogInformation("Removing {0} Emby content records that no longer exist on the server", staleContent.Count);

            var staleSeriesIds = staleContent
                .Where(x => x.Type == MediaType.Series && !string.IsNullOrEmpty(x.EmbyId))
                .Select(x => x.EmbyId)
                .ToHashSet();
            if (staleSeriesIds.Any())
            {
                var allEpisodes = await _repo.GetAllEpisodeIdentifiers();
                var orphanedEpisodes = allEpisodes.Where(x => staleSeriesIds.Contains(x.ParentId)).ToList();
                if (orphanedEpisodes.Any())
                {
                    Logger.LogInformation("Removing {0} episodes belonging to the removed series", orphanedEpisodes.Count);
                    foreach (var chunk in orphanedEpisodes.Chunk(DeleteBatchSize))
                    {
                        await _repo.DeleteEpisodes(chunk);
                    }
                }
            }

            foreach (var chunk in staleContent.Chunk(DeleteBatchSize))
            {
                await _repo.DeleteRange(chunk);
            }
        }
    }

}
