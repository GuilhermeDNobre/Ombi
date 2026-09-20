using System.Threading.Tasks;
using Ombi.Core.Models.Requests;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Engine.Requests
{
    public interface IMovieRequestFactory
    {
        Task<MovieRequestBuildResult> Build(MovieRequestViewModel model);
        Task<RequestEngineResult> Add(MovieRequests model, string movieName, string requestOnBehalf, bool isExisting, bool is4k);
    }
}
