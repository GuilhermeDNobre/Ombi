using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.AutoMock;
using NUnit.Framework;
using Ombi.Core.Notifications;
using Ombi.Helpers;
using Quartz;
using System.Threading;
using System.Threading.Tasks;
using Ombi.Notifications.Models;

namespace Ombi.Notifications.Tests
{
    [TestFixture]
    public class NotificationServiceHandleTests
    {
        private Mock<IScheduler> _scheduler;
        private NotificationService _subject;

        [SetUp]
        public void Setup()
        {
            _scheduler = new Mock<IScheduler>();
            _ = new QuartzMock(_scheduler);

            var mocker = new AutoMocker();
            mocker.Use(NullLogger<NotificationService>.Instance);
            _subject = mocker.CreateInstance<NotificationService>();
        }

        [Test]
        public async Task Handle_TriggersTheNotificationServiceJob_WithTheNotificationOptions()
        {
            var model = new NotificationOptions
            {
                RequestId = 42,
                NotificationType = NotificationType.RequestApproved
            };

            await _subject.Handle(model);

            _scheduler.Verify(x => x.TriggerJob(
                It.Is<JobKey>(j => j.Name == nameof(INotificationService) && j.Group == "Notifications"),
                It.Is<JobDataMap>(d => (NotificationOptions)d[JobDataKeys.NotificationOptions] == model),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    public class QuartzMock : OmbiQuartz
    {
        public QuartzMock(Mock<IScheduler> mock) : base(false)
        {
            _instance = this;
            _scheduler = mock.Object;
        }
    }
}
