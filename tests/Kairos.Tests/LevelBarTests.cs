using System.Windows.Threading;
using Kairos.App;
using Kairos.Engine;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>
/// The Audio page's meters draw every display frame only while their level moves: once it has settled (silence, or a
/// steady sound) they rest, and a new level wakes them at once. What they show is unchanged.
/// </summary>
public static class LevelBarTests
{
    static void OnSta(Action test)
    {
        string? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception e)
            {
                error = e.ToString();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.True(error == null, error ?? "");
    }

    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    static double Settle(LevelBar bar, ref double ms)
    {
        for (int i = 0; i < 1000 && bar.Rendering; i++)
            bar.Frame(TimeSpan.FromMilliseconds(ms += 16));
        return bar.BarDb;
    }

    [Test]
    public static void A_meter_rests_while_its_level_stays_and_wakes_when_it_moves() => OnSta(() =>
    {
        var stream = new LevelStream(connect: false);
        LevelBar.StreamFactory = () => stream;
        try
        {
            var bar = new LevelBar { Channel = "system" };
            bar.Attach();
            Assert.True(bar.Rendering, "drawing when it comes on screen");
            // Frame times after the real ones the dispatcher may have drawn while pumping.
            double ms = 1e7;
            bar.Frame(TimeSpan.FromMilliseconds(ms));
            Assert.True(!bar.Rendering, "silence that stays: no more frames");

            stream.Push([("system", -12), ("mic", LevelMeter.Silent)]);
            Pump();
            Assert.Near(-12, bar.BarDb, 1e-9, "a new level wakes it: the bar jumps to it (a rise is instant)");
            Assert.Near(-12, bar.HoldDb, 1e-9, "its peak mark is on the level");
            Assert.True(!bar.Rendering, "a steady level: resting again");

            stream.Push([("system", LevelMeter.Silent), ("mic", -20)]);
            Pump();
            Assert.True(bar.Rendering, "the sound stopped: it falls back, frame by frame");
            Assert.Near(LevelMeter.Silent, Settle(bar, ref ms), 1e-9, "back to silence, then rests");
            Assert.True(ms > 1e7 + 1000, "it fell at its own pace (the peak mark holds a second first)");
            bar.Detach();
            Assert.True(!bar.Rendering, "off screen: nothing");
        }
        finally
        {
            LevelBar.StreamFactory = () => new LevelStream();
        }
    });

    [Test]
    public static void A_resting_meter_wakes_from_the_streams_own_thread() => OnSta(() =>
    {
        var stream = new LevelStream(connect: false);
        LevelBar.StreamFactory = () => stream;
        try
        {
            var bar = new LevelBar { Channel = "mic" };
            bar.Attach();
            double ms = 1e7;
            bar.Frame(TimeSpan.FromMilliseconds(ms));
            Assert.True(!bar.Rendering, "resting on silence");
            // The levels arrive on the stream's reader thread, never the window's (as from the engine's pipe).
            string? error = null;
            var reader = new Thread(() =>
            {
                try
                {
                    stream.Push([("mic", -20)]);
                }
                catch (Exception e)
                {
                    error = e.GetType().Name + ": " + e.Message;
                }
            });
            reader.Start();
            reader.Join();
            Assert.Equal(null, error, "a level from another thread does not touch the meter's own properties");
            Pump();
            Assert.Near(-20, bar.BarDb, 1e-9, "and it wakes the meter on its own thread");
            bar.Detach();
        }
        finally
        {
            LevelBar.StreamFactory = () => new LevelStream();
        }
    });
}
