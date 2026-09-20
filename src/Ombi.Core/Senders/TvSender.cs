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
    public class TvSender : ITvSender
    {
        public TvSender(ITvDvrSenderFactory factory, IRepository<RequestQueue> requestQueue, INotificationHelper notify, ILogger<TvSender> log)
        {
            _factory = factory;
            _requestQueueRepository = requestQueue;
            _notificationHelper = notify;
            _log = log;
        }

        private readonly ITvDvrSenderFactory _factory;
        private readonly IRepository<RequestQueue> _requestQueueRepository;
        private readonly INotificationHelper _notificationHelper;
        private readonly ILogger<TvSender> _log;

        public async Task<SenderResult> Send(ChildRequests model)
        {
            var senders = await _factory.GetEnabledSendersAsync();
            if (!senders.Any())
            {
                return new SenderResult { Success = true };
            }

            SenderResult lastFailure = null;
            foreach (var sender in senders)
            {
                try
                {
                    var result = await sender.Send(model);
                    if (result.Success)
                    {
                        return result;
                    }
                    lastFailure = result;
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Exception thrown when sending a series to DVR app");
                    lastFailure = new SenderResult { Success = false, Message = e.Message };
                }
            }

            _log.LogWarning("TV send to DVR app failed: {Message}, added to the request queue", lastFailure.Message);
            await AddToRequestFailureQueue(model, lastFailure.Message);
            return lastFailure;
        }

        private async Task AddToRequestFailureQueue(ChildRequests model, string errorMessage)
        {
            var existingQueue = await _requestQueueRepository.FirstOrDefaultAsync(x => x.RequestId == model.Id && x.Type == RequestType.TvShow);
            if (existingQueue != null)
            {
                existingQueue.RetryCount++;
                existingQueue.Error = errorMessage;
                await _requestQueueRepository.SaveChangesAsync();
            }
            else
            {
                await _requestQueueRepository.Add(new RequestQueue
                {
                    Dts = DateTime.UtcNow,
                    Error = errorMessage,
                    RequestId = model.Id,
                    Type = RequestType.TvShow,
                    RetryCount = 0
                });
                await _notificationHelper.Notify(model, NotificationType.ItemAddedToFaultQueue);
            }
        }
    }
}
