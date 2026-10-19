namespace WinAudioPlayer.Core;

public static class VideoCandidateSelector
{
    public static int FindBestIndex(double audioDurationSeconds, IReadOnlyList<double?> videoDurations)
    {
        if (videoDurations.Count == 0) return -1;
        if (!double.IsFinite(audioDurationSeconds) || audioDurationSeconds <= 0) return 0;

        double tolerance = Math.Max(30, audioDurationSeconds * 0.15);
        int closestIndex = 0;
        double closestDifference = double.MaxValue;
        for (int index = 0; index < videoDurations.Count; index++)
        {
            double? duration = videoDurations[index];
            if (!duration.HasValue || !double.IsFinite(duration.Value) || duration.Value <= 0) continue;
            double difference = Math.Abs(duration.Value - audioDurationSeconds);
            if (difference < closestDifference)
            {
                closestDifference = difference;
                closestIndex = index;
            }
            if (difference <= tolerance) return index;
        }
        return closestDifference < double.MaxValue ? closestIndex : 0;
    }
}
