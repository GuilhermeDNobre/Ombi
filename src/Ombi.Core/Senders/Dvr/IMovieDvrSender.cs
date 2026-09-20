using System.Threading.Tasks;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Senders.Dvr
{
    public interface IMovieDvrSender
    {
        int Priority { get; }
        Task<bool> IsEnabledAsync(bool is4K);
        Task<SenderResult> Send(MovieRequests model, bool is4K);
    }
}
