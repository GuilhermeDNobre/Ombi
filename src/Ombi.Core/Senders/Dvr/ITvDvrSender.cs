using System.Threading.Tasks;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Senders.Dvr
{
    public interface ITvDvrSender
    {
        int Priority { get; }
        Task<bool> IsEnabledAsync();
        Task<SenderResult> Send(ChildRequests model);
    }
}
