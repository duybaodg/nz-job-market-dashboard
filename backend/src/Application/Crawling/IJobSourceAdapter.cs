namespace Application.Crawling;

public interface IJobSourceAdapter
{
    string Method { get; }

    Task<SourceFetchResult> FetchJobsAsync(
        JobSearchRequest request,
        CancellationToken cancellationToken);
}
