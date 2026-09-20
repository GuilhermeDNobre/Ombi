using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ombi.Core.Senders.Dvr;
using Ombi.Helpers;
using Ombi.Store.Entities;
using Ombi.Store.Entities.Requests;
using Ombi.Store.Repository;

namespace Ombi.Core.Senders
{
    public class MovieSender : IMovieSender
    {
        public MovieSender(IMovieDvrSenderFactory factory, IRepository<RequestQueue> requestQueue, INotificationHelper notify, ILogger<MovieSender> log)
        {
            _factory = factory;
            _requestQueuRepository = requestQueue;
            _notificationHelper = notify;
            _log = log;
        }

        private readonly IMovieDvrSenderFactory _factory;
        private readonly IRepository<RequestQueue> _requestQueuRepository;
        private readonly INotificationHelper _notificationHelper;
        private readonly ILogger<MovieSender> _log;

        public async Task<SenderResult> Send(MovieRequests model, bool is4K)
        {
            var senders = await _factory.GetEnabledSendersAsync(is4K);
            if (!senders.Any())
            {
                return new SenderResult { Success = true, Sent = false };
            }

            SenderResult lastFailure = null;
            foreach (var sender in senders)
            {
                try
                {
                    var result = await sender.Send(model, is4K);
                    if (result.Success)
                    {
                        return result;
                    }
                    lastFailure = result;
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Error when sending movie to DVR app");
                    lastFailure = new SenderResult { Success = false, Sent = false, Message = e.Message };
                }
            }

            _log.LogWarning("Movie send to DVR app failed: {Message}, added to the request queue", lastFailure.Message);
            await AddToRequestFailureQueue(model, lastFailure.Message);
            return lastFailure;
        }

        private async Task AddToRequestFailureQueue(MovieRequests model, string errorMessage)
        {
            var existingQueue = await _requestQueuRepository.FirstOrDefaultAsync(x => x.RequestId == model.Id && x.Type == RequestType.Movie);
            if (existingQueue != null)
            {
                existingQueue.RetryCount++;
                existingQueue.Error = errorMessage;
                await _requestQueuRepository.SaveChangesAsync();
            }
            else
            {
                await _requestQueuRepository.Add(new RequestQueue
                {
                    Dts = DateTime.UtcNow,
                    Error = errorMessage,
                    RequestId = model.Id,
                    Type = RequestType.Movie,
                    RetryCount = 0
                });
                await _notificationHelper.Notify(model, NotificationType.ItemAddedToFaultQueue);
            }
        }
    }
}
