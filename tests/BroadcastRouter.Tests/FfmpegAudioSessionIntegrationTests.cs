using System.Diagnostics;
using System.IO.Pipes;
using System.Text.RegularExpressions;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class FfmpegAudioSessionIntegrationTests
{
    internal static readonly MediaProperties AudioOnly = new(null, "aac", null, null, null, null, 48000, 2, false);

    [Fact]
    public async Task RealAudioIsMeasuredBeforeTimestampFillingAndResampling()
    {
        await using var session = new FfmpegMediaSession(AudioOnly, true, new(new ManualTimeProvider()));
        var start = Command(AudioOnly);
        await session.AttachToAsync(start);
        var args = start.ArgumentList.ToArray();
        var filter = args[Array.IndexOf(args, "-af") + 1];
        Assert.StartsWith("astats=metadata=1:measure_perchannel=none:measure_overall=Number_of_samples,ametadata=", filter);
        Assert.EndsWith(":direct=1,aresample=48000:async=1:first_pts=0", filter);
        Assert.DoesNotContain("-stats_enc_pre:a:0", args);
        Assert.Equal(2, Endpoints(args).Length);
    }

    [Fact]
    public async Task AudioOnlySessionRecoversAbsentPcmDespiteFreshSyntheticVideoHeartbeat()
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        await using var session = new FfmpegMediaSession(AudioOnly, true, clock);
        var start = Command(AudioOnly);
        await session.AttachToAsync(start);
        var args = start.ArgumentList.ToArray();
        var filter = args[Array.IndexOf(args,"-af")+1];
        var match = Regex.Match(filter, "file='([^']+)'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.True(match.Success, "Decoded PCM observer is missing before the resampler.");
        var inputEndpoint = match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
        var outputEndpoint = args[Array.IndexOf(args,"-stats_mux_pre:a:0")+1];
        await using (var inspection = await Connect(inputEndpoint)) { }
        await using var input = await Connect(inputEndpoint);
        await using var output = await Connect(outputEndpoint);
        for (var i=0; i<=4; i++)
        {
            if (i>0) time.Advance(TimeSpan.FromMilliseconds(500));
            await input.WriteLineAsync($"frame:{i} pts:{i*1024} pts_time:{(i*1024/48000m).ToString(System.Globalization.CultureInfo.InvariantCulture)}\nlavfi.astats.Overall.Number_of_samples={(i+1)*1024}.000000");
            await output.WriteLineAsync($"brmedia|mux|a|{i}");
            await FaultHarnessTests.Eventually(() => session.Snapshot(true).Signals.All(s=>s.LastAdvancedAt==clock.Now));
        }
        Assert.All(session.Snapshot(true).Signals,s=>Assert.Equal(MediaSignalState.Healthy,s.State));
        time.Advance(TimeSpan.FromSeconds(23));
        session.ObserveHeartbeat();
        var stalled=session.Snapshot(true);
        Assert.Equal(MediaSignalState.Healthy,stalled.Process);
        Assert.Equal(MediaSignalState.Healthy,stalled.Heartbeat);
        Assert.Equal(new[] { MediaSignalKind.InputAudio, MediaSignalKind.OutputAudio }, stalled.Signals.Select(s=>s.Kind));
        Assert.All(stalled.Signals,s=>Assert.Equal(MediaSignalState.Failed,s.State));
        Assert.True(stalled.MayRecover);
    }

    [Fact]
    public async Task GeneratedSilenceDoesNotGetAnInputPcmObserver()
    {
        var videoOnly=new MediaProperties("h264",null,1920,1080,25,null,null,null,true);
        await using var session=new FfmpegMediaSession(videoOnly,true,new(new ManualTimeProvider()));
        var start=Command(videoOnly);
        await session.AttachToAsync(start);
        Assert.DoesNotContain(start.ArgumentList,arg=>arg.Contains("astats=",StringComparison.Ordinal));
        Assert.DoesNotContain(session.Snapshot(true).Signals,s=>s.Kind==MediaSignalKind.InputAudio);
    }

    internal static ProcessStartInfo Command(MediaProperties media) => FfmpegCommandBuilder.Build(new("synthetic.exe"),
        new(new("TEST","fixture","_default_","audio"),"Synthetic",new Uri("rtsp://example.invalid/fixture"),SourceState.Ready,0,media),
        new("TEST-OUTPUT","TEST-DEVICE","Synthetic",0,0,null,[]),new("test-mode","Test mode",new(1920,1080,25,1,"uyvy422"),false,0));

    internal static string[] Endpoints(IEnumerable<string> arguments)
    {
        var args=arguments.ToArray();
        var endpoints=args.Where(arg=>arg.StartsWith(@"\\.\pipe\broadcast-router-media-",StringComparison.Ordinal)).ToList();
        foreach (var arg in args.Where(arg=>arg.StartsWith("astats=",StringComparison.Ordinal)))
        {
            var match=Regex.Match(arg,"file='([^']+)'",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
            if (match.Success) endpoints.Add(match.Groups[1].Value.Replace("\\\\","\\",StringComparison.Ordinal));
        }
        return endpoints.ToArray();
    }

    private static async Task<StreamWriter> Connect(string endpoint)
    {
        var pipe=new NamedPipeClientStream(".",endpoint[9..],PipeDirection.Out,PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(3)); return new(pipe) { AutoFlush=true }; }
        catch { pipe.Dispose(); throw; }
    }
}
