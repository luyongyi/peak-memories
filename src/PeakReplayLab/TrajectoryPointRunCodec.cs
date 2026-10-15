using System;
using System.Collections.Generic;
using System.Threading;

namespace PeakReplayLab;

// Changes only the upload representation. Builder samples/events and replay
// seeking remain untouched; each run reconstructs every original sample time.
internal static class TrajectoryPointRunCodec
{
    public static TrajectoryPackage Encode(TrajectoryPackage source, CancellationToken cancellation = default)
    {
        var players = new List<TrajectoryPlayer>(source.Players.Count);
        foreach (var player in source.Players)
        {
            cancellation.ThrowIfCancellationRequested();
            players.Add(new TrajectoryPlayer { Key = player.Key, Name = player.Name, Owner = player.Owner,
                Evidence = player.Evidence, Points = EncodePoints(player.Points, cancellation), Events = player.Events });
        }
        return new TrajectoryPackage { Format = "trajectory-v2", RecordingId = source.RecordingId,
            RunKey = source.RunKey, TimeOriginMs = source.TimeOriginMs, StartedUtc = source.StartedUtc,
            DurationMs = source.DurationMs, SampleHz = source.SampleHz, CoordinateUnit = source.CoordinateUnit,
            Map = source.Map, Difficulty = source.Difficulty, Players = players };
    }

    public static List<int[]> EncodePoints(IReadOnlyList<int[]> points, CancellationToken cancellation = default)
    {
        var result = new List<int[]>();
        for (int index = 0; index < points.Count;)
        {
            cancellation.ThrowIfCancellationRequested();
            var first = points[index];
            int count = 1, step = 0;
            if (index + 1 < points.Count && SamePosition(first, points[index + 1]))
            {
                step = points[index + 1][0] - first[0];
                if (step > 0)
                {
                    count = 2;
                    while (index + count < points.Count && SamePosition(first, points[index + count])
                        && points[index + count][0] - points[index + count - 1][0] == step)
                    { cancellation.ThrowIfCancellationRequested(); count++; }
                }
            }
            if (count >= 3)
            { result.Add(new[] { first[0], first[1], first[2], first[3], step, count }); index += count; }
            else { result.Add(first); index++; }
        }
        return result;
    }

    private static bool SamePosition(int[] left, int[] right)
        => left[1] == right[1] && left[2] == right[2] && left[3] == right[3];
}
