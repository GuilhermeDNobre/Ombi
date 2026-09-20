using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.ExternalApis.SickRage;
using Ombi.Api.External.ExternalApis.SickRage.Models;
using Ombi.Core.Settings;
using Ombi.Helpers;
using Ombi.Settings.Settings.Models.External;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Senders.Dvr
{
    public class SickRageDvrSender : ITvDvrSender
    {
        public SickRageDvrSender(ISickRageApi srApi, ILogger<SickRageDvrSender> log, ISettingsService<SickRageSettings> srSettings)
        {
            SickRageApi = srApi;
            Logger = log;
            SickRageSettings = srSettings;
        }

        private ISickRageApi SickRageApi { get; }
        private ILogger<SickRageDvrSender> Logger { get; }
        private ISettingsService<SickRageSettings> SickRageSettings { get; }

        public int Priority => 1;

        public async Task<bool> IsEnabledAsync()
        {
            var settings = await SickRageSettings.GetSettingsAsync();
            return settings.Enabled;
        }

        public async Task<SenderResult> Send(ChildRequests model)
        {
            var settings = await SickRageSettings.GetSettingsAsync();
            var result = await SendToSickRage(model, settings);
            return new SenderResult
            {
                Success = result,
                Sent = result,
                Message = result ? null : "Could not send to SickRage!"
            };
        }

        private async Task<bool> SendToSickRage(ChildRequests model, SickRageSettings settings, string qualityId = null)
        {
            var tvdbid = model.ParentRequest.TvDbId;
            if (qualityId.HasValue())
            {
                var id = qualityId;
                if (settings.Qualities.All(x => x.Value != id))
                {
                    qualityId = settings.QualityProfile;
                }
            }
            else
            {
                qualityId = settings.QualityProfile;
            }
            // Check if the show exists
            var existingShow = await SickRageApi.GetShow(tvdbid, settings.ApiKey, settings.FullUri);

            if (existingShow.message.Equals("Show not found", StringComparison.CurrentCultureIgnoreCase))
            {
                var addResult = await SickRageApi.AddSeries(model.ParentRequest.TvDbId, qualityId, SickRageStatus.Ignored,
                    settings.ApiKey, settings.FullUri);

                Logger.LogDebug("Added the show (tvdbid) {0}. The result is '{2}' : '{3}'", tvdbid, addResult.result, addResult.message);
                if (addResult.result.Equals("failure") || addResult.result.Equals("fatal"))
                {
                    // Do something
                    return false;
                }
            }

            foreach (var seasonRequests in model.SeasonRequests)
            {
                var srEpisodes = await SickRageApi.GetEpisodesForSeason(tvdbid, seasonRequests.SeasonNumber, settings.ApiKey, settings.FullUri);
                int retryTimes = 10;
                var currentRetry = 0;
                while (srEpisodes.message.Equals("Show not found", StringComparison.CurrentCultureIgnoreCase) || srEpisodes.message.Equals("Season not found", StringComparison.CurrentCultureIgnoreCase) && srEpisodes.data.Count <= 0)
                {
                    if (currentRetry > retryTimes)
                    {
                        Logger.LogWarning("Couldnt find the SR Season or Show, message: {0}", srEpisodes.message);
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    currentRetry++;
                    srEpisodes = await SickRageApi.GetEpisodesForSeason(tvdbid, seasonRequests.SeasonNumber, settings.ApiKey, settings.FullUri);
                }

                var totalSrEpisodes = srEpisodes.data.Count;

                if (totalSrEpisodes == seasonRequests.Episodes.Count)
                {
                    // This is a request for the whole season
                    var wholeSeasonResult = await SickRageApi.SetEpisodeStatus(settings.ApiKey, settings.FullUri, tvdbid, SickRageStatus.Wanted,
                        seasonRequests.SeasonNumber);

                    Logger.LogDebug("Set the status to Wanted for season {0}. The result is '{1}' : '{2}'", seasonRequests.SeasonNumber, wholeSeasonResult.result, wholeSeasonResult.message);
                    continue;
                }

                foreach (var srEp in srEpisodes.data)
                {
                    var epNumber = srEp.Key;
                    var epData = srEp.Value;

                    var epRequest = seasonRequests.Episodes.FirstOrDefault(x => x.EpisodeNumber == epNumber);
                    if (epRequest != null)
                    {
                        // We want to monior this episode since we have a request for it
                        // Let's check to see if it's wanted first, save an api call
                        if (epData.status.Equals(SickRageStatus.Wanted, StringComparison.CurrentCultureIgnoreCase))
                        {
                            continue;
                        }
                        var epResult = await SickRageApi.SetEpisodeStatus(settings.ApiKey, settings.FullUri, tvdbid,
                            SickRageStatus.Wanted, seasonRequests.SeasonNumber, epNumber);

                        Logger.LogDebug("Set the status to Wanted for Episode {0} in season {1}. The result is '{2}' : '{3}'", seasonRequests.SeasonNumber, epNumber, epResult.result, epResult.message);
                    }
                }
            }
            return true;
        }
    }
}
