using System.Threading.Tasks;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Engine.Requests
{
    public interface IMovieRequestStatusService
    {
        Task<RequestEngineResult> Approve(MovieRequests request, bool is4K);
        Task<RequestEngineResult> ApproveById(int requestId, bool is4K);
        Task<RequestEngineResult> DenyById(int modelId, string denyReason, bool is4K);
        Task<RequestEngineResult> MarkAvailable(int modelId, bool is4K);
        Task<RequestEngineResult> MarkUnavailable(int modelId, bool is4K);
    }
}
