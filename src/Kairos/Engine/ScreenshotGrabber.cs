using System.Runtime.InteropServices;
using Kairos.Native;

namespace Kairos.Engine;

/// <summary>
/// Copies the canvas (what the clips show) off the GPU, as OBS Studio's own screenshot does: one frame renders it into
/// a texture and stages it, the next maps the copy. It runs on libobs's graphics thread through a tick callback, so the
/// caller gets a task and never waits on the GPU, and the game loses no more than one small copy.
/// </summary>
public static class ScreenshotGrabber
{
    static readonly object Gate = new();
    static readonly ObsTickCallback Tick = OnTick; // held here for as long as libobs may call it
    static Job? _job;

    sealed class Job(int width, int height, int canvasWidth, int canvasHeight)
    {
        public readonly int Width = width, Height = height, CanvasWidth = canvasWidth, CanvasHeight = canvasHeight;
        public readonly TaskCompletionSource<ScreenshotFrame?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IntPtr TexRender, Stage;
        public int Ticks;
    }

    /// <summary>A grab is under way (another press now would be ignored).</summary>
    public static bool Busy
    {
        get
        {
            lock (Gate)
                return _job != null;
        }
    }

    /// <summary>
    /// The next frame of the canvas at <paramref name="width"/> × <paramref name="height"/>, or null when libobs is not
    /// running, another grab is under way, or it took longer than <paramref name="timeout"/>. With the canvas's own size
    /// (<paramref name="canvasWidth"/> × <paramref name="canvasHeight"/>) the whole canvas is scaled down into it.
    /// </summary>
    public static Task<ScreenshotFrame?> Grab(int width, int height, TimeSpan timeout, int canvasWidth = 0, int canvasHeight = 0)
    {
        if (!ObsHost.Started || width <= 0 || height <= 0)
            return Task.FromResult<ScreenshotFrame?>(null);
        Job job;
        lock (Gate)
        {
            if (_job != null)
                return Task.FromResult<ScreenshotFrame?>(null);
            _job = job = new Job(width, height, canvasWidth > 0 ? canvasWidth : width, canvasHeight > 0 ? canvasHeight : height);
        }
        Obs.obs_add_tick_callback(Tick, IntPtr.Zero);
        _ = GiveUpAfter(job, timeout);
        return job.Done.Task;
    }

    static async Task GiveUpAfter(Job job, TimeSpan timeout)
    {
        if (await Task.WhenAny(job.Done.Task, Task.Delay(timeout)) == job.Done.Task)
            return;
        ObsLog.Warn("screenshot: no frame in time");
        if (ObsHost.Started)
            Obs.obs_remove_tick_callback(Tick, IntPtr.Zero); // waits for a tick in progress to end
        Finish(job, null);
    }

    static void OnTick(IntPtr param, float seconds)
    {
        Job? job;
        lock (Gate)
            job = _job;
        if (job == null)
            return;
        ScreenshotFrame? frame = null;
        bool finished;
        Obs.obs_enter_graphics();
        try
        {
            if (job.Ticks++ == 0)
                finished = !Render(job);
            else
            {
                frame = Read(job);
                finished = true;
            }
        }
        catch (Exception e)
        {
            ObsLog.Warn($"screenshot: {e.Message}");
            finished = true;
        }
        finally
        {
            Obs.obs_leave_graphics();
        }
        if (finished)
        {
            Obs.obs_remove_tick_callback(Tick, IntPtr.Zero);
            Finish(job, frame);
        }
    }

    /// <summary>Draws the canvas into a texture of its size and starts the copy to memory.</summary>
    static bool Render(Job job)
    {
        job.TexRender = Obs.gs_texrender_create(ObsConst.GsBgra, ObsConst.GsZsNone);
        job.Stage = Obs.gs_stagesurface_create((uint)job.Width, (uint)job.Height, ObsConst.GsBgra);
        if (job.TexRender == IntPtr.Zero || job.Stage == IntPtr.Zero || !Obs.gs_texrender_begin(job.TexRender, (uint)job.Width, (uint)job.Height))
            return false;
        var clear = new Vec4();
        Obs.gs_clear(ObsConst.GsClearColor, ref clear, 0, 0);
        Obs.gs_ortho(0, job.CanvasWidth, 0, job.CanvasHeight, -100, 100);
        Obs.gs_blend_state_push();
        Obs.gs_blend_function(ObsConst.GsBlendOne, ObsConst.GsBlendZero);
        Obs.obs_render_main_texture();
        Obs.gs_blend_state_pop();
        Obs.gs_texrender_end(job.TexRender);
        Obs.gs_stage_texture(job.Stage, Obs.gs_texrender_get_texture(job.TexRender));
        return true;
    }

    /// <summary>The staged copy, row by row (the GPU's rows can be wider than the picture).</summary>
    static ScreenshotFrame? Read(Job job)
    {
        if (!Obs.gs_stagesurface_map(job.Stage, out IntPtr data, out uint linesize))
            return null;
        try
        {
            int row = job.Width * 4;
            var bytes = new byte[row * job.Height];
            for (int y = 0; y < job.Height; y++)
                Marshal.Copy(data + y * (int)linesize, bytes, y * row, row);
            return new ScreenshotFrame(bytes, job.Width, job.Height);
        }
        finally
        {
            Obs.gs_stagesurface_unmap(job.Stage);
        }
    }

    /// <summary>Frees the GPU objects and answers the caller, once, whoever gets here first.</summary>
    static void Finish(Job job, ScreenshotFrame? frame)
    {
        lock (Gate)
        {
            if (_job != job)
                return;
            _job = null;
        }
        if ((job.TexRender != IntPtr.Zero || job.Stage != IntPtr.Zero) && ObsHost.Started)
        {
            Obs.obs_enter_graphics();
            if (job.Stage != IntPtr.Zero)
                Obs.gs_stagesurface_destroy(job.Stage);
            if (job.TexRender != IntPtr.Zero)
                Obs.gs_texrender_destroy(job.TexRender);
            Obs.obs_leave_graphics();
        }
        job.Done.TrySetResult(frame);
    }
}
