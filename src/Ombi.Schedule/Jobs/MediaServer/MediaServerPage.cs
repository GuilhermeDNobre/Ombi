using System.Collections.Generic;

namespace Ombi.Schedule.Jobs.MediaServer
{
    public class MediaServerPage<T>
    {
        public List<T> Items { get; set; }
        public int TotalRecordCount { get; set; }
    }
}
