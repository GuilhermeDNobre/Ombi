using Ombi.Api.External.ExternalApis.Sonarr.Models;
using System.Collections.Generic;

namespace Ombi.Core.Senders.Dvr
{
    internal class SonarrSendOptions
    {
        public List<int> Tags { get; set; } = new List<int>();
    }
}
