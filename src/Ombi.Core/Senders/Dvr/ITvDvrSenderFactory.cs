using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ombi.Core.Senders.Dvr
{
    public interface ITvDvrSenderFactory
    {
        Task<IReadOnlyList<ITvDvrSender>> GetEnabledSendersAsync();
    }
}
