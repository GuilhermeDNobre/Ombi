using System.Collections.Generic;

namespace Ombi.Schedule.Jobs.MediaServer
{
    public class MediaServerInstance
    {
        public string Name { get; set; }
        public string Ip { get; set; }
        public string ApiKey { get; set; }
        public string ServerId { get; set; }
        public string ServerHostname { get; set; }
        public string AdministratorId { get; set; }
        public string FullUri { get; set; }
        public IReadOnlyList<MediaServerLibrary> SelectedLibraries { get; set; } = new List<MediaServerLibrary>();
    }
}
