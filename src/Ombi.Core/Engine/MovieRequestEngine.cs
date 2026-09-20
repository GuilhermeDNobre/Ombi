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
            OmbiUserManager manager, IRepository<RequestLog> rl, ICacheService cache,
            ISettingsService<OmbiSettings> ombiSettings, IRepository<RequestSubscription> sub, IMediaCacheService mediaCacheService,
            IFeatureService featureService,
            IMovieRequestQueryBuilder queryBuilder,
            IMovieRequestEnricher enricher,
            IMovieRequestDispatcher dispatcher,
            IMovieRequestStatusService statusService)
            : base(user, requestService, r, manager, cache, ombiSettings, sub)
        {
            MovieApi = movieApi;
            NotificationHelper = helper;
            Logger = log;
            _requestLog = rl;
            _mediaCacheService = mediaCacheService;
            _featureService = featureService;
            _queryBuilder = queryBuilder;
            _enricher = enricher;
            _dispatcher = dispatcher;
            _statusService = statusService;
        }

        private IMovieDbApi MovieApi { get; }
        private INotificationHelper NotificationHelper { get; }
        private ILogger<MovieRequestEngine> Logger { get; }
        private readonly IRepository<RequestLog> _requestLog;
        private readonly IMediaCacheService _mediaCacheService;
        private readonly IFeatureService _featureService;
        private readonly IMovieRequestQueryBuilder _queryBuilder;
        private readonly IMovieRequestEnricher _enricher;
        private readonly IMovieRequestDispatcher _dispatcher;
        private readonly IMovieRequestStatusService _statusService;

        /// <summary>
        /// Requests the movie.
        /// </summary>
        /// <param name="model">The model.</param>
        /// <returns></returns>
        public async Task<RequestEngineResult> RequestMovie(MovieRequestViewModel model)
        {
            var movieInfo = await MovieApi.GetMovieInformationWithExtraInfo(model.TheMovieDbId, model.LanguageCode);
            if (movieInfo == null || movieInfo.Id == 0)
            {
                return new RequestEngineResult
                {
                    Result = false,
                    Message = "There was an issue adding this movie!",
                    ErrorMessage = $"Please try again later"
                };
            }

            var fullMovieName =
                $"{movieInfo.Title}{(!string.IsNullOrEmpty(movieInfo.ReleaseDate) ? $" ({DateTime.Parse(movieInfo.ReleaseDate).Year})" : string.Empty)}";

            var userDetails = await GetUser();
            var canRequestOnBehalf = model.RequestOnBehalf.HasValue();

            var isAdmin = Username.Equals("API", StringComparison.CurrentCultureIgnoreCase)
                || await UserManager.IsInRoleAsync(userDetails, OmbiRoles.PowerUser)
                || await UserManager.IsInRoleAsync(userDetails, OmbiRoles.Admin);
            if (canRequestOnBehalf && !isAdmin)
            {
                return new RequestEngineResult
                {
                    Result = false,
                    Message = "You do not have the correct permissions to request on behalf of users!",
                    ErrorMessage = $"You do not have the correct permissions to request on behalf of users!"
                };
            }

            if ((model.RootFolderOverride.HasValue || model.QualityPathOverride.HasValue) && !isAdmin)
            {
                return new RequestEngineResult
                {
                    Result = false,
                    Message = "You do not have the correct permissions!",
                    ErrorMessage = $"You do not have the correct permissions!"
                };
            }

            var is4kFeatureEnabled = await _featureService.FeatureEnabled(FeatureNames.Movie4KRequests);
            var is4kRequest = is4kFeatureEnabled && model.Is4kRequest;

            MovieRequests requestModel;
            bool isExisting = false;
            // Do we already have a request? 4k or non 4k
            var existingRequest = await MovieRepository.GetRequestAsync(movieInfo.Id);
            if (existingRequest != null && is4kFeatureEnabled)
            {
                if (model.Is4kRequest)
                {
                    existingRequest.Is4kRequest = true;
                    existingRequest.RequestedDate4k = DateTime.UtcNow;
                }
                else
                {
                    existingRequest.RequestedDate = DateTime.UtcNow;
                }
                isExisting = true;
                requestModel = existingRequest;
            }
            else
            {
                requestModel = new MovieRequests
                {
                    TheMovieDbId = movieInfo.Id,
                    RequestType = RequestType.Movie,
                    Overview = movieInfo.Overview,
                    ImdbId = movieInfo.ImdbId,
                    PosterPath = PosterPathHelper.FixPosterPath(movieInfo.PosterPath),
                    Title = movieInfo.Title,
                    ReleaseDate = !string.IsNullOrEmpty(movieInfo.ReleaseDate)
                        ? DateTime.Parse(movieInfo.ReleaseDate)
                        : DateTime.MinValue,
                    Status = movieInfo.Status,
                    RequestedDate = model.Is4kRequest ? DateTime.MinValue : DateTime.UtcNow,
                    Approved = false,
                    Approved4K = false,
                    RequestedUserId = canRequestOnBehalf ? model.RequestOnBehalf : userDetails.Id,
                    Background = movieInfo.BackdropPath,
                    LangCode = model.LanguageCode,
                    RequestedByAlias = model.RequestedByAlias,
                    RootPathOverride = model.RootFolderOverride.GetValueOrDefault(),
                    QualityOverride = model.QualityPathOverride.GetValueOrDefault(),
                    RequestedDate4k = model.Is4kRequest ? DateTime.UtcNow : DateTime.MinValue,
                    Is4kRequest = model.Is4kRequest,
                    Source = model.Source
                };
            }

            var usDates = movieInfo.ReleaseDates?.Results?.FirstOrDefault(x => x.IsoCode == "US");
            requestModel.DigitalReleaseDate = usDates?.ReleaseDate
                ?.FirstOrDefault(x => x.Type == ReleaseDateType.Digital)?.ReleaseDate;
            
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
                var requestEngineResult = await AddMovieRequest(requestModel, fullMovieName, model.RequestOnBehalf, isExisting, is4kRequest);
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

            return await AddMovieRequest(requestModel, fullMovieName, model.RequestOnBehalf, isExisting, is4kRequest);
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

        private async Task<RequestEngineResult> AddMovieRequest(MovieRequests model, string movieName, string requestOnBehalf, bool isExisting, bool is4k)
        {
            if (is4k)
            {
                model.Has4KRequest = true;
            }
            if (!isExisting)
            {
                await MovieRepository.Add(model);
            }
            else
            {
                await MovieRepository.Update(model);
            }

            var result = await RunSpecificRule(model, SpecificRules.CanSendNotification, requestOnBehalf);
            if (result.Success)
            {
                await NotificationHelper.NewRequest(model);
            }

            await _mediaCacheService.Purge();

            await _requestLog.Add(new RequestLog
            {
                UserId = requestOnBehalf.HasValue() ? requestOnBehalf : (await GetUser()).Id,
                RequestDate = DateTime.UtcNow,
                RequestId = model.Id,
                RequestType = RequestType.Movie,
            });

            return new RequestEngineResult { Result = true, Message = $"{movieName} has been successfully added!", RequestId = model.Id };
        }
    }
}
