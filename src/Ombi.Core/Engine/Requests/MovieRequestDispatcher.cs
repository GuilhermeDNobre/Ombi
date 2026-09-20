using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ombi.Store.Entities.Requests;

namespace Ombi.Core.Engine.Requests
{
    public class MovieRequestDispatcher : IMovieRequestDispatcher
    {
        public MovieRequestDispatcher(IMovieSender sender, ILogger<MovieRequestEngine> log)
        {
            _sender = sender;
            _logger = log;
        }

        private readonly IMovieSender _sender;
        private readonly ILogger<MovieRequestEngine> _logger;

        public async Task<RequestEngineResult> Send(MovieRequests request, bool is4K)
        {
            if (is4K ? request.Approved4K : request.Approved)
            {
                var result = await _sender.Send(request, is4K);
                if (result.Success && result.Sent)
                {
                    return new RequestEngineResult
                    {
                        Result = true
                    };
                }

                if (!result.Success)
                {
                    _logger.LogWarning("Tried auto sending movie but failed. Message: {0}", result.Message);
                    return new RequestEngineResult
                    {
                        Message = result.Message,
                        ErrorMessage = result.Message,
                        Result = false
                    };
                }

                // If there are no providers then it's successful but movie has not been sent
            }

            return new RequestEngineResult
            {
                Result = true
            };
        }
    }
}
