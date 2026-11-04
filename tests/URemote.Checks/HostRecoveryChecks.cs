using URemote.Core;
using URemote.Linux;

internal static class HostRecoveryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        DesktopOutput[] first = [new(11,"wayland:DP-1","DP-1"),new(22,"wayland:DP-2","DP-2")];
        check(DesktopOutputSelection.Select(first,["wayland:DP-2"]).SequenceEqual(new uint[]{22}),"explicit selection targets the chosen physical output");
        DesktopOutput[] changed = [new(99,"wayland:DP-2","DP-2"),new(100,"wayland:DP-3","DP-3")];
        check(DesktopOutputSelection.Select(changed,["wayland:DP-1"]).Length==0,"unplugged first output never selects another monitor at ordinal zero");
        check(DesktopOutputSelection.Select(changed,["wayland:DP-2"]).SequenceEqual(new uint[]{99}),"selected second monitor survives reordered and replaced protocol IDs");
        check(DesktopOutputSelection.Select([new(1,"duplicate","A"),new(2,"duplicate","B")],["duplicate"]).Length==0,"ambiguous identities fail closed instead of sharing the wrong screen");
        check(DesktopOutputSelection.Select(first,[]).Length==0,"empty saved selection never becomes all displays");

        long clock=0;
        var profiles = new HostVideoSettings([(1920,1080)],clock:()=>clock);
        profiles.ObserveReceiver(1000,0,30);
        for(var n=2;n<20;n++) { clock+=50; profiles.ObserveReceiver(n*1000,0,30); }
        check(profiles.Get(0) is {Bitrate:8000000,Recovery:0},"rapid duplicate decode statistics never collapse bitrate or restart the encoder");
        clock=3100;
        check(profiles.ObserveReceiver(20000,0,30) && profiles.Get(0) is {Bitrate:8000000,Recovery:1},"sustained decoder stall requests an IDR without treating it as network congestion");
        clock=4000;
        check(!profiles.ObserveReceiver(21000,0,30),"stall recovery is rate limited");
        clock=9000;
        check(!profiles.ObserveReceiver(21000,0,30),"repeated unchanged packet counters do not request another IDR");
        check(profiles.ObserveReceiver(22000,0,30) && profiles.Get(0).Recovery==2,"a continuing genuine stall may retry recovery after the cooldown");
        clock=9100;
        profiles.ObserveReceiver(23000,0,40);
        clock=9200;
        check(!profiles.ObserveReceiver(24000,0,40),"decoded progress resets the stall timer");

        var delayed=new HostVideoSettings([(1920,1080)],clock:()=>clock);
        delayed.ObserveReceiver(1000,0,30);clock+=5000;
        check(!delayed.ObserveReceiver(2000,0,30),"one delayed report cannot restart the encoder even after a long report interval");
        clock+=5000;
        check(delayed.ObserveReceiver(3000,0,30) && delayed.Get(0).Bitrate==8000000,"consecutive stalled reports can request recovery while retaining bitrate");

        var congested = new HostVideoSettings([(1920,1080)],clock:()=>clock);
        check(congested.ObserveReceiver(1000,100,10) && congested.Get(0).Bitrate==4000000,"measured loss still lowers bitrate");
        for(var i=2;i<=20;i++) { clock+=50; congested.ObserveReceiver(i*1000,i*100,i*10); }
        check(congested.Get(0).Bitrate==4000000,"many feedback messages cannot repeatedly halve bitrate inside one cooldown");
        clock+=5000;
        check(congested.ObserveReceiver(21000,2100,210) && congested.Get(0).Bitrate==2000000,"sustained congestion is reevaluated after the cooldown");
        var floor=congested.Get(0).Bitrate;
        congested.ObserveReceiver(22000,2100,220);congested.ObserveReceiver(23000,2100,230);
        check(congested.Get(0).Bitrate>floor,"healthy advancing feedback restores the requested quality budget");
    }
}
