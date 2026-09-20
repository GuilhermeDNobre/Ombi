using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ombi.Store.Entities;

namespace Ombi.Schedule.Jobs.MediaServer
{
    public interface IMediaServerCatalog<TContent> where TContent : MediaServerContent
    {
        string ServerName { get; }
        EventId ContentCacherLog { get; }
        int PageSize { get; }

        Task<IReadOnlyList<MediaServerInstance>> OpenAsync();

        Task<MediaServerPage<MediaServerSeries>> GetShows(MediaServerInstance server, string parentId, int startIndex, int count, bool recentlyAdded);
        Task<MediaServerPage<MediaServerMovie>> GetMovies(MediaServerInstance server, string parentId, int startIndex, int count, bool recentlyAdded);
        Task<IReadOnlyList<MediaServerMovie>> GetCollection(MediaServerInstance server, string collectionId);

        Task<TContent> GetByMediaServerId(string mediaServerId);
        string GetMediaServerId(TContent content);
        void SetIdentity(TContent content, string mediaServerId, MediaServerInstance server);

        Task NotifySyncStarting(bool recentlyAdded);
        Task NotifySyncStarted();
        Task NotifySyncFailed();
        Task NotifySyncFinished();
    }
}
