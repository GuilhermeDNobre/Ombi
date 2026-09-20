using Ombi.Api.External.ExternalApis.TheMovieDb;
using Ombi.Core.Models.Requests;
using Ombi.Helpers;
using Ombi.Store.Entities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.ExternalApis.TheMovieDb.Models;
using Ombi.Core.Authentication;
using Ombi.Core.Engine.Interfaces;
using Ombi.Core.Engine.Requests;
using Ombi.Core.Models.UI;
using Ombi.Core.Rule.Interfaces;
using Ombi.Core.Settings;
using Ombi.Settings.Settings.Models;
using Ombi.Store.Entities.Requests;
using Ombi.Store.Repository;
using Ombi.Core.Models;
using System.Threading;
using Ombi.Core.Services;
using Ombi.Core.Helpers;
using Ombi.Notifications.Models;

namespace Ombi.Core.Engine
{
    public class MovieRequestEngine : BaseMediaEngine, IMovieRequestEngine
    {
        public MovieRequestEngine(IMovieDbApi movieApi, IRequestServiceMain requestService, ICurrentUser user,
            INotificationHelper helper, IRuleEvaluator r, ILogger<MovieRequestEngine> log,
            OmbiUserManager manager, ICacheService cache,
            ISettingsService<OmbiSettings> ombiSettings, IRepository<RequestSubscription> sub, IMediaCacheService mediaCacheService,
            IMovieRequestQueryBuilder queryBuilder,
            IMovieRequestEnricher enricher,
            IMovieRequestDispatcher dispatcher,
            IMovieRequestStatusService statusService,
            IMovieRequestFactory factory)
            : base(user, requestService, r, manager, cache, ombiSettings, sub)
        {
            MovieApi = movieApi;
            NotificationHelper = helper;
            Logger = log;
            _mediaCacheService = mediaCacheService;
            _queryBuilder = queryBuilder;
            _enricher = enricher;
            _dispatcher = dispatcher;
            _statusService = statusService;
            _factory = factory;
        }

        private IMovieDbApi MovieApi { get; }
        private INotificationHelper NotificationHelper { get; }
        private ILogger<MovieRequestEngine> Logger { get; }
        private readonly IMediaCacheService _mediaCacheService;
        private readonly IMovieRequestQueryBuilder _queryBuilder;
        private readonly IMovieRequestEnricher _enricher;
        private readonly IMovieRequestDispatcher _dispatcher;
        private readonly IMovieRequestStatusService _statusService;
        private readonly IMovieRequestFactory _factory;

        /// <summary>
        /// Requests the movie.
        /// </summary>
        /// <param name="model">The model.</param>
        /// <returns></returns>
        public async Task<RequestEngineResult> RequestMovie(MovieRequestViewModel model)
        {
            var buildResult = await _factory.Build(model);
            if (buildResult.Error != null)
            {
                return buildResult.Error;
            }

            var requestModel = buildResult.Request;

            var ruleResults = (await RunRequestRules(requestModel)).ToList();
            var ruleResultInError = ruleResults.Find(x => !x.Success);
            if (ruleResultInError != null)
            {
                return new RequestEngineResult
                {
                    ErrorMessage = ruleResultInError.Message,
                    ErrorCode = ruleResultInError.ErrorCode
                };
            }

            if (requestModel.Approved || requestModel.Approved4K) // The rules have auto approved this
            {
                var requestEngineResult = await _factory.Add(requestModel, buildResult.FullMovieName, model.RequestOnBehalf, buildResult.IsExisting, buildResult.Is4kRequest);
                if (requestEngineResult.Result)
                {
                    var result = await ApproveMovie(requestModel, model.Is4kRequest);
                    if (result.IsError)
                    {
                        Logger.LogWarning("Tried auto sending movie but failed. Message: {0}", result.Message);
                        return new RequestEngineResult
                        {
                            Message = result.Message,
                            ErrorMessage = result.Message,
                            Result = false
                        };
                    }

                    return requestEngineResult;
                }

                // If there are no providers then it's successful but movie has not been sent
            }

            return await _factory.Add(requestModel, buildResult.FullMovieName, model.RequestOnBehalf, buildResult.IsExisting, buildResult.Is4kRequest);
        }

        /// <summary>
        /// Gets the requests.
        /// </summary>
        /// <param name="count">The count.</param>
        /// <param name="position">The position.</param>
        /// <param name="orderFilter">The order/filter type.</param>
        /// <returns></returns>
        public async Task<RequestsViewModel<MovieRequests>> GetRequests(int count, int position,
            OrderFilterModel orderFilter)
        {
            var shouldHide = await HideFromOtherUsers();
            var allRequests = LoadRequests(shouldHide);

            allRequests = _queryBuilder.FilterByAvailability(allRequests, orderFilter.AvailabilityFilter);
            allRequests = _queryBuilder.FilterByStatus(allRequests, orderFilter.StatusFilter);

            var total = allRequests.Count();

            var requests = await (_queryBuilder.Order(allRequests, orderFilter.OrderType)).Skip(position).Take(count)
                .ToListAsync();

            await _enricher.FillAdditionalFields(shouldHide, requests);
            return new RequestsViewModel<MovieRequests>
            {
                Collection = requests,
                Total = total
            };
        }

        public async Task<RequestsViewModel<MovieRequests>> GetRequests(int count, int position, string sortProperty, string sortOrder, string requestedByUserId = null)
        {
            var shouldHide = await HideFromOtherUsers();
            var allRequests = LoadRequests(shouldHide);

            allRequests = FilterByRequestedUser(allRequests, requestedByUserId, shouldHide.IsAdmin);

            var total = await allRequests.CountAsync();
            var requests = await _queryBuilder.Sort(allRequests, sortProperty, sortOrder)
                .Skip(position).Take(count).ToListAsync();

            await _enricher.FillAdditionalFields(shouldHide, requests);
            return new RequestsViewModel<MovieRequests>
            {
                Collection = requests,
                Total = total
            };
        }

        public async Task<RequestsViewModel<MovieRequests>> GetRequestsByStatus(int count, int position, string sortProperty, string sortOrder, RequestStatus status, string requestedByUserId = null)
        {
            var shouldHide = await HideFromOtherUsers();
            var allRequests = LoadRequests(shouldHide);

            allRequests = FilterByRequestedUser(allRequests, requestedByUserId, shouldHide.IsAdmin);

            allRequests = _queryBuilder.FilterByRequestStatus(allRequests, status);

            var total = await allRequests.CountAsync();
            if (total == 0)
            {
                return new RequestsViewModel<MovieRequests>
                {
                    Collection = Enumerable.Empty<MovieRequests>(),
                    Total = total
                };
            }

            var requests = await _queryBuilder.Sort(allRequests, sortProperty, sortOrder)
                .Skip(position).Take(count).ToListAsync();

            await _enricher.FillAdditionalFields(shouldHide, requests);
            return new RequestsViewModel<MovieRequests>
            {
                Collection = requests,
                Total = total
            };
        }

        public async Task<RequestsViewModel<MovieRequests>> GetUnavailableRequests(int count, int position, string sortProperty, string sortOrder, string requestedByUserId = null)
        {
            var shouldHide = await HideFromOtherUsers();
            var allRequests = LoadRequests(shouldHide).Where(x => !x.Available && x.Approved);

            allRequests = FilterByRequestedUser(allRequests, requestedByUserId, shouldHide.IsAdmin);

            var total = await allRequests.CountAsync();
            var requests = await _queryBuilder.Sort(allRequests, sortProperty, sortOrder)
                .Skip(position).Take(count).ToListAsync();

            await _enricher.FillAdditionalFields(shouldHide, requests);
            return new RequestsViewModel<MovieRequests>
            {
                Collection = requests,
                Total = total
            };
        }

        public async Task<RequestEngineResult> UpdateAdvancedOptions(MediaAdvancedOptions options)
        {
            var request = await MovieRepository.Find(options.RequestId);
            if (request == null)
            {
                return new RequestEngineResult
                {
                    Result = false,
                    ErrorMessage = "Request does not exist"
                };
            }

            request.QualityOverride = options.QualityOverride;
            request.RootPathOverride = options.RootPathOverride;

            await MovieRepository.Update(request);

            return new RequestEngineResult
            {
                Result = true
            };
        }

        public async Task<int> GetTotal()
        {
            var shouldHide = await HideFromOtherUsers();
            return await LoadRequests(shouldHide).CountAsync();
        }

        /// <summary>
        /// Gets the requests.
        /// </summary>
        /// <returns></returns>
        public async Task<IEnumerable<MovieRequests>> GetRequests()
        {
            var shouldHide = await HideFromOtherUsers();
            var allRequests = await LoadRequests(shouldHide).ToListAsync();

            await _enricher.FillAdditionalFields(shouldHide, allRequests);

            return allRequests;
        }

        public async Task<MovieRequests> GetRequest(int requestId)
        {
            var shouldHide = await HideFromOtherUsers();
            // TODO: this query should return the request only if the user is allowed to see it (see shouldHide implementations)
            var request = await MovieRepository.GetWithUser().Where(x => x.Id == requestId).FirstOrDefaultAsync();
            await _enricher.FillAdditionalFields(shouldHide, new List<MovieRequests> { request });

            return request;
        }
        private IQueryable<MovieRequests> LoadRequests(HideResult shouldHide)
        {
            return shouldHide.Hide
                ? MovieRepository.GetWithUser(shouldHide.UserId)
                : MovieRepository.GetWithUser();
        }

        /// <summary>
        /// Searches the movie request.
        /// </summary>
        /// <param name="search">The search.</param>
        /// <returns></returns>
        public async Task<IEnumerable<MovieRequests>> SearchMovieRequest(string search)
        {
            var shouldHide = await HideFromOtherUsers();
            var allRequests = await LoadRequests(shouldHide).ToListAsync();

            var results = allRequests.Where(x => x.Title.Contains(search, CompareOptions.IgnoreCase)).ToList();
            await _enricher.FillAdditionalFields(shouldHide, results);

            return results;
        }

        public async Task<RequestEngineResult> ApproveMovieById(int requestId, bool is4K)
        {
            return await _statusService.ApproveById(requestId, is4K);
        }

        public async Task<RequestEngineResult> DenyMovieById(int modelId, string denyReason, bool is4K)
        {
            return await _statusService.DenyById(modelId, denyReason, is4K);
        }

        public async Task<RequestEngineResult> ApproveMovie(MovieRequests request, bool is4K)
        {
            return await _statusService.Approve(request, is4K);
        }

        public async Task<RequestEngineResult> RequestCollection(int collectionId, CancellationToken cancellationToken)
        {
            var langCode = await DefaultLanguageCode(null);
            var collections = await Cache.GetOrAddAsync($"GetCollection{collectionId}{langCode}",
                () => MovieApi.GetCollection(langCode, collectionId, cancellationToken), DateTimeOffset.Now.AddDays(1));

            var results = new List<RequestEngineResult>();
            foreach (var collection in collections.parts)
            {
                results.Add(await RequestMovie(new MovieRequestViewModel
                {
                    TheMovieDbId = collection.id,
                    LanguageCode = langCode
                }));
            }


            if (results.All(x => x.IsError))
            {
                new RequestEngineResult { Result = false, ErrorMessage = $"The whole collection {collections.name} Is already monitored or requested!" };
            }

            return new RequestEngineResult { Result = true, Message = $"The collection {collections.name} has been successfully added!", RequestId = results.FirstOrDefault().RequestId };
        }

        /// <summary>
        /// Updates the movie request.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <returns></returns>
        public async Task<MovieRequests> UpdateMovieRequest(MovieRequests request)
        {
            var allRequests = await MovieRepository.GetWithUser().ToListAsync();
            var results = allRequests.FirstOrDefault(x => x.Id == request.Id);

            results.Approved = request.Approved;
            results.Available = request.Available;
            results.Denied = request.Denied;
            results.DeniedReason = request.DeniedReason;
            results.ImdbId = request.ImdbId;
            results.IssueId = request.IssueId;
            results.Issues = request.Issues;
            results.Overview = request.Overview;
            results.PosterPath = PosterPathHelper.FixPosterPath(request.PosterPath);
            results.QualityOverride = request.QualityOverride;
            results.RootPathOverride = request.RootPathOverride;

            await MovieRepository.Update(results);
            await _mediaCacheService.Purge();
            return results;
        }

        /// <summary>
        /// Removes the movie request.
        /// </summary>
        /// <param name="requestId">The request identifier.</param>
        /// <returns></returns>
        public async Task<RequestEngineResult> RemoveMovieRequest(int requestId)
        {
            var request = await MovieRepository.GetAll().FirstOrDefaultAsync(x => x.Id == requestId);

            var result = await CheckCanManageRequest(request);
            if (result.IsError)
                return result;

            await NotificationHelper.Notify(new NotificationOptions
            {
                RequestId = 0,
                DateTime = DateTime.Now,
                NotificationType = NotificationType.RequestDeleted,
                RequestType = request.RequestType,
                Recipient = request.RequestedUser?.Email ?? string.Empty,
                UserId = request.RequestedUserId,
                Substitutes = new Dictionary<string, string>
                {
                    { NotificationSubstitues.Title, request.Title },
                    { NotificationSubstitues.RequestType, request.RequestType.ToString() },
                }
            });

            await MovieRepository.Delete(request);
            await _mediaCacheService.Purge();
            return new RequestEngineResult
            {
                Result = true,
            };
        }

        public async Task RemoveAllMovieRequests()
        {
            var request = MovieRepository.GetAll();
            await MovieRepository.DeleteRange(request);
            await _mediaCacheService.Purge();
        }

        public async Task<bool> UserHasRequest(string userId)
        {
            return await MovieRepository.GetAll().AnyAsync(x => x.RequestedUserId == userId);
        }

        public async Task<RequestEngineResult> ReProcessRequest(int requestId, bool is4K, CancellationToken cancellationToken)
        {
            var request = await MovieRepository.GetWithUser().FirstOrDefaultAsync(x => x.Id == requestId);
            if (request == null)
            {
                return new RequestEngineResult
                {
                    Result = false,
                    ErrorMessage = "Request does not exist"
                };
            }

            return await _dispatcher.Send(request, is4K);
        }

        public async Task<RequestEngineResult> MarkUnavailable(int modelId, bool is4K)
        {
            return await _statusService.MarkUnavailable(modelId, is4K);
        }

        public async Task<RequestEngineResult> MarkAvailable(int modelId, bool is4K)
        {
            return await _statusService.MarkAvailable(modelId, is4K);
        }

    }
}
