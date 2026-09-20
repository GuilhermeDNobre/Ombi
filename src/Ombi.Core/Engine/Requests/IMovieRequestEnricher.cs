using System.Collections.Generic;
using System.Threading.Tasks;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Engine.Requests
{
    public interface IMovieRequestEnricher
    {
        Task FillAdditionalFields(BaseMediaEngine.HideResult shouldHide, List<MovieRequests> requests);
    }
}
