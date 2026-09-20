using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.ExternalApis.Radarr;
using Ombi.Api.External.ExternalApis.Radarr.Models;
using Ombi.Helpers;
using Ombi.Settings.Settings.Models.External;
using Ombi.Store.Entities;
using Ombi.Store.Entities.Requests;
using Ombi.Store.Repository;

namespace Ombi.Core.Senders.Dvr
{
    public abstract class RadarrDvrSenderBase : IMovieDvrSender
    {
        protected RadarrDvrSenderBase(IRadarrV3Api radarrV3Api, ILogger<RadarrDvrSenderBase> log, IRepository<UserQualityProfiles> userProfiles)
        {
            RadarrV3Api = radarrV3Api;
            Log = log;
            UserProfiles = userProfiles;
        }

        private IRadarrV3Api RadarrV3Api { get; }
        private ILogger<RadarrDvrSenderBase> Log { get; }
        private IRepository<UserQualityProfiles> UserProfiles { get; }

        protected abstract bool IsFor4K { get; }
        protected abstract Task<RadarrSettings> GetSettingsAsync();

        public int Priority => 0;

        public async Task<bool> IsEnabledAsync(bool is4K)
        {
            if (is4K != IsFor4K)
            {
                return false;
            }
            var settings = await GetSettingsAsync();
            return settings.Enabled;
        }

        public async Task<SenderResult> Send(MovieRequests model, bool is4K)
        {
            var settings = await GetSettingsAsync();
            return await SendToRadarr(model, settings, is4K);
        }

        private async Task<SenderResult> SendToRadarr(MovieRequests model, RadarrSettings settings, bool is4k)
        {
            var qualityToUse = int.Parse(settings.DefaultQualityProfile);

            var rootFolderPath = settings.DefaultRootPath;

            var profiles = await UserProfiles.GetAll().FirstOrDefaultAsync(x => x.UserId == model.RequestedUserId);
            if (profiles != null)
            {
                if (is4k)
                {
                    if (profiles.Radarr4KRootPath > 0)
                    {
                        var tempPath = await RadarrRootPath(profiles.Radarr4KRootPath, settings);
                        if (tempPath.HasValue())
                        {
                            rootFolderPath = tempPath;
                        }
                    }
                    if (profiles.Radarr4KQualityProfile > 0)
                    {
                        qualityToUse = profiles.Radarr4KQualityProfile;
                    }
                }
                else
                {
                    if (profiles.RadarrRootPath > 0)
                    {
                        var tempPath = await RadarrRootPath(profiles.RadarrRootPath, settings);
                        if (tempPath.HasValue())
                        {
                            rootFolderPath = tempPath;
                        }
                    }
                    if (profiles.RadarrQualityProfile > 0)
                    {
                        qualityToUse = profiles.RadarrQualityProfile;
                    }
                }
            }

            var tags = new List<int>();
            if (settings.Tag.HasValue)
            {
                tags.Add(settings.Tag.Value);
            }
            if (settings.SendUserTags)
            {
                var userTag = await GetOrCreateTag(model, settings);
                if (userTag != null)
                {
                    tags.Add(userTag.id);
                }
            }

            // Overrides on the request take priority
            if (model.QualityOverride > 0)
            {
                qualityToUse = model.QualityOverride;
            }
            if (model.RootPathOverride > 0)
            {
                rootFolderPath = await RadarrRootPath(model.RootPathOverride, settings);
            }

            // `settings` already resolved to the right Radarr/Radarr4K instance for this sender
            var movies = await RadarrV3Api.GetMovies(settings.ApiKey, settings.FullUri);

            var existingMovie = movies.FirstOrDefault(x => x.tmdbId == model.TheMovieDbId);
            if (existingMovie == null)
            {
                var result = await RadarrV3Api.AddMovie(model.TheMovieDbId, model.Title, model.ReleaseDate.Year,
                    qualityToUse, rootFolderPath, settings.ApiKey, settings.FullUri, !settings.AddOnly,
                    settings.MinimumAvailability, tags);

                if (!string.IsNullOrEmpty(result.Error?.message))
                {
                    Log.LogError(LoggingEvents.RadarrCacher, result.Error.message);
                    return new SenderResult { Success = false, Message = result.Error.message, Sent = false };
                }
                return new SenderResult { Success = true, Sent = false };
            }
            // We have the movie, check if we can request it or change the status
            if (!existingMovie.monitored)
            {
                // let's set it to monitored and search for it
                existingMovie.monitored = true;

                await RadarrV3Api.UpdateMovie(existingMovie, settings.ApiKey, settings.FullUri);
                // Search for it
                if (!settings.AddOnly)
                {
                    await RadarrV3Api.MovieSearch(new[] { existingMovie.id }, settings.ApiKey, settings.FullUri);
                }

                return new SenderResult { Success = true, Sent = true };
            }

            return new SenderResult { Success = false, Sent = false, Message = "Movie is already monitored" };
        }

        private async Task<string> RadarrRootPath(int overrideId, RadarrSettings settings)
        {
            var paths = await RadarrV3Api.GetRootFolders(settings.ApiKey, settings.FullUri);
            var selectedPath = paths.FirstOrDefault(x => x.id == overrideId);
            return selectedPath?.path ?? string.Empty;
        }

        private async Task<Tag> GetOrCreateTag(MovieRequests model, RadarrSettings s)
        {
            if (model.RequestedUser == null)
            {
                Log.LogWarning("Cannot create tag - RequestedUser is null for movie request {MovieTitle}", model.Title);
                return null;
            }

            // Sanitize username to comply with Radarr tag requirements (a-z, 0-9, and - only)
            var tagName = StringHelper.SanitizeTagLabel(model.RequestedUser.UserName);

            if (string.IsNullOrEmpty(tagName))
            {
                Log.LogWarning("Cannot create tag - sanitized username is empty for user {Username}", model.RequestedUser.UserName);
                return null;
            }

            // Does tag exist?
            var allTags = await RadarrV3Api.GetTags(s.ApiKey, s.FullUri);
            var existingTag = allTags.FirstOrDefault(x => x.label.Equals(tagName, System.StringComparison.InvariantCultureIgnoreCase));
            existingTag ??= await RadarrV3Api.CreateTag(s.ApiKey, s.FullUri, tagName);

            return existingTag;
        }
    }
}
