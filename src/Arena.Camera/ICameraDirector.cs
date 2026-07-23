namespace OpenTtd.ModelArena.Camera;

public interface ICameraDirector
{
    Task PublishAsync(CameraEvent cameraEvent, CancellationToken cancellationToken);
}

public sealed record CameraEvent(
    string RunId,
    string CorrelationId,
    string EventType,
    int Importance,
    int TileX,
    int TileY,
    TimeSpan SuggestedDuration);
