using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.MediaServers.Jellyfin;
using Ombi.Api.External.MediaServers.Jellyfin.Models.Movie;
using Ombi.Core.Settings;
using Ombi.Core.Settings.Models.External;
using Ombi.Helpers;
using Ombi.Hubs;
using Ombi.Schedule.Jobs.MediaServer;
using Ombi.Store.Entities;
using Ombi.Store.Repository;

namespace Ombi.Schedule.Jobs.Jellyfin
{
    public class JellyfinCatalogAdapter : IMediaServerCatalog<JellyfinContent>
    {
        public JellyfinCatalogAdapter(
            ISettingsService<JellyfinSettings> settings,
            IJellyfinApiFactory api,
            IJellyfinContentRepository repo,
            INotificationHubService notification)
        {
            _settings = settings;
            _apiFactory = api;
            _repo = repo;
            _notification = notification;
        }

        private readonly ISettingsService<JellyfinSettings> _settings;
        private readonly IJellyfinApiFactory _apiFactory;
        private readonly IJellyfinContentRepository _repo;
        private readonly INotificationHubService _notification;

        private IJellyfinApi Api { get; set; }

        public string ServerName => "Jellyfin";

        public EventId ContentCacherLog => LoggingEvents.JellyfinContentCacher;

        public int PageSize => 200;

        public async Task<IReadOnlyList<MediaServerInstance>> OpenAsync()
        {
            var jellyfinSettings = await _settings.GetSettingsAsync();
            if (!jellyfinSettings.Enable)
                return null;

            Api = _apiFactory.CreateClient(jellyfinSettings);

            return jellyfinSettings.Servers.Select(MapServer).ToList();
        }

        public async Task<MediaServerPage<MediaServerSeries>> GetShows(MediaServerInstance server, string parentId, int startIndex, int count, bool recentlyAdded)
        {
            var shows = await Api.GetAllShows(server.ApiKey, parentId, startIndex, count, server.AdministratorId, server.FullUri);

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
            var movies = await Api.GetAllMovies(server.ApiKey, parentId, startIndex, count, server.AdministratorId, server.FullUri);

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

        public Task<JellyfinContent> GetByMediaServerId(string mediaServerId)
        {
            return _repo.GetByJellyfinId(mediaServerId);
        }

        public string GetMediaServerId(JellyfinContent content)
        {
            return content.JellyfinId;
        }

        public void SetIdentity(JellyfinContent content, string mediaServerId, MediaServerInstance server)
        {
            content.JellyfinId = mediaServerId;
            content.Url = JellyfinHelper.GetJellyfinMediaUrl(mediaServerId, server?.ServerId, server.ServerHostname);
        }

        public Task NotifySyncStarting(bool recentlyAdded)
        {
            return Task.CompletedTask;
        }

        public Task NotifySyncStarted()
        {
            return _notification.SendNotificationToAdmins("Jellyfin Content Sync Started");
        }

        public Task NotifySyncFailed()
        {
            return _notification.SendNotificationToAdmins("Jellyfin Content Sync Failed");
        }

        public Task NotifySyncFinished()
        {
            return _notification.SendNotificationToAdmins("Jellyfin Content Sync Finished");
        }

        private static MediaServerMovie MapMovie(JellyfinMovie movie)
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

        private static MediaServerInstance MapServer(JellyfinServers server)
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
                SelectedLibraries = server.JellyfinSelectedLibraries.Select(x => new MediaServerLibrary
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
