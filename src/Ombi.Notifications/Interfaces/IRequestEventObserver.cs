using System.Threading.Tasks;
using Ombi.Notifications.Models;

namespace Ombi.Notifications
{
    public interface IRequestEventObserver
    {
        Task Handle(NotificationOptions notification);
    }
}
