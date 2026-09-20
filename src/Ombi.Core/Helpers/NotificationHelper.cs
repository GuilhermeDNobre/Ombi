using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ombi.Helpers;
using Ombi.Notifications;
using Ombi.Notifications.Models;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core
{
    public class NotificationHelper : INotificationHelper
    {
        public NotificationHelper(IEnumerable<IRequestEventObserver> observers)
        {
            _observers = observers;
        }

        private readonly IEnumerable<IRequestEventObserver> _observers;

        public async Task NewRequest(FullBaseRequest model)
        {
            var notificationModel = new NotificationOptions
            {
                RequestId = model.Id,
                DateTime = DateTime.Now,
                NotificationType = NotificationType.NewRequest,
                RequestType = model.RequestType
            };
            await PublishAsync(notificationModel);
        }

        public async Task NewRequest(ChildRequests model)
        {
            var notificationModel = new NotificationOptions
            {
                RequestId = model.Id,
                DateTime = DateTime.Now,
                NotificationType = NotificationType.NewRequest,
                RequestType = model.RequestType
            };
            await PublishAsync(notificationModel);
        }

        public async Task NewRequest(AlbumRequest model)
        {
            var notificationModel = new NotificationOptions
            {
                RequestId = model.Id,
                DateTime = DateTime.Now,
                NotificationType = NotificationType.NewRequest,
                RequestType = model.RequestType
            };
            await PublishAsync(notificationModel);
        }


        public async Task Notify(MovieRequests model, NotificationType type)
        {
            var notificationModel = new NotificationOptions
            {
                RequestId = model.Id,
                DateTime = DateTime.Now,
                NotificationType = type,
                RequestType = model.RequestType,
                Recipient = model.RequestedUser?.Email ?? string.Empty
            };

            await PublishAsync(notificationModel);
        }
        public async Task Notify(ChildRequests model, NotificationType type)
        {
            var notificationModel = new NotificationOptions
            {
                RequestId = model.Id,
                DateTime = DateTime.Now,
                NotificationType = type,
                RequestType = model.RequestType,
                Recipient = model.RequestedUser?.Email ?? string.Empty
            };
            await PublishAsync(notificationModel);
        }

        public async Task Notify(AlbumRequest model, NotificationType type)
        {
            var notificationModel = new NotificationOptions
            {
                RequestId = model.Id,
                DateTime = DateTime.Now,
                NotificationType = type,
                RequestType = model.RequestType,
                Recipient = model.RequestedUser?.Email ?? string.Empty
            };

            await PublishAsync(notificationModel);
        }

        public async Task Notify(NotificationOptions model)
        {
            await PublishAsync(model);
        }

        private async Task PublishAsync(NotificationOptions model)
        {
            foreach (var observer in _observers)
            {
                await observer.Handle(model);
            }
        }
    }
}
