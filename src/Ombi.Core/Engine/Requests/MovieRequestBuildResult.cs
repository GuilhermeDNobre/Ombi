using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Engine.Requests
{
    public class MovieRequestBuildResult
    {
        public RequestEngineResult Error { get; set; }
        public MovieRequests Request { get; set; }
        public string FullMovieName { get; set; }
        public bool IsExisting { get; set; }
        public bool Is4kRequest { get; set; }
    }
}
