using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.Models;
using Ombi.Helpers;
using Ombi.Store.Entities;
using Ombi.Store.Repository;
using Quartz;

namespace Ombi.Schedule.Jobs.MediaServer
{
    public abstract class MediaServerContentSync<TContent> where TContent : MediaServerContent, new()
    {
        protected MediaServerContentSync(IMediaServerCatalog<TContent> catalog, IMediaServerContentRepository<TContent> repo, ILogger logger)
        {
            Catalog = catalog;
            Repo = repo;
            Logger = logger;
        }

        protected readonly IMediaServerCatalog<TContent> Catalog;
        protected readonly IMediaServerContentRepository<TContent> Repo;
        protected readonly ILogger Logger;

        protected bool recentlyAdded;

        /// <summary>
        /// Set when any server was skipped or failed part-way through, meaning the sync
        /// did not see the full picture of what exists on the media server. Derived jobs
        /// must not remove records they consider stale while this is set.
        /// </summary>
        protected bool syncIncomplete;

        protected virtual bool StopOnEmptyPage => false;

        protected virtual bool AddSeriesWithoutProviderIds => false;

        protected virtual bool ToleratesMissingProviderIds => false;

        protected virtual bool RunSyncCompletedWhenDisabled => false;

        public virtual async Task Execute(IJobExecutionContext context)
        {
            OnSyncStarting(context);

            await Catalog.NotifySyncStarting(recentlyAdded);

            var servers = await Catalog.OpenAsync();
            if (servers == null)
            {
                if (RunSyncCompletedWhenDisabled)
                {
                    await OnSyncCompleted();
                }
                return;
            }

            await Catalog.NotifySyncStarted();

            foreach (var server in servers)
            {
                try
                {
                    await StartServerCache(server);
                }
                catch (Exception e)
                {
                    syncIncomplete = true;
                    await Catalog.NotifySyncFailed();
                    Logger.LogError(e, "Exception when caching {0} for server {1}", Catalog.ServerName, server.Name);
                }
            }

            await Catalog.NotifySyncFinished();

            await OnSyncCompleted();
        }

        protected virtual void OnSyncStarting(IJobExecutionContext context)
        {
        }

        protected virtual Task OnSyncCompleted()
        {
            return Task.CompletedTask;
        }

        protected virtual void OnContentSeen(string mediaServerId)
        {
        }

        private async Task StartServerCache(MediaServerInstance server)
        {
            if (!ValidateSettings(server))
            {
                syncIncomplete = true;
                return;
            }

            if (server.SelectedLibraries.Any() && server.SelectedLibraries.Any(x => x.Enabled))
            {
                var movieLibsToFilter = server.SelectedLibraries.Where(x => x.Enabled && x.CollectionType == "movies");

                foreach (var movieParentIdFilder in movieLibsToFilter)
                {
                    Logger.LogInformation($"Scanning Lib '{movieParentIdFilder.Title}'");
                    await ProcessMovies(server, movieParentIdFilder.Key);
                }

                var tvLibsToFilter = server.SelectedLibraries.Where(x => x.Enabled && x.CollectionType == "tvshows");
                foreach (var tvParentIdFilter in tvLibsToFilter)
                {
                    Logger.LogInformation($"Scanning Lib '{tvParentIdFilter.Title}'");
                    await ProcessTv(server, tvParentIdFilter.Key);
                }

                var mixedLibs = server.SelectedLibraries.Where(x => x.Enabled && x.CollectionType == "mixed");
                foreach (var m in mixedLibs)
                {
                    Logger.LogInformation($"Scanning Lib '{m.Title}'");
                    await ProcessTv(server, m.Key);
                    await ProcessMovies(server, m.Key);
                }
            }
            else
            {
                await ProcessMovies(server);
                await ProcessTv(server);
            }
        }

        private async Task ProcessTv(MediaServerInstance server, string parentId = default)
        {
            // TV Time
            var mediaToAdd = new HashSet<TContent>();
            MediaServerPage<MediaServerSeries> tv;
            if (recentlyAdded)
            {
                var recentlyAddedAmountToTake = Catalog.PageSize / 2;
                tv = await Catalog.GetShows(server, parentId, 0, recentlyAddedAmountToTake, true);
                if (tv.TotalRecordCount > recentlyAddedAmountToTake)
                {
                    tv.TotalRecordCount = recentlyAddedAmountToTake;
                }
            }
            else
            {
                tv = await Catalog.GetShows(server, parentId, 0, Catalog.PageSize, false);
            }
            var totalTv = tv.TotalRecordCount;
            var processed = 0;
            while (processed < totalTv)
            {
                if (StopOnEmptyPage && (tv.Items == null || !tv.Items.Any()))
                {
                    syncIncomplete = true;
                    Logger.LogWarning("{0} returned no TV shows at offset {1} but reported {2} total records. Stopping the sync for this library to avoid an infinite loop.",
                        Catalog.ServerName, processed, totalTv);
                    break;
                }

                foreach (var tvShow in tv.Items)
                {
                    processed++;
                    OnContentSeen(tvShow.Id);

                    if (!AddSeriesWithoutProviderIds && !tvShow.ProviderIds.Any())
                    {
                        Logger.LogInformation("Provider Id on tv {0} is null", tvShow.Name);
                        continue;
                    }

                    // ProviderIds can be missing entirely from the API response, so guard
                    // against null as well as empty. A series without provider ids is still
                    // added (with its media server id only) so that its episodes can be linked
                    // and the metadata refresh job can backfill the ids from the title later.
                    var hasProviderIds = HasProviderIds(tvShow.ProviderIds);
                    if (!hasProviderIds)
                    {
                        Logger.LogInformation("Provider Id on tv {0} is null, adding it with its {1} Id only. The metadata refresh will attempt to backfill the provider ids.", tvShow.Name, Catalog.ServerName);
                    }

                    var existingTv = await Catalog.GetByMediaServerId(tvShow.Id);

                    // Only treat differing ids as a reidentification when the incoming item
                    // actually has provider ids, otherwise a series that lost its metadata
                    // would wipe the ids we already have (including ones backfilled by the
                    // metadata refresh job).
                    if (existingTv != null && hasProviderIds &&
                        ( existingTv.ImdbId != tvShow.ProviderIds?.Imdb
                        || existingTv.TheMovieDbId != tvShow.ProviderIds?.Tmdb
                        || existingTv.TvDbId != tvShow.ProviderIds?.Tvdb))
                    {
                        // TODO: Existing TvRequest records referencing the old TheMovieDbId via ExternalProviderId
                        // will not be updated here. This is a pre-existing limitation (the old delete+reinsert
                        // also left requests orphaned). Consider migrating TvRequest.ExternalProviderId in future.
                        Logger.LogDebug($"Series '{tvShow.Name}' has different IDs, probably a reidentification. Updating IDs in place.");
                        existingTv.ImdbId = tvShow.ProviderIds?.Imdb;
                        existingTv.TheMovieDbId = tvShow.ProviderIds?.Tmdb;
                        existingTv.TvDbId = tvShow.ProviderIds?.Tvdb;
                        Repo.UpdateWithoutSave(existingTv);
                    }

                    if (existingTv == null)
                    {
                        Logger.LogDebug("Adding TV Show {0}", tvShow.Name);
                        var newTv = new TContent
                        {
                            TvDbId = tvShow.ProviderIds?.Tvdb,
                            ImdbId = tvShow.ProviderIds?.Imdb,
                            TheMovieDbId = tvShow.ProviderIds?.Tmdb,
                            Title = tvShow.Name,
                            Type = MediaType.Series,
                            AddedAt = DateTime.UtcNow,
                        };
                        Catalog.SetIdentity(newTv, tvShow.Id, server);
                        mediaToAdd.Add(newTv);
                    }
                    else
                    {
                        Logger.LogDebug("We already have TV Show {0}", tvShow.Name);
                    }
                }
                // Get the next batch
                if (!recentlyAdded)
                {
                    tv = await Catalog.GetShows(server, parentId, processed, Catalog.PageSize, false);
                }
                await Repo.AddRange(mediaToAdd);
                mediaToAdd.Clear();
            }

            if (mediaToAdd.Any())
                await Repo.AddRange(mediaToAdd);
        }

        private async Task ProcessMovies(MediaServerInstance server, string parentId = default)
        {
            MediaServerPage<MediaServerMovie> movies;
            if (recentlyAdded)
            {
                var recentlyAddedAmountToTake = Catalog.PageSize / 2;
                movies = await Catalog.GetMovies(server, parentId, 0, recentlyAddedAmountToTake, true);
                // Setting this so we don't attempt to grab more than we need
                if (movies.TotalRecordCount > recentlyAddedAmountToTake)
                {
                    movies.TotalRecordCount = recentlyAddedAmountToTake;
                }
            }
            else
            {
                movies = await Catalog.GetMovies(server, parentId, 0, Catalog.PageSize, false);
            }
            var totalCount = movies.TotalRecordCount;
            var processed = 0;
            var mediaToAdd = new HashSet<TContent>();
            var mediaToUpdate = new HashSet<TContent>();
            while (processed < totalCount)
            {
                if (StopOnEmptyPage && (movies.Items == null || !movies.Items.Any()))
                {
                    syncIncomplete = true;
                    Logger.LogWarning("{0} returned no movies at offset {1} but reported {2} total records. Stopping the sync for this library to avoid an infinite loop.",
                        Catalog.ServerName, processed, totalCount);
                    break;
                }

                foreach (var movie in movies.Items)
                {
                    if (movie.Type.Equals("boxset", StringComparison.InvariantCultureIgnoreCase))
                    {
                        var collection = await Catalog.GetCollection(server, movie.Id);
                        foreach (var item in collection)
                        {
                            await ProcessMovies(item, mediaToAdd, mediaToUpdate, server);
                        }
                    }
                    else
                    {
                        // Regular movie
                        await ProcessMovies(movie, mediaToAdd, mediaToUpdate, server);
                    }

                    processed++;
                }

                // Get the next batch
                // Recently Added should never be checked as the TotalRecords should equal the amount to take
                if (!recentlyAdded)
                {
                    movies = await Catalog.GetMovies(server, parentId, processed, Catalog.PageSize, false);
                }
                await Repo.AddRange(mediaToAdd);
                await Repo.UpdateRange(mediaToUpdate);
                mediaToAdd.Clear();
            }
        }

        private async Task ProcessMovies(MediaServerMovie movieInfo, ICollection<TContent> content, ICollection<TContent> toUpdate, MediaServerInstance server)
        {
            OnContentSeen(movieInfo.Id);

            var quality = movieInfo.Quality;
            var has4K = false;
            if (quality.Contains("4K", CompareOptions.IgnoreCase))
            {
                has4K = true;
            }

            // Check if it exists
            var existingMovie = await Catalog.GetByMediaServerId(movieInfo.Id);
            var alreadyGoingToAdd = content.Any(x => Catalog.GetMediaServerId(x) == movieInfo.Id);
            if (alreadyGoingToAdd)
            {
                Logger.LogDebug($"Detected duplicate for {movieInfo.Name}");
                return;
            }

            var hasProviderIds = HasProviderIds(movieInfo.ProviderIds);
            if (existingMovie == null)
            {
                if (!hasProviderIds)
                {
                    Logger.LogWarning($"Movie {movieInfo.Name} has no relevant metadata. Skipping.");
                    return;
                }
                Logger.LogDebug($"Adding new movie {movieInfo.Name}");
                var newMovie = new TContent();
                newMovie.AddedAt = DateTime.UtcNow;
                MapMovie(newMovie, movieInfo, server, has4K, quality);
                content.Add(newMovie);
            }
            else
            {
                var movieHasChanged = false;
                if ((!ToleratesMissingProviderIds || hasProviderIds)
                    && (existingMovie.ImdbId != movieInfo.ProviderIds.Imdb || existingMovie.TheMovieDbId != movieInfo.ProviderIds.Tmdb))
                {
                    Logger.LogDebug($"Updating existing movie '{movieInfo.Name}'");
                    MapMovie(existingMovie, movieInfo, server, has4K, quality);
                    movieHasChanged = true;
                }
                else if (!quality.Equals(existingMovie?.Quality, StringComparison.InvariantCultureIgnoreCase))
                {
                    Logger.LogDebug($"We have found another quality for Movie '{movieInfo.Name}', Quality: '{quality}'");
                    existingMovie.Quality = has4K ? null : quality;
                    existingMovie.Has4K = has4K;

                    // Probably could refactor here
                    // If a 4k movie comes in (we don't store the quality on 4k)
                    // it will always get updated even know it's not changed
                    movieHasChanged = true;
                }

                if (movieHasChanged)
                {
                    toUpdate.Add(existingMovie);
                }
                else
                {
                    // we have this
                    Logger.LogDebug($"We already have movie {movieInfo.Name}");
                }
            }
        }

        private void MapMovie(TContent content, MediaServerMovie movieInfo, MediaServerInstance server, bool has4K, string quality)
        {
            content.ImdbId = movieInfo.ProviderIds?.Imdb;
            content.TheMovieDbId = movieInfo.ProviderIds?.Tmdb;
            content.Title = movieInfo.Name;
            content.Type = MediaType.Movie;
            Catalog.SetIdentity(content, movieInfo.Id, server);
            content.Quality = has4K ? null : quality;
            content.Has4K = has4K;
        }

        private bool HasProviderIds(BaseProviderids providerIds)
        {
            return ToleratesMissingProviderIds ? providerIds?.Any() == true : providerIds.Any();
        }

        private bool ValidateSettings(MediaServerInstance server)
        {
            if (server?.Ip == null || string.IsNullOrEmpty(server?.ApiKey))
            {
                Logger.LogInformation(Catalog.ContentCacherLog, $"Server {server?.Name} is not configured correctly");
                return false;
            }

            return true;
        }

        private bool _disposed;
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                //_settings?.Dispose();
            }
            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
