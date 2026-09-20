using Ombi.Api.External.Models;

namespace Ombi.Schedule.Jobs.MediaServer
{
    public class MediaServerMovie
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Quality { get; set; }
        public BaseProviderids ProviderIds { get; set; }
    }
}
