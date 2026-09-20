using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.ExternalApis.Sonarr;
using Ombi.Api.External.ExternalApis.Sonarr.Models;
using Ombi.Core.Settings;
using Ombi.Helpers;
using Ombi.Settings.Settings.Models.External;
using Ombi.Store.Entities;
using Ombi.Store.Entities.Requests;
using Ombi.Store.Repository;

namespace Ombi.Core.Senders.Dvr
{
    public class SonarrDvrSender : ITvDvrSender
    {
        public SonarrDvrSender(ISonarrV3Api sonarrV3Api, ILogger<SonarrDvrSender> log,
            ISettingsService<SonarrSettings> sonarrSettings, IRepository<UserQualityProfiles> userProfiles)
        {
            SonarrApi = sonarrV3Api;
            Logger = log;
            SonarrSettings = sonarrSettings;
            UserQualityProfiles = userProfiles;
        }

        private ISonarrV3Api SonarrApi { get; }
        private ILogger<SonarrDvrSender> Logger { get; }
        private ISettingsService<SonarrSettings> SonarrSettings { get; }
        private IRepository<UserQualityProfiles> UserQualityProfiles { get; }

        public int Priority => 0;

        public async Task<bool> IsEnabledAsync()
        {
            var s = await SonarrSettings.GetSettingsAsync();
            return s.Enabled && !string.IsNullOrEmpty(s.ApiKey);
        }

        public async Task<SenderResult> Send(ChildRequests model)
        {
            var s = await SonarrSettings.GetSettingsAsync();
            var result = await SendToSonarr(model, s);
            return new SenderResult
            {
                Success = result != null,
                Sent = result != null,
            };
        }

        /// <summary>
        /// Send the request to Sonarr to process
        /// </summary>
        /// <param name="s"></param>
        /// <param name="model"></param>
        /// <returns></returns>
        private async Task<NewSeries> SendToSonarr(ChildRequests model, SonarrSettings s)
        {
            if (string.IsNullOrEmpty(s.ApiKey))
            {
                return null;
            }
            var options = new SonarrSendOptions();

            int qualityToUse;
            var languageProfileId = s.LanguageProfile;
            string rootFolderPath;
            string seriesType;
            int? tagToUse = null;

            Logger.LogInformation("Starting SendToSonarr for series {Title} (TvDbId: {TvDbId})", model.ParentRequest.Title, model.ParentRequest.TvDbId);
            Logger.LogInformation("Series type: {SeriesType}", model.SeriesType);

            var profiles = await UserQualityProfiles.GetAll().FirstOrDefaultAsync(x => x.UserId == model.RequestedUserId);
            if (profiles != null)
            {
                Logger.LogInformation("Found user quality profile for user {UserId}", model.RequestedUserId);
            }

            if (model.SeriesType == SeriesType.Anime)
            {
                // Get the root path from the rootfolder selected.
                // For some reason, if we haven't got one use the first root folder in Sonarr
                if (!int.TryParse(s.RootPathAnime, out int animePath))
                {
                    Logger.LogWarning("Failed to parse RootPathAnime: {RootPathAnime}, falling back to main root path", s.RootPathAnime);
                    animePath = int.Parse(s.RootPath); // Set it to the main root folder if we have no anime folder.
                }
                Logger.LogInformation("Using anime path ID: {AnimePath}", animePath);
                rootFolderPath = await GetSonarrRootPath(animePath, s);
                languageProfileId = s.LanguageProfileAnime > 0 ? s.LanguageProfileAnime : s.LanguageProfile;

                if (!int.TryParse(s.QualityProfileAnime, out qualityToUse))
                {
                    qualityToUse = int.Parse(s.QualityProfile);
                }
                if (profiles != null)
                {
                    if (profiles.SonarrRootPathAnime > 0)
                    {
                        Logger.LogInformation("Checking user's anime root path override: {RootPath}", profiles.SonarrRootPathAnime);
                        var userAnimeRootPath = await GetSonarrRootPath(profiles.SonarrRootPathAnime, s);
                        // Only use the user's root path if it's valid (exists in Sonarr)
                        if (!string.IsNullOrEmpty(userAnimeRootPath))
                        {
                            Logger.LogInformation("Using user's anime root path override: {RootPath}", profiles.SonarrRootPathAnime);
                            rootFolderPath = userAnimeRootPath;
                        }
                        else
                        {
                            Logger.LogWarning("User's anime root path ID {RootPath} no longer exists in Sonarr, falling back to global default", profiles.SonarrRootPathAnime);
                        }
                    }
                    if (profiles.SonarrQualityProfileAnime > 0)
                    {
                        qualityToUse = profiles.SonarrQualityProfileAnime;
                    }
                }
                seriesType = "anime";
                tagToUse = s.AnimeTag;
            }
            else
            {
                int.TryParse(s.QualityProfile, out qualityToUse);
                // Get the root path from the rootfolder selected.
                // For some reason, if we haven't got one use the first root folder in Sonarr
                Logger.LogInformation("Using standard path ID: {RootPath}", s.RootPath);
                rootFolderPath = await GetSonarrRootPath(int.Parse(s.RootPath), s);
                if (profiles != null)
                {
                    if (profiles.SonarrRootPath > 0)
                    {
                        Logger.LogInformation("Checking user's standard root path override: {RootPath}", profiles.SonarrRootPath);
                        var userRootPath = await GetSonarrRootPath(profiles.SonarrRootPath, s);
                        // Only use the user's root path if it's valid (exists in Sonarr)
                        if (!string.IsNullOrEmpty(userRootPath))
                        {
                            Logger.LogInformation("Using user's standard root path override: {RootPath}", profiles.SonarrRootPath);
                            rootFolderPath = userRootPath;
                        }
                        else
                        {
                            Logger.LogWarning("User's standard root path ID {RootPath} no longer exists in Sonarr, falling back to global default", profiles.SonarrRootPath);
                        }
                    }
                    if (profiles.SonarrQualityProfile > 0)
                    {
                        qualityToUse = profiles.SonarrQualityProfile;
                    }
                }
                seriesType = "standard";
                tagToUse = s.Tag;
            }

            // Overrides on the request take priority
            if (model.ParentRequest.QualityOverride.HasValue && model.ParentRequest.QualityOverride.Value > 0)
            {
                qualityToUse = model.ParentRequest.QualityOverride.Value;
            }

            if (model.ParentRequest.RootFolder.HasValue && model.ParentRequest.RootFolder.Value > 0)
            {
                Logger.LogInformation("Using request root folder override: {RootFolder}", model.ParentRequest.RootFolder.Value);
                rootFolderPath = await GetSonarrRootPath(model.ParentRequest.RootFolder.Value, s);
            }

            if (model.ParentRequest.LanguageProfile.HasValue && model.ParentRequest.LanguageProfile.Value > 0)
            {
                languageProfileId = model.ParentRequest.LanguageProfile.Value;
            }

            Logger.LogInformation("Final root folder path: {RootFolderPath}", rootFolderPath);

            try
            {
                if (tagToUse.HasValue)
                {
                    options.Tags.Add(tagToUse.Value);
                }
                if (s.SendUserTags)
                {
                    var userTag = await GetOrCreateTag(model, s);
                    if (userTag != null)
                    {
                        options.Tags.Add(userTag.id);
                    }
                }

                // Does the series actually exist?
                var allSeries = await SonarrApi.GetSeries(s.ApiKey, s.FullUri);
                var existingSeries = allSeries.FirstOrDefault(x => x.tvdbId == model.ParentRequest.TvDbId);

                if (existingSeries == null)
                {
                    // Time to add a new one
                    var newSeries = new NewSeries
                    {
                        title = model.ParentRequest.Title,
                        imdbId = model.ParentRequest.ImdbId,
                        tvdbId = model.ParentRequest.TvDbId,
                        cleanTitle = model.ParentRequest.Title,
                        monitored = true,
                        seasonFolder = s.SeasonFolders,
                        rootFolderPath = rootFolderPath,
                        qualityProfileId = qualityToUse,
                        titleSlug = model.ParentRequest.Title,
                        seriesType = seriesType,
                        addOptions = new AddOptions
                        {
                            ignoreEpisodesWithFiles = false, // There shouldn't be any episodes with files, this is a new season
                            ignoreEpisodesWithoutFiles = false, // We want all missing
                            searchForMissingEpisodes = false // we want dont want to search yet. We want to make sure everything is unmonitored/monitored correctly.
                        },
                        languageProfileId = languageProfileId,
                        tags = options.Tags
                    };


                    // Montitor the correct seasons,
                    // If we have that season in the model then it's monitored!
                    var seasonsToAdd = GetSeasonsToCreate(model);
                    newSeries.seasons = seasonsToAdd;
                    var result = await SonarrApi.AddSeries(newSeries, s.ApiKey, s.FullUri);
                    if (result?.ErrorMessages?.Any() ?? false)
                    {
                        throw new Exception(string.Join(',', result.ErrorMessages));
                    }
                    existingSeries = await SonarrApi.GetSeriesById(result.id, s.ApiKey, s.FullUri);
                    await SendToSonarr(model, existingSeries, s, options);
                }
                else
                {
                    if (existingSeries is { monitored: false })
                    {
                        existingSeries.monitored = true;
                        await SonarrApi.UpdateSeries(existingSeries,  s.ApiKey, s.FullUri);
                    }
                    await SendToSonarr(model, existingSeries, s, options);
                }

                return new NewSeries
                {
                    id = existingSeries.id,
                    seasons = existingSeries.seasons.ToList(),
                    cleanTitle = existingSeries.cleanTitle,
                    title = existingSeries.title,
                    tvdbId = existingSeries.tvdbId
                };
            }
            catch (Exception e)
            {
                Logger.LogError(LoggingEvents.SonarrSender, e, "Exception thrown when attempting to send series over to Sonarr");
                throw;
            }
        }

        private async Task<Tag> GetOrCreateTag(ChildRequests model, SonarrSettings s)
        {
            // Sanitize username to comply with Sonarr tag requirements (a-z, 0-9, and - only)
            var tagName = StringHelper.SanitizeTagLabel(model.RequestedUser.UserName);

            if (string.IsNullOrEmpty(tagName))
            {
                Logger.LogWarning("Cannot create tag - sanitized username is empty for user {Username}", model.RequestedUser.UserName);
                return null;
            }

            // Does tag exist?
            var allTags = await SonarrApi.GetTags(s.ApiKey, s.FullUri);
            var existingTag = allTags.FirstOrDefault(x => x.label.Equals(tagName, StringComparison.InvariantCultureIgnoreCase));
            existingTag ??= await SonarrApi.CreateTag(s.ApiKey, s.FullUri, tagName);

            return existingTag;
        }

        private async Task<Tag> GetTag(int tagId, SonarrSettings s)
        {
            var tag = await SonarrApi.GetTag(tagId, s.ApiKey, s.FullUri);
            if (tag == null)
            {
                Logger.LogError($"Tag ID {tagId} does not exist in sonarr. Please update the settings");
                return null;
            }
            return tag;
        }

        private async Task SendToSonarr(ChildRequests model, SonarrSeries result, SonarrSettings s, SonarrSendOptions options)
        {
            // Check to ensure we have the all the seasons, ensure the Sonarr metadata has grabbed all the data
            Season existingSeason = null;
            foreach (var season in model.SeasonRequests)
            {
                var attempt = 0;
                existingSeason = result.seasons.FirstOrDefault(x => x.seasonNumber == season.SeasonNumber);
                while (existingSeason == null && attempt < 5)
                {
                    attempt++;
                    Logger.LogInformation("There was no season numer {0} in Sonarr for title {1}. Will try again as the metadata did not get created", season.SeasonNumber, model.ParentRequest.Title);
                    result = await SonarrApi.GetSeriesById(result.id, s.ApiKey, s.FullUri);
                    existingSeason = result.seasons.FirstOrDefault(x => x.seasonNumber == season.SeasonNumber);
                    await Task.Delay(500);
                }

                if (existingSeason == null)
                {
                    Logger.LogWarning("Unable to locate season number {SeasonNumber} in Sonarr for title {Title} after {Attempts} attempts. Skipping monitoring updates for this season.", season.SeasonNumber, model.ParentRequest.Title, attempt);
                }
            }

            // Does the show have the correct tags we are expecting
            if (options.Tags.Any())
            {
                result.tags ??= options.Tags;
                var tagsToAdd = options.Tags.Except(result.tags);

                if (tagsToAdd.Any())
                {
                    result.tags.AddRange(tagsToAdd);
                }
                result = await SonarrApi.UpdateSeries(result, s.ApiKey, s.FullUri);
            }

            if (model.SeriesType == SeriesType.Anime)
            {
                result.seriesType = "anime";
                result = await SonarrApi.UpdateSeries(result, s.ApiKey, s.FullUri);
            }

            var episodesToUpdate = new List<Episode>();
            // Ok, now let's sort out the episodes.
            var sonarrEpisodes = await SonarrApi.GetEpisodes(result.id, s.ApiKey, s.FullUri);
            var sonarrEpList = sonarrEpisodes.ToList() ?? new List<Episode>();
            while (!sonarrEpList.Any())
            {
                // It could be that the series metadata is not ready yet. So wait
                sonarrEpList = (await SonarrApi.GetEpisodes(result.id, s.ApiKey, s.FullUri)).ToList();
                await Task.Delay(500);
            }

            foreach (var season in model.SeasonRequests)
            {
                foreach (var ep in season.Episodes)
                {
                    var sonarrEp = sonarrEpList.FirstOrDefault(x =>
                        x.episodeNumber == ep.EpisodeNumber && x.seasonNumber == season.SeasonNumber);
                    if (sonarrEp != null && !sonarrEp.monitored)
                    {
                        sonarrEp.monitored = true;
                        episodesToUpdate.Add(sonarrEp);
                    }
                }

                existingSeason = result.seasons.FirstOrDefault(x => x.seasonNumber == season.SeasonNumber);

                if (existingSeason == null)
                {
                    Logger.LogWarning("Season {SeasonNumber} still missing in Sonarr for title {Title}; skipping monitoring changes for this season.", season.SeasonNumber, model.ParentRequest.Title);
                    continue;
                }


                // Make sure this season is set to monitored
                if (!existingSeason.monitored)
                {
                    // We need to monitor it, problem being is all episodes will now be monitored
                    // So we need to monitor the series but unmonitor every episode
                    existingSeason.monitored = true;
                    var sea = result.seasons.FirstOrDefault(x => x.seasonNumber == existingSeason.seasonNumber);
                    sea.monitored = true;

                    result = await SonarrApi.UpdateSeries(result, s.ApiKey, s.FullUri);
                    var epToUnmonitored = new List<Episode>();
                    var newEpList = sonarrEpList.ConvertAll(ep => new Episode(ep)); // Clone it so we don't modify the original member
                    foreach (var ep in newEpList.Where(x => x.seasonNumber == existingSeason.seasonNumber).ToList())
                    {
                        ep.monitored = false;
                        epToUnmonitored.Add(ep);
                    }

                    await SonarrApi.MonitorEpisode(epToUnmonitored.Select(x => x.id).ToArray(), false, s.ApiKey, s.FullUri);
                }
            }

            // Now update the episodes that need updating
            await SonarrApi.MonitorEpisode(episodesToUpdate.Select(x => x.id).ToArray(), true, s.ApiKey, s.FullUri);

            if (!s.AddOnly)
            {
                await SearchForRequest(model, sonarrEpList, result, s, episodesToUpdate);
            }
        }

        private static List<Season> GetSeasonsToCreate(ChildRequests model)
        {
            // Let's get a list of seasons just incase we need to change it
            var seasonsToUpdate = new List<Season>();
            for (var i = 0; i < model.ParentRequest.TotalSeasons + 1; i++)
            {
                var sea = new Season
                {
                    seasonNumber = i,
                    monitored = false
                };
                seasonsToUpdate.Add(sea);
            }

            return seasonsToUpdate;
        }

        private async Task SearchForRequest(ChildRequests model, IEnumerable<Episode> sonarrEpList, SonarrSeries existingSeries, SonarrSettings s,
            IReadOnlyCollection<Episode> episodesToUpdate)
        {
            foreach (var season in model.SeasonRequests)
            {
                var sonarrSeason = sonarrEpList.Where(x => x.seasonNumber == season.SeasonNumber);
                var sonarrEpCount = sonarrSeason.Count();
                var ourRequestCount = season.Episodes.Count;

                // We have the same amount of requests as all of the episodes in the season,
                // or Sonarr has more episodes than Ombi (incomplete metadata).
                // Do a season search in both cases.
                if (sonarrEpCount >= ourRequestCount)
                {
                    await SonarrApi.SeasonSearch(existingSeries.id, season.SeasonNumber, s.ApiKey, s.FullUri);
                }
                else
                {
                    // There is a miss-match, let's search the episodes indiviaully
                    await SonarrApi.EpisodeSearch(episodesToUpdate.Select(x => x.id).ToArray(), s.ApiKey, s.FullUri);
                }
            }
        }

        private async Task<string> GetSonarrRootPath(int pathId, SonarrSettings sonarrSettings)
        {
            Logger.LogInformation("Getting Sonarr root path for ID: {PathId}", pathId);
            var rootFoldersResult = await SonarrApi.GetRootFolders(sonarrSettings.ApiKey, sonarrSettings.FullUri);

            if (rootFoldersResult == null || !rootFoldersResult.Any())
            {
                Logger.LogError("No root folders returned from Sonarr API");
                return string.Empty;
            }

            Logger.LogInformation("Found {Count} root folders in Sonarr", rootFoldersResult.Count());
            foreach (var folder in rootFoldersResult)
            {
                Logger.LogDebug("Root folder - ID: {Id}, Path: {Path}", folder.id, folder.path);
            }

            if (pathId == 0)
            {
                var defaultPath = rootFoldersResult.FirstOrDefault()?.path;
                Logger.LogInformation("Using first root folder as default: {Path}", defaultPath);
                return defaultPath;
            }

            var matchingFolder = rootFoldersResult.FirstOrDefault(r => r.id == pathId);
            if (matchingFolder != null)
            {
                Logger.LogInformation("Found matching root folder for ID {PathId}: {Path}", pathId, matchingFolder.path);
                return matchingFolder.path;
            }

            Logger.LogError("No matching root folder found for ID: {PathId}", pathId);
            return string.Empty;
        }
    }
}
