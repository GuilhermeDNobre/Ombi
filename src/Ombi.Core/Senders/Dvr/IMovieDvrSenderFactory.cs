using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ombi.Core.Senders.Dvr
{
    public interface IMovieDvrSenderFactory
    {
        Task<IReadOnlyList<IMovieDvrSender>> GetEnabledSendersAsync(bool is4K);
    }
}
