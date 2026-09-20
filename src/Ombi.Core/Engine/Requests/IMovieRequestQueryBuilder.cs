using System.Linq;
using Ombi.Core.Models.Requests;
using Ombi.Core.Models.UI;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Engine.Requests
{
    public interface IMovieRequestQueryBuilder
    {
        IQueryable<MovieRequests> FilterByAvailability(IQueryable<MovieRequests> requests, FilterType filter);
        IQueryable<MovieRequests> FilterByStatus(IQueryable<MovieRequests> requests, FilterType filter);
        IQueryable<MovieRequests> FilterByRequestStatus(IQueryable<MovieRequests> requests, RequestStatus status);
        IQueryable<MovieRequests> Order(IQueryable<MovieRequests> requests, OrderType type);
        IQueryable<MovieRequests> Sort(IQueryable<MovieRequests> requests, string sortProperty, string sortOrder);
    }
}
