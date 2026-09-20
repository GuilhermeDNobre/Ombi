using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.MediaServers.Emby;
using Ombi.Api.External.MediaServers.Emby.Models.Movie;
using Ombi.Core.Settings;
using Ombi.Core.Settings.Models.External;
using Ombi.Helpers;
using Ombi.Hubs;
using Ombi.Schedule.Jobs.MediaServer;
using Ombi.Store.Entities;
using Ombi.Store.Repository;

namespace Ombi.Schedule.Jobs.Emby
{
    public class EmbyCatalogAdapter : IMediaServerCatalog<EmbyContent>
    {
        public EmbyCatalogAdapter(
            ISettingsService<EmbySettings> settings,
            IEmbyApiFactory api,
            IEmbyContentRepository repo,
            INotificationHubService notification)
        {
            _settings = settings;
            _apiFactory = api;
            _repo = repo;
            _notification = notification;
        }

        private readonly ISettingsService<EmbySettings> _settings;
        private readonly IEmbyApiFactory _apiFactory;
        private readonly IEmbyContentRepository _repo;
        private readonly INotificationHubService _notification;

        private IEmbyApi Api { get; set; }

        public string ServerName => "Emby";

        public EventId ContentCacherLog => LoggingEvents.EmbyContentCacher;

        public int PageSize => 300;

        public async Task<IReadOnlyList<MediaServerInstance>> OpenAsync()
        {
            var embySettings = await _settings.GetSettingsAsync();
            if (!embySettings.Enable)
                return null;

            Api = _apiFactory.CreateClient(embySettings);

            return embySettings.Servers.Select(MapServer).ToList();
        }

        public async Task<MediaServerPage<MediaServerSeries>> GetShows(MediaServerInstance server, string parentId, int startIndex, int count, bool recentlyAdded)
        {
            var shows = recentlyAdded
                ? await Api.RecentlyAddedShows(server.ApiKey, parentId, startIndex, count, server.AdministratorId, server.FullUri)
                : await Api.GetAllShows(server.ApiKey, parentId, startIndex, count, server.AdministratorId, server.FullUri);

            return new MediaServerPage<MediaServerSeries>
            {
                TotalRecordCount = shows.TotalRecordCount,
                Items = shows.Items?.Select(x => new MediaServerSeries
                {
                    Id = x.Id,
                    Name = x.Name,
                    ProviderIds = x.ProviderIds
                }).ToList()
            };
        }

        public async Task<MediaServerPage<MediaServerMovie>> GetMovies(MediaServerInstance server, string parentId, int startIndex, int count, bool recentlyAdded)
        {
            var movies = recentlyAdded
                ? await Api.RecentlyAddedMovies(server.ApiKey, parentId, startIndex, count, server.AdministratorId, server.FullUri)
                : await Api.GetAllMovies(server.ApiKey, parentId, startIndex, count, server.AdministratorId, server.FullUri);

            return new MediaServerPage<MediaServerMovie>
            {
                TotalRecordCount = movies.TotalRecordCount,
                Items = movies.Items?.Select(MapMovie).ToList()
            };
        }

        public async Task<IReadOnlyList<MediaServerMovie>> GetCollection(MediaServerInstance server, string collectionId)
        {
            var collection = await Api.GetCollection(collectionId, server.ApiKey, server.AdministratorId, server.FullUri);
            return collection.Items.Select(MapMovie).ToList();
        }

        public Task<EmbyContent> GetByMediaServerId(string mediaServerId)
        {
            return _repo.GetByEmbyId(mediaServerId);
        }

        public string GetMediaServerId(EmbyContent content)
        {
            return content.EmbyId;
        }

        public void SetIdentity(EmbyContent content, string mediaServerId, MediaServerInstance server)
        {
            content.EmbyId = mediaServerId;
            content.Url = EmbyHelper.GetEmbyMediaUrl(mediaServerId, server?.ServerId, server.ServerHostname);
        }

        public Task NotifySyncStarting(bool recentlyAdded)
        {
            return _notification.SendNotificationToAdmins(recentlyAdded ? "Emby Recently Added Started" : "Emby Content Sync Started");
        }

        public Task NotifySyncStarted()
        {
            return Task.CompletedTask;
        }

        public Task NotifySyncFailed()
        {
            return _notification.SendNotificationToAdmins("Emby Content Sync Failed");
        }

        public Task NotifySyncFinished()
        {
            return _notification.SendNotificationToAdmins("Emby Content Sync Finished");
        }

        private static MediaServerMovie MapMovie(EmbyMovie movie)
        {
            return new MediaServerMovie
            {
                Id = movie.Id,
                Name = movie.Name,
                Type = movie.Type,
                Quality = movie.MediaStreams?.FirstOrDefault()?.DisplayTitle ?? string.Empty,
                ProviderIds = movie.ProviderIds
            };
        }

        private static MediaServerInstance MapServer(EmbyServers server)
        {
            if (server == null)
            {
                return null;
            }

            return new MediaServerInstance
            {
                Name = server.Name,
                Ip = server.Ip,
                ApiKey = server.ApiKey,
                ServerId = server.ServerId,
                ServerHostname = server.ServerHostname,
                AdministratorId = server.AdministratorId,
                FullUri = server.FullUri,
                SelectedLibraries = server.EmbySelectedLibraries.Select(x => new MediaServerLibrary
                {
                    Key = x.Key,
                    Title = x.Title,
                    CollectionType = x.CollectionType,
                    Enabled = x.Enabled
                }).ToList()
            };
        }
    }
}
