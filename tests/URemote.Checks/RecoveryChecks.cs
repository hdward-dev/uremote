using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using URemote.Media;

internal static class RecoveryChecks
{
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); }
    public static async Task RunAsync(string ffmpeg)
    {
        Check(VideoFeedbackParser.RoundTripMilliseconds(100000, 90000, 3446) is { } rtt && Math.Abs(rtt - 100) < .02, "RTCP RTT subtracts receiver processing delay");
        Check(VideoFeedbackParser.RoundTripMilliseconds(1000, uint.MaxValue - 5553, 0) is { } wrapped && Math.Abs(wrapped - 100) < .02, "RTCP RTT handles compact timestamp wrap");
        Check(VideoFeedbackParser.RoundTripMilliseconds(1000, 0, 0) is null && VideoFeedbackParser.RoundTripMilliseconds(1000, 2000, 0) is null, "unavailable and invalid RTT are not reported as zero");
        byte[] nack = [0x81,205,0,4, 0,0,0,1, 0,0,0,7, 255,255,0,3, 0,10,0,1];
        Check(VideoFeedbackParser.Parse(nack).Select(x=>x.Sequence).SequenceEqual(new ushort[]{65535,0,1,10,11}), "all NACK blocks and sequence wrap");
        Check(VideoFeedbackParser.Parse(nack[..^1]).Count==0, "truncated feedback rejected");
        byte[] pli=[0x81,206,0,2,0,0,0,1,0,0,0,7];
        byte[] fir=[0x84,206,0,4,0,0,0,1,0,0,0,0,0,0,0,7,1,0,0,0];
        Check(VideoFeedbackParser.Parse([..pli,..fir]).All(x=>x.KeyFrame&&x.Ssrc==7)
            &&VideoFeedbackParser.Parse([..pli,..fir]).Count==2, "compound PLI and FIR target their correct SSRC");
        byte[] wire=[0x80,98,0,5,0,0,0,9,0,0,0,7,1,2,3,4];
        using(var cache=new ProtectedVideoCache(2,64))
        {
            cache.Store(wire,1000);
            Check(cache.Take(8,5,1001)==null,"foreign SSRC cannot retrieve packet");
            Check(cache.Take(7,5,1001)!.SequenceEqual(wire),"cached protected packet is byte exact");
            Check(cache.Take(7,5,1010)==null,"duplicate requests are throttled");
            Check(cache.Take(7,5,1100)!=null&&cache.Take(7,5,1200)!=null&&cache.Take(7,5,1300)==null,"retry count bounded");
            wire[3]=6;cache.Store(wire,1300);wire[3]=7;cache.Store(wire,1400);
            Check(cache.Take(7,5,1401)==null,"cache capacity evicts oldest packet");
            Check(cache.Take(7,7,3001)==null,"expired packet cannot be resent");
            cache.Dispose();cache.Store(wire,4000);Check(cache.Take(7,7,4001)==null,"closed cache cannot revive");
        }
        var info=new ProcessStartInfo(ffmpeg){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
        foreach(var a in new[]{"-hide_banner","-loglevel","error","-f","lavfi","-i","testsrc2=size=640x360:rate=30","-frames:v","90","-an","-c:v","libx264","-preset","ultrafast","-tune","zerolatency","-profile:v","baseline","-x264-params","aud=1:repeat-headers=1:keyint=30","-f","h264","pipe:1"})info.ArgumentList.Add(a);
        using var process=Process.Start(info)!;using var output=new MemoryStream();var errors=process.StandardError.ReadToEndAsync();
        await process.StandardOutput.BaseStream.CopyToAsync(output);await process.WaitForExitAsync();Check(process.ExitCode==0,"synthetic encoder runs");await errors;
        var parser=new AnnexBAccessUnits();var frames=parser.Push(output.ToArray());if(parser.Finish()is{}tail)frames.Add(tail);
        Check(frames.Count==90,"synthetic stream has 90 frames");
        foreach(var loss in new[]{0,1,3,5})await LoopbackAsync(frames,loss,ffmpeg);
    }
    private static async Task LoopbackAsync(List<byte[]> input,int loss,string ffmpeg)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var receiver=new RTCPeerConnection(new RTCConfiguration());using var sender=new HostMediaPeer(activeVideoStreams:1);
        var received=new ConcurrentQueue<byte[]>();var dropped=new ConcurrentDictionary<ushort,byte[]>();var repaired=new ConcurrentDictionary<ushort,bool>();
        var requests=System.Threading.Channels.Channel.CreateUnbounded<(uint Ssrc,ushort Seq)>();
        var feedbackWorker=Task.Run(async()=>{await foreach(var request in requests.Reader.ReadAllAsync(deadline.Token))
        { await Task.Delay(15,deadline.Token);receiver.SendRtcpFeedback(SDPMediaTypesEnum.video,new RTCPFeedback(1,request.Ssrc,RTCPFeedbackTypesEnum.NACK,request.Seq,0)); }});
        int ordinal=0, mismatches=0;uint mediaSsrc=0;
        receiver.addTrack(new MediaStreamTrack(new List<AudioFormat>{new(AudioCodecsEnum.OPUS,111,48000,2,"minptime=10;useinbandfec=1")},MediaStreamStatusEnum.RecvOnly));
        for(var track=0;track<5;track++) receiver.addTrack(new MediaStreamTrack(new List<VideoFormat>{new(VideoCodecsEnum.H264,98,90000,"level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e033")},MediaStreamStatusEnum.RecvOnly));
        receiver.VideoStreamList[0].AddBuffer(TimeSpan.FromMilliseconds(400));
        receiver.OnVideoFrameReceived+=(_,_,bytes,_)=>received.Enqueue(bytes);
        receiver.onicecandidate+=c=>sender.AddRemoteCandidate(JsonNode.Parse(c.toJSON())!.AsObject());
        sender.LocalCandidate+=c=>receiver.addIceCandidate(new RTCIceCandidateInit{candidate=c["candidate"]!.GetValue<string>(),sdpMid=c["sdpMid"]?.GetValue<string>(),sdpMLineIndex=(ushort)(c["sdpMLineIndex"]?.GetValue<int>()??0)});
        var offer=receiver.createOffer();await receiver.setLocalDescription(offer);
        var answer=await sender.AnswerAsync(offer.sdp);receiver.setRemoteDescription(new RTCSessionDescriptionInit{type=RTCSdpType.answer,sdp=answer});
        while(sender.State!=RTCPeerConnectionState.connected||receiver.connectionState!=RTCPeerConnectionState.connected)await Task.Delay(20,deadline.Token);
        var stream=receiver.VideoStreamList[0];var secure=stream.GetSecurityContext();
        stream.SetSecurityContext(secure.ProtectRtpPacket,(byte[] bytes,int length,out int written)=>
        {
            var seq=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2));var ssrc=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8));mediaSsrc=ssrc;
            if(dropped.TryGetValue(seq,out var original))
            {
                if(!bytes.AsSpan(0,length).SequenceEqual(original))Interlocked.Increment(ref mismatches);
                var rc=secure.UnprotectRtpPacket(bytes,length,out written);if(rc==0)repaired[seq]=true;return rc;
            }
            var number=Interlocked.Increment(ref ordinal);
            if(number>20&&number%100<loss)
            {
                dropped[seq]=bytes.AsSpan(0,length).ToArray();written=0;
                requests.Writer.TryWrite((ssrc,seq));
                return -1;
            }
            return secure.UnprotectRtpPacket(bytes,length,out written);
        },secure.ProtectRtcpPacket,secure.UnprotectRtcpPacket);
        var sent = 0; var forcedRestarts = 0;
        foreach(var frame in input)
        {
            await sender.SendH264PacedAsync(frame,3000,0,4000000,deadline.Token);
            if (++sent % 5 == 0) receiver.SendRtcpFeedback(SDPMediaTypesEnum.video,new RTCPFeedback(1,mediaSsrc,PSFBFeedbackTypesEnum.PLI));
            await Task.Delay(33,deadline.Token);
            if(sender.ConsumeKeyFrameRequest(0)) forcedRestarts++;
        }
        Check(forcedRestarts==0,"continuous PLI does not restart a healthy video encoder");
        requests.Writer.TryComplete();await feedbackWorker;await Task.Delay(1000,deadline.Token);
        Check(mismatches==0,$"{loss}% loss: retransmissions preserve original ciphertext");
        Check(dropped.Count==repaired.Count,$"{loss}% loss: all {dropped.Count} dropped packets authenticated after retransmission");
        Check(received.Count==input.Count,$"{loss}% loss: all {received.Count} frames reconstructed");
        // Last synthetic IDR is already older than two seconds after draining the stream.
        await Task.Delay(1100,deadline.Token);
        receiver.SendRtcpFeedback(SDPMediaTypesEnum.video,new RTCPFeedback(1,mediaSsrc,PSFBFeedbackTypesEnum.PLI));
        await Task.Delay(100,deadline.Token);Check(sender.ConsumeKeyFrameRequest(0),"authenticated PLI requests video refresh");
        Check(!sender.ConsumeKeyFrameRequest(0),"keyframe request consumed only once");
        var start=new ProcessStartInfo(ffmpeg){RedirectStandardInput=true,RedirectStandardError=true,UseShellExecute=false};
        foreach(var a in new[]{"-hide_banner","-loglevel","error","-f","h264","-i","pipe:0","-f","null","-"})start.ArgumentList.Add(a);
        using var decoder=Process.Start(start)!;var error=decoder.StandardError.ReadToEndAsync();
        foreach(var frame in received)await decoder.StandardInput.BaseStream.WriteAsync(frame,deadline.Token);
        decoder.StandardInput.Close();await decoder.WaitForExitAsync(deadline.Token);
        Check(decoder.ExitCode==0&&(await error).Length==0,$"{loss}% loss: recovered stream decodes without errors");
    }
}
