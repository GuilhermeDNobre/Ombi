using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ombi.Core.Senders.Dvr
{
    public class MovieDvrSenderFactory : IMovieDvrSenderFactory
    {
        private readonly IEnumerable<IMovieDvrSender> _senders;

        public MovieDvrSenderFactory(IEnumerable<IMovieDvrSender> senders)
        {
            _senders = senders;
        }

        public async Task<IReadOnlyList<IMovieDvrSender>> GetEnabledSendersAsync(bool is4K)
        {
            var enabled = new List<IMovieDvrSender>();
            foreach (var sender in _senders.OrderBy(x => x.Priority))
            {
                if (await sender.IsEnabledAsync(is4K))
                {
                    enabled.Add(sender);
                }
            }
            return enabled;
        }
    }
}
