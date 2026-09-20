using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using Moq.AutoMock;
using NUnit.Framework;
using Ombi.Core.Helpers;
using Ombi.Core.Models.Requests;
using Ombi.Helpers;
using Ombi.Notifications;
using Ombi.Notifications.Models;
using Ombi.Store.Entities;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Tests.Helpers
{
    [TestFixture]
    public class NotificationHelperTests
    {
        [Test]
        public async Task Notify_MovieRequest_CallsHandleOnEveryRegisteredObserver()
        {
            var observer1 = new Mock<IRequestEventObserver>();
            var observer2 = new Mock<IRequestEventObserver>();
            var mocker = new AutoMocker();
            mocker.Use<IEnumerable<IRequestEventObserver>>(new[] { observer1.Object, observer2.Object });
            var subject = mocker.CreateInstance<NotificationHelper>();

            var model = new MovieRequests
            {
                Id = 10,
                RequestType = RequestType.Movie,
                RequestedUser = new OmbiUser { Email = "user@test.local" }
            };

            await subject.Notify(model, NotificationType.RequestApproved);

            observer1.Verify(x => x.Handle(It.Is<NotificationOptions>(n =>
                n.RequestId == 10 &&
                n.NotificationType == NotificationType.RequestApproved &&
                n.RequestType == RequestType.Movie &&
                n.Recipient == "user@test.local"
            )), Times.Once);
            observer2.Verify(x => x.Handle(It.Is<NotificationOptions>(n =>
                n.RequestId == 10 &&
                n.NotificationType == NotificationType.RequestApproved
            )), Times.Once);
        }

        [Test]
        public void Notify_NoObserversRegistered_DoesNotThrow()
        {
            var mocker = new AutoMocker();
            mocker.Use<IEnumerable<IRequestEventObserver>>(new IRequestEventObserver[0]);
            var subject = mocker.CreateInstance<NotificationHelper>();

            Assert.DoesNotThrowAsync(async () =>
                await subject.Notify(new MovieRequests { Id = 1 }, NotificationType.RequestDeclined));
        }

        [Test]
        public async Task NewRequest_ChildRequest_CallsHandleOnEveryRegisteredObserver()
        {
            var observer = new Mock<IRequestEventObserver>();
            var mocker = new AutoMocker();
            mocker.Use<IEnumerable<IRequestEventObserver>>(new[] { observer.Object });
            var subject = mocker.CreateInstance<NotificationHelper>();

            var model = new ChildRequests { Id = 5, RequestType = RequestType.TvShow };

            await subject.NewRequest(model);

            observer.Verify(x => x.Handle(It.Is<NotificationOptions>(n =>
                n.RequestId == 5 &&
                n.NotificationType == NotificationType.NewRequest &&
                n.RequestType == RequestType.TvShow
            )), Times.Once);
        }

        [Test]
        public async Task Notify_NotificationOptionsOverload_PublishesModelUnchanged()
        {
            var observer = new Mock<IRequestEventObserver>();
            var mocker = new AutoMocker();
            mocker.Use<IEnumerable<IRequestEventObserver>>(new[] { observer.Object });
            var subject = mocker.CreateInstance<NotificationHelper>();

            var model = new NotificationOptions
            {
                RequestId = 0,
                NotificationType = NotificationType.RequestDeleted,
                RequestType = RequestType.Album,
                UserId = "u1"
            };

            await subject.Notify(model);

            observer.Verify(x => x.Handle(model), Times.Once);
        }
    }
}
