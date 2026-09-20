using System.Threading.Tasks;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Engine.Requests
{
    public interface IMovieRequestDispatcher
    {
        Task<RequestEngineResult> Send(MovieRequests request, bool is4K);
    }
}
