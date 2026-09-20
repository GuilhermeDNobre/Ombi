using System;
using System.Linq;
using Ombi.Core.Models.Requests;
using Ombi.Core.Models.UI;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Engine.Requests
{
    public class MovieRequestQueryBuilder : IMovieRequestQueryBuilder
    {
        public IQueryable<MovieRequests> FilterByAvailability(IQueryable<MovieRequests> requests, FilterType filter)
        {
            switch (filter)
            {
                case FilterType.None:
                    return requests;
                case FilterType.Available:
                    return requests.Where(x => x.Available);
                case FilterType.NotAvailable:
                    return requests.Where(x => !x.Available);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public IQueryable<MovieRequests> FilterByStatus(IQueryable<MovieRequests> requests, FilterType filter)
        {
            switch (filter)
            {
                case FilterType.None:
                    return requests;
                case FilterType.Approved:
                    return requests.Where(x => x.Approved);
                case FilterType.Processing:
                    return requests.Where(x => x.Approved && !x.Available);
                case FilterType.PendingApproval:
                    return requests.Where(x => !x.Approved && !x.Available && !(x.Denied ?? false));
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public IQueryable<MovieRequests> FilterByRequestStatus(IQueryable<MovieRequests> requests, RequestStatus status)
        {
            switch (status)
            {
                case RequestStatus.PendingApproval:
                    return requests.Where(x =>
                            (x.RequestedDate != DateTime.MinValue && !x.Approved && !x.Available && (!x.Denied.HasValue || !x.Denied.Value))
                            ||
                            (x.Has4KRequest && !x.Approved4K && !x.Available4K && (!x.Denied4K.HasValue || !x.Denied4K.Value))
                       );
                case RequestStatus.ProcessingRequest:
                    return requests.Where(x =>
                            (x.RequestedDate != DateTime.MinValue && x.Approved && !x.Available && (!x.Denied.HasValue || !x.Denied.Value))
                            ||
                            (x.Has4KRequest && x.Approved4K && !x.Available4K && (!x.Denied4K.HasValue || !x.Denied4K.Value))
                        );
                case RequestStatus.Available:
                    return requests.Where(x => x.Available || x.Available4K);
                case RequestStatus.Denied:
                    return requests.Where(x =>
                            (x.Denied.HasValue && x.Denied.Value && !x.Available)
                            ||
                            (x.Has4KRequest && x.Denied4K.HasValue && x.Denied4K.Value && !x.Available4K)
                        );
                default:
                    return requests;
            }
        }

        public IQueryable<MovieRequests> Order(IQueryable<MovieRequests> requests, OrderType type)
        {
            switch (type)
            {
                case OrderType.RequestedDateAsc:
                    return requests.OrderBy(x => x.RequestedDate);
                case OrderType.RequestedDateDesc:
                    return requests.OrderByDescending(x => x.RequestedDate);
                case OrderType.TitleAsc:
                    return requests.OrderBy(x => x.Title);
                case OrderType.TitleDesc:
                    return requests.OrderByDescending(x => x.Title);
                case OrderType.StatusAsc:
                    return requests.OrderBy(x => x.Status);
                case OrderType.StatusDesc:
                    return requests.OrderByDescending(x => x.Status);
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        public IQueryable<MovieRequests> Sort(IQueryable<MovieRequests> requests, string sortProperty, string sortOrder)
        {
            var asc = sortOrder.Equals("asc", StringComparison.InvariantCultureIgnoreCase);
            return sortProperty.ToLowerInvariant() switch
            {
                "id" => asc ? requests.OrderBy(x => x.Id) : requests.OrderByDescending(x => x.Id),
                "title" => asc ? requests.OrderBy(x => x.Title) : requests.OrderByDescending(x => x.Title),
                "releasedate" => asc ? requests.OrderBy(x => x.ReleaseDate) : requests.OrderByDescending(x => x.ReleaseDate),
                _ => asc ? requests.OrderBy(x => x.RequestedDate) : requests.OrderByDescending(x => x.RequestedDate)
            };
        }
    }
}
