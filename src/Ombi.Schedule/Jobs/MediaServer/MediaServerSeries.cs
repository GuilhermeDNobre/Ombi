using Ombi.Api.External.Models;

namespace Ombi.Schedule.Jobs.MediaServer
{
    public class MediaServerSeries
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public BaseProviderids ProviderIds { get; set; }
    }
}
