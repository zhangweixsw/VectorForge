namespace VectorForge.Services;

public interface IIllustratorExporter
{
    Task<ExportResult> ExportAsync(ExportRequest request, TimeSpan timeout, CancellationToken cancellationToken);
}
