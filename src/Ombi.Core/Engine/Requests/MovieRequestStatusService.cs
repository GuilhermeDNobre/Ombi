using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ombi.Core.Models.Requests;
using Ombi.Core.Rule.Interfaces;
using Ombi.Core.Services;
using Ombi.Helpers;
using Ombi.Store.Entities.Requests;
using Ombi.Store.Repository.Requests;

namespace Ombi.Core.Engine.Requests
{
    public class MovieRequestStatusService : IMovieRequestStatusService
    {
        public MovieRequestStatusService(IRequestServiceMain requestService, INotificationHelper helper,
            IRuleEvaluator rules, IMediaCacheService mediaCacheService, IMovieRequestDispatcher dispatcher)
        {
            _requestService = requestService;
            _notificationHelper = helper;
            _rules = rules;
            _mediaCacheService = mediaCacheService;
            _dispatcher = dispatcher;
        }

        private readonly IRequestServiceMain _requestService;
        private readonly INotificationHelper _notificationHelper;
        private readonly IRuleEvaluator _rules;
        private readonly IMediaCacheService _mediaCacheService;
        private readonly IMovieRequestDispatcher _dispatcher;

        private IMovieRequestRepository MovieRepository => _requestService.MovieRequestService;

        public async Task<RequestEngineResult> ApproveById(int requestId, bool is4K)
        {
            var request = await MovieRepository.GetWithUser().FirstOrDefaultAsync(x => x.Id == requestId);
            return await Approve(request, is4K);
        }

        public async Task<RequestEngineResult> DenyById(int modelId, string denyReason, bool is4K)
        {
            var request = await MovieRepository.GetWithUser().FirstOrDefaultAsync(x => x.Id == modelId);
            if (request == null)
            {
                return new RequestEngineResult
                {
                    ErrorMessage = "Request does not exist"
                };
            }

            if (is4K)
            {
                request.Denied4K = true;
                request.DeniedReason4K = denyReason;
            }
            else
            {
                request.Denied = true;
                request.DeniedReason = denyReason;
            }
            await MovieRepository.Update(request);
            await _mediaCacheService.Purge();

            // We are denying a request
            await _notificationHelper.Notify(request, NotificationType.RequestDeclined);

            return new RequestEngineResult
            {
                Result = true,
                Message = "Request successfully deleted",
            };
        }

        public async Task<RequestEngineResult> Approve(MovieRequests request, bool is4K)
        {
            if (request == null)
            {
                return new RequestEngineResult
                {
                    ErrorMessage = "Request does not exist"
                };
            }

            if (is4K)
            {
                request.MarkedAsApproved4K = DateTime.Now;
                request.Approved4K = true;
                request.Denied4K = false;
            }
            else
            {
                request.MarkedAsApproved = DateTime.Now;
                request.Approved = true;
                request.Denied = false;
            }
            await MovieRepository.Update(request);

            var canNotify = await _rules.StartSpecificRules(request, SpecificRules.CanSendNotification, string.Empty);
            if (canNotify.Success)
            {
                await _notificationHelper.Notify(request, NotificationType.RequestApproved);
            }
            await _mediaCacheService.Purge();

            return await _dispatcher.Send(request, is4K);
        }

        public async Task<RequestEngineResult> MarkUnavailable(int modelId, bool is4K)
        {
            var request = await MovieRepository.GetWithUser().FirstOrDefaultAsync(x => x.Id == modelId);
            if (request == null)
            {
                return new RequestEngineResult
                {
                    ErrorMessage = "Request does not exist"
                };
            }

            if (is4K)
            {
                request.Available4K = false;
            }
            else
            {
                request.Available = false;
            }
            await MovieRepository.Update(request);
            await _mediaCacheService.Purge();

            return new RequestEngineResult
            {
                Message = "Request is now unavailable",
                Result = true
            };
        }

        public async Task<RequestEngineResult> MarkAvailable(int modelId, bool is4K)
        {
            var request = await MovieRepository.GetWithUser().FirstOrDefaultAsync(x => x.Id == modelId);
            if (request == null)
            {
                return new RequestEngineResult
                {
                    ErrorMessage = "Request does not exist"
                };
            }
            if (!is4K)
            {
                request.Available = true;
                request.MarkedAsAvailable = DateTime.Now;
            }
            else
            {
                request.Available4K = true;
                request.MarkedAsAvailable4K = DateTime.Now;
            }
            await _notificationHelper.Notify(request, NotificationType.RequestAvailable);
            await MovieRepository.Update(request);
            await _mediaCacheService.Purge();

            return new RequestEngineResult
            {
                Message = "Request is now available",
                Result = true
            };
        }
    }
}
