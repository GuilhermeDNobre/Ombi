using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ombi.Core.Senders.Dvr
{
    public class TvDvrSenderFactory : ITvDvrSenderFactory
    {
        private readonly IEnumerable<ITvDvrSender> _senders;

        public TvDvrSenderFactory(IEnumerable<ITvDvrSender> senders)
        {
            _senders = senders;
        }

        public async Task<IReadOnlyList<ITvDvrSender>> GetEnabledSendersAsync()
        {
            var enabled = new List<ITvDvrSender>();
            foreach (var sender in _senders.OrderBy(x => x.Priority))
            {
                if (await sender.IsEnabledAsync())
                {
                    enabled.Add(sender);
                }
            }
            return enabled;
        }
    }
}
