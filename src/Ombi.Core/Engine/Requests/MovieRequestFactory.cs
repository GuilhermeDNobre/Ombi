using System;
using System.Linq;
using System.Threading.Tasks;
using Ombi.Api.External.ExternalApis.TheMovieDb;
using Ombi.Api.External.ExternalApis.TheMovieDb.Models;
using Ombi.Core.Authentication;
using Ombi.Core.Helpers;
using Ombi.Core.Models.Requests;
using Ombi.Core.Rule.Interfaces;
using Ombi.Core.Services;
using Ombi.Helpers;
using Ombi.Settings.Settings.Models;
using Ombi.Store.Entities;
using Ombi.Store.Entities.Requests;
using Ombi.Store.Repository;
using Ombi.Store.Repository.Requests;

namespace Ombi.Core.Engine.Requests
{
    public class MovieRequestFactory : IMovieRequestFactory
    {
        public MovieRequestFactory(IMovieDbApi movieApi, IRequestServiceMain requestService, ICurrentUser user,
            INotificationHelper helper, IRuleEvaluator rules, OmbiUserManager manager, IRepository<RequestLog> rl,
            IMediaCacheService mediaCacheService, IFeatureService featureService)
        {
            _movieApi = movieApi;
            _requestService = requestService;
            _currentUser = user;
            _notificationHelper = helper;
            _rules = rules;
            _userManager = manager;
            _requestLog = rl;
            _mediaCacheService = mediaCacheService;
            _featureService = featureService;
        }

        private readonly IMovieDbApi _movieApi;
        private readonly IRequestServiceMain _requestService;
        private readonly ICurrentUser _currentUser;
        private readonly INotificationHelper _notificationHelper;
        private readonly IRuleEvaluator _rules;
        private readonly OmbiUserManager _userManager;
        private readonly IRepository<RequestLog> _requestLog;
        private readonly IMediaCacheService _mediaCacheService;
        private readonly IFeatureService _featureService;

        private IMovieRequestRepository MovieRepository => _requestService.MovieRequestService;

        public async Task<MovieRequestBuildResult> Build(MovieRequestViewModel model)
        {
            var movieInfo = await _movieApi.GetMovieInformationWithExtraInfo(model.TheMovieDbId, model.LanguageCode);
            if (movieInfo == null || movieInfo.Id == 0)
            {
                return new MovieRequestBuildResult
                {
                    Error = new RequestEngineResult
                    {
                        Result = false,
                        Message = "There was an issue adding this movie!",
                        ErrorMessage = $"Please try again later"
                    }
                };
            }

            var fullMovieName =
                $"{movieInfo.Title}{(!string.IsNullOrEmpty(movieInfo.ReleaseDate) ? $" ({DateTime.Parse(movieInfo.ReleaseDate).Year})" : string.Empty)}";

            var userDetails = await _currentUser.GetUser();
            var canRequestOnBehalf = model.RequestOnBehalf.HasValue();

            var isAdmin = _currentUser.Username.Equals("API", StringComparison.CurrentCultureIgnoreCase)
                || await _userManager.IsInRoleAsync(userDetails, OmbiRoles.PowerUser)
                || await _userManager.IsInRoleAsync(userDetails, OmbiRoles.Admin);
            if (canRequestOnBehalf && !isAdmin)
            {
                return new MovieRequestBuildResult
                {
                    Error = new RequestEngineResult
                    {
                        Result = false,
                        Message = "You do not have the correct permissions to request on behalf of users!",
                        ErrorMessage = $"You do not have the correct permissions to request on behalf of users!"
                    }
                };
            }

            if ((model.RootFolderOverride.HasValue || model.QualityPathOverride.HasValue) && !isAdmin)
            {
                return new MovieRequestBuildResult
                {
                    Error = new RequestEngineResult
                    {
                        Result = false,
                        Message = "You do not have the correct permissions!",
                        ErrorMessage = $"You do not have the correct permissions!"
                    }
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

            return new MovieRequestBuildResult
            {
                Request = requestModel,
                FullMovieName = fullMovieName,
                IsExisting = isExisting,
                Is4kRequest = is4kRequest
            };
        }

        public async Task<RequestEngineResult> Add(MovieRequests model, string movieName, string requestOnBehalf, bool isExisting, bool is4k)
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

            var result = await _rules.StartSpecificRules(model, SpecificRules.CanSendNotification, requestOnBehalf);
            if (result.Success)
            {
                await _notificationHelper.NewRequest(model);
            }

            await _mediaCacheService.Purge();

            await _requestLog.Add(new RequestLog
            {
                UserId = requestOnBehalf.HasValue() ? requestOnBehalf : (await _currentUser.GetUser()).Id,
                RequestDate = DateTime.UtcNow,
                RequestId = model.Id,
                RequestType = RequestType.Movie,
            });

            return new RequestEngineResult { Result = true, Message = $"{movieName} has been successfully added!", RequestId = model.Id };
        }
    }
}
