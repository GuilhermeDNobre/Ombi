using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ombi.Helpers;
using Ombi.Store.Entities;
using Ombi.Store.Entities.Requests;
using Ombi.Store.Repository;

namespace Ombi.Core.Engine.Requests
{
    public class MovieRequestEnricher : IMovieRequestEnricher
    {
        public MovieRequestEnricher(IRepository<RequestSubscription> subscriptionRepository,
            IUserPlayedMovieRepository userPlayedMovieRepository)
        {
            _subscriptionRepository = subscriptionRepository;
            _userPlayedMovieRepository = userPlayedMovieRepository;
        }

        private readonly IRepository<RequestSubscription> _subscriptionRepository;
        private readonly IUserPlayedMovieRepository _userPlayedMovieRepository;

        public async Task FillAdditionalFields(BaseMediaEngine.HideResult shouldHide, List<MovieRequests> requests)
        {
            await CheckForSubscription(shouldHide.UserId, requests);
            await CheckForPlayed(shouldHide, requests);
        }

        private async Task CheckForSubscription(string UserId, List<MovieRequests> movieRequests)
        {
            var requestIds = movieRequests.Select(x => x.Id);
            var sub = await _subscriptionRepository.GetAll().Where(s =>
                s.UserId == UserId && requestIds.Contains(s.RequestId) && s.RequestType == RequestType.Movie)
                .ToListAsync();
            foreach (var x in movieRequests)
            {
                x.PosterPath = PosterPathHelper.FixPosterPath(x.PosterPath);
                if (UserId == x.RequestedUserId)
                {
                    x.ShowSubscribe = false;
                }
                else
                {
                    if (!x.Available && !x.Available4K && (!x.Denied ?? true) && (!x.Denied4K ?? true))
                    {
                        x.ShowSubscribe = true;
                    }
                    var hasSub = sub.FirstOrDefault(r => r.RequestId == x.Id);
                    x.Subscribed = hasSub != null;
                }
            }
        }

        private async Task CheckForPlayed(BaseMediaEngine.HideResult shouldHide, List<MovieRequests> movieRequests)
        {
            var theMovieDbIds = movieRequests.Select(x => x.TheMovieDbId);
            var plays = await _userPlayedMovieRepository.GetAll().Where(x =>
                theMovieDbIds.Contains(x.TheMovieDbId))
                .ToListAsync();
            foreach (var request in movieRequests)
            {
                request.WatchedByRequestedUser = plays.Exists(x => x.TheMovieDbId == request.TheMovieDbId && x.UserId == request.RequestedUserId);

                if (!shouldHide.Hide)
                {
                    request.PlayedByUsersCount = plays.Count(x => x.TheMovieDbId == request.TheMovieDbId);
                }
            }
        }
    }
}
