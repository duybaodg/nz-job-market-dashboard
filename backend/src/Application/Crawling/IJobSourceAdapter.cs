namespace Application.Crawling;

public interface IJobSourceAdapter
{
    string SourceName { get; }

    Task<SourceFetchResult> FetchJobsAsync(
        JobSearchRequest request,
        CancellationToken cancellationToken);
}
